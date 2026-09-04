# Win Desktop Helper — SKILL（Agent 操作手册）

> ⚠️ **本文件是本服务的 SKILL（skill）**：任何获得本能力的 AI 都必须——
> ① 首次操作前先通过 `get_skill` 读取本文件（服务端强制，未读会拒绝工具）；
> ② **每次踩坑后必须用 `update_skill` 把经验写回本文件**（全体 agent 共享、立即生效），**禁止只记在自己记忆里**；
> ③ 不要依赖路径：文件位置通过工具读取，安装目录可随部署变化。
> HTTP 接入者：`GET /guide` 等价读取。

## 能力边界

- **看**：截图（全屏/显示器/区域/窗口）、活动窗口、按标题查窗口、显示器元数据
- **区域截图（托盘/热键 Ctrl+Shift+S，PixPin 交互）**：冻结屏遮罩框选 → 图标工具条（矩形/椭圆/箭头/画笔/文字/序号标注 + 撤销 + OCR 识别 + 翻译 + 保存/复制/另存）→ 松手选区保持、动作完成才关遮罩
- **动**：鼠标（移动/点击/滚轮）、键盘（中文输入/组合键）、运行程序
- **音量（常驻）**：任务栏滚轮=调音量、任务栏中键=静音开关（HTTP `/taskbar-volume` 查状态/开关/步进/反向，MCP 工具 `taskbar_volume`）
- **剪贴板（常驻）**：自动记录文本历史（最多50条），热键 Ctrl+Alt+V 弹 UI（单击复制/双击粘贴/右键删除），HTTP `/clipboard/history`、MCP 工具 `clipboard_history`
- **不做**：文件删除/消息发送默认由 agent 自己的工具完成（敏感操作需在对话里向用户确认后方可执行）

## 入口与健康检查

- HTTP: `127.0.0.1:18800`，先 `GET /health` 确认 `session:1` 且存活
- MCP 工具：screen_capture / window_info / active_window / monitors / mouse_move / mouse_click / mouse_scroll / keyboard_type / keyboard_press / app_run / get_skill / update_skill / taskbar_volume / clipboard_history

## 铁律（违反必踩坑）

1. **点任何东西之前，先定位**：点窗口用 `window_info(标题)` 拿 rect 再点区域中心；点之前 `active_window` 确认它在前台（后台/被遮挡窗口点了等于点在遮蔽层上）
2. **输入之前，先确认目标**：`keyboard_type` 发给**当前前台窗口**。输入前必须 `active_window` 确认前台是目标（历史教训：误发 34 字符到浏览器）
3. **操作后立即验证**：保存文件 → 用文件系统检查（`Test-Path`/`Get-Content`），不要只信 UI；UI 会骗人（保存对话框的默认位置常常不是你以为的地方）
4. **等待 + 截图确认**：启动程序/弹窗后 sleep 1-3s 再继续；关键节点截图看效果再走下一步
5. **敏感操作先问**：删除文件、发送消息等在对话里向用户确认；付款不做；改系统设置/运行程序的敏感场景同样先确认
6. **遇到"点不动/找不到"**：先 `active_window` + 全屏截图看真实状态，别盲试

## 常见坑速查

| 现象 | 原因 | 解法 |
|---|---|---|
| 截图全黑 / 只有十几 KB | 在 Session 0 直接截图 | 确认服务 `session:1`；任何截图操作都经本服务 |
| `window_info` 找不到 | 目标窗口在别的桌面/被遮挡/Win11 商店应用（如新记事本标题是英文 "Notepad"） | 用 `active_window` 兜底；或先 `app_run`/点击把它带起来 |
| 程序开了但没在前台 | 前台锁：后台启动的窗口不自动置前 | `alt+tab` 切到最近窗口，或点击它可见区域 |
| 点窗口没反应 | 点到了遮蔽它的其他窗口 | 先 `alt+tab` 置前再点；用窗口 rect 中心附近点击 |
| 点击/输入打到别处 | 光标下/前台不是目标 | 立即 `active_window` 检查，停止继续操作并汇报 |
| 保存文件找不到 | 应用默认位置不是预期目录 | 保存后文件系统验证；用 `app_run` 打开目标目录再操作 |
| 区域截图"卡死"：遮罩看不见/关不掉、托盘全点不了 | **WinForms `TransparencyKey=BackColor` 做透明遮罩** → color-key 把整窗抠成完全不可见+鼠标穿透+抢不到焦点，`OnMouseUp` 永远收不到，`ShowDialog` 永不返回；托盘线程若 `Invoke` 同步等待则整个托盘假死（2026-09-05 实测复现） | 遮罩窗**绝不用 TransparencyKey**；"半透明"用「冻结全屏图+暗层一次性预合成为 BackgroundImage」实现；托盘入口一律 `BeginInvoke` 不许同步等遮罩 |
| 拖框巨卡 | OnPaint 逐帧 `DrawImage` 全屏 + alpha `FillRectangle` 全屏（GDI 慢） | 背景（原图+暗层）预合成一次，OnPaint 只画框线/亮块/文字，且只 Invalidate 新旧框脏区 |
| 工具条图标只剩 3 个/跑屏幕顶 | ToolStrip 默认 `Dock=Top` 会吸顶；`AutoSize=false` 不设宽度会把图标挤进溢出区 | `bar.Dock=None; bar.AutoSize=true; bar.CanOverflow=false` |
| 跑的还是旧代码，"改了没变化" | 单实例互斥：不先 Stop-Process 就 Start-Process，新进程静默退出 | 已堵死：v0.0.17+ 新实例自动顶替旧实例（日志 `take-over`）；启动日志/托盘菜单/启动气泡/`GET /health` 都带 `build=MM-dd HH:mm 大小KB`（=exe 编译时刻），肉眼比对即验证 |
| 桌面/开始菜单图标变空白（更新后） | 旧 exe 未内嵌图标，快捷方式也没显式 `IconFilename`，更新替换 exe 后 .lnk 缓存图标解析失效 | 编译时 `/win32icon:icon.ico` 把图标焊进 exe；`setup.iss` 的 `[Icons]` 显式写 `IconFilename: "{app}\icon.ico"; IconIndex: 0`，且 `icon.ico` 必须进 `[Files]`（否则装完磁盘上根本没有图标源） |
| 分辨率/缩放一变，任务栏滚轮音量失效（改回 2560×1440 才恢复） | .NET winexe 默认 DPI 不感知：`GetWindowRect(Shell_TrayWnd)` 返回逻辑像素，而鼠标钩子 `pt` 是物理像素；非 100% 缩放下两边坐标空间不一致 → `IsPointOnTaskbar` 恒 false → 滚轮静默失效（2560×1440/100% 时两边恰好相等所以正常） | 给 exe 嵌 `PerMonitorV2` DPI manifest（`/win32manifest:app.manifest`，声明 `dpiAwareness=PerMonitorV2`），让 `GetWindowRect` 与钩子 `pt` 都用物理像素；`shot-service.cs:1122` 的 `SetProcessDPIAware()` 保留作兜底（manifest 生效时它会被忽略） |
| 更新抽风：自动/手动检查都提示有新版、安装器反复 launch 版本却不变，最终进程丢失、下次启动又重试死循环 | **根因两层**：①`setup.iss` 的 `PrepareToInstall` 早期用 `taskkill /IM shot-service.exe /F /T`，`/T` 把整进程树杀掉，而安装器(`wdh-update-setup.exe`)本身就是 `shot-service.exe` 的子进程 → 安装器被一起杀 → exe 永远替换不完；②旧 `DoUpdateSilent` 自己从不退出，文件锁一直握在手里，全靠安装器来 kill 自己 | ①`setup.iss` 去掉 `/T`，只 `taskkill /F /IM shot-service.exe`（安装器独立进程名，不受影响）；②`DoUpdateSilent` 改为：下载后 `Environment.Exit(0)` 释放 exe 锁 → 经 `cmd start` 拉起 **detached** 安装器（不属于本进程树，绝不会被误杀）→ 安装器替换 exe 后由 iss `[Run]` 拉起新版；并加 **30 分钟失败冷却**（`wdh-update-guard.txt`），手动托盘菜单/`/update` 绕过冷却。**铁律：绝不要再给 PrepareToInstall 加回 `/T`，也不要在 DoUpdateSilent 里赖着不退出的旧写法** |

## 标准流程模板

```
打开应用: app_run(path) → sleep 2-4s → active_window 确认 → 截图确认界面
点击:     window_info(标题) → 计算中心 → mouse_click(x,y) → 截图确认效果
输入:     active_window 确认目标 → mouse_click 聚焦 → keyboard_type → 截图/验证
滚动:     mouse_move 到滚动区 → mouse_scroll(delta)（正值向上、负值向下）→ 截图确认
保存:     完成操作 → 文件系统验证文件存在且内容正确 → 汇报路径
```

## 运行程序提示

- `app_run` 用 ShellExecute：可传 exe 路径、快捷方式、URL（会打开默认浏览器）
- 需要管理员权限的程序会弹 UAC（secure desktop）——需要用户手动点"是"，无法自动确认

## 发布流程（Agent 视角）

- **源码入库、安装包走 GitHub release**：`.gitignore` 忽略 `*.exe`，所以 `shot-service.exe` 与 `release/*.exe` 都不进 git。发版只提交源码（`shot-service.cs`/`setup.iss`/`SKILL.md`/`app.manifest` 等）+ 打 tag + push；安装包通过 GitHub release 分发。
- **编译 C# 的平台差异（重要）**：WorkBuddy 的 Bash/PowerShell 调 `csc.exe` 会被平台安全策略**关键词级拒绝**（平台层策略，开权限也放不开）。**WorkBuddy 环境必须用户手动编译**。但 **ZCode 环境实测不拦 csc**，agent 可直接编译自测（2026-09-05 实测）。Git Bash 下 `/参数` 会被转成路径，csc 参数一律用 `-` 风格：
  ```
  cd C:\D\opt\win-desktop-helper
  C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:winexe -optimize+ -win32icon:icon.ico -win32manifest:app.manifest -out:shot-service.exe -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll shot-service.cs shot-capture.cs shot-ocr.cs shot-translate.cs shot-config.cs
  ```
  （编译前 `Stop-Process -Name shot-service -Force` 释放 exe 锁，否则 CS0016；编完 `Start-Process` 拉起——忘杀旧进程也没事，v0.0.17+ 新实例会自动顶替旧实例并在日志记 `take-over`）
  （`shot-service.cs` 与其余 `shot-*.cs` 是同一 `ShotService` 类的 partial 拆分，多文件一起传入 `csc` 编译成单个 `shot-service.exe`；后续新增 `shot-scroll/annotate/...` 照此追加到命令末尾）
- **自验证（改动后必做，别让用户猜）**：启动后日志首行、托盘菜单第一行、启动气泡、`GET /health` 的 `build` 字段，四处的值都来自 exe 文件的 mtime+大小——与刚才编译完成时间一致 = 跑的是新代码。
- **打包**：`ISCC.exe setup.iss` 不被拦，Agent 可在沙箱外直接跑，产物 `release/win-desktop-helper-setup-<ver>.exe`。
- **上传 GitHub release（Agent 可自助，需沙箱外网络）**：
  ```
  gh release create v<ver> --repo oadank/win-desktop-helper --title "Win Desktop Helper v<ver>" --notes "<更新说明>" "release/win-desktop-helper-setup-<ver>.exe"
  ```
  ⚠️ 文件作为**位置参数**传（gh 无 `--attach` flag）；新 release 自动成为 Latest，已装用户自动更新即可拉到。
- **完整发版顺序**：改源码 → commit 源码 + 打 tag → push(main+tag) → 用户手动 csc 编译 → Agent 跑 ISCC 打包 → 静默安装验证(`/health` 看版本) → Agent 建 gh release 上传 exe。