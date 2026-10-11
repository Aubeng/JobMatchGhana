<#
.SYNOPSIS
    Builds a JobMatch release package for the Windows/IIS VPS (run on your dev machine).

.DESCRIPTION
    Produces artifacts\jobmatch-<timestamp>.zip containing:
      app\          the published API (framework-dependent, .NET 8, in-process IIS hosting)
      migrate.sql   an idempotent EF Core migration script (safe to re-run)
      server\       the server-side scripts (Setup-Server, Set-AppSettings, Deploy-Release, SQL)
    Copy the zip to the VPS and run server\Deploy-Release.ps1 from it.
    See docs/vps-deployment.md.

.EXAMPLE
    .\deploy\Publish.ps1
.EXAMPLE
    .\deploy\Publish.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)." }
}

$root    = Split-Path $PSScriptRoot -Parent
$api     = Join-Path $root 'src\JobMatch.Api\JobMatch.Api.csproj'
$infra   = Join-Path $root 'src\JobMatch.Infrastructure\JobMatch.Infrastructure.csproj'
$name    = 'jobmatch-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$staging = Join-Path $root "artifacts\$name"
$zip     = "$staging.zip"

Push-Location $root
try {
    Invoke-Step 'Restore local tools (dotnet-ef 8.0.8)' { dotnet tool restore }

    if (-not $SkipTests) {
        Invoke-Step 'Run tests' { dotnet test -c Release --nologo }
    }

    Invoke-Step 'Publish API' {
        dotnet publish $api -c Release -o (Join-Path $staging 'app') --nologo
    }

    Invoke-Step 'Generate idempotent migration script' {
        dotnet tool run dotnet-ef migrations script --idempotent `
            --project $infra --startup-project $api `
            --output (Join-Path $staging 'migrate.sql')
    }

    Copy-Item (Join-Path $PSScriptRoot 'server') (Join-Path $staging 'server') -Recurse

    Write-Host '==> Zip package' -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
    Remove-Item $staging -Recurse -Force

    Write-Host ''
    Write-Host "Package ready: $zip" -ForegroundColor Green
    Write-Host 'Copy it to the VPS, then run (as Administrator):'
    Write-Host "  .\server\Deploy-Release.ps1 -Package C:\path\to\$name.zip"
}
finally {
    Pop-Location
}
