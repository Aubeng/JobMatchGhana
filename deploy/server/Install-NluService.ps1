<#
.SYNOPSIS
    One-time setup of the Python NLU + matching service on the VPS, as a Windows service.
    Run as Administrator. Idempotent: re-run it to install new models or repair the service.

.DESCRIPTION
    Layout under <Root>\nlp:
      venv\      Python virtual environment (CPU-only torch etc. from requirements.txt)
      app\       the service code + data (refreshed by Deploy-Release.ps1 on every release)
      models\    the trained intent and entity models (from Package-NluModels.ps1)
      hf-cache\  Hugging Face cache: the MiniLM matching model downloads here on first start
      logs\      service stdout/stderr (rotated by NSSM)

    The service ("JobMatchNlu") runs uvicorn on 127.0.0.1:<Port> only: it has no authentication,
    so it must never listen on a public interface. It runs as NT AUTHORITY\LocalService (a
    low-privilege built-in account), not as an administrator.

    Prerequisites (see docs/vps-deployment.md):
      - Python 3.12 (64-bit) installed for all users; the "py" launcher is used to find it.
      - NSSM (https://nssm.cc, 2.24 or later): pass the path to nssm.exe.
      - Outbound internet for pip (PyPI, download.pytorch.org) and the first MiniLM download.

.EXAMPLE
    # First install (from the extracted release folder):
    .\server\Install-NluService.ps1 -Nssm C:\Tools\nssm\win64\nssm.exe -ModelsZip C:\Deploy\jobmatch-models-20261017.zip
.EXAMPLE
    # New models later:
    .\server\Install-NluService.ps1 -Nssm C:\Tools\nssm\win64\nssm.exe -ModelsZip C:\Deploy\jobmatch-models-<new>.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Nssm,
    [string]$ModelsZip,
    [string]$Root        = 'C:\inetpub\jobmatch',
    [string]$PythonVersion = '3.12',
    [int]$Port           = 8000,
    [int]$Threads        = 4,
    [string]$ServiceName = 'JobMatchNlu'
)

$ErrorActionPreference = 'Stop'

# Native tools (pip, robocopy, tar, sqlcmd) write progress and warnings to stderr; under 'Stop'
# Windows PowerShell 5.1 would turn those into terminating errors. Run them through Invoke-Native,
# which judges success by exit code alone.
function Invoke-Native([scriptblock]$Command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $saved }
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}
if (-not (Test-Path $Nssm)) { throw "nssm.exe not found at $Nssm." }

$nlp     = Join-Path $Root 'nlp'
$venv    = Join-Path $nlp 'venv'
$app     = Join-Path $nlp 'app'
$models  = Join-Path $nlp 'models'
$hfCache = Join-Path $nlp 'hf-cache'
$logs    = Join-Path $nlp 'logs'
$release = Join-Path (Split-Path $PSScriptRoot -Parent) 'nlp'   # the release this script came in

foreach ($dir in $nlp, $app, $models, $hfCache, $logs) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}

# --- 1. Code: copy this release's service code (Deploy-Release.ps1 does this on later releases)
if (Test-Path (Join-Path $release 'jobmatch_nlp')) {
    Write-Host "==> Copying service code from $release" -ForegroundColor Cyan
    Invoke-Native { & robocopy $release $app /MIR /NFL /NDL /NJH /NJS /NP | Out-Null }
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)." }
}
if (-not (Test-Path (Join-Path $app 'requirements.txt'))) {
    throw "No service code in $app. Run this script from an extracted release (it contains nlp\)."
}

# --- 2. Python virtual environment + packages
if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) {
    Write-Host "==> Creating virtual environment (Python $PythonVersion)" -ForegroundColor Cyan
    Invoke-Native { & py "-$PythonVersion" -m venv $venv }
    if ($LASTEXITCODE -ne 0) { throw "Could not create the venv. Is Python $PythonVersion installed (py -$PythonVersion --version)?" }
}
$python = Join-Path $venv 'Scripts\python.exe'
Write-Host '==> Installing Python packages (CPU-only torch; first run downloads ~1 GB)' -ForegroundColor Cyan
Invoke-Native { & $python -m pip install --quiet --upgrade pip }
Invoke-Native { & $python -m pip install --quiet -r (Join-Path $app 'requirements.txt') --extra-index-url https://download.pytorch.org/whl/cpu }
if ($LASTEXITCODE -ne 0) { throw 'pip install failed.' }
(Get-FileHash (Join-Path $app 'requirements.txt')).Hash | Set-Content (Join-Path $nlp 'requirements.sha256')

# --- 3. Models
if ($ModelsZip) {
    Write-Host "==> Installing models from $ModelsZip" -ForegroundColor Cyan
    Get-ChildItem $models | Remove-Item -Recurse -Force
    Invoke-Native { & tar.exe -x -f $ModelsZip -C $models }
    if ($LASTEXITCODE -ne 0) { throw "Could not extract $ModelsZip." }
}
foreach ($m in 'intent', 'ner') {
    if (-not (Test-Path (Join-Path $models "$m\version.json"))) {
        throw "Model '$m' missing in $models. Pass -ModelsZip (built by deploy\Package-NluModels.ps1)."
    }
}

# --- 4. Permissions: the service account reads code/venv/models, writes cache and logs only
$account = 'NT AUTHORITY\LocalService'
& icacls $nlp /grant "${account}:(OI)(CI)RX" /T /Q | Out-Null
foreach ($dir in $hfCache, $logs) { & icacls $dir /grant "${account}:(OI)(CI)M" /T /Q | Out-Null }

# --- 5. The Windows service
$existing = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "==> Updating service $ServiceName" -ForegroundColor Cyan
    if ($existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force }
} else {
    Write-Host "==> Installing service $ServiceName" -ForegroundColor Cyan
    & $Nssm install $ServiceName $python | Out-Null
}
& $Nssm set $ServiceName Application $python | Out-Null
& $Nssm set $ServiceName AppParameters "-m uvicorn jobmatch_nlp.app:app --host 127.0.0.1 --port $Port --log-level warning" | Out-Null
& $Nssm set $ServiceName AppDirectory $app | Out-Null
& $Nssm set $ServiceName AppEnvironmentExtra "JOBMATCH_NLP_MODELS=$models" "JOBMATCH_NLP_THREADS=$Threads" "HF_HOME=$hfCache" "PYTHONUNBUFFERED=1" | Out-Null
& $Nssm set $ServiceName AppStdout (Join-Path $logs 'nlu.log') | Out-Null
& $Nssm set $ServiceName AppStderr (Join-Path $logs 'nlu.log') | Out-Null
& $Nssm set $ServiceName AppRotateFiles 1 | Out-Null
& $Nssm set $ServiceName AppRotateBytes 10485760 | Out-Null
& $Nssm set $ServiceName ObjectName $account | Out-Null
& $Nssm set $ServiceName Start SERVICE_AUTO_START | Out-Null
& $Nssm set $ServiceName DisplayName 'JobMatch Ghana NLU + matching' | Out-Null
& $Nssm set $ServiceName Description 'Python FastAPI: intent/entity models and MiniLM job ranking for the JobMatch gateway (127.0.0.1 only).' | Out-Null

Start-Service $ServiceName

# --- 6. Wait until both model sets answer (first start downloads MiniLM, ~90 MB)
$url = "http://127.0.0.1:$Port/health"
Write-Host "==> Waiting for $url (loading models takes up to a few minutes)" -ForegroundColor Cyan
for ($i = 1; $i -le 60; $i++) {
    try {
        $h = Invoke-RestMethod -Uri $url -TimeoutSec 5
        if ($h.status -eq 'ok' -and $h.matching_model) {
            Write-Host "Healthy: models $($h.model_version); matching $($h.matching_model)" -ForegroundColor Green
            return
        }
    } catch { }
    Start-Sleep -Seconds 5
}
Write-Warning "The service didn't report healthy in 5 minutes. Check $logs\nlu.log and 'Get-Service $ServiceName'."
