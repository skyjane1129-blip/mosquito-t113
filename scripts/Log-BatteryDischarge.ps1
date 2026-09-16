# Logs the board's reported battery voltage from the demo server once a minute so a discharge
# curve (and hours-per-charge for each power profile) can be computed without a meter.
# Usage: .\Log-BatteryDischarge.ps1 [-DeviceId MQ-SH-001] [-Base http://127.0.0.1:5080] [-Out artifacts\battery-log.csv]
# Runs until stopped (Ctrl+C or Stop-Process). Start hidden with:
#   Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','scripts\Log-BatteryDischarge.ps1' -WindowStyle Hidden
param(
    [string]$DeviceId = 'MQ-SH-001',
    [string]$Base = 'http://127.0.0.1:5080',
    [string]$Out = 'artifacts\battery-log.csv',
    [int]$IntervalSeconds = 60,
    [string]$Username = 'demo',
    [string]$Password = 'change-me'
)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $root $Out
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null
if (-not (Test-Path $outPath)) {
    'utc,local,batteryMv,vbusGood,chargeState,chargeCurrentMa,online,lastSeenUtc,boardUptimeS,note' | Set-Content -Path $outPath -Encoding utf8
}
$token = $null
function Get-Token {
    $body = @{ username = $Username; password = $Password } | ConvertTo-Json -Compress
    (Invoke-RestMethod -Proxy $null -Method Post -Uri "$Base/api/auth/login" -ContentType 'application/json' -Body $body -TimeoutSec 15).accessToken
}
while ($true) {
    $note = ''
    try {
        if (-not $token) { $token = Get-Token }
        $d = Invoke-RestMethod -Proxy $null -Uri "$Base/api/v2/devices/$DeviceId" -Headers @{ Authorization = "Bearer $token" } -TimeoutSec 15
        $p = $d.lastPower
        $line = '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}' -f (Get-Date).ToUniversalTime().ToString('o'), (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'),
            $p.batteryMv, $p.vbusGood, $p.chargeState, $p.chargeCurrentMa, $d.online, $d.lastSeenAtUtc, [int]($p.sampledUptimeMs / 1000), $note
        Add-Content -Path $outPath -Value $line -Encoding utf8
    }
    catch {
        $token = $null
        Add-Content -Path $outPath -Value ('{0},{1},,,,,,,,error: {2}' -f (Get-Date).ToUniversalTime().ToString('o'), (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), ($_.Exception.Message -replace ',', ';')) -Encoding utf8
    }
    Start-Sleep -Seconds $IntervalSeconds
}
