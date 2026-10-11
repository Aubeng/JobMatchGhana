<#
.SYNOPSIS
    Deploys a JobMatch release package (from deploy\Publish.ps1) on the VPS. Run as Administrator.

.DESCRIPTION
    Each release is unpacked into its own folder, <Root>\releases\<package-name>, and the IIS
    site is then re-pointed at it. No files are overwritten in place, so there are no
    locked-DLL problems, and rolling back is just pointing the site at the previous folder:
        .\Deploy-Release.ps1 -Rollback

    Steps:
      1. unpack the zip into a new release folder;
      2. apply migrate.sql (idempotent EF Core script) with sqlcmd;
      3. switch the site's physical path to the new release (IIS restarts the app);
      4. poll /health until the app answers;
      5. delete old releases, keeping the newest -Keep.

    Migrations run before the switch, so the old release briefly runs against the new schema.
    Keep migrations additive (add columns/tables, don't rename or drop in the same release).

.EXAMPLE
    .\Deploy-Release.ps1 -Package C:\Users\Administrator\Downloads\jobmatch-20261011-140000.zip
.EXAMPLE
    .\Deploy-Release.ps1 -Rollback
.EXAMPLE
    # SQL login instead of Windows authentication for the migration step:
    .\Deploy-Release.ps1 -Package <zip> -SqlCredential (Get-Credential sa)
#>
[CmdletBinding(DefaultParameterSetName = 'Deploy')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Deploy')]
    [string]$Package,

    [Parameter(Mandatory, ParameterSetName = 'Rollback')]
    [switch]$Rollback,

    [string]$SiteName     = 'jobmatch',
    [string]$Root         = 'C:\inetpub\jobmatch',
    [string]$SqlInstance  = '.',
    [string]$DatabaseName = 'JobMatchGhana',
    [pscredential]$SqlCredential,
    [string]$HealthUrl    = 'https://jobmatch.intellite.net/health',
    [int]$Keep            = 5
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

$sitePath = "IIS:\Sites\$SiteName"
if (-not (Test-Path $sitePath)) { throw "Site '$SiteName' not found. Run Setup-Server.ps1 first." }
$releases = Join-Path $Root 'releases'

function Get-Releases {
    Get-ChildItem $releases -Directory | Where-Object { $_.Name -like 'jobmatch-*' } | Sort-Object Name
}

function Get-CurrentRelease {
    $path = (Get-Item $sitePath).physicalPath
    return Split-Path (Split-Path $path -Parent) -Leaf   # ...\releases\<name>\app -> <name>
}

function Switch-Release([string]$Name) {
    $app = Join-Path (Join-Path $releases $Name) 'app'
    if (-not (Test-Path (Join-Path $app 'JobMatch.Api.dll'))) { throw "No app found in $app." }
    Set-ItemProperty $sitePath -Name physicalPath -Value $app
    Write-Host "Site '$SiteName' now serves $app"
}

function Wait-Healthy {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    for ($i = 1; $i -le 15; $i++) {
        try {
            $r = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 10
            if ($r.StatusCode -eq 200) {
                Write-Host "Healthy: $HealthUrl -> $($r.Content)" -ForegroundColor Green
                return
            }
        } catch {
            Write-Host "  waiting for $HealthUrl ($i/15): $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 2
    }
    Write-Warning ("$HealthUrl did not answer 200. Check Event Viewer > Windows Logs > Application " +
                   "(source 'IIS AspNetCore Module V2' and '.NET Runtime'). Roll back with: .\Deploy-Release.ps1 -Rollback")
}

if ($Rollback) {
    $current = Get-CurrentRelease
    $previous = Get-Releases | Where-Object { $_.Name -lt $current } | Select-Object -Last 1
    if (-not $previous) { throw "No release older than '$current' to roll back to." }
    Write-Host "Rolling back $current -> $($previous.Name). The database schema is NOT rolled back." -ForegroundColor Yellow
    Switch-Release $previous.Name
    Wait-Healthy
    return
}

# --- 1. Unpack ----------------------------------------------------------------------------
$Package = (Resolve-Path $Package).Path
$name = [IO.Path]::GetFileNameWithoutExtension($Package)
$target = Join-Path $releases $name
if (Test-Path $target) { throw "Release folder $target already exists; was this package already deployed?" }
Write-Host "==> Unpacking $name" -ForegroundColor Cyan
Expand-Archive -Path $Package -DestinationPath $target

# --- 2. Migrate ---------------------------------------------------------------------------
Write-Host '==> Applying database migrations' -ForegroundColor Cyan
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw 'sqlcmd not found. Install it with "winget install sqlcmd" (or SQL Server Command Line Utilities).'
}
$sqlArgs = @('-S', $SqlInstance, '-d', $DatabaseName, '-b', '-I', '-i', (Join-Path $target 'migrate.sql'))
if ($SqlCredential) {
    # Password via the environment, not -P, so it never shows in the process list.
    $sqlArgs += @('-U', $SqlCredential.UserName)
    $env:SQLCMDPASSWORD = $SqlCredential.GetNetworkCredential().Password
} else {
    $sqlArgs += '-E'
}
try { & sqlcmd @sqlArgs }
finally { Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue }
if ($LASTEXITCODE -ne 0) {
    throw "Migration failed (sqlcmd exit $LASTEXITCODE). The site still serves the previous release."
}

# --- 3 + 4. Switch and verify -------------------------------------------------------------
Write-Host '==> Switching site to the new release' -ForegroundColor Cyan
Switch-Release $name
Wait-Healthy

# --- 5. Prune -----------------------------------------------------------------------------
$old = Get-Releases | Select-Object -SkipLast $Keep
foreach ($dir in $old) {
    Remove-Item $dir.FullName -Recurse -Force
    Write-Host "Removed old release $($dir.Name)"
}

Write-Host ''
Write-Host "Deployed $name." -ForegroundColor Green
