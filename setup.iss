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
CloseApplications=force
RestartApplications=no
SetupIconFile=icon.ico

[Tasks]
Name: "desktopicon"; Description: "创建桌面图标"; GroupDescription: "附加任务:"
Name: "autostart"; Description: "开机自动启动（托盘常驻）"; GroupDescription: "附加任务:"

[Files]
Source: "SKILL.md"; DestDir: "{app}\skill"
Source: "patterns\*"; DestDir: "{app}\skill\patterns"
Source: "shot-service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "mcp-bridge.js"; DestDir: "{app}"; Flags: ignoreversion
Source: "SKILL.md"; DestDir: "{app}"; Flags: ignoreversion
; [2026-09-22] 随包带"工具合并对照册"
Source: "patterns\tool-merge-20260922.md"; DestDir: "{app}\patterns"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion
; onlyifdoesntexist = 升级不覆盖已存在的配置
; uninsneveruninstall = 卸载时保留配置
Source: "shot-service.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "shot-service"; ValueData: """{app}\shot-service.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Icons]
Name: "{autoprograms}\Win Desktop Helper"; Filename: "{app}\shot-service.exe"; IconFilename: "{app}\icon.ico"; IconIndex: 0
Name: "{autodesktop}\Win Desktop Helper"; Filename: "{app}\shot-service.exe"; IconFilename: "{app}\icon.ico"; IconIndex: 0; Tasks: desktopicon

[Run]
; 双保险: ① 计划任务(有提权) ② 直接拉 exe(首次安装/任务不存在时兜底)
; 单实例互斥会保证只跑一个, 不怕起两次
Filename: "{cmd}"; Parameters: "/c schtasks /Run /TN WinDesktopHelper >nul 2>&1"; Flags: nowait runhidden; StatusMsg: "正在启动 Win Desktop Helper..."
Filename: "{app}\shot-service.exe"; Flags: nowait runhidden shellexec; Description: "启动 Win Desktop Helper"

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c schtasks /End /TN WinDesktopHelper >nul 2>&1 & taskkill /F /IM shot-watcher.exe >nul 2>&1 & taskkill /F /IM shot-watcher.exe /F >nul 2>&1 & taskkill /F /IM shot-service.exe >nul 2>&1"; Flags: runhidden; StatusMsg: "停止服务进程..."

[Code]
// 升级前必须把进程杀干净，否则 exe 被锁、覆盖失败（gs 实测）。
// 三个坑:
//  ① 只杀 shot-service → shot-watcher / -watch 30s 内拉回，文件锁不放
//  ② 非提升安装器 taskkill 不掉管理员权限的 shot-service
//  ③ 直接拉 exe 启动 744 要提升 → [Run] 用 schtasks
// 对策: HTTP 让助手自退(提权进程可自杀) → 停任务 → 杀 watcher/-watch → taskkill → 轮询死透

function ProcsAlive(): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if Exec('cmd.exe', '/c tasklist /FI "IMAGENAME eq shot-service.exe" /NH | findstr /I "shot-service" >nul',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    if ResultCode = 0 then Result := True;
  if Exec('cmd.exe', '/c tasklist /FI "IMAGENAME eq shot-watcher.exe" /NH | findstr /I "shot-watcher" >nul',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    if ResultCode = 0 then Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode, i: Integer;
begin
  // 1) 助手自退 (HTTP /app/exit; 提权进程也能自己关)
  Exec('powershell.exe',
    '-NoProfile -Command "try{(New-Object Net.WebClient).DownloadString(''http://127.0.0.1:18800/app/exit?confirm=1'')}catch{}"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
  // 2) 停计划任务 + 先杀守护(它会拉活服务)
  Exec('schtasks.exe', '/End /TN WinDesktopHelper', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('cmd.exe', '/c taskkill /F /IM shot-watcher.exe >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // 3) 杀 shot-service / shot-watcher / -watch 副本
  Exec('powershell.exe',
    '-NoProfile -Command "Get-CimInstance Win32_Process | ? { $_.Name -match ''shot-service|shot-watcher'' } | %% { try { Stop-Process -Id $_.ProcessId -Force } catch {} }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
  Exec('cmd.exe', '/c taskkill /F /IM shot-service.exe >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // 4) 轮询最多 ~6s 等死透
  for i := 1 to 12 do
  begin
    if not ProcsAlive() then break;
    Sleep(500);
    Exec('cmd.exe', '/c taskkill /F /IM shot-service.exe >nul 2>&1 & taskkill /F /IM shot-watcher.exe >nul 2>&1',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // 清残留守护
    Exec('cmd.exe', '/c taskkill /F /IM shot-watcher.exe >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // 兜底再拉一次: 卸载重装后计划任务可能不存在, schtasks /Run 会失败
    Exec('schtasks.exe', '/Run /TN WinDesktopHelper', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(800);
    ShellExec('open', ExpandConstant('{app}\shot-service.exe'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;
