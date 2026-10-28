<#
.SYNOPSIS
    Builds a JobMatch release package for the Windows/IIS VPS (run on your dev machine).

.DESCRIPTION
    Produces artifacts\jobmatch-<timestamp>.zip containing:
      app\          the published API (framework-dependent, .NET 8, in-process IIS hosting)
      migrate.sql   an idempotent EF Core migration script (safe to re-run)
      server\       the server-side scripts (Setup-Server, Set-AppSettings, Deploy-Release, SQL, NLU service)
      nlp\          the NLU service's code, data and requirements (models ship separately)
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
    # Native tools (npm, dotnet) write warnings to stderr; Windows PowerShell 5.1 would turn
    # those into terminating errors under 'Stop'. Success is judged by the exit code instead.
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $saved }
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

    # The admin SPA is served by the API from wwwroot/admin, so it is built before publishing.
    Push-Location (Join-Path $root 'admin')
    try {
        Invoke-Step 'Install admin SPA packages' { npm ci --no-audit --no-fund }
        if (-not $SkipTests) { Invoke-Step 'Test admin SPA' { npm test } }
        Invoke-Step 'Build admin SPA' { npm run build }
    }
    finally {
        Pop-Location
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

    # The NLU service's code and data (not its virtualenv or trained models: the models ship once,
    # separately, via Package-NluModels.ps1; the server builds its own virtualenv).
    Write-Host '==> Copy NLU service code and data' -ForegroundColor Cyan
    $nlpSrc = Join-Path $root 'nlp'
    $nlpDst = Join-Path $staging 'nlp'
    New-Item -ItemType Directory -Path $nlpDst | Out-Null
    Copy-Item (Join-Path $nlpSrc 'jobmatch_nlp') $nlpDst -Recurse
    Copy-Item (Join-Path $nlpSrc 'data') $nlpDst -Recurse
    Copy-Item (Join-Path $nlpSrc 'requirements.txt'), (Join-Path $nlpSrc 'pyproject.toml') $nlpDst
    Get-ChildItem $nlpDst -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force
    $review = Join-Path $nlpDst 'data/review'
    if (Test-Path $review) { Remove-Item $review -Recurse -Force }

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
