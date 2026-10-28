<#
.SYNOPSIS
    Stores JobMatch's production settings and secrets as environment variables on its IIS
    app pool. Run as Administrator on the VPS.

.DESCRIPTION
    Values live in IIS's applicationHost.config (readable by administrators only), not in
    the site folder, so they are never in the repo or the release zip and they survive every
    redeploy. ASP.NET Core reads them as configuration ("__" maps to ":"), and they override
    appsettings.json. This is the VPS equivalent of App Service "Application settings" (DL-007).

    Each prompt shows whether a value is already set; press Enter to keep it unchanged.
    Secrets are typed hidden and never echoed. The app pool is recycled at the end.

.EXAMPLE
    .\Set-AppSettings.ps1
#>
[CmdletBinding()]
param(
    [string]$AppPool      = 'JobMatch',
    [string]$SqlInstance  = '.',
    [string]$DatabaseName = 'JobMatchGhana',
    [int]$NluPort         = 8000
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

if (-not (Test-Path "IIS:\AppPools\$AppPool")) {
    throw "App pool '$AppPool' not found. Run Setup-Server.ps1 first."
}

$apphost = 'MACHINE/WEBROOT/APPHOST'
$filter  = "system.applicationHost/applicationPools/add[@name='$AppPool']/environmentVariables"

function Get-PoolVariable([string]$Name) {
    $attr = Get-WebConfigurationProperty -PSPath $apphost -Filter "$filter/add[@name='$Name']" -Name 'value'
    if ($attr) { return [string]$attr.Value }
    return $null
}

function Set-PoolVariable([string]$Name, [string]$Value) {
    if (Get-PoolVariable $Name) {
        Remove-WebConfigurationProperty -PSPath $apphost -Filter $filter -Name '.' -AtElement @{ name = $Name }
    }
    Add-WebConfigurationProperty -PSPath $apphost -Filter $filter -Name '.' -Value @{ name = $Name; value = $Value }
}

function Read-Setting([string]$Name, [string]$Label, [switch]$Secret, [string]$Default) {
    $current = Get-PoolVariable $Name
    $hint = if ($current) { 'set; Enter keeps it' } elseif ($Default) { "Enter for $Default" } else { 'not set' }
    if ($Secret) {
        $secure = Read-Host "$Label [$hint]" -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    } else {
        $value = Read-Host "$Label [$hint]"
    }
    if ($value) { return $value }
    if (-not $current -and $Default) { return $Default }
    return $null   # keep the current value
}

$changes = [ordered]@{
    'ASPNETCORE_ENVIRONMENT'  = 'Production'
    # Windows auth as the app pool identity; see create-database.sql.
    'ConnectionStrings__SqlServer' =
        "Server=$SqlInstance;Database=$DatabaseName;Trusted_Connection=True;TrustServerCertificate=True"
}

Write-Host 'Redis (tporadowski port on this server). Leave the password blank if Redis has no requirepass.'
$redisPassword = Read-Setting 'Redis__ConnectionString' 'Redis password' -Secret
if ($redisPassword -or -not (Get-PoolVariable 'Redis__ConnectionString')) {
    # The 24h session TTL and the "jobmatch:session:" key prefix (isolation from other apps
    # sharing this Redis) come from appsettings.json.
    $redis = 'localhost:6379'
    if ($redisPassword) { $redis += ",password=$redisPassword" }
    $changes['Redis__ConnectionString'] = $redis
}

Write-Host ''
Write-Host 'WhatsApp Cloud API (Meta developer dashboard; see docs/whatsapp-cloud-api-setup.md).'
$prompts = @(
    @{ Name = 'WhatsApp__AppSecret';     Label = 'App Secret';              Secret = $true  },
    @{ Name = 'WhatsApp__VerifyToken';   Label = 'Webhook verify token';    Secret = $true  },
    @{ Name = 'WhatsApp__PhoneNumberId'; Label = 'Phone number ID';         Secret = $false },
    @{ Name = 'WhatsApp__AccessToken';   Label = 'Access token (permanent)'; Secret = $true }
)
foreach ($p in $prompts) {
    $value = Read-Setting $p.Name $p.Label -Secret:$p.Secret
    if ($value) { $changes[$p.Name] = $value }
}

Write-Host ''
$current = Get-PoolVariable 'WhatsApp__DryRun'
$answer = Read-Host "Dry run: log WhatsApp messages instead of sending them? y/n [now: $(if ($current) { $current } else { 'false' })]"
if ($answer -match '^(y|yes)$') { $changes['WhatsApp__DryRun'] = 'true' }
elseif ($answer -match '^(n|no)$') { $changes['WhatsApp__DryRun'] = 'false' }

# The Python NLU + matching service on this machine (Install-NluService.ps1), 127.0.0.1 only.
$changes['Nlu__UseStub'] = 'false'
$changes['Nlu__BaseUrl'] = "http://127.0.0.1:$NluPort"

Write-Host ''
Write-Host 'Research exports: pseudonym key (HMAC secret). Enter keeps the current one; a new one is generated if none is set.'
$key = Read-Setting 'Research__PseudonymKey' 'Pseudonym key (16+ characters)' -Secret
if (-not $key -and -not (Get-PoolVariable 'Research__PseudonymKey')) {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $key = [Convert]::ToBase64String($bytes)
    Write-Host '  generated a new key. Keep a copy somewhere safe: changing it changes every exported participant id.' -ForegroundColor Yellow
    Write-Host "  key: $key" -ForegroundColor Yellow
}
if ($key) {
    if ($key.Length -lt 16) { throw 'The pseudonym key must be at least 16 characters.' }
    $changes['Research__PseudonymKey'] = $key
}

foreach ($name in $changes.Keys) {
    Set-PoolVariable $name $changes[$name]
    Write-Host "  set $name"
}

if ((Get-WebAppPoolState -Name $AppPool).Value -eq 'Started') {
    Restart-WebAppPool -Name $AppPool
} else {
    Start-WebAppPool -Name $AppPool
}
Write-Host ''
Write-Host "Saved. App pool '$AppPool' recycled so the new settings take effect." -ForegroundColor Green
Write-Host 'The first log lines of the app say whether WhatsApp is LIVE or DRY RUN.'
