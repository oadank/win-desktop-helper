; Win Desktop Helper — Inno Setup 安装脚本
; 编译: "C:\Users\oadan\AppData\Local\Programs\Inno Setup 6\ISCC.exe" setup.iss
[Setup]
AppId={{FE6F68E9-0CEB-450B-B438-49BFDF5FFB15}
AppName=Win Desktop Helper
AppVersion=0.0.24
AppPublisher=oadank
AppPublisherURL=https://github.com/oadank/win-desktop-helper
DefaultDirName={localappdata}\Programs\win-desktop-helper
DefaultGroupName=Win Desktop Helper
UninstallDisplayIcon={app}\icon.ico
Compression=lzma2
SolidCompression=yes
OutputDir=release
OutputBaseFilename=win-desktop-helper-setup-0.0.24
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
; onlyifdoesntexist = 升级不覆盖已存在的配置
; uninsneveruninstall = 卸载时保留配置 (2026-09-12 修: 原缺此标志, 卸载重装=配置归零)
Source: "shot-service.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "shot-service"; ValueData: """{app}\shot-service.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Icons]
Name: "{autoprograms}\Win Desktop Helper"; Filename: "{app}\shot-service.exe"; IconFilename: "{app}\icon.ico"; IconIndex: 0
Name: "{autodesktop}\Win Desktop Helper"; Filename: "{app}\shot-service.exe"; IconFilename: "{app}\icon.ico"; IconIndex: 0; Tasks: desktopicon

[Run]
; 计划任务改走 Pascal Exec(Unicode CreateProcess)，不经 cmd —— 中文用户名/路径经 OEM 码页会变乱码
Filename: "{app}\shot-service.exe"; Description: "立即启动 Win Desktop Helper"; Flags: nowait runhidden

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /IM shot-service.exe /F"; Flags: runhidden; StatusMsg: "停止服务进程..."

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

// 计划任务: 安装器不再创建任何任务。
// 自启由两条机制负责, 都不需要安装器介入:
//   ① [Registry] 段的 HKCU\...\Run\shot-service
//   ② 程序自注册的 WinDesktopHelper (源码 TaskCreate: /sc ONLOGON /rl HIGHEST)
// 历史: 安装器曾创建 dsh-shot-helper —— 与 ② 职责完全重复, 且旧参数
//   (/sc once /st 00:00) 是一次性且时刻已过的触发器 ⇒ 永不启动。2026-09-18 起
//   本机任务已删除, 安装器也不再创建。

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // 保留回调(预留). 这里过去调 CreateHelperTask(), 已移除.
end;