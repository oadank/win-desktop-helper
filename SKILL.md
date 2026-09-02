# Win Desktop Helper — SKILL（Agent 操作手册）

> ⚠️ **本文件是本服务的 SKILL（skill）**：任何获得本能力的 AI 都必须——
> ① 首次操作前先通过 `get_skill` 读取本文件（服务端强制，未读会拒绝工具）；
> ② **每次踩坑后必须用 `update_skill` 把经验写回本文件**（全体 agent 共享、立即生效），**禁止只记在自己记忆里**；
> ③ 不要依赖路径：文件位置通过工具读取，安装目录可随部署变化。
> HTTP 接入者：`GET /guide` 等价读取。

## 能力边界

- **看**：截图（全屏/显示器/区域/窗口）、活动窗口、按标题查窗口、显示器元数据
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
| 桌面/开始菜单图标变空白（更新后） | 旧 exe 未内嵌图标，快捷方式也没显式 `IconFilename`，更新替换 exe 后 .lnk 缓存图标解析失效 | 编译时 `/win32icon:icon.ico` 把图标焊进 exe；`setup.iss` 的 `[Icons]` 显式写 `IconFilename: "{app}\icon.ico"; IconIndex: 0`，且 `icon.ico` 必须进 `[Files]`（否则装完磁盘上根本没有图标源） |
| 分辨率/缩放一变，任务栏滚轮音量失效（改回 2560×1440 才恢复） | .NET winexe 默认 DPI 不感知：`GetWindowRect(Shell_TrayWnd)` 返回逻辑像素，而鼠标钩子 `pt` 是物理像素；非 100% 缩放下两边坐标空间不一致 → `IsPointOnTaskbar` 恒 false → 滚轮静默失效（2560×1440/100% 时两边恰好相等所以正常） | 给 exe 嵌 `PerMonitorV2` DPI manifest（`/win32manifest:app.manifest`，声明 `dpiAwareness=PerMonitorV2`），让 `GetWindowRect` 与钩子 `pt` 都用物理像素；`shot-service.cs:1122` 的 `SetProcessDPIAware()` 保留作兜底（manifest 生效时它会被忽略） |

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