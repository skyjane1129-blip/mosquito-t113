# Starts the demo API (SQLite + local storage) and the frp client on this Windows machine.
# Both run hidden with logs under artifacts\; safe to re-run, already-running processes are left alone.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$api = Join-Path $root 'src\Mosquito.Cloud.Api'
$dotnet = 'C:\project\mosquito\mosquito-windows-client\.tools\dotnet\dotnet.exe'
$dll = Join-Path $api 'bin\Release\net8.0\Mosquito.Cloud.Api.dll'
$frp = 'C:\Users\Jane\Documents\frp'
$logs = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

if (-not (Test-Path $dll)) { throw "Build the API first: dotnet build src\Mosquito.Cloud.Api -c Release" }

$apiRunning = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like '*Mosquito.Cloud.Api.dll*' }
if ($apiRunning) {
    Write-Output "API already running (PID $($apiRunning.ProcessId))"
} else {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    Start-Process -FilePath $dotnet -ArgumentList $dll, '--urls', 'http://127.0.0.1:5080' -WorkingDirectory $api -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logs 'api.out.log') -RedirectStandardError (Join-Path $logs 'api.err.log')
    Write-Output "API started on http://127.0.0.1:5080"
}

if (Get-Process frpc -ErrorAction SilentlyContinue) {
    Write-Output "frpc already running"
} else {
    Start-Process -FilePath (Join-Path $frp 'frpc.exe') -ArgumentList '-c', 'frpc.toml' -WorkingDirectory $frp -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $frp 'frpc.out.log') -RedirectStandardError (Join-Path $frp 'frpc.err.log')
    Write-Output "frpc started"
}

Start-Sleep -Seconds 4
try { (Invoke-WebRequest -Uri 'http://127.0.0.1:5080/health' -UseBasicParsing -TimeoutSec 5 -Proxy $null).Content } catch { Write-Warning "local health check failed: $($_.Exception.Message)" }
