<#
.SYNOPSIS
    One-time IIS setup for JobMatch on the Windows Server 2022 VPS. Run as Administrator.

.DESCRIPTION
    Idempotent: re-running skips anything that already exists. It:
      1. checks the IIS role and the .NET 8 Hosting Bundle are installed;
      2. creates the folder layout  <Root>\releases  and  <Root>\logs;
      3. creates a dedicated app pool (No Managed Code, ApplicationPoolIdentity) so JobMatch
         is isolated from the other sites on this shared server;
      4. grants that app pool identity read access to <Root> (and write to <Root>\logs);
      5. creates the IIS site with an HTTP host-name binding on port 80. win-acme later adds
         the HTTPS binding on 443 and needs port 80 for the Let's Encrypt challenge.

    It deliberately does NOT change the app pool's recycling / start-mode settings; see
    "App pool: keep the queue alive" in docs/vps-deployment.md.

.EXAMPLE
    .\Setup-Server.ps1
.EXAMPLE
    .\Setup-Server.ps1 -HostName jobmatch.intellite.net -Root D:\Sites\jobmatch
#>
[CmdletBinding()]
param(
    [string]$SiteName = 'jobmatch',
    [string]$AppPool  = 'JobMatch',
    [string]$HostName = 'jobmatch.intellite.net',
    [string]$Root     = 'C:\inetpub\jobmatch'
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

# --- 1. Prerequisites ---------------------------------------------------------------------
if (-not (Get-WindowsFeature Web-Server).Installed) {
    throw 'The IIS role (Web-Server) is not installed.'
}
$ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
$hasRuntime = (& dotnet --list-runtimes 2>$null) -match '^Microsoft\.AspNetCore\.App 8\.'
if (-not (Test-Path $ancm) -or -not $hasRuntime) {
    throw ('The .NET 8 Hosting Bundle is missing. Install the latest "ASP.NET Core Runtime 8.0 - ' +
           'Windows Hosting Bundle" from https://dotnet.microsoft.com/download/dotnet/8.0, then run ' +
           '"net stop was /y" and "net start w3svc" and re-run this script.')
}
Write-Host 'Prerequisites OK (IIS + .NET 8 Hosting Bundle).' -ForegroundColor Green

Import-Module WebAdministration

# --- 2. Folders ---------------------------------------------------------------------------
$releases    = Join-Path $Root 'releases'
$logs        = Join-Path $Root 'logs'
$placeholder = Join-Path $releases '_placeholder'
foreach ($dir in $Root, $releases, $logs, $placeholder) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}
# Until the first release is deployed, the site serves a holding page.
$offline = Join-Path $placeholder 'app_offline.htm'
if (-not (Test-Path $offline)) {
    Set-Content -Path $offline -Encoding ASCII -Value '<html><body><h1>JobMatch Ghana</h1><p>No release deployed yet.</p></body></html>'
}

# --- 3. App pool --------------------------------------------------------------------------
$poolPath = "IIS:\AppPools\$AppPool"
if (-not (Test-Path $poolPath)) {
    New-WebAppPool -Name $AppPool | Out-Null
    Write-Host "Created app pool '$AppPool'."
} else {
    Write-Host "App pool '$AppPool' already exists."
}
Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''          # No Managed Code
Set-ItemProperty $poolPath -Name managedPipelineMode -Value 'Integrated'
Set-ItemProperty $poolPath -Name processModel.identityType -Value 'ApplicationPoolIdentity'
Set-ItemProperty $poolPath -Name processModel.loadUserProfile -Value $true

# --- 4. File-system permissions -----------------------------------------------------------
$identity = "IIS AppPool\$AppPool"
& icacls $Root /grant "${identity}:(OI)(CI)RX" /T /Q | Out-Null
& icacls $logs /grant "${identity}:(OI)(CI)M" /T /Q | Out-Null
Write-Host "Granted '$identity' read on $Root and modify on $logs."

# --- 5. Site + HTTP binding ---------------------------------------------------------------
if (-not (Test-Path "IIS:\Sites\$SiteName")) {
    New-Website -Name $SiteName -ApplicationPool $AppPool -PhysicalPath $placeholder `
        -HostHeader $HostName -Port 80 -IPAddress '*' | Out-Null
    Write-Host "Created site '$SiteName' bound to http://${HostName}:80."
} else {
    Write-Host "Site '$SiteName' already exists; left its bindings and path unchanged."
}

# --- 6. Anonymous access only --------------------------------------------------------------
# The app does its own sign-in. If the server enables Windows/Basic authentication by default,
# IIS adds "WWW-Authenticate: Negotiate, NTLM, Basic" to the app's 401s and browsers pop up a
# Windows login box. This site only: other sites keep their settings.
$authPath = 'system.webServer/security/authentication'
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter "$authPath/anonymousAuthentication" -Name enabled -Value $true
foreach ($scheme in 'windowsAuthentication', 'basicAuthentication', 'digestAuthentication') {
    if (Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "$authPath/$scheme" -ErrorAction SilentlyContinue) {
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter "$authPath/$scheme" -Name enabled -Value $false
    }
}
Write-Host "Site '$SiteName': anonymous access on; Windows/Basic/Digest authentication off."

Write-Host ''
Write-Host 'Server setup done. Next steps (docs/vps-deployment.md):' -ForegroundColor Green
Write-Host "  1. Create the database:   sqlcmd -S . -E -b -i .\create-database.sql -v DatabaseName=`"JobMatchGhana`" AppPool=`"$AppPool`""
Write-Host '  2. Store the settings:    .\Set-AppSettings.ps1'
Write-Host "  3. HTTPS certificate:     run win-acme (wacs.exe) for $HostName"
Write-Host '  4. Deploy a release:      .\Deploy-Release.ps1 -Package <zip>'
Write-Host ''
Write-Host "REMINDER: change app pool '$AppPool' so IIS doesn't recycle it: Start Mode = AlwaysRunning," -ForegroundColor Yellow
Write-Host '          Idle Time-out = 0, periodic recycle off, preloadEnabled = true on the site.' -ForegroundColor Yellow
Write-Host '          See "App pool: keep the queue alive" in docs/vps-deployment.md.' -ForegroundColor Yellow
