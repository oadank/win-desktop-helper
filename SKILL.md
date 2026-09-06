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
  C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:winexe -optimize+ -win32icon:icon.ico -win32manifest:app.manifest -out:shot-service.exe -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll -r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll -r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll shot-service.cs shot-capture.cs shot-ocr.cs shot-translate.cs shot-config.cs shot-automation.cs
  ```
  （⚠️ 后 3 个 `-r:` 是 UIA 语义操作的 GAC 程序集，漏掉会 CS0246；`shot-automation.cs` 漏传则全部 /win /ui /drag 端点缺失。编译前 `Stop-Process -Name shot-service -Force` 释放 exe 锁）
- **自验证（改动后必做，别让用户猜）**：启动后日志首行、托盘菜单第一行、启动气泡、`GET /health` 的 `build` 字段，四处的值都来自 exe 文件的 mtime+大小——与刚才编译完成时间一致 = 跑的是新代码。
- **打包**：`ISCC.exe setup.iss` 不被拦，Agent 可在沙箱外直接跑，产物 `release/win-desktop-helper-setup-<ver>.exe`。
- **上传 GitHub release（Agent 可自助，需沙箱外网络）**：
  ```
  gh release create v<ver> --repo oadank/win-desktop-helper --title "Win Desktop Helper v<ver>" --notes "<更新说明>" "release/win-desktop-helper-setup-<ver>.exe"
  ```
  ⚠️ 文件作为**位置参数**传（gh 无 `--attach` flag）；新 release 自动成为 Latest，已装用户自动更新即可拉到。
- **完整发版顺序**：改源码 → commit 源码 + 打 tag → push(main+tag) → 用户手动 csc 编译 → Agent 跑 ISCC 打包 → 静默安装验证(`/health` 看版本) → Agent 建 gh release 上传 exe。
## 2026-09-05 MCP 实测三坑

1. **窗口截图拍不到 DirectWrite 内容**：Win11 记事本正文区（RichEditD2DPT 硬件层）在 /shot 截图里是黑的，只有状态栏等 GDI 部分可见。验证写入是否成功改用 **ui_read 读 value** 或看状态栏字符计数，别靠截图。修复方向：PrintWindow+PW_RENDERFULLCONTENT 或 DXGI 桌面复制。
2. **app_run 返回的 pid 不是窗口进程**：Store 应用（记事本/计算器）启动器进程即刻退出，真实 UI 是 spawn 出的新进程（pid 不同、标题可能英文如 "无标题 - Notepad"）。定位一律用 window_info/active 看真实标题，别信 app_run 的 pid。
3. **keyboard_type 盲打危险**：activate 失败后打字会打进用户正在用的前台窗口（实测打进了用户的终端）。铁律：active_window 确认前台 == 目标窗口后才许 type/press。

## 2026-09-06 交叉测试（workbuddy 攻语义组+剪贴板多媒体）实测结论

### 好消息（都可用，姿势照抄）
- **图片通道端到端可用**：`/clipboard/set` 前若剪贴板是图 → `/clipboard/get` 回 `type=image` 并把 PNG 落盘 `C:\Users\oadan\Pictures\Screenshots\clip_*.png`，同时给出 `url=http://127.0.0.1:18800/img/<名>`。
- **`/img/` URL 逐字节可靠**（801/801、2129/2129 字节精确，同文件重复 5 次全一致）——之前怀疑"URL 截断/0 字节"是**测试方的错**：把二进制按 UTF-8 解码统计、以及 curl 在 msys 下 `-o /c/...` 路径静默不落盘。**验字节数请用 python socket 收原始字节或 curl 用 Windows 路径**。
- **`type` 优先级正确**：图 > 文件列表 > 文本 > empty。`Clipboard.SetFileDropList` → 回 `type=files, count, files[]`；`Clipboard.Clear()` → `type=empty`（不报错）。
- **图片条目已持久化**：`clipboard-history.json` 里 `[图片] 路径` 条目正常落盘，实测 4 条全部存活无死链（50 条上限）。
- **三击真的生效**：`/mouse/click?triple=1` 记事本里 3/3 命中**整行**，中文行/数字行都正确。

### 待修的 4 个坑（按严重度）—— ✅ 2026-09-06 zcode 已全部修复 (build 09-06 09:4x)

1. **图片去重 3 点采样指纹** → 已改 **PNG 字节 MD5 作文件名**（`SaveClipboardImage`），同图精确去重、异图（哪怕只差 1 像素）必入库；顺带根治 D5（轮询 /clipboard/get 不再重复落盘，同内容复用既有文件）。
2. **三击左边缘=全选（RichEdit 边距行为）** → 无法在服务端修，已在 **bridge/内嵌 schema 描述**注明「坐标务必取行内 rect.x+20 以上、行垂直中线」。
3. **ui_select 假成功** → 已全参数校验：start/end 必须都给且非负（否则 ok:false），越界 clamp 返回 `clamped:true`，start>end 交换返回 `swapped:true`，相等返回 `collapsed:true`（光标定位语义），杜绝静默吞参数。
4. **ui_find 不支持 type-only + 索引 i 不稳定** → ui_find 已支持 name 和/或 type（至少一个），bridge 空串不再被吞（`!== undefined` 透传）；ui_find/ui_tree 描述已注明「i 仅本次响应内有效，跨调用重查」。
5. **图片历史条目不持久化（D4）** → 图片分支已补 `SaveClipHistory()` 即时落盘，重启/崩溃不再丢图片历史。


### 有效测试姿势（复用）
- 验证"选中/复制"类操作一律用**哨兵法**：`/clipboard/set?text=SENT-XX` → 操作 → `ctrl+c` → `/clipboard/get`，值没变即"未生效"，避免把上次残留选择误判成功（本人上一轮 C1f/C1g 就是这么误判成"复制了残留"，实为"未生效+假成功"）。
- 需要行几何时无 `/ui/lines` 端点：用**三击阶梯探针**（固定 x，y 从 `rect.y+12` 起每 8px 试一次，看复制内容何时换行）+ 二分定边界，实测量出行高≈27px。

## 2026-09-06 追加两项（同一轮，已实测定位）
5. **🟠 图片历史条目不持久化** —— ✅ 已修（图片分支已补 SaveClipHistory()，MD5 入库即时落盘）：`ClipWatcherLoop` 图片分支入库后**漏调 `SaveClipHistory()`**（全文件仅文本/删除/清空三处调用）。实测唯一尺寸 401×303 的图已进内存 history，但 `clipboard-history.json` 里没有，直到下一次**文本复制**才被顺带写盘 → 期间服务重启/更新则图片条目全丢。修一行即可。
6. **🟡 `/clipboard/get` 每次都新存一份 PNG** —— ✅ 已修（MD5 文件名天然去重，同图复用零新落盘；agent 侧纪律仍建议 history 探测）：同一张图连读 4 次 → 磁盘多出 4 个 `clip_*.png`（无内容去重）。**agent 侧纪律：不要用 `/clipboard/get` 轮询等用户复制**，会灌盘；要探测用 `clipboard_history`。

## 🟠 D6 /ui/tree|find 大DOM挂死+max 无效 —— ✅ 已修（2026-09-06 zcode 闭环, 三层防御）

1. **计数即停**：`root.FindAll(Descendants,TrueCondition)` 全物化 → **`WalkLimited` TreeWalker(ControlView) 迭代 DFS，凑够 max+1 即停**（max 真正限制遍历成本；tree/readall/element/find 共 5 处）。
2. **服务端精确过滤**：`ui_find`/`UiElement` 的 name（精确）/type 走 **PropertyCondition FindFirst/FindAll**（小窗口秒回，响应带 `match:"exact"`）。
3. **硬超时**：全部 14 个 ui 入口（HTTP 7 + MCP 7）包 `UiCall(8000ms)` —— 极端 Electron 树（WorkBuddy）服务端遍历也 >8s 时，**返回 ok:false + 降级指引**（改用 /shot 截图坐标操作），handler 线程不再永久阻塞（实测：tree max=30 从 25s 零字节 → **0.71s**；readall 50 → 2.1s；find WorkBuddy → 8.1s 干净超时）。
4. i 索引不稳定 = FindAll 平铺序的固有语义（DOM 一变就变），已在描述注明"i 仅本次响应有效，跨调用重查/按 name 定位"。

## 🔴 2026-09-06 重大自伤坑：给聊天类输入框 `keyboard/type` 打多行文本 = 自动连发多条

**实测**：向 ZCode 对话输入框打 1466 字（含 20 个 `\n`）的结论 → `{"ok":true,"chars":1472}` 返回"成功"，但**只有第一行成了消息被发出**（聊天框 Enter 即发送），后续段落全部丢失/散投，且对方的输入框被留下残留 `\n`。这是**污染用户对话**级别的事故。

**纪律（对所有聊天/搜索/命令框适用）**：
1. 多行文本**绝不**直接 `keyboard/type` —— 换行会被当回车。
2. 正确姿势：**剪贴板粘贴法**
   `clipboard_set?text=<整段含换行>` → 点输入框 → `/keyboard/press?keys=ctrl+v`（粘贴不触发发送，换行原样保留）→ 校验 → 最后只按**一次** `enter`。
3. 单行短消息才允许直接 `keyboard/type`，且打之前把文本里的 `\n`/`\r` 全换成 `；`。
4. **`/ui/read?i=` 的索引会随消息流实时漂移**（实测同一输入框在一分钟内 i=637→644→659→664）：验证送达一律用 **`/ui/read?title=X&name=<占位符文本>`**（按 name 定位），别缓存 i、更别因"读不到"就以为没发出去而重发。
5. 发送前必查**对方是否空闲**：ZCode 生成中时按钮是"停止生成"；此时发消息会进"排队"（占位符也从"提出后续修改要求"变成"继续输入以排队后续修改"），语义不同，先等它跑完。

## 🔴 2026-09-06 D6：`/ui/tree`、`/ui/find` 在大 DOM（Electron）窗口上无限期挂死，`max` 不生效

**实测**（v0.0.18 build 09-06 09:37，挂死期间服务全程存活）
```
/ui/tree?title=WorkBuddy&max=30    -> 25s 零字节超时 (http=000)
/ui/find?title=WorkBuddy&name=输入 -> 10s 零字节超时
/ui/tree?title=ZCode&max=150       -> 0.56s  ✅
/ui/tree?title=Microsoft(Edge)&max=60 -> 0.36s  ✅
/ui/tree?title=文件资源管理器&max=60  -> 0.04s  ✅
```
`/health`、`/active` 在挂死期间 0.02s 正常 → **不是服务崩，是 handler 线程永久阻塞**（日志里连 `req` 行都没有，请求直接"消失"）。

**根因**（`shot-automation.cs:389` `UiTree`、`:748` `UiFind`，另 448/469/487 同型）
```csharp
var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition); // 先物化整棵树
int n = Math.Min(all.Count, max);                                       // 之后才截断
```
`max` 只限制后面的循环，**完全限制不了 `FindAll` 的成本**；Electron 几万节点 → 几十秒到无限期，且全程无超时/取消。

**修法建议**：① 改 `TreeWalker(ControlViewWalker)` 逐层 DFS **计数即停**，让 `max` 真有界；② `ui_find` 用 `PropertyCondition(NameProperty, name)` 让 UIA **服务端过滤**，别拉平再自己比字符串；③ 整体用 `Thread.Join(timeoutMs)` 包硬超时（`ClipboardGet` 已是这写法），超时回 `{"ok":false,"error":"uia timeout..."}` 给调用方降级提示。

**agent 侧规避（修好之前）**：对 Electron/大 DOM 应用（WorkBuddy、VSCode 系、网页壳）**不要用 `/ui/tree`/`/ui/find`/`/ui/readall`**；改用 `/shot` + 视觉定位 + 物理坐标点击，或用 `/active` 拿窗口 rect 按比例估点位。必须枚举时先 `max=30` + 短超时试一次，超时立刻放弃该路线，别裸等（会卡死整条工具链）。

**顺带解释"i 索引不稳定"**：`i` 就是 `FindAll` 的平铺顺序，DOM 一变就变（实测同一输入框一分钟内 i=637→644→659→664，637 已变成"08:00"时间戳文本）。工具描述应明确"i 仅在本次响应内有效，跨调用必须重查"。

### ✅ 聊天框连发的服务侧根治（2026-09-06 zcode，双保险）

1. **`keyboard/type` 默认软换行**：`\n` 不再直发 Enter，改发 **Shift+Enter**（记事本照常换行；聊天/搜索框软换行不触发发送）。要真回车传 `nl=enter`。响应带 `newlines` 计数+warn。
2. **`clipboard/set` 默认 CR 归一**：含 `\r` 的文本自动转 `\n`（响应 `crNormalized:true`），根治粘贴路径的回车符连发；`keep_cr=1` 保留原样。
3. **截图/贴图 HTTP 化**：`/ocr?path=&wait=`（本地 qwen3-vl，限截图目录）、`/pin?path=&x=&y=`（贴图窗），MCP 同步 `ocr_image`/`pin_image` —— 之前只有遮罩按钮/F3/托盘入口，agent 完全用不了。
4. **记事本靶子纪律**：测记事本一律 `/app/run?path=notepad.exe`（返回真实 window.hwnd/pid/title）；`Start-Process notepad` 在 Store 启动器模型下秒退假象。
5. **第六轮 R3/R4 补齐**：record 响应带 `video_size`/`fpsNormalized`/`sizeClamped`；R1 像素上限 4M 超限回退全屏（100000×100000 攻击复验进程存活）。

### 第六轮有效测试姿势（workbuddy 原版，zcode 据其报告重建）

1. **窗口状态断言**：用 `screen_capture(region=x,y,1,1)` 裁单像素（GetPixel 在 DWM 下不可信），或像素放大截图（900→1800 NEAREST）。
2. **选中/复制类断言一律哨兵法**：`clipboard_set=SENT-XX` → 操作 → `ctrl+c` → `clipboard_get`，值没变即未生效（返回值 ok:true 不可信）。
3. **UIA 树验证选中**：聊天应用粘贴文本会进消息列表，`ui_find` 树里能找到 = 真送达（别截图找，截不到内容层）。
4. **重启服务后旧 PID 的全局热键/遮罩失效**：遮罩弹在旧进程，新进程收不到 Esc——先杀残留再测。
5. **验证 `/img/` 二进制完整性用字节数**（python raw socket / Content-Length），别按 UTF-8 解码统计（假阴性元凶）。
6. 攻击录屏/窗口管理用 `hwnd` 直控 + 自建靶子，别拿用户真实工作窗口做破坏性用例。


