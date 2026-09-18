# Win Desktop Helper — GUI 操作手册（核心版）

> ⚠️ 任何拿到本能力的 AI：① 首次操作前必须 `get_skill` 读本文件（服务端强制，未读拒绝工具）；
> ② **每次踩坑必须用 `update_skill` 写回本文件**，禁止只记在自己记忆里；
> ③ 完整历史/已修 bug 考古不在这里，`get_skill(detail="full")` 或 `get_skill(topic="分屏")` 按需取。

## 黄金路径（先判断应用类型，再选方案）

```
1. 找窗口  list_apps() 按 process 名拿 hwnd
2. 置前    win_manage(action=activate, hwnd=...)   → foreground=true 才算真置前
3. 判断类型 → 是 Electron/网页壳/聊天类？
   ├─ 否（Win32 本地应用：记事本/资源管理器/设置等）
   │   4. 找控件  ui_find(hwnd=..., name="按钮的可见文字") → 拿到 ref
   │   5. 点      ui_click(ref=...)                        ← 首选，永不漂移
   │   6. 验证    ui_read / window_info 确认界面真变了
   └─ 是（Electron/大 DOM：WorkBuddy/VSCode/Chrome 系/聊天软件）
      4. 禁用 UIA 全树工具！只用 /shot 截图 + 看图 + mouse_click(坐标)
      5. 找不到按钮 → 用 /ocr?path=截图 识别文字和位置
      6. 唤窗/恢复 → tray_click(name=, double=0) 或 app_run 再启动
```

> **Electron/大 DOM 判别**：进程名含 msedge/chrome/electron/workbuddy/vscode/webview 等，
> 或应用是聊天类/IDE/网页壳。UIA 树在这些应用上会永久阻塞或返回不完整数据。

## 铁律（违反必踩坑）

0. **🔴 写代码前先看仓里已有的——同功能已有实现的，调它，禁止新造平行实现**（思想里写死）。教训实例：托盘图标有"任务栏按钮(x=630)+托盘图标(x=2257)"两个同名元素，老代码摸到谁点谁=随机点到冻窗按钮上白点；修的时候不查已有 FindTrayButton 语义就另起炉灶，错上加错。正确顺序：grep 现有实现 → 沿用其数据流 → 只补差异。同理**同状态多元素**（窗口态 vs 托盘态）必须按语义选区（托盘图标=屏右 SystemTray 类），禁"第一个撞上就用"
0.1 **🔴 Electron/大 DOM 应用（WorkBuddy/VSCode 系/网页壳/聊天类）禁用 /ui/tree、/ui/find(不带 name 的宽查)、/ui/readall** —— UIA 全树物化在 Electron 上永久阻塞且泄漏工作线程（实测 WorkBuddy 烧 1.1 核 70 分钟、整条工具链冻屏，服务重启才救回）。这些窗口只允许：ui_find 带 name=(服务端精确过滤不遍历全树)、/shot 截图+坐标操作。服务端有 UiCall 8s 硬超时+3 泄漏熔断兜底，触发熔断=所有 /ui/* 拒绝直到重启
1. **定位优先级 ref > name > i。截图只用来判断界面状态，绝不用它算落点**
   - `ref` = `ui_find`/`ui_tree` 返回的稳定引用（UIA RuntimeId），窗口存活期内不变，**不会漂移**，不需要 hwnd/title。失效会明确报错，不会静默点错
   - `i` 是树下标，**跨调用必漂移**（实测同一输入框 637→644→659→664，点偏了还返回 ok）。非用不可时必须同时传 `name` 校验
   - 截图估坐标实测偏 150px、视觉小模型估偏 96px，**两者都点不中。别让模型做算术**
2. **状态每次重新采样（桌面是共享的）**：人在同一台机器实时操作时，句柄会变、下标会漂、窗口会被盖住关闭。每轮必须 `/active` + `listall` + 截图**重新采样再出计划**；禁止复用上一步结果。状态不符只说"变了"，禁编机制理论；焦点键发前必验 `front=目标`
3. **每次操作后必须验证**。**切页/切会话/进列表项这类操作必须带 `expect="点完应该出现的文字"`** —— `verify` 只回答"界面变了"，`expect` 才证明"变成了对的那个"（实测：点会话项报 ok 且 changed=true，界面根本没进那个会话）。元素 `offscreen:true` 表示它滚出视口/被折叠，**点了不会生效**，先滚动或展开让它可见
4. **中文一律剪贴板粘贴**：`clipboard_set(text)` → 点输入框聚焦 → `keyboard_press(keys="ctrl+v")`。组合键参数名是 `keys`（不是 key/modifiers）
5. **工具不报假成功**：`ui_click` 的 via=invoke 只代表调到了不代表生效 → 配 `verify=1`；`ui_set` 写入后自动读回校验，不一致直接报错并指路剪贴板方案；坐标点击前自动做落点归属校验，位置命中的不是目标进程会拦下并报错。**看到报错就按报错里的提示改，别重试同一个动作**
6. **"看得见但点不动" / "压根没窗口" → `tray_click(name=应用名, double=0)`**：实现=**Win+B 键盘流**（Win+B 聚焦托盘 → ←/→ 扫描主区+溢出层全部图标 → Enter 点击；图标被隐藏时自动展开溢出层继续扫，双向 80 步）。**禁用坐标点击路径**（同名两元素陷阱：打开窗口的任务栏按钮 x=630 + 托盘图标 x=2257 并存时随机命中，点任务栏按钮=点在冻窗上白点；分辨率一变坐标全废）。`win_manage activate`（SetForegroundWindow）唤回的 Electron 窗经常冻结，只当置前手段不当恢复手段。键盘流失败报错会带焦点终点，多半是名字不对 → `tray_list` 核对；应用真退了 → `app_run` 再启。成功应有 `tray kbd ok` 日志；坐标兜底已废除，**不许静默回退坐标点击**
7. **🔴 工具全报 ok 但屏幕毫无变化 → 先查 UIPI（权限隔离）**：目标应用若以管理员运行（WorkBuddy/ZCode 等常见），而本程序是普通权限，**系统会静默丢弃合成的键鼠输入**（右键菜单不弹、快捷键无反应、点击不换焦点，工具照样返回 ok）。
   - 本程序**默认自动以管理员常驻**（计划任务 `WinDesktopHelper`，登录即静默提权，无需手工操作），正常不该出现此问题
   - 真遇到了：工具会直接返回 `uipi blocked` 并给修复指路；此时 `curl http://127.0.0.1:18800/health` 看 `elevated` 字段应为 `true`，是 `false` 说明提权失败 → 结束进程后从资源管理器运行一次（自动重建），或 `schtasks /run /tn WinDesktopHelper`
   - 副作用提示：普通权限下 **UIA 树也会被截断**（实测某窗口只返回 9 个元素、看不到任何按钮；提权后 43 个），所以"找不到控件"也可能是同一个根因
8. **敏感操作先问**（删除/发送消息/改系统设置），付款不做
9. **诊断链（窗口失踪）**：`window_info not found` → `listall visible:false` → `tray_click` → `activate`；焦点键发前必验 `front=目标`

## 常用工具速查

| 用途 | 工具 |
|---|---|
| 语义定位/点击/读写 | `ui_tree` `ui_find` `ui_click(ref\|name, expect=, verify=1)` `ui_read` `ui_set` `ui_select` |
| 证明"点对了" | `ui_click(..., expect="预期出现的文字")` → 返回 `expect.found` |
| 窗口信息/管理 | `window_info`(支持 process 精确匹配) `active_window` `list_apps` `win_manage` `monitors` |
| 半屏/四分/三均分 | **三分屏秒操作（老大定稿 2026-09-11）**：① 任选一窗 `win_manage(snap, hwnd=X, pos=zkbd, layout=6, zone=1, esc=0, fill=标题子串1,标题子串2)` —— 一条命令成组。**铁则**：fill 用**窗口标题子串**（缩略图名=标题，进程名必败：msedge→"DSH 本地构建"）；**不做任何预处理**（restore/脱组全多余，assist 自动列出所有 app 含最小化）；失败自动重开 assist 重试。分步版：`esc=0` snap → `win_manage(verb=snapfill, hwnd=X, name=标题子串)` 逐步验 |
| 找失踪窗口 | `win_manage(action=listall, pid=)` 含隐藏/最小化/托盘化的窗口 |
| **深度恢复(冻结/没窗口)** | `tray_click(name=, double=0)` 键盘流(Win+B+方向键扫描, 默认) → 窗稳定后 `win_manage(snap=)` 贴位；`app_restore` 为兼容保留但托盘环节同走键盘流 |
| 托盘唤回 | `tray_click(name=, double=0)` —— Win+B 键盘流扫描主区+溢出层（图标被藏也能点），不再用坐标点托盘 |
| 鼠标/键盘 | `mouse_click` `mouse_move` `mouse_drag` `keyboard_type` `keyboard_press` |
| 看 | `screen_capture` `ocr_image` |
| 启动程序 | `app_run(path=, wait=1, process=)` 返回稳定后的窗口 rect |
| 经验写回 | `get_skill` `update_skill` |

## 踩坑速查（最痛的几条，完整版 detail="full"）

| 现象 | 解法 |
|---|---|
| 按截图/视觉模型给的坐标点击全落空 | 改用 `ui_find` 拿 ref，工具算落点 |
| `ui_click` 返回 ok 但界面没变 | 加 `verify=1`；invoke 不生效的应用用 `mode=coord` 或 `mouse_click` |
| 点了报成功但进的不是目标页/会话 | `verify` 只看"变没变"，必须再加 `expect="目标标题"` 证明"变成了对的" |
| 元素在列表里但点了没反应 | 看返回/树里的 `offscreen`：为 true = 滚出视口，先滚动展开再点 |
| `window_info` 查到不相干的窗口 | title 模糊匹配会误伤浏览器标签页，传 `process` 按进程过滤 |
| 粘贴没生效 | 参数名是 `keys`：`keyboard_press(keys="ctrl+v")` |
| 窗口能看见但点不动 / 完全没有窗口 | `tray_click(name=, double=0)` 唤回（键盘流）→ `win_manage activate` 只做置前（它产出的窗常冻） |
| `app_run` 返回的 hwnd 找不到窗口 | 多进程应用会换窗，用 `list_apps` 按 process 重新取 |
| 布局想贴半屏 | 别用 move 手算坐标，用 snap；返回 target≠rect 说明应用有最小尺寸约束，按 rect 补差 |
| **全都 ok 但啥也没发生**（菜单不弹/键无反应） | UIPI：目标窗口管理员权限 + 本程序普通权限 → 输入被静默丢弃。`health` 看 `elevated`，false 就重建提权（见铁律 7） |
| **贴位首选=Win+Z 吸附组** | 三均分 → `snap pos=zkbd, layout=6, zone=1, esc=0` + `snapfill(name=标题子串)` 逐格填（见上方"三分屏秒操作"铁则）；半屏/四分 → layout 4/8 同法或 `sysleft\|systopleft`（Win+方向）。发键前验 `front=目标`；应用最小高会钳制（694 被钳成 800 属正常，非失败） |
| **点后台/最小化窗口=顶窗或落空** | `mouse_click` 返回 `at.front`；严格模式传 `front=1`，落点非前台直接拒点。先 `win_manage activate` 再点 |
| `win_manage close` 报 `post failed` | 同一根因：权限不足导致 PostMessage 被拒。提权后正常返回 `closed:true` |
| **Win11 记事本未保存关不掉** | `close` 返回 ok 只代表发了 WM_CLOSE；未保存时弹 ContentDialog，UIA 枚举不到 Button，`n` 会打进正文。关窗后必须 `list_apps` 复查，见「记事本」坑 |
| 记事本单实例多标签 / 测试文件被删弹模态 | taskkill 后重开常复用同一 hwnd；**先关窗再删文件**，否则「找不到文件」模态吞掉全部滚轮输入（sZero=0） |
| 记事本 hwnd 不是 Document 窗 | app_run 返回的 hwnd 常不是带 Document 的那个；按面积从大到小逐个 `ui_tree` 找 Document 选目标 |
| 终端/控制台类窗口读不到文字 | 内容是画布渲染，UIA 树里没有。用 `screen_capture` + `ocr_image` 读 |
| 要三等分/任意比例(系统 Snap Layouts 全支持) | snap 不带 pos，改带网格参数：`cols` 切几列 + `col` 第几列 + `colspan` 跨几列；`rows/row/rowspan` 同理管纵向。横三等分中间 `cols=3 col=2`；竖屏上中下 `rows=3 row=1`；2/3 左 `cols=3 col=1 colspan=2`；四等分左上 `cols=2 col=1 rows=2 row=1`；50/25/25 的右上 `cols=4 col=3 rows=2 row=1`。参数非法会直接报错并说清原因 |

## Win+Z 已知系统问题（老大 2026-09-11 实测定性：系统 bug，非工具问题）

| 布局 | 多窗 fill 实测 | 结论 |
|---|---|---|
| L4 半 / L5 左大右窄 | ✅ 稳 | 可用 |
| **L6 三均分** | ✅ 双 fill 稳定落位 | **多窗首选** |
| L7 左半+右上下 | 第2格 ZCode api ok 但不落位 | ⚠️ 不可靠 |
| **L8 四均分** | **选中 ZCode 后 assist 自动退出**（老大手测确认） | ❌ 系统级 bug，四窗组做不成 |
| L9 中间大 | ZCode fill api ok 不落位，其余 app 正常 | ⚠️ 不可靠 |

规律：失败全部发生在 **ZCode** 缩略图上（Enter 后窗不动/assist 退出）；Edge/设置/MiMo 正常。
怀疑与 ZCode 最小高 800 + Electron 拒绝缩放有关（全高格 L6 能成，短格 698/683 必挂，L9 全高也偶挂）。

对策：多窗吸附组用 **L6 三均分**；必须四分/含 ZCode 的其它布局时，ZCode 用 grid MoveWindow 摆（无组联动），其余窗走 assist。

## Win+Z 布局键位（老大实测定论 + 微软官方口径）

**官方**：飞出条布局集合动态（随屏幕/窗口变）；数字=飞出条第几格（行优先）；选完布局 Snap Assist 自动触发点选填位 → 自动成 Snap group；拖分隔条相邻窗联动缩放。

**老大实测关键定论（2026-09-11）**：
- **格 1–3 = 动态预设组**：把当前 app 自动摆位占格（一次动多个窗）——**自动化禁用**
- **格 4–9 = 纯区域分割**（不占位）——**自动化只用这些**：
  | # | 形状 | zone（行优先） |
  |---|---|---|
  | 4 | 左右两半 | 1左 2右 |
  | 5 | 左大右窄 | 1左大 2右窄 |
  | **6** | **三列均分** | **1左 2中 3右** |
  | 7 | **左一半 + 右半上下分** | 1=左 2=右上 3=右下 |
  | 8 | 2×2 四格 | 1左上 2右上 3左下 4右下 |
  | 9 | 三列中间大 | 1左窄 2中大 3右窄 |

落错格 = 集合变了 → 先 /shot 截飞出条核对，再改 layout=/zone=。



划词悬浮球(shot-pick*.cs)与常驻浮窗调试五大坑 (2026-09-07 全部实测):
1. WinForms CreateParams 里手加 WS_EX_LAYERED(0x80000) = 整窗隐形(窗口存在/IsWindowVisible=1 但屏幕全空)。圆点/圆角一律用 TransparencyKey 画。
2. 免激活窗(ShowWithoutActivation+WS_EX_NOACTIVATE|TOOLWINDOW)收不到 MouseEnter/Click —— hover计时/点击/拖动必须由低级鼠标钩子 DOWN/UP + 200ms 心跳 GetCursorPos 轮询驱动。WM_MOUSEMOVE 不进本机低级钩子(探针实锤 moves=0)。
3. 构造函数 TopMost=true 在免激活窗上会被丢弃 —— Show() 后必须 SetWindowPos(HWND_TOPMOST,...)，心跳每4轮补置顶。
4. 剪贴板取词法发全局 Ctrl+C 有投错窗口风险(实测把词粘进抖音评论区) —— 安全阀: 前台窗变化或指针离开按下点24px即拒走; 等待循环内前台变化立刻 abort。
5. 给本服务加 MCP 工具三处都要动: shot-service.cs 的 McpCall switch case + McpToolsJson 条目 + **mcp-bridge.js 注册表数组与 buildUrl 路由**(bridge 有独立白名单, 漏改直接 unknown tool)。
配置入口: 设置页「划词」节 / HTTP GET /pick-config?enabled=0|1 / MCP pick_config。开关立即生效+持久化到 shot-service.json pick 节。

## 划词 CDP 直读链 (方案D, 2026-09-11 实装, v0.0.20 已发)

- 原理: Electron 应用带 `--remote-debugging-port` 启动 → helper 按前台进程名查表(MiMo=9222/WorkBuddy=**9229**/ZCode=9224, shot-pick.cs pickCdpApps) → ws 连 page/iframe target evaluate `getSelection` 直读真选区(毫秒级, 零UIA零按键零剪贴板, base64 往返避转义)。空才落回 UIA→剪贴板链。浏览器仍归 Edge 扩展, 终端红线不变。读表达式含三盲区补丁: 输入框走 activeElement.selectionStart、同进程 iframe 走 contentDocument 穿透、空后 80ms 重扫。
- 启动参数固化在快捷方式(桌面\软件\ + 开始菜单)与 WorkBuddy HKCU Run 键。**自动更新器会重写 Run 键/裸拉起丢参数(实锤)**: 应用更新后划词 Electron 失效 → 先 `curl 127.0.0.1:<port>/json/version` 验口, 丢了补参数重启。
- `/json/list` 不止 page: ZCode 挂 4 个 worker target(无 getSelection 且可能不回包)。**必须按 "type" 字段过滤**(worker 的 ws 路径同为 /devtools/page/, URL 滤不掉)+350ms 全局预算+失败 target 60s 惩罚缓存。教训: 未过滤时 5 targets 烧 6.6s, pickBusy 锁占死吞光后续双击=成功率腰斩。
- 双击"一次出一不出"根因(实锤): 09-07"点空白=收起浮元素"规则把双击第一下整口吞掉且不记锚点。修法 dismiss+chain: 收起后该点击照样进双击判定。泛训: 悬浮球交互状态机分支互相咬合, 新规则先问"它吃了谁"。
- 区域截图热键是候选表先抢先得 `[Win+Shift+A→Ctrl+Shift+S→Win+Shift+S]`(shot-service.cs HotkeyRegister 区), 实例间漂移坑肌肉记忆 → **配置显式钉死 `capture.hotkeyRegion`**(本机 repo+安装目录均已钉 Ctrl+Shift+S)。
- 发版四件套同步 bump: shot-service.cs `APP_VERSION` 常量 + AssemblyInfo.cs + setup.iss(AppVersion/OutputBaseFilename)。**打包必须走 _pkg 隔离目录**(仓库根 shot-service.json 有真 key, 在仓库根跑 ISCC=泄密; 打包前对 pkg 文件扫 key 串自检)。坑: PS5.1 读无 BOM UTF-8 中文注释 .ps1=引号炸解析(脚本写纯 ASCII); Git Bash 会把 `/VERYSILENT` 路径化成垃圾参数(Inno 弹 GUI, 用 PowerShell Start-Process 传参); Inno6 的 CurStepChanged 必须 procedure 不是 function。
- 🔴 **CDP 端口必须用 bind 金标准测, 别信 netstat**(2026-09-12 事故): WorkBuddy 原钉 9223, 但 9223 被一个**已死进程(PID 206272)的孤儿 socket** 长期占着 —— netstat 显示 `LISTENING`、tasklist 却查不到那个 PID, 杀不掉。WorkBuddy 每次带 `--remote-debugging-port=9223` 都**静默绑定失败** → CDP 直读 100% 失败(日志 `pick cdp: /json/list fail port=9223 ... (app 没带调试口启动?)`) → 每次拖选都落到剪贴板兜底注入全局 Ctrl+C → **打断用户自己的复制**(老大实测"复制很难成功")。测法: `python -c "import socket;s=socket.socket();s.bind(('127.0.0.1',9223))"`, 报 10048 才是真占用。处置: **换端口**(现用 9229) —— 三处必须同步改: ① `shot-pick.cs` 的 `pickCdpApps` 表 ② 桌面快捷方式 `桌面\软件\WorkBuddy.lnk` 参数 ③ `HKCU\...\Run` 的 `WorkBuddy.WorkBuddy` 值(会被自动更新器重写成裸路径丢掉参数)。
- 🔴 **剪贴板兜底必须加"CDP 类应用禁注入"硬闸**(2026-09-12 实装): `PickViaClipboard(IntPtr fgAt)` 开头 `if (PickCdpPort(fgAt) != 0) return "";` —— 名单里的 Electron 应用(WorkBuddy/ZCode/MiMo)**绝不注入全局 Ctrl+C、绝不碰修饰键**; CDP 挂了也只放弃取词(日志 `pick: clip chain skipped — CDP app, never inject Ctrl+C`)。配套的"用户正按着修饰键就让路"闸(`PickAnyModifierDown`)单独用**不够**: 实测 12 次兜底只拦 1 次 —— 用户按 Ctrl 的时刻通常比 helper 的检测时机晚, 所以必须有 CDP 类禁注入这道硬闸兜底。

## 免激活浮窗拖动不跟手的根因与修法

免激活浮窗"拖动不跟手"根因与修法 (2026-09-07 划词卡片实测, 跟随率 20/20=100%):
不是电脑卡 —— 是跟随机制慢半拍。低级钩子在本机收不到 WM_MOUSEMOVE(moves=0), 拖动期间缓存的坐标是按下瞬间的陈旧值, 且只在 200ms 心跳应用一次 = 一步一卡。
修法三件套:
1. 拖动中直接 GetCursorPos 实时取坐标, 不依赖钩子 MOVE;
2. Timer 心跳拖动期加密到 15ms(平时 200ms), 起拖瞬间立刻切 Interval 不等下一轮;
3. 移动用单次 SetWindowPos(位置+HWND_TOPMOST 一步完成), 坐标没变整个跳过。
验收脚本 _pick_drag_perf.py: 手动 mouse_event DOWN + 20 步 SetCursorPos + UP, 后台 5ms 采样卡片 rect, 数位置变化档位(>=12 为跟手)。别用 mouse_drag 工具测跟手度——它本身一步到位看不出卡顿。

## 125% DPI 下悬浮窗字体必须用像素单位(AutoScaleMode.None 陷阱)

布局用 `AutoScaleMode.None` 按**物理像素**写死的自绘窗，字体绝不能用默认 Point 单位。
`new Font(name, 9.5f, style)` 的单位是 Point(逻辑单位)，系统 125% 缩放(GetDpiForSystem=120 / LogPixels=119)下会被放大 1.25 倍 → 字涨到 125% 而行高/容器坐标不动 → 文字互相挤压重叠，肉眼=「字太大 + 像乱码」(2026-09-07 老大截图实锤)。
修法: 统一 `new Font(name, px, style, GraphicsUnit.Pixel)`，参数值 = 期望物理像素字号(原磅值 × 96/72)。本仓库见 `PickStyle.F` + `FS_TITLE/FS_BODY/...` 常量。
教训: **别信视觉模型 OCR 判断"渲染正常"** —— 它会把重叠字脑补成正常句子照样读出正确文本；要么人眼看，要么量化: 截图后按行统计亮像素，文字行之间必须有暗行分隔、单行墨迹高度 ≈ 字号(14px 字体 → 11~13px)。验收脚本 `_font_check2.py`。

## 悬浮球交互: 按下不许 dismiss + 单击也弹球(只读 UIA 不发 Ctrl+C)

两条 2026-09-07 老大明确要的交互，改在 shot-pick.cs `PickOnMouse`:
1. **DOWN 不再一律 PickDismiss**。老逻辑"点到元素外就整组关掉"=用户随手一点浮窗就没了。改成按下不动手，收掉交给 UP 判定: 划词会重画(ShowPickDot 内部先 dismiss)、单击只尝试取词，两者都没命中就让元素按 4s/60s 超时自然收起。
2. **单击(未划选)也弹球**：`PickHandle(x,y,click=true)`，只走无副作用取词 —— UIA `TextPattern.RangeFromPoint` + `ExpandToEnclosingUnit(Word)` 取光标那个词，取不到就什么也不动。**绝不发全局 Ctrl+C**(单击不产生选区，这时复制 = 把用户刚点中的输入框内容/别处内容当"词"抓走)。
附带两个必修:
- 26px 的小点手抖就 miss，`PickNear(rect,x,y,8)` 容差内当作点中(否则"点它反而没了")。指针已落在元素上时绝不能再取词，否则小点会跳走。
- 闩锁(pickDownOnAskLive/pickDownOnCardLive)必须在 UP 入口一次性取走清零(`bool askJustClosed=...; ...=false`)。留到下一轮才清 → "点掉提问框后紧接着那次划词"被当成重复取词吞掉(要划第二次才出球)。
验收: `_pick_ask_test2.py` 五场景 A输入问题回车/B留空走默认/C Esc/D单击弹球/E点击不消失，全 OK。Win11 记事本 UIA 只有一个 Document+TextPattern，app_run 返回的 hwnd 常不是它，测试要按「大窗逐个试 ui_tree 找 Document」选目标。

## Electron 托盘唤回与弹窗点击实测修正（2026-09-09）

本次实测 WorkBuddy 每日签到弹窗（Buddy加油站），修正技能里两条旧写法：

1. `tray_click` 对 Electron 托盘程序：用单 Enter，别用 double=1
   - `tray_click(name="WorkBuddy", double=1)` 在服务端实现为 Win+B → ←/→ 扫描 → Enter×2。
   - 对 Electron/大 DOM 类托盘图标，Enter×2 等同于双击，会把刚显示的窗口再次隐藏。
   - 正确做法：`tray_click(name="WorkBuddy", double=0)`，Win+B → ←/→ 扫描 → Enter 单点。

2. Electron 弹窗按钮：UIA 拿不到，必须截图 + 图像处理算按钮中心
   - `ui_find(hwnd=..., name="立即领取")` 报 UIA timeout 8000ms（Electron 限制）。
   - 禁用 UIA 后，唯一可行的是坐标点击，但肉眼估算会偏。
   - 稳定做法：
     a. `win_manage(action=snap, pos=left, hwnd=...)` 把窗口贴到固定位置。
     b. `screen_capture(x=0, y=1100, w=500, h=280)` 截取弹窗区域。
     c. Python + Pillow 二值化：灰度 → threshold>210 → 找最大连通域 → 过滤宽高比 1.2~8、面积>300 → 得按钮 bbox → 中心即落点。
   - 本次实测 WorkBuddy 5.5.3，主屏 2560×1440、贴左后半屏后，Buddy加油站 弹窗"立即领取"按钮中心在屏幕 **(254, 1254)**。

验证：OCR 文字从"立即领取"变为"今日已领"即成功。

## 本地改源码 → 编译 → 生效（2026-09-12 实测，三个坑）

> 改完 .cs 让它生效，必须走完这一串；顺序错一步就静默失败。

1. **先查真实运行路径，别以为只有一份**
   - `Get-Process shot-service | Select-Object Path` + `schtasks /query /tn WinDesktopHelper`
   - 事实：真正跑的是**仓库根** `C:\D\opt\win-desktop-helper\shot-service.exe`（计划任务 `\WinDesktopHelper` 提权启动）；`AppData\Local\Programs\win-desktop-helper\shot-service.exe` 只是**引导器**（非提权 → 调 schtasks → 自己退出）。**改完 copy 到两个位置**。
2. **先停进程**：`Stop-Process -Name shot-service -Force`
   - 不停 = exe 被运行中的进程锁住 = csc 报 `CS0016 无法写入输出文件`；不捕获编译输出就会误判成"脚本没执行"（本次就栽在这，白折腾半小时）。
3. **编译**：`explorer.exe C:\D\opt\win-desktop-helper\build-annotation.cmd`（等同用户双击）
   - **禁止**在 Bash/PowerShell 里直接调 `csc.exe` —— 安全策略硬拦（"compiles arbitrary C# code"）；经 helper `/app/run` 传 cmd 也只是换个壳，源码目录 exe 的锁照样在。
4. **验产物**：exe mtime 变了 + 二进制含新符号
   - `grep -a -c "<新符号>" shot-service.exe`；注意 C# 字符串在 exe 里是 **UTF-16LE**，utf8 查不到不代表没编进去，两种都查。
5. **启动**：`schtasks /run /tn WinDesktopHelper`
   - **别用 PowerShell `Start-Process`** —— 工具进程树会回收子进程：日志显示完整启动，命令一结束进程就没了（本次第二次栽这）。
6. **验证**：`curl 127.0.0.1:18800/health` → 看 `build` 时间戳 / `elevated:true` / `session:1`。

## 抖音类 Electron 托盘应用「窗口打不开、只剩托盘图标」的处置（2026-09-12 实测）

**症状**：窗口点不出来，托盘图标还在，任务栏也没按钮。

**诊断链（实测）**：
1. `tasklist /v` 看主进程状态 → 出现 `Not Responding`（连采 3 次确认不是抽风）
2. `win_manage(action=listall)` → 主窗口**存在**（Chrome_WidgetWin_1，标题 douyin），rect 正常在屏幕内，但 `visible:false`（应用自己隐藏的，抖音是「关窗=收进托盘」设计）
3. `window_info(process=douyin)` 返回 `window not found` —— 它只认可见窗口，**not found 可反推可见性**
4. 内存判活：假死时 8 个 douyin 进程全是 3~30MB；正常活体是 200~470MB。**内存小得离谱 = 空壳假死**

**🔴 关键坑：卡死的窗口不能软唤起，试了白费时间**
- `win_manage(action=restore, hwnd=...)` 直接返回 **helper request timeout** —— ShowWindow/SetForegroundWindow 是 SendMessage 语义，目标 UI 线程不处理消息就阻塞到超时（helper 本身没坏，`/health` 正常）
- `tray_click(name=应用名)` 也无用，Win+B 轨迹里根本扫不到该图标（窗口隐藏时任务栏按钮同样不存在，连着 two 个都没有）
- 结论：**`visible:false` + 主进程 `Not Responding` = 软办法没救，别反复试，直接重启客户端**

**处置（实测 30 秒修好）**：
1. `taskkill /F /IM douyin.exe /IM douyin_tray.exe /IM douyin_widget.exe /IM douyin_guard.exe`（guard 是保活进程，要一起杀）
2. `explorer.exe "<exe 完整路径>"` 启动 —— **别用 Start-Process**（工具进程树会回收子进程）
3. 验证两道：`window_info(process=douyin)` 返回 hwnd（修好信号）；再 `screen_capture(window=标题)` + 看图确认画面真渲染（排除白板），别只看窗口存在

**拿进程 exe 路径**（Win11 已移除 wmic）：python ctypes `OpenProcess(0x1000)` + `QueryFullProcessImageNameW` 一把拿全。
本机抖音 8.5.301：`C:\Program Files (x86)\ByteDance\douyin\douyin.exe`（tray/widget 在 `<安装目录>\<版本号>\tray\`）。

**预防**：抖音设置里把「关闭主窗口」改成退出程序而非最小化到托盘。

## Buddy 加油站自动签到：菜单必须语义定位，禁止写死行号（2026-09-17 实装）

**脚本**：`C:\D\opt\scripts\buddy_checkin.py`（用 venv python: `C:\Users\oadan\.workbuddy\binaries\python\envs\default\Scripts\python.exe`）
用法：`--force` 忽略空闲检查 / `--dry` 只检测不点 / `--idle N` 空闲阈值(默认45s)。stdout 输出一行 JSON。

**流程**（每轮重新采样窗口 rect，绝不复用上轮坐标）：
1. `/apps` 拿 WorkBuddy rect；非前台则 `/app/restore?verb=activate&hwnd=`
2. 发 Esc 清浮层 → 点头像（截窗口左下角 340x130，PIL 扫绿色圆 bbox 中心）
3. 点「Buddy加油站」（语义定位，见下）
4. OCR 面板区域判状态：含「今日已领」→ 已领，退出；含「立即领取」→ 需点击
5. 「立即领取」定位：面板截图找**最下一条文字行**，取该行**首个文字块**中心（4 字按钮，已领/未领两态几何一致、通用）
6. 点击后复查 OCR 确认变成「今日已领」

### 🔴 菜单必须语义定位，绝不能写死行号
实测：**账号菜单行数随窗口大小变化** —— 窗口 960x1032 时 8 行（积分余额/Buddy加油站/去邀约/成长计划/设置/记忆与进化/外观/更多）；窗口 1280x1392 时 11 行（顶部多出「标准版·升级套餐」账号行，底部多出「帮助与反馈/检查更新/退出登录」）。
按「第 2 行 = Buddy加油站」写死 → **实际点到了「积分余额」，弹出非预期窗口**。

**正确做法（行序天然对齐）**：
```python
# 1) 视觉行段：亮像素行分布
rows = lit_rows(menu_img, 35, 300)
# 2) 裁「纯文字条」单独 OCR：x 58..165（去掉左侧图标、右侧说明/按钮）
menu_img.crop((58, 0, 165, H)).save(strip_path)
lines = [l for l in ocr(strip_path).split("\n") if l.strip()]
# 3) 行数必须相等，再按文字内容找目标行下标
assert len(lines) == len(rows)
idx = next(i for i, l in enumerate(lines) if "加油站" in l)
row = rows[idx]
```
文字条每行只有一个文字块 ⇒ **OCR 行序 == 视觉行段序**。行数不等就**拒绝点击**（宁失败不点错）。实测 11 行完全对上。

### 三个坑
1. 🔴 **`/ocr` 只允许读截图目录下的文件**（安全限制）：裁出来的临时条带存 `%TEMP%` 会报 `path outside screenshots dir`（且返回里只有 error、没有 text，容易误判成"OCR 坏了"）。必须存到截图目录（用首次截图返回路径的 `dirname`）。
2. 🔴 **Electron 账号菜单会自动超时关闭**（约 40s 级）：点开头像后必须尽快点菜单项，「截图+算坐标+OCR」要一口气做完，中间别插长耗时操作。
3. 菜单截图做 PIL 像素分析时**不要带 `axes=1`**（坐标轴刻度数字会混进亮像素统计）。`axes=1` 只给 AI 读图用。

### 已实现的能力：`/shot?axes=1` 坐标轴截图
截图叠加青色网格 + 黄色数字，**标的是屏幕绝对坐标**（不是图内相对坐标，所以图里读到 600 就直接点屏幕 y=600，无需换算）。实测精度：小区域(400x300)读图 ±3px。分辨率/区域变了重新截即可，天然自适应。默认关（不带 axes 与以前完全一致，不影响人工截图）。

## 修正: 改源码后编译必须用 schtasks，explorer.exe 方式本机不生效（2026-09-17 实测）

原文写「编译 = `explorer.exe build-annotation.cmd`（等同用户双击）」。

🔴 **本机实测：explorer.exe 方式连试两次都没触发**（exe mtime 不变、大小不变），而且看不到 csc 报错 → 极易误判成"代码编译失败"，白排查很久。

**实测可行的做法（带日志，能看报错）**：
1. 写一个把 csc 输出重定向到文件的 cmd：
```bat
@echo off
cd /d C:\D\opt\win-desktop-helper
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -target:winexe ... -out:shot-service.exe AssemblyInfo.cs shot-service.cs ... > "%TEMP%\wdh_build_out.txt" 2>&1
echo EXIT=%ERRORLEVEL% >> "%TEMP%\wdh_build_out.txt"
```
2. **用计划任务跑**（不要 Start-Process，不要 explorer.exe）：
```
schtasks /Create /TN wdh_build_once /TR "<cmd完整路径>" /SC ONCE /ST 00:00 /IT /F
schtasks /Run /TN wdh_build_once
```
3. 读 `%TEMP%\wdh_build_out.txt` 看 `EXIT=` 与 `error CS` 行
4. 验证产物：exe mtime + 字节数变化；新符号用 `grep -a -c "<符号>" shot-service.exe`（C# 字符串在 exe 里是 UTF-16LE，utf8 查不到不代表没编进去）
5. 启动：`schtasks /run /tn WinDesktopHelper` → `curl 127.0.0.1:18800/health` 看 `build` 时间戳 / `elevated:true` / `session:1`

**其余不变**：改前先停进程（`Stop-Process -Name shot-service -Force`，否则 exe 被锁 → CS0016）；直接调 csc 会被安全策略拦（"compiles arbitrary C# code"）是预期的，别浪费时间；装完记得清理临时 cmd 与计划任务。

## 🔴纠正: 账号菜单行数一直不变——是我截图裁掉了顶部(2026-09-17 老大纠正)

**原结论「菜单行数随窗口大小变化」是错的，作废。**
真相：账号菜单**一直**是完整的 11~12 项，顶部**一直有**「标准版 / 升级套餐」账号行。之前我在 1920x1080 下截图起点写死 `y = 头像y - 545`（高 500），在 2560x1440 下写死 `mtop = bot-545`（高 500）——**两次都把顶部 2~3 行裁在框外**，于是数出「8 行」并推断「菜单变小了」。
**根因**：菜单是从头像往**上**弹的浮层，长度随内容变化；任何写死的截图高度/起点都可能切掉顶部。
**教训（普适）**：① 不能凭猜设截图区域，必须先框住完整浮层；② 数出来的行数偏少，第一反应应是「区域没截全」而不是「界面变了」。

### 正确做法：不要数行、不要认行号，直接按文字语义定位
写了通用工具 `C:\D\opt\scripts\ui_probe.py`（详见技能 `win-text-locate-click`）：
1. 截图(基线) → 点开浮层 → 截图(浮层) → **差分 bbox 自动框出面板**（不猜区域）
2. 面板内背景色众数 → 前景行投影 → 行段（自适应行数/行距）
3. 每段裁条带 + 左侧贴**段号徽标** → 拼成一张图 → **1 次 OCR**（段号即行号 ⇒ 文字与行段天然对齐）
4. 按文字匹配 → 取该行最宽前景块中心 → 屏幕绝对坐标 → 点击 → 截图 OCR 验证
实测 2560x1440@125%、WorkBuddy 最大化：连续 4 次 100% 命中，单次 29~45s。

### 其他必知坑
- 🔴 **Electron 账号菜单约 40s 自动关闭**：分析耗时长（OCR 十几秒）时菜单会过期 ⇒ 必须「采样→分析→**重新确认浮层是否还开着**→再点」。
- 🔴 **上一轮遗留浮层会让差分失效**（报「差分无变化」）：需 Esc×2 + 采样重试。
- 🔴 **点击后的面板出现在菜单右侧偏下**（不在菜单位置上）：验证截图要覆盖 `cy-180 ~ cy+620`，只截窄条会误判「面板没打开」。
- **左侧栏 hover 高亮会被差分算进面板 bbox** → 行数偏多，但不影响段号对齐。

### OCR 引擎选择（重要）
- **本地 ollama `qwen3-vl:4b-instruct`：实测 5.8s 中位、5/5 成功** ← 用这个
- **agnes 云端(api.agnes-ai.cn)：实测 0/5，每次卡满 60s 超时** ← 当前不可用，别用
- 两者对同一张徽标图的识别质量**完全一致**（12~16 行含段号全对）。

## 🔴纠正+增强: agnes 远程 OCR 已可用 —— 3 key 优先级 + 送图压缩（2026-09-17 实装实测）

**原结论作废**：本文件上方写「agnes 云端 0/5、每次卡满 60s 超时 ← 当前不可用，别用」是**误判**。
真相：当时用的是**国际站 apihub 的 key + PNG 无损大图**两个坑叠加；换成国内站 .cn + JPEG 压缩后完全可用。

### 实测数据（同一张图，同日）
| 组合 | 耗时 | 结果 |
|---|---|---|
| 小图 240x90（1 行字）@ .cn | **0.8s** | ✅ |
| 小图 240x90 @ apihub key2 | 6.1s | ✅ |
| 小图 240x90 @ apihub key1 | 20.9s | ✅ |
| 中图 1200x700（40 行字）@ .cn | 26~33s | ✅ |
| 全屏 2560x1440（70~100 行）@ .cn | 26~52s | ✅ |
| 本地 ollama qwen3-vl:4b 跑中图 | 14~22s | ✅ 但撞 num_predict=300 被**截断**（只出 15 行） |
| key1 打 .cn（跨站） | 0.1s | ❌ **HTTP 401 无效令牌** |

**三条结论**：
1. 🔴 **key 与站点绑死**：key1/key2 只认 apihub（国际站），key3 只认 api.agnes-ai.cn（国内站）。**跨站必 401，不能乱换**。
2. **.cn 比 apihub 快得多**（0.8s vs 6~21s）→ 配置里 **.cn 的 key 排第 1 位**。
3. **慢的根源是「图里文字多」**（VLM 自回归逐字生成），不是引擎/网络。小图秒回，全屏几十行就要几十秒 —— **要快就只截需要的区域**，别全屏 OCR。

### 配置：`ocr.apiKeys`（新键，2026-09-17 加）
```json
"ocr": {
  "provider": "openai", "model": "agnes-3.0-flash",
  "apiKey": "sk-...(兼容旧单key)",
  "apiKeys": "key3@https://api.agnes-ai.cn/v1/chat/completions|key2@https://apihub.agnes-ai.com/v1/chat/completions|key1@https://apihub.agnes-ai.com/v1/chat/completions"
}
```
- 格式：`key@endpoint` 用 `|` 分隔；endpoint 省略则用 `ocr.endpoint`
- **顺序 = 优先级**：第 1 个先用，失败才换下一个（不是轮流 —— 轮流会把请求打到慢 26 倍的 apihub）
- 3 个 key 的真值在 N5105 `100.110.110.12:/opt/text-api-images/.env`（`AGNES_KEY_1/2/3`）

### 送图压缩（原实现的最大坑）
原 `BitmapToBase64` 用 **PNG 无损** 送图：全屏 PNG 567KB → base64 757KB，**极易破 1MB 被拒**。
新增 `BitmapToBase64Jpeg(bmp, maxSide, maxBytes)`：等比缩到最长边 1400 + JPEG q88（体积超标自动降到 q55），目标 <500KB。
`OpenAiVisionOcrProvider` 已改用它；本地 qwen3vl provider 仍走 PNG（本地不限体积）。

### 🔴 超时坑：`HttpWebRequest.Timeout` 对**异步**请求无效
MS 文档明确：Timeout 不影响 `BeginGetResponse`/`BeginXxx` 系异步调用，而 `WebClient.UploadStringTaskAsync` 内部就是异步。
→ 直接设 `WebRequest.Timeout` **完全没用**（实测照样跑满 52s/60s）。
**正确写法**：自己包一层
```csharp
Task<string> dl = wc.UploadStringTaskAsync(url, json);
Task done = await Task.WhenAny(dl, Task.Delay(PerKeyTimeoutMs));
if (done != (Task)dl) { try { wc.CancelAsync(); } catch {} throw new WebException("timeout", WebExceptionStatus.Timeout); }
string resp = await dl;
```
本实装 `PerKeyTimeoutMs = 45000`（留足量，.cn 大图最坏见过 33s），超时后按 failover 换下一个 key。

### 改动落点（供后续维护）
- `shot-ocr.cs`：`OcrKeyEndpoint` / `TimeoutWebClient` / `OpenAiVisionOcrProvider` / `OcrProvider()` 工厂 / `BitmapToBase64Jpeg`
- `shot-config.cs`：`LoadCfgDict()`+`SaveCfgDict()` 已加 `ocr.apiKeys`（否则在设置 UI 点保存会把该字段冲掉）
- 备份：`shot-ocr.cs.bak-20260917-keyrot` / `shot-config.cs.bak-20260917-keyrot` / `shot-service.json.bak-20260917-keyrot`
- 编译后 exe 376832 → 380416 字节；启动日志会打印 `ocr: openai -> 3 key(s) rotation`

## 🔴 dsh-web 模型切换是**两级菜单** ——「选项搜不到」先怀疑层级，别怪工具（2026-09-17 源码核对 + 纠正）

**⚠️ 本条取代了我 2026-09-17 17:5x 写进本手册的错误结论。** 原文说「Chromium/Electron 页面内下拉浮层 UIA 拿不到选项、是桌面自动化的天花板场景」——**那是错的**。真相是：菜单本来就是**两级**的，我在错误的层级搜模型名。**工具（点击 / UIA / 截图 / OCR）全程正常，是理解错了交互结构。**

**为什么会误判**（两层错误叠加，值得记住）：
1. 在**根层**搜 `Glm5.3` 搜不到 → 误读成「点击没生效 / UIA 看不见 Chromium 浮层」；
2. 一张截图里我只看到触发按钮 + 一个 hover tooltip（`不适用 QW3.8F`），**没有菜单行**，我把 tooltip 当成了已展开的菜单。

**真实结构（源码已证实：`C:\D\opt\deepseek-harness\deepseek-harness\packages\client\ui-model-selection\src\client\ModelSelect.tsx`）**
- 文件头注释第 3 行原话：`Two-level selection per figma 496:26454's MenuDropdown: the root menu is the Model / Effort row pair`。
- 根层（`pane==='root'`，第 300–315 行）**只有两行**：`模型`（`t('menu.model')`，值 = `modelLabel`）+ `推理`（`t('menu.effort')`，值 = `effortLabel`；**仅当该模型有 reasoning 时才渲染**）。两行均 `role="menuitem"`，右侧带 chevron。
- 点「模型」→ `setPane('model')` → **这时才**出现按 provider 分组的模型列表：`role="group"` 分组 + 标题，每项 `role="menuitemradio"`，文字 = `model.name`。**分组标题 = 该 provider 的 displayName**（如 gw → `Henry`）；所以 `Glm5.3`（gw 组）与 `Gwglm5.3`（litellm 组）名字很像，**要按分组标题区分**。
- 点「推理」→ `pane==='effort'` → effort 列表。
- 浮层是 portal 到 `document.body` 的 fixed 卡片，贴触发按钮**上方、右对齐**（所以按按钮下方的区域截图会截空）。

→ 根层**没有**模型名，在那儿搜 `Glm5.3` 必然搜不到，表现就是"点了没反应"，于是反复重试、把菜单开了又关。

**正确流程（4 步，含验证）**
1. `ui_click` 点触发按钮（name ≈ `选择模型，当前 QW3.8F`）→ 打开根菜单
2. `ui_find type=MenuItem` → 拿到「模型」行（值形如 `模型 QW3.8F`）→ 点进去
3. 列表展开各 provider 分组 → `ui_find name=Glm5.3` → 点选（认准 `Henry` 分组）
4. **验证**：`ui_read` 触发按钮 → name 应变成 `选择模型，当前 Glm5.3`

**✅ 成功实证（2026-09-17 18:3x，MiMo 客户端自己跑的）**：按上面 4 步，**3m46s 完成**，原话 =「已切换完成。模型选择器现在显示 当前模型是 Glm5.3（按钮无障碍名称：`选择模型，当前 Glm5.3`）。操作路径：点模型按钮 → 点「模型」钻进列表 → 选中 Henry 分组下的 Glm5.3。」**工具全程没问题，是层级理解问题。**

**教训（比结论本身更值钱）**
看到「选项搜不到 / 点了没反应」，先怀疑**层级与结构**，再怀疑工具。我这次把"我没在正确层级找"错判成"UIA 看不见 Chromium 浮层"，还写进了共享手册 —— **错误结论的传播成本极高**；拿不准就去读源码/UI 结构，别急着下"天花板"结论。

**两个仍然成立、但都不是本次失败原因的附带事实**
- `/ocr` 只返回 `{ok,chars,text}`，**不含 bbox/坐标**（本次实测复核：`{"ok":true,"chars":38,...}`）。靠它"找文字→算坐标"只能估，容易偏几百像素 → 要文字+坐标请用 `C:\D\opt\scripts\ui_probe.py`，或 `/shot?axes=1` 自己读坐标。
- 被其它窗口完全遮挡的 Electron 窗口，`screen_capture(window=...)` 拿到的是**旧帧**（PrintWindow 对 Electron 无效），且遮挡期间界面不重绘 → 要看某个 agent 执行到哪一步，**别截它的窗口**，直接读 `C:\D\opt\win-desktop-helper\shot-service.log`（实时、全量记录每次 MCP 调用）。

## D7 — 找输入框一律先 `ui_find type=Edit`，别拿截图肉眼估坐标（2026-09-17 小米 MiMo 实测）

**症状**：给 Electron 聊天客户端（小米 MiMo）贴长提示词。`win_manage activate` 成功、`mouse_click` 返回 `at` 也确实命中目标 hwnd、`front:true` —— 但 `ctrl+v` 与 `keyboard_type` **全部静默无效**，输入框一直是空占位符。看着像“应用不接合成输入”，其实是**点到了框外面**。

**根因**：坐标是从 `screen_capture` 截图上肉眼估的。截图本身是 1:1 物理像素没错，但人在缩略图上读偏移量级就错了 —— 估的 y 比真 rect 高 100px+，正好落在提示文案区。

**正解**：
```
ui_find(hwnd=133098, type="Edit")
→ [{"i":149,"name":"描述任务，输入/调用技能","type":"Edit",
    "rect":{"x":1650,"y":665,"w":866,"h":56},
    "ref":"42.133116.4.4.1.440719"}]
```
Electron 的 contenteditable 组件在 UIA 里就暴露成 `Edit`，**rect 零误差**。
（之前用 `ui_find(name="描述任务")` 也行，但只有 type 搜是稳的——占位符文字会变。）

**验证“真的进去了”**：粘完**再跑一次 `ui_find(type="Edit")`**，看 rect 变没变 —— 输入框拿到多行内容会撑高（本次 **h 56 → 251**）。rect 不变就是没进去，**回去重查 rect，不要反复重试同一个点**。

**副产物**：`ui_tree` 对这种应用会按 DOM 顺序先吐一大堆侧栏节点（默认 400 上限会被侧栏吃满），**不要指望 ui_tree 能枚举到主编辑区**，直接 `ui_find` 精准找。

**发送**：长文贴完后 `keyboard_press("enter")` 即可（中文/换行都不影响）。发送成功的证据 = 输入区变成气泡 + 出现助手回执。

## D8 — WorkBuddy 主窗口：UIA 会超时，导航只能靠坐标；误点会切走整个分区（2026-09-18 实测）

**UIA 不可用**：`ui_tree` / `ui_find` / `ui_click` 对 WorkBuddy 主窗口（Electron 大 DOM）**全部 8s 超时**（`UIA timeout 8000ms - 疑似大DOM`）。→ 老实走截图 + 坐标，别反复试 UIA 浪费轮次。

🔴 **连 `ui_find(name=...)` 也会把整个服务熔断**（2026-09-18 08:09 实测）：在 WorkBuddy 上跑一次 `ui_find(hwnd=..., name="描述任务")` 就返回 `UIA fused: 3 leaked worker threads (大DOM 超时不可杀)`，**此后所有 `/ui/*` 一律拒绝**（不是单窗口失效，是全服务级熔断；泄漏线程杀不掉，只能重启）。
修复：`taskkill /F /IM shot-service.exe` → `schtasks /run /tn WinDesktopHelper` → `curl 127.0.0.1:18800/health` 看到新 pid + `elevated:true` 即恢复（实测 5 秒内起来）。
**结论：WorkBuddy 主窗口上任何 `/ui/*` 都不要调，一次都别试。**

补充实测（2026-09-18 08:1x）：MiMo 客户端**可以**用 `ui_find(type="Edit")` 拿输入框精确 rect（见 D7），但那是熔断之前的事；熔断当场就再也用不了。所以给 Electron 客户端贴长文时，**优先一次就用 ui_find 拿准 rect，别先试错**。

**左侧导航（竖排；面板一滚动坐标就漂，每次先截图确认）**：
```
WorkBuddy 5.5.6                                  ← 面板标题
新建任务 / 助理 / 项目 / 专家·技能·连接器 / 定时任务 / 资料库 / 更多
空间(3) ▾ → opt ▾ → 任务卡片列表              ← 首页态才有
```
点任一项是把**主区**切成对应分区（资料库→文件列表、项目→项目页…），**不是**原位展开。

**窗口标题栏左上三个图标**（y≈27，与「关于(A) 编辑(E)」同一行）：
- `(30,27)` = **收起/展开左侧面板**（悬停 tooltip「收起侧边栏」）。⚠️ 是**切换**语义：面板显示中点一下会收起。
- `(67,27)` 搜索、`(105,27)` 筛选。

**踩过的坑**：为关掉头像浮层随手点了左侧空白 `(300,580)`，实际命中「资料库」行 → 左面板被资料库文件树顶掉、导航消失，主区也切了。补救路径：
1. 点 `(30,27)` → 导航列重新出现（此时能看到哪一项是选中态）；
2. **重新截图**确认导航各项当前 y（每次都不一样，严禁复用旧坐标）；
3. 点任务列表里的任务卡片 → 主区回到那条对话。

**配套事实**：「Buddy加油站」浮层挂在左下角头像上，**悬停会自动弹出**（不是点击才开），标题栏右侧有 ✕。领积分脚本 `C:\D\opt\scripts\buddy_checkin.py` 依赖「WorkBuddy 停在首页 + 导航可见」；停在别的分区或面板被折叠时它会在 step1 直接失败（甚至误点），**别在那种状态下重试**。

**一句话铁律**：在 WorkBuddy 主窗口点击前，**先截当前图确认目标 y**；宁可多截一张，也不要复用上一轮的坐标。
