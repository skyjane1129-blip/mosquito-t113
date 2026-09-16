# Stops the demo API process started by Start-DemoServer.ps1. frpc is left running unless -IncludeFrpc is given.
param([switch]$IncludeFrpc)
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -like '*Mosquito.Cloud.Api.dll*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Output "stopped API PID $($_.ProcessId)" }
if ($IncludeFrpc) {
    Get-Process frpc -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force; Write-Output "stopped frpc PID $($_.Id)" }
}
