; Win Desktop Helper — Inno Setup 安装脚本
; 编译: "C:\Users\oadan\AppData\Local\Programs\Inno Setup 6\ISCC.exe" setup.iss
[Setup]
AppId={{FE6F68E9-0CEB-450B-B438-49BFDF5FFB15}
AppName=Win Desktop Helper
AppVersion=0.0.18
AppPublisher=oadank
AppPublisherURL=https://github.com/oadank/win-desktop-helper
DefaultDirName={localappdata}\Programs\win-desktop-helper
DefaultGroupName=Win Desktop Helper
UninstallDisplayIcon={app}\icon.ico
Compression=lzma2
SolidCompression=yes
OutputDir=release
OutputBaseFilename=win-desktop-helper-setup-0.0.18
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ShowLanguageDialog=no
WizardStyle=modern
CloseApplications=yes
SetupIconFile=icon.ico

[Tasks]
Name: "desktopicon"; Description: "创建桌面图标"; GroupDescription: "附加任务:"
Name: "autostart"; Description: "开机自动启动（托盘常驻）"; GroupDescription: "附加任务:"

[Files]
Source: "shot-service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "mcp-bridge.js"; DestDir: "{app}"; Flags: ignoreversion
Source: "SKILL.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "shot-service.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "shot-service"; ValueData: """{app}\shot-service.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Icons]
Name: "{autoprograms}\Win Desktop Helper"; Filename: "{app}\shot-service.exe"; IconFilename: "{app}\icon.ico"; IconIndex: 0
Name: "{autodesktop}\Win Desktop Helper"; Filename: "{app}\shot-service.exe"; IconFilename: "{app}\icon.ico"; IconIndex: 0; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/c schtasks /create /tn dsh-shot-helper /tr ""{app}\shot-service.exe"" /sc once /st 00:00 /it /ru {username} /f"; Flags: runhidden; StatusMsg: "创建计划任务(手动拉起入口)..."
Filename: "{app}\shot-service.exe"; Description: "立即启动 Win Desktop Helper"; Flags: nowait runhidden

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /IM shot-service.exe /F"; Flags: runhidden; StatusMsg: "停止服务进程..."
Filename: "{cmd}"; Parameters: "/c schtasks /delete /tn dsh-shot-helper /f"; Flags: runhidden

[Code]
// 安装前强制结束运行中的进程，避免覆盖 exe 时 DeleteFile code 5 (CloseApplications 对无窗口进程不可靠)
// 关键修复: 早期用 /F /T 会把整进程树杀掉, 而安装器本身是 shot-service 的子进程 -> 安装器被一起杀 -> 替换永远完不成 -> 更新死循环。
// 现改为只杀 shot-service.exe 本体(/T 去掉), 安装器(独立进程名)不受影响, 才能正常替换并拉起新版。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM shot-service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM shot-watcher.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;