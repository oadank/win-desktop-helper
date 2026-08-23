; Win Desktop Helper — Inno Setup 安装脚本
; 编译: "C:\Users\oadan\AppData\Local\Programs\Inno Setup 6\ISCC.exe" setup.iss
[Setup]
AppId={{FE6F68E9-0CEB-450B-B438-49BFDF5FFB15}
AppName=Win Desktop Helper
AppVersion=3.0.0
AppPublisher=oadank
AppPublisherURL=https://github.com/oadank/win-desktop-helper
DefaultDirName={localappdata}\Programs\win-desktop-helper
DefaultGroupName=Win Desktop Helper
UninstallDisplayIcon={app}\shot-service.exe
Compression=lzma2
SolidCompression=yes
OutputDir=release
OutputBaseFilename=win-desktop-helper-setup
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ShowLanguageDialog=no
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "创建桌面图标"; GroupDescription: "附加任务:"
Name: "autostart"; Description: "开机自动启动（托盘常驻 + 崩溃自愈）"; GroupDescription: "附加任务:"

[Files]
Source: "shot-service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "shot-watcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "mcp-bridge.js"; DestDir: "{app}"; Flags: ignoreversion
Source: "SKILL.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "shot-service"; ValueData: """{app}\shot-service.exe"""; Flags: uninsdeletevalue; Tasks: autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "shot-watch"; ValueData: """{app}\shot-watcher.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Icons]
Name: "{autoprograms}\Win Desktop Helper"; Filename: "{app}\shot-service.exe"
Name: "{autodesktop}\Win Desktop Helper"; Filename: "{app}\shot-service.exe"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/c schtasks /create /tn dsh-shot-helper /tr ""{app}\shot-service.exe"" /sc once /st 00:00 /it /ru {username} /f"; Flags: runhidden; StatusMsg: "创建计划任务(手动拉起入口)..."
Filename: "{app}\shot-service.exe"; Description: "立即启动 Win Desktop Helper"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c schtasks /delete /tn dsh-shot-helper /f"; Flags: runhidden