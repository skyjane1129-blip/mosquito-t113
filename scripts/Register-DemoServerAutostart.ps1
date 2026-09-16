# Registers the Scheduled Tasks that keep the demo server alive without anyone touching this PC:
#   1. 'Mosquito Demo Server'   – runs Start-DemoServer.ps1 30 s after logon AND every 5 minutes as a
#                                 watchdog (the start script is idempotent: running processes are left alone).
#   2. 'Mosquito Demo Backup'   – runs Backup-DemoData.ps1 daily at 03:00 (SQLite DB + config snapshot).
# Re-running replaces both tasks. Remove with:
#   Unregister-ScheduledTask -TaskName 'Mosquito Demo Server' -Confirm:$false
#   Unregister-ScheduledTask -TaskName 'Mosquito Demo Backup' -Confirm:$false
$ErrorActionPreference = 'Stop'
$start = Join-Path $PSScriptRoot 'Start-DemoServer.ps1'
$backup = Join-Path $PSScriptRoot 'Backup-DemoData.ps1'
$hidden = Join-Path $PSScriptRoot 'Run-Hidden.vbs'
foreach ($f in $start, $backup, $hidden) { if (-not (Test-Path $f)) { throw "missing $f" } }

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew

# wscript + Run-Hidden.vbs: no console window flashes every 5 minutes (powershell -WindowStyle Hidden still does).
$serverAction = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "`"$hidden`" `"$start`""
$logon = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$logon.Delay = 'PT30S'
$watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName 'Mosquito Demo Server' -Action $serverAction -Trigger @($logon, $watchdog) -Settings $settings `
    -Description 'Starts (and every 5 min re-checks) the Mosquito demo cloud API on 127.0.0.1:5080 and the frp client.' -Force | Out-Null
Write-Output "registered 'Mosquito Demo Server': 30 s after logon of $env:USERNAME + every 5 minutes"

$backupAction = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "`"$hidden`" `"$backup`""
$daily = New-ScheduledTaskTrigger -Daily -At 03:00
Register-ScheduledTask -TaskName 'Mosquito Demo Backup' -Action $backupAction -Trigger $daily -Settings $settings `
    -Description 'Daily snapshot of the Mosquito demo SQLite database and configuration.' -Force | Out-Null
Write-Output "registered 'Mosquito Demo Backup': daily 03:00"

Write-Output "Sleep policy is not changed by this script. Keep the PC awake for the demo:"
Write-Output "  powercfg /change standby-timeout-ac 0 ; powercfg /change hibernate-timeout-ac 0"
