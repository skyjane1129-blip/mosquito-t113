; Inno Setup script for the Mosquito capture client (Windows 10/11 x64).
; Built by scripts\Build-Installer.ps1, which stages:
;   installer\stage\app\            self-contained publish output (no .NET install needed)
;   installer\stage\platform-tools\ verified ADB host components (adb.exe + DLLs + NOTICE.txt)
;   installer\stage\config\         appsettings.json with an app-relative ADB path, appsettings.Local.json
; The installer never touches PATH or any ADB already present on the machine; the client only
; launches its own platform-tools\adb.exe, so an existing developer ADB is left alone.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "智能诱蚊诱卵器监测客户端"
#define AppExe "MosquitoCapture.exe"
#define AppPublisher "上海市疾病预防控制中心 智能诱蚊诱卵器监测平台"

[Setup]
AppId={{7C2C0F52-2C6D-4B0B-9C3E-5A1E1B0C4D01}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\MosquitoCapture
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=MosquitoCapture-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=yes
RestartApplications=no

[Languages]
; Simplified Chinese messages ship next to this script (from the official Inno Setup repository,
; Files\Languages\ChineseSimplified.isl) because the installed compiler does not bundle them.
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "stage\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\platform-tools\*"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
; Deployment configuration is written last so it overrides the copies inside the publish output.
Source: "stage\config\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "stage\config\appsettings.Local.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "安装完成后启动客户端"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the bundled ADB server so the uninstaller can remove adb.exe.
Filename: "{app}\platform-tools\adb.exe"; Parameters: "kill-server"; Flags: runhidden; RunOnceId: "AdbKillServer"

[Code]
// The board enumerates as an ADB interface (VID 18D1 PID D002) that Windows 10/11 binds to the
// in-box WinUSB driver automatically, so no driver package is installed here. A short note is
// shown on the finish page so the operator knows what to expect the first time the board is plugged in.
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      '首次用 Type-C 线连接板子时，Windows 会自动为其安装内置 WinUSB 驱动，等待“设备已就绪”提示后再在客户端点击检测。' + #13#10 +
      '本程序自带 ADB 组件，无需另行安装。';
end;
