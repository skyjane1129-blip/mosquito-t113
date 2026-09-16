' Launches a PowerShell script with no console window at all (scheduled-task helper).
' Usage: wscript.exe Run-Hidden.vbs "C:\path\script.ps1"
' powershell.exe -WindowStyle Hidden still flashes a console for a moment; WScript.Shell.Run with
' window style 0 does not, so the 5-minute watchdog stays invisible to the operator.
If WScript.Arguments.Count < 1 Then WScript.Quit 1
Dim shell, script
script = WScript.Arguments(0)
Set shell = CreateObject("WScript.Shell")
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -NonInteractive -File """ & script & """", 0, False
