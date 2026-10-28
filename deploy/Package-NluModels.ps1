<#
.SYNOPSIS
    Packages the trained NLU models (nlp\models, about 1.8 GB) for the VPS. Run on your dev
    machine after training; copy the zip to the server once and install it with
    server\Install-NluService.ps1 -ModelsZip <zip>. Retrain -> repackage -> reinstall.

.EXAMPLE
    .\deploy\Package-NluModels.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$models = Join-Path $root 'nlp\models'
foreach ($m in 'intent', 'ner') {
    if (-not (Test-Path (Join-Path $models "$m\version.json"))) {
        throw "No trained '$m' model in $models. Train it first (nlp/README.md section 3)."
    }
}
$out = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$zip = Join-Path $out ('jobmatch-models-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')

# tar.exe (built into Windows) handles files over 2 GB and is much faster than Compress-Archive.
Write-Host "==> Zipping $models (this takes a few minutes)" -ForegroundColor Cyan
& tar.exe -a -c -f $zip -C $models intent ner
if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)." }

$intent = Get-Content (Join-Path $models 'intent\version.json') -Raw | ConvertFrom-Json
$ner = Get-Content (Join-Path $models 'ner\version.json') -Raw | ConvertFrom-Json
Write-Host ''
Write-Host "Models package: $zip ($([math]::Round((Get-Item $zip).Length / 1GB, 2)) GB)" -ForegroundColor Green
Write-Host "  intent trained $($intent.trained_utc), ner trained $($ner.trained_utc)"
