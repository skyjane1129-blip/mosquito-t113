$ErrorActionPreference = 'Stop'
$clientRoot = Split-Path -Parent $PSScriptRoot
$clientDotnet = Join-Path $clientRoot '.tools\dotnet'
if (-not (Test-Path (Join-Path $clientDotnet 'dotnet.exe'))) {
    throw 'Project SDK is missing. Run: powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Setup'
}
$env:DOTNET_ROOT = $clientDotnet
$env:DOTNET_ROOT_X64 = $clientDotnet
if (($env:PATH -split ';') -notcontains $clientDotnet) {
    $env:PATH = "$clientDotnet;$env:PATH"
}
$env:DOTNET_CLI_HOME = Join-Path $clientRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $clientRoot '.tools\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
