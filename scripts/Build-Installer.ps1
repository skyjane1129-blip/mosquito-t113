# Builds the one-click Windows installer: publish the client self-contained, stage the verified ADB
# host components and the deployment configuration, then compile installer\MosquitoCapture.iss.
# Usage: .\scripts\Build-Installer.ps1 [-Version 1.0.0] [-AdbSource C:\embedded\android-platform-tools] [-ApiBaseUrl http://...] [-DeviceId MQ-SH-001]
param(
    [string]$Version = '1.0.0',
    [string]$AdbSource = 'C:\embedded\android-platform-tools',
    [string]$ApiBaseUrl = '',
    [string]$DeviceId = '',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install with: winget install --id JRSoftware.InnoSetup -e' }
    foreach ($f in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll') {
        if (-not (Test-Path (Join-Path $AdbSource $f))) { throw "ADB component missing: $AdbSource\$f" }
    }

    if (-not $SkipPublish) {
        & .\dev.ps1 Publish
        if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }
    }
    $publish = Join-Path $root 'artifacts\publish\win-x64'
    if (-not (Test-Path (Join-Path $publish 'MosquitoCapture.exe'))) { throw "publish output not found: $publish" }

    $stage = Join-Path $root 'installer\stage'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force -Path "$stage\app", "$stage\platform-tools", "$stage\config" | Out-Null

    # 1) application (self-contained publish output)
    Copy-Item -Path (Join-Path $publish '*') -Destination "$stage\app" -Recurse -Force
    # Never ship machine-local overrides from the publish folder; the deployment config is staged separately.
    Remove-Item -Force -ErrorAction SilentlyContinue "$stage\app\appsettings.Local.json"

    # 2) verified ADB host components only (no fastboot/mke2fs etc.), with the Android notice file
    foreach ($f in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'libwinpthread-1.dll', 'NOTICE.txt', 'source.properties') {
        $src = Join-Path $AdbSource $f
        if (Test-Path $src) { Copy-Item -Path $src -Destination "$stage\platform-tools" -Force }
    }

    # 3) deployment configuration: ADB path relative to the install directory
    $settings = Get-Content -Raw (Join-Path $root 'src\Mosquito.Client\appsettings.json') | ConvertFrom-Json
    $settings.AdbPath = 'platform-tools\adb.exe'
    $settings | ConvertTo-Json -Depth 5 | Set-Content -Path "$stage\config\appsettings.json" -Encoding utf8
    $local = [ordered]@{}
    if ($ApiBaseUrl) { $local.ApiBaseUrl = $ApiBaseUrl }
    if ($DeviceId) { $local.DeviceId = $DeviceId }
    if ($local.Count -eq 0 -and (Test-Path (Join-Path $root 'src\Mosquito.Client\appsettings.Local.json'))) {
        # Fall back to this machine's demo overrides (device id + public API address, no secrets).
        Copy-Item (Join-Path $root 'src\Mosquito.Client\appsettings.Local.json') "$stage\config\appsettings.Local.json"
    } else {
        $local | ConvertTo-Json -Depth 3 | Set-Content -Path "$stage\config\appsettings.Local.json" -Encoding utf8
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts\installer') | Out-Null
    & $iscc "/DAppVersion=$Version" (Join-Path $root 'installer\MosquitoCapture.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
    $out = Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter "MosquitoCapture-Setup-$Version.exe" | Select-Object -First 1
    Write-Output ("installer: {0} ({1:N1} MB)" -f $out.FullName, ($out.Length / 1MB))
    Get-FileHash $out.FullName -Algorithm SHA256 | ForEach-Object { Write-Output ("sha256: " + $_.Hash.ToLower()) }
}
finally { Pop-Location }
