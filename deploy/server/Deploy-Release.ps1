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
      2. apply pending EF Core migrations with the new release itself (idempotent);
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
    # Migrations already applied by hand (migrate.sql in SSMS):
    .\Deploy-Release.ps1 -Package <zip> -SkipMigrations
#>
[CmdletBinding(DefaultParameterSetName = 'Deploy')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Deploy')]
    [string]$Package,

    [Parameter(Mandatory, ParameterSetName = 'Rollback')]
    [switch]$Rollback,

    [string]$SiteName     = 'jobmatch',
    [string]$Root         = 'C:\inetpub\jobmatch',
    [string]$AppPool      = 'JobMatch',
    [switch]$SkipMigrations,
    [string]$HealthUrl    = 'https://jobmatch.intellite.net/health',
    [int]$Keep            = 5,
    [string]$NluService   = 'JobMatchNlu',
    [int]$NluPort         = 8000
)

$ErrorActionPreference = 'Stop'

# Native tools (pip, robocopy, tar, dotnet) write progress and warnings to stderr; under 'Stop'
# Windows PowerShell 5.1 would turn those into terminating errors. Run them through Invoke-Native,
# which judges success by exit code alone.
function Invoke-Native([scriptblock]$Command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $saved }
}
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

function Update-Nlu([string]$Name) {
    # Refresh the NLU service's code from this release (models stay as installed).
    $src = Join-Path (Join-Path $releases $Name) 'nlp'
    if (-not (Test-Path (Join-Path $src 'jobmatch_nlp'))) { return }
    if (-not (Get-Service $NluService -ErrorAction SilentlyContinue)) {
        Write-Warning ("NLU service '$NluService' isn't installed: the gateway will use its keyword fallback. " +
                       'Run .\server\Install-NluService.ps1 once to install it.')
        return
    }
    Write-Host '==> Updating the NLU service' -ForegroundColor Cyan
    $nlp = Join-Path $Root 'nlp'
    $app = Join-Path $nlp 'app'
    Stop-Service $NluService -Force
    Invoke-Native { & robocopy $src $app /MIR /NFL /NDL /NJH /NJS /NP | Out-Null }
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)." }
    $hashFile = Join-Path $nlp 'requirements.sha256'
    $hash = (Get-FileHash (Join-Path $app 'requirements.txt')).Hash
    if (-not (Test-Path $hashFile) -or (Get-Content $hashFile) -ne $hash) {
        Write-Host '    requirements changed: installing packages' -ForegroundColor Cyan
        Invoke-Native { & (Join-Path $nlp 'venv\Scripts\python.exe') -m pip install --quiet -r (Join-Path $app 'requirements.txt') `
            --extra-index-url https://download.pytorch.org/whl/cpu }
        if ($LASTEXITCODE -ne 0) { throw 'pip install failed; the NLU service is stopped. Fix and re-run.' }
        $hash | Set-Content $hashFile
    }
    Start-Service $NluService
    for ($i = 1; $i -le 36; $i++) {
        try {
            $h = Invoke-RestMethod -Uri "http://127.0.0.1:$NluPort/health" -TimeoutSec 5
            if ($h.status -eq 'ok') { Write-Host "    NLU healthy: $($h.model_version)" -ForegroundColor Green; return }
        } catch { }
        Start-Sleep -Seconds 5
    }
    Write-Warning "NLU service not healthy after 3 minutes; the gateway falls back to keywords. See $nlp\logs\nlu.log."
}

if ($Rollback) {
    $current = Get-CurrentRelease
    $previous = Get-Releases | Where-Object { $_.Name -lt $current } | Select-Object -Last 1
    if (-not $previous) { throw "No release older than '$current' to roll back to." }
    Write-Host "Rolling back $current -> $($previous.Name). The database schema is NOT rolled back." -ForegroundColor Yellow
    Update-Nlu $previous.Name
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
if ($SkipMigrations) {
    Write-Host '==> Skipping migrations (-SkipMigrations): make sure migrate.sql was applied.' -ForegroundColor Yellow
} else {
    # The new release applies its own migrations (EF Core; no sqlcmd needed), with the site's
    # settings from the app pool. It connects as YOU (Windows authentication), so run this script
    # from an account with rights to change the JobMatch database's schema.
    Write-Host '==> Applying database migrations' -ForegroundColor Cyan
    $filter = "system.applicationHost/applicationPools/add[@name='$AppPool']/environmentVariables"
    foreach ($v in Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "$filter/add") {
        Set-Item -Path "Env:\$($v.name)" -Value $v.value
    }
    Push-Location (Join-Path $target 'app')
    try { Invoke-Native { & dotnet .\JobMatch.Api.dll migrate } }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $target -Recurse -Force   # so a corrected re-run doesn't hit "already exists"
        throw "Migration failed (exit $LASTEXITCODE). The site still serves the previous release. " +
              "Fallback: run this release's migrate.sql in SSMS, then re-run with -SkipMigrations."
    }
}

# --- 3. NLU service code ---------------------------------------------------------------------
Update-Nlu $name

# --- 4. Switch and verify -----------------------------------------------------------------
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
