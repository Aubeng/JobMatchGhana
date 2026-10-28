<#
.SYNOPSIS
    Runs one of the app's operator commands on the VPS (seed-jobs, create-admin, alert-job) with
    the same settings the website uses. Run as Administrator.

.DESCRIPTION
    The site's settings live as environment variables on its IIS app pool (Set-AppSettings.ps1),
    which a console doesn't see. This script copies them into its own process, then runs the
    live release's JobMatch.Api.dll with your arguments. The command connects to SQL Server as
    YOU (Windows authentication), so run it from an account with rights on the JobMatch database.

.EXAMPLE
    .\Invoke-AppCommand.ps1 seed-jobs
.EXAMPLE
    .\Invoke-AppCommand.ps1 create-admin you@example.org
#>
# PositionalBinding off: otherwise "seed-jobs" binds to -SiteName, the first positional parameter.
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$SiteName = 'jobmatch',
    [string]$AppPool  = 'JobMatch',
    [Parameter(ValueFromRemainingArguments)][string[]]$Command
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration
if (-not $Command) { throw 'Usage: .\Invoke-AppCommand.ps1 seed-jobs | create-admin <email> | alert-job <id>' }

$app = (Get-Item "IIS:\Sites\$SiteName").physicalPath
if (-not (Test-Path (Join-Path $app 'JobMatch.Api.dll'))) { throw "No deployed release at $app. Run Deploy-Release.ps1 first." }

$filter = "system.applicationHost/applicationPools/add[@name='$AppPool']/environmentVariables"
$vars = Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "$filter/add"
foreach ($v in $vars) { Set-Item -Path "Env:\$($v.name)" -Value $v.value }
Write-Host "Running '$($Command -join ' ')' against $app with $(@($vars).Count) app-pool settings." -ForegroundColor Cyan

Push-Location $app
try {
    & dotnet .\JobMatch.Api.dll @Command
    if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE)." }
}
finally {
    Pop-Location
}
