param(
    [ValidateSet('Setup', 'Check', 'Build', 'Test', 'UiTest', 'Run', 'Publish')]
    [string]$Action = 'Check'
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if ($Action -eq 'Setup') {
        $sdkVersion = (Get-Content -Raw global.json | ConvertFrom-Json).sdk.version
        $installer = Join-Path $PSScriptRoot '.tools\dotnet-install.ps1'
        $sdkDirectory = Join-Path $PSScriptRoot '.tools\dotnet'
        if (-not (Test-Path (Join-Path $sdkDirectory "sdk\$sdkVersion"))) {
            New-Item -ItemType Directory -Force -Path (Split-Path $installer) | Out-Null
            & curl.exe --fail --location --retry 2 https://dot.net/v1/dotnet-install.ps1 --output $installer
            if ($LASTEXITCODE -ne 0) { throw 'Failed to download the Microsoft SDK installer.' }
            $signature = Get-AuthenticodeSignature $installer
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
                throw 'The SDK installer does not have a valid Microsoft signature.'
            }
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Version $sdkVersion -Architecture x64 -InstallDir $sdkDirectory -NoPath
            if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed.' }
        }
    }

    . (Join-Path $PSScriptRoot 'scripts\Enter-Dev.ps1')
    switch ($Action) {
        'Setup' {
            & dotnet restore Mosquito.Client.sln
            if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
        }
        'Check' {
            & dotnet --info
            if ($LASTEXITCODE -ne 0) { throw 'SDK check failed.' }
            $settings = Get-Content -Raw src\Mosquito.Client\appsettings.json | ConvertFrom-Json
            if (-not (Test-Path -LiteralPath $settings.AdbPath)) { throw "ADB not found: $($settings.AdbPath)" }
            & $settings.AdbPath version
            if ($LASTEXITCODE -ne 0) { throw 'ADB check failed.' }
            Write-Host "API configured: $($settings.ApiBaseUrl) (connectivity is checked separately)"
        }
        'Build' { & dotnet build Mosquito.Client.sln -c Release }
        'Test' { & dotnet run --project tests\Mosquito.Client.CoreTests -c Release }
        'UiTest' { & dotnet run --project tests\Mosquito.Client.UiTests -c Release -- artifacts\ui-check }
        'Run' { & dotnet run --project src\Mosquito.Client\Mosquito.Client.csproj -c Release }
        'Publish' {
            & dotnet publish src\Mosquito.Client\Mosquito.Client.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "$Action failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
