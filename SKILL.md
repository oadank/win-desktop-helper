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
| 半屏/四分/三均分 | **吸附组正道(唯一正确)**：第一窗 `win_manage(snap, pos=zkbd, layout=6, zone=1, fill=ZCode,标题子串)` —— Win+Z 选布局6选区1，**Snap Assist 自动点选** fill 列表填满剩余区 → 三窗成一组拖边联动。**别每窗各发 Win+Z**（三个独立窗，边不联动）。预设 `zthirdleft\|zthirdmid\|zthirdright` = layout 6 zone 1/2/3；无 fill 时选完 zone 自动 Esc 提交 |
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
| **贴位首选=Win+Z / Win+方向** | 三均分 → `pos=zthirdleft\|zthirdmid\|zthirdright`（本机 **layout=6** zone=1/2/3；**9=中间大两边小**）；半屏/四分 → `sysleft\|sysright\|systopleft\|…`。发键前验 `front=目标`。**选完 zone 后必须 Esc 提交**（默认 esc=1）：不收口窗停在 Snap Assist 悬浮预览，系统不算贴牢、邻窗不是吸附组。连续贴多窗 = 每窗完整走一遍 Win+Z+Esc |
| **点后台/最小化窗口=顶窗或落空** | `mouse_click` 返回 `at.front`；严格模式传 `front=1`，落点非前台直接拒点。先 `win_manage activate` 再点 |
| `win_manage close` 报 `post failed` | 同一根因：权限不足导致 PostMessage 被拒。提权后正常返回 `closed:true` |
| **Win11 记事本未保存关不掉** | `close` 返回 ok 只代表发了 WM_CLOSE；未保存时弹 ContentDialog，UIA 枚举不到 Button，`n` 会打进正文。关窗后必须 `list_apps` 复查，见「记事本」坑 |
| 记事本单实例多标签 / 测试文件被删弹模态 | taskkill 后重开常复用同一 hwnd；**先关窗再删文件**，否则「找不到文件」模态吞掉全部滚轮输入（sZero=0） |
| 记事本 hwnd 不是 Document 窗 | app_run 返回的 hwnd 常不是带 Document 的那个；按面积从大到小逐个 `ui_tree` 找 Document 选目标 |
| 终端/控制台类窗口读不到文字 | 内容是画布渲染，UIA 树里没有。用 `screen_capture` + `ocr_image` 读 |
| 要三等分/任意比例(系统 Snap Layouts 全支持) | snap 不带 pos，改带网格参数：`cols` 切几列 + `col` 第几列 + `colspan` 跨几列；`rows/row/rowspan` 同理管纵向。横三等分中间 `cols=3 col=2`；竖屏上中下 `rows=3 row=1`；2/3 左 `cols=3 col=1 colspan=2`；四等分左上 `cols=2 col=1 rows=2 row=1`；50/25/25 的右上 `cols=4 col=3 rows=2 row=1`。参数非法会直接报错并说清原因 |

## Win+Z 布局表（2560 宽实测，飞出条 1–9 从左到右从上到下）

| # | 布局 | zone 键位（左→右/上→下） |
|---|---|---|
| 1 | 左右两半（带当前窗预览态） | 1=左 2=右 |
| 2 | 左大右窄（左~2/3+右~1/3） | 1=左大 2=右窄 |
| 3 | 左大 + 右侧上下两格 | 1=左大 2=右上 3=右下 |
| 4 | 左右两半（与 1 同形） | 1=左 2=右 |
| 5 | 左大右窄变体（更宽左） | 1=左大 2=右窄 |
| 6 | **三列均分**（三窗吸附组用它） | 1=左 2=中 3=右 |
| 7 | 上下两半 | 1=上 2=下 |
| 8 | 2×2 四格 | 1=左上 2=右上 3=左下 4=右下 |
| 9 | 三列中间大两边窄 | 1=左窄 2=中大 3=右窄 |

布局列表随窗宽/系统变——fill 失败或 zone 落错格时，先截图飞出条核对再改 layout=/zone=。



划词悬浮球(shot-pick*.cs)与常驻浮窗调试五大坑 (2026-09-07 全部实测):
1. WinForms CreateParams 里手加 WS_EX_LAYERED(0x80000) = 整窗隐形(窗口存在/IsWindowVisible=1 但屏幕全空)。圆点/圆角一律用 TransparencyKey 画。
2. 免激活窗(ShowWithoutActivation+WS_EX_NOACTIVATE|TOOLWINDOW)收不到 MouseEnter/Click —— hover计时/点击/拖动必须由低级鼠标钩子 DOWN/UP + 200ms 心跳 GetCursorPos 轮询驱动。WM_MOUSEMOVE 不进本机低级钩子(探针实锤 moves=0)。
3. 构造函数 TopMost=true 在免激活窗上会被丢弃 —— Show() 后必须 SetWindowPos(HWND_TOPMOST,...)，心跳每4轮补置顶。
4. 剪贴板取词法发全局 Ctrl+C 有投错窗口风险(实测把词粘进抖音评论区) —— 安全阀: 前台窗变化或指针离开按下点24px即拒走; 等待循环内前台变化立刻 abort。
5. 给本服务加 MCP 工具三处都要动: shot-service.cs 的 McpCall switch case + McpToolsJson 条目 + **mcp-bridge.js 注册表数组与 buildUrl 路由**(bridge 有独立白名单, 漏改直接 unknown tool)。
配置入口: 设置页「划词」节 / HTTP GET /pick-config?enabled=0|1 / MCP pick_config。开关立即生效+持久化到 shot-service.json pick 节。

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
