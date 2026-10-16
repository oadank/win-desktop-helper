# Win Desktop Helper — 操作手册（只这一份，按应用再翻小册子）

> **三条元规矩**
> 1. 新会话开工前先 `get_skill` 读本文件（服务端强制，没读连只读工具都拒绝）。**读一次管一整轮会话**，不会每轮重来（旧手册写的"每轮重置"是错的，已废）。
> 2. 踩了坑**必须** `update_skill` 写回，不许只记在自己记忆里 —— 写的时候带 `app=WorkBuddy` 这类归属，落到对应小册子，别往本文件堆。
> 3. 别拿瞎猜的 `topic=` 关键字抽段（无命中会把整份吐回来）。想精准取内容：`read` 对应小册子文件，路径见下面索引表。

## ⚠️ 2026-09-22 工具已合并：42 个 → 12 个（带 action 参数）

本手册正文里的旧工具名（`list_apps`/`window_state`/`ui_click`/`mouse_click`/`screen_capture`…）**已不存在**，一律换成新工具名 + `action` 调用，否则报 `unknown tool/action`。
完整对照表与调用示例：`get_skill(app=tool-merge-20260922)`，或直接读 `patterns/tool-merge-20260922.md`。

| 新工具 | action 取值 |
|---|---|
| `window` | active / list / info / monitors / state / manage（manage 里再带 verb/pos/…） |
| `mouse` | move / click / down / up / drag / pos / scroll |
| `keyboard` | type / press / hold |
| `clipboard` | get / set / history |
| `capture` | shot / longshot / pin / ocr |
| `ui` | tree / find / click / read / readall / set / select |
| `record` | start / stop / status |
| `app` | run / runas / restore / tray |
| `desk_skill` | get / update（原 get_skill / update_skill） |
| `wait_for` / `pick_config` / `taskbar_volume` | 单动作，action 可省 |

闸门按 **(工具, action)** 判只读：`window(state/list/info/active/monitors)`、`mouse(pos)`、`clipboard(get/history)`、`record(status)`、`desk_skill(get)` 免读手册；其余动手类必须先 `desk_skill(action="get")`。

## 黄金路径：先判应用类型，再选方案

```
1. list_apps() 按 process 名拿 hwnd            （桌面是共享的，句柄每轮都要重新采样）
2. window_state(hwnd) 查真实状态                （零副作用：不激活/不落盘/不碰 UIA，Electron 冻结窗也秒回）
                                                ← 别再"截一个像素"当断言用，那是拿重活干轻活
3. 这应用是哪类？
   ├─ Win32 本地应用（记事本/资源管理器/设置/传统控件）
   │    ui_find(hwnd, name="按钮上的可见文字") → 拿 ref → ui_click(ref, expect="点完该出现的文字", verify=1)
   └─ Electron / 大 DOM（WorkBuddy/VSCode/Chromium 壳/聊天软件/IDE）
        先看下面索引表里它那一本小册子；宽查 UIA 会被入口当场拦下（不再白等 8 秒连累别人）
        改走：screen_capture 截图 + 人眼/OCR 判断 + mouse_click 坐标 + wait_for 兜内容
4. 点完不要猜 → wait_for(text=..., hwnd=...) 盯到真看见为止
   切忌 sleep 固定秒数再判断，更切忌"没找到就再点一次"（连点事故的头号来源）
```

## 铁律（违反必出事）

0. **动手前先 grep 仓库里已有的实现**，同功能禁止新造平行实现；同一状态有多个同名元素（窗口按钮 + 托盘图标）必须按语义选区，禁止"第一个撞上就用"。
1. **定位优先级 `ref` > `name` > `i`**。`i` 是树下标，跨调用必漂移（实测同一输入框 637→644→659→664，点偏了还返回 ok）；非用不可就同时传 `name` 校验。**截图只用来判断界面状态，绝不让模型拿它算坐标**（肉眼估偏 150px、本地视觉小模型估偏 96px，两者都点不中）。
2. **状态每轮重新采样**：人在同一台机器上实时操作，句柄会变、下标会漂、窗口会被盖被关。状态不符只说"变了"，不许编机制理论。
3. **每次操作后必须验证**。`verify=1` 只回答"界面变了"，`expect="预期出现的文字"` 才证明"变成了对的那个"（实测：点会话项报 ok 且 changed=true，界面根本没进那个会话）。`expect` 现在是**轮询到超时**（默认 2.5s，`expect_timeout` 最高 7s），返回带 `waitedMs`/`samples`，不再"等一秒就下结论"。元素 `offscreen:true` = 点了不会生效，先滚动/展开。
4. **中文一律剪贴板粘贴**：`clipboard_set(text)` → 聚焦输入框 → `keyboard_press(keys="ctrl+v")`。组合键参数名是 **`keys`**（写成 `key`/`modifiers` 现在会被当场拦下并提示正确写法）。
5. **工具不报假成功**：`ui_click` 的 `via=invoke` 只代表"调到了"不代表"生效"；`ui_set` 写入后自动读回校验；坐标点击自动做落点归属校验，命中的不是目标进程会直接报错。**看到报错就按提示改，别原样重试同一个动作。**
6. **唤窗/恢复用 `tray_click(name=应用名, double=0)`**（Win+B 键盘流，主区+溢出层双向扫 80 步）。坐标兜底已废除，**不许静默回退成点坐标**（同名两元素陷阱：任务栏按钮 x=630 与托盘图标 x=2257 并存时随机命中；分辨率一变坐标全废）。`win_manage(activate)` 唤回的 Electron 窗经常是冻的 —— 它只当置前手段，不当恢复手段。
7. **全报 ok 但屏幕毫无变化 → 先查 UIPI（权限隔离）**：目标以管理员运行而本程序是普通权限时，系统会**静默丢弃**合成键鼠输入。`/health` 看 `elevated` 应为 `true`；为 `false` 就结束进程从资源管理器跑一次，或 `schtasks /run /tn WinDesktopHelper`。副作用：普通权限下 UIA 树还会被截断（实测同窗口 9 个元素 vs 提权后 43 个）。
7.5 🔴🔴 **点击/右键「报 `ok:true` 但界面毫无反应」→ 先查有没有【遮罩层】盖着**（2026-09-26 真根因，**先前的「划词取词吞右键」结论已作废，见下**）：
   Chromium/Electron 应用常有不透明的遮罩/抽屉层，**它自己不响应你的目标操作、但把点击全吃掉**。
   实测 WorkBuddy 的 `_drawerBackdrop_7xbcw_157 _drawerBackdropOpen_7xbcw_170`（z-index 200，铺满 1024px 宽）
   盖住侧栏 → 右键事件**确实到达页面**（挂 `contextmenu` 监听能收到），但 target 是 backdrop，菜单不弹。
   **查法（30 秒，比猜快 100 倍）**：CDP 挂一次性监听确认事件到了没，再列遮罩层：
   ```js
   document.addEventListener('contextmenu',e=>window.__rc=[e.clientX,e.clientY,e.target.className],true)
   // 事件到达但 target 是 backdrop/overlay/mask → 就是遮罩问题
   [...document.querySelectorAll('*')].filter(e=>/backdrop|overlay|mask|scrim|drawerOpen/i.test(e.className||'')
        && e.getBoundingClientRect().width>0 && getComputedStyle(e).pointerEvents!=='none')
   ```
   **修法**：把它关掉（按 Esc / 点遮罩空白 / 点它的关闭按钮）；**卡住的残留**只能 `el.remove()` +
   `grid.classList.remove('cb-grid--drawer-open')` 复位（实测 Esc 与点空白都关不掉）。
   🔴 判据口诀：**事件到了页面但 target 不对 = 有层盖着**；**事件根本没到 = 才去查钩子/权限**。
   ⚠️ **`/mouse/click?button=right` 是否受划词取词影响：实测无影响**（取词开/关各 3 次，结果一致）；
   `shot-pick.cs:188` 那句 `return` 只是从 `PickOnMouse`（`void`，纯内存记坐标）返回，**不拦消息**。
   钩子里仅两处 `return (IntPtr)1` 拦截，**都只针对任务栏**（中键静音 / 滚轮音量）。
   ⇒ **别再关取词**（那是老大的划词功能），它从来不是原因。
   注：HTTP 端点真名是 `/mouse/down` `/mouse/up`（**不是** `/mouse_down`，写错会 404 静默当失败）。
8. **UIA 熔断按进程隔离**（旧版是全局连坐，已改）：某进程攒够 2 个超时线程 → 只拒这个进程的 `/ui/*`，别的窗口照常用；累计到 8 才需要重启服务。已知必挂的应用在**入口直接拦**（当前：`workbuddy`，13 毫秒返回并给替代路子），被拦时按提示改走截图+坐标，不要重试。
9. **敏感操作先问用户**（删除 / 发消息 / 改系统设置），付款不做。

## 结果怎么读（失败分三态，别再一锅端）

| 标记 | 含义 | 你该干什么 |
|---|---|---|
| `severity: self_heal` | 能自己救 | 按 error 里的指路换姿势，**别原样重试** |
| `severity: need_user` | 要用户动手 | 直接告诉老大要做什么，别循环重试烧 token |
| `severity: dead_end` | 此路不通 | 停下换方案，不要绕 |
| HTTP **409** | 落点非前台保护 | 正常防护不是故障，先 `active_window` 确认前台 |
| `stale_handle: true` | 句柄已失效 | 重新 `list_apps` 采样，窗口被关或被重建了 |

超时类错误：**先看 `/health` 的 `uia` 账本**再决定重不重试 —— 反复重试只会把泄漏线程攒满。

## 小册子索引（按应用直达，别在主手册里找细节）

| 你要动谁 / 干什么 | 看这份 |
|---|---|
| WorkBuddy（含账号菜单点击失败真根因、误点补救） | `patterns/workbuddy.md` |
| 抖音类 Electron 托盘应用（窗口打不开只剩图标） | `patterns/douyin.md` |
| 任意 Electron/Chromium 客户端（黑屏处置阶梯、焦点漂移） | `patterns/electron-black.md` |
| Electron 托盘唤回 / 弹窗点击实测 | `patterns/electron-tray.md` |
| 小米 MiMo（找输入框、别用肉眼估坐标） | `patterns/mimo.md` |
| 分屏 / Win+Z Snap Layouts 键位与系统 bug | `patterns/win-z.md` |
| 划词悬浮球 CDP 直读链 | `patterns/edge-cdp.md` |
| OCR / 取字（引擎选择、送图压缩、异步超时坑、look_image vs ocr_image） | `patterns/ocr.md` |
| 改源码 → 编译 → 生效（必须走 schtasks）、开机自启 | `dev/build.md` |
| 悬浮窗/DPI/拖动跟手等自绘 UI 开发坑 | `dev/floating-ui.md` |
| 临时文件删除姿势、日志位置、探针坑 | `dev/files-and-diag.md` |
| Buddy 加油站每日领积分脚本（**不属于本能力**，历史全在此） | `scripts-doc/buddy-checkin.md` |
| 🔴 **经验反刍机制**（踩了坑怎么沉淀成手册条目 —— 每个干完活的人都该跑） | `scripts/wb_reflect.py`（用法见下） |
| dsh-web 模型选择是两级菜单（**属于 DSH 项目**，只是借桌面助手操作时踩的） | `docs-moved/dsh-model-menu.md` |

### 经验反刍：干完活收口（2026-09-26 起强制）

踩完坑**当场记账**，收工**蒸馏落位** —— 不然下次原样再踩（实测发生过：手册写了的坑照踩）。

```
python C:\D\opt\scripts\wb_reflect.py line "点 More 按钮" "菜单不弹" --trap --evidence "试2次"
python C:\D\opt\scripts\wb_reflect.py distill --app <应用名> --task "<任务名>"
python C:\D\opt\scripts\wb_reflect.py append --app <应用名> --title "坑 X：..." --body "..."
python C:\D\opt\scripts\wb_reflect.py verify "坑 X" --app <应用名>     # 🔴 改完必须验证
```
四类产物自动分流：**新坑**→`patterns/<app>.md`；**全局坑**→本文件铁律段；
**可脚本化链路**→`scripts/*.py`；**环境硬事实**→openmem。
🔴 **没有 `--evidence` 的坑不许进共享手册**（防猜测毒到所有 agent）。

## 已作废的结论（别再传，遇到旧文档里有这些话直接删）

1. ~~get_skill 闸门按每轮对话重置~~ → **每会话一次**（实测：跨若干轮直接调工具不拦）。
2. ~~改源码后编译可以用 explorer.exe 方式拉起~~ → 本机不生效，必须 `schtasks /run /tn WinDesktopHelper`。
3. ~~WorkBuddy 账号菜单行数一直不变~~ → 是截图裁掉了顶部，不是行数不变。
4. ~~WorkBuddy 点击失败是"被系统挡住"~~ → 真根因是**等太短 + 自己连点**（详见 `patterns/workbuddy.md`）。
5. ~~UIA 超时靠全局熔断兜~~ → 已改成按进程隔离 + 已知必挂应用入口拦截。
6. ~~验证窗口状态请用截图裁一个像素~~ → 用 `window_state`，零副作用且不走 UIA。
7. ~~「WalkLimited 没用 CacheRequest，逐属性 COM 往返是 /ui/* 超时的直接成本源，最值得抄 windows-mcp」~~ → **2026-09-27 A/B 实测推翻**：旧 TreeWalker 路径 Notepad 0.285s / Edge 1.077s（最小化窗口），本机跨进程 UIA 属性读很便宜；CacheRequest 三次实现三次失败（MTA 无缓存 → PropertyCondition 挂 Edge 8s → TrueCondition 连 Notepad 都 8s/20s 超时熔断），已回滚。证据与全过程：openmem `f2bcc8b1`。

## 本次改造留下的新契约（2026-09-19，v0.0.23 起）

- `window_state`：零副作用窗口状态断言（visible/minimized/maximized/foreground/responsive/rect/style + pid/process/title）。`responsive=false` 说明窗口线程已不理会消息（真卡死）；但 **Electron 渲染层黑屏时它仍可能为 true**，那种情况按"截图字节数"判据（黑屏约 22KB vs 正常 300~400KB）。
- `wait_for`：等**内容**出现/消失（`disappear=1` 等消失），服务端轮询，只回结论+`waitedMs`+`samples`。与 `win_manage(action=wait)` 只等"窗口存在"互补。`timeout` 上限刻意压在 25s（DSH 侧 MCP 调用预算 30s，给更久只会先被上游掐断）。
- 每个工具的 HTTP 预算单独配（长截图 150s、OCR 130s…），不再一刀切 30s；参数名写错、类型写错**在发出前就硬错并提示正确写法**。

---
*瘦身记录（2026-09-19）：本文件原 43,535 字 / 74 节 → 现约 6 千字。删掉的只有重复表述与已作废结论；其余内容**逐字搬**进上表小册子，未改写、未丢字。整本原文另存 `SKILL-ARCHIVE-20260919.md`，git 快照 `3a49cd3` 可一键还原。*

## 新工具 cdp：Chromium/Electron 界面元素直读（不截图不 OCR，毫秒级拿按钮名+坐标）

_记录日期: 2026-09-26 · 通用_

## 新工具 `cdp` —— Chromium/Electron 应用「界面元素直读」，2026-09-26 上线（老大批准，实测通过）

**它解决什么**：Electron/Chromium 壳的应用（WorkBuddy、MiMo 桌面端、Edge、VSCode 类）自带远程调试端口，能直接问出"屏幕上有哪些按钮、叫什么、在第几像素"。
以前遇到这种应用只有两条路：UIA（大 DOM 必超时/熔断）或 截图+OCR（一次领积分实测 17 次串行认字 ≈ 112 秒）。
**现在第三条路优先**：`cdp` 一次查询毫秒级返回整张元素表，带名字和矩形，**不截图不认字**。

**动作**（`port` 必填，scan 除外）：
| action | 干什么 | 备注 |
|---|---|---|
| `scan` | 扫本机哪些应用开着调试端口 | 先跑它，别猜端口。已扫到：9223=WorkBuddy(Chrome/138) |
| `targets` | 列某端口的页面（多窗应用先看清第几页） | 只读 |
| `elements` | **出元素表**（可带 `match=文字` / `selector=.css` / `limit`） | 只读，**递归穿透影子根** |
| `eval` | 跑自定义只读 JS（`expr`） | 含写特征会被当场拦，需 `allowWrite=1` |
| `click` | 页面内合成点击（`x,y` 用**页面坐标**） | 敏感：要先读手册 + `allowClick=1` |

**四条必守纪律**：
1. **读优先用 `elements`，别自己写 OCR**。判状态最省：`eval` 读一个 `innerText` 就够（例：WorkBuddy 的 `.fuel-btn` 写「今日已领」= 已完成，含「立即领取」= 该点）。
2. 🔴 **`elements` 的结果会同时命中侧栏同名会话**（实测 match=加油站 先回的是会话卡片）。**必须再用 `selector` 收窄**（`.fuel-menu-entry`），或只认"点开浮层后新出现的元素"。
3. 🔴 **`click` 只能点页面内部元素；外部浮层（账号菜单之类）不吃合成事件** —— 那类要 `mouse` 工具点物理坐标。点完一律**回读验证**，别当已生效。
4. **坐标换算**：`elements` 每条给 `px,py` = 页面坐标 × dpr（**窗口内**物理坐标）；屏幕绝对坐标 = `window` 工具取到的窗口原点 + px,py。本机 dpr=1.25。
5. 🆕 **`elements` 每条带 `cx/cy`（页面坐标中心，直接喂给 `click`，别自己算 `x+w/2`）和 `vis`（1=当前可见，可见的已排在前面）**。
   🔴 **坐标字段同名同义（2026-09-26 统一，别再自己加减）**：`cx/cy` = **页面坐标中心** → 喂 `click`；`px/py` = 窗口内物理中心 → 加窗口原点 = 屏幕绝对坐标。**脚本侧** `C:\D\opt\scripts\cdp_probe.py` 另给 `sx/sy`（屏幕绝对物理中心，喂真鼠标）与 `sx0/sy0`（屏幕绝对左上角，需按边偏移时用，如头像圆心 = 左上+高/2）。⚠️ 已踩：拿 `cx/cy`（页面坐标）当屏幕坐标喂合成点击 → 点空，差 `dpr` 倍。字段全表见 `patterns/workbuddy.md`。
   ⚠️ **同一个 selector 常回多条**（实测 `.fuel-btn` 回 3 条，含一条 0×0 未渲染副本）—— **认 `name` 里的文字，别按顺序假定第一条就是目标**。
6. 🆕 **`selector` 也穿透影子根**（2026-09-26 修）：`.fuel-close`、`.fuel-btn` 这些藏在 shadowRoot 里的元素，用 `selector` 直接查得到（此前会回 0 条）。**凡是「文字只在图标里、靠 `aria-label` 命名」的按钮**（关闭 ×、图标按钮）现在也能读到名字（同日修：纯空白文本节点曾把 `aria-label` 兜底短路掉）。

**影子根这条要单独记**：有些行（如 WorkBuddy 的「Buddy加油站」菜单项）在 `shadowRoot` 里，**普通 DOM 查询读到的是空字符串** —— 这就是过去只能靠 OCR 的真原因。`cdp` 的 `elements` 已经递归穿透，直接能读到。

**别发 `Runtime.enable`**（自己写 eval 时）：事件流会淹掉回包，表现为"读不到"，极易误判成页面卡死。

🔧 **改 `powershell`/`cdp` 里那段元素表 expr 时必跑三条自检**（2026-09-26 血泪：语法全过、实跑全错）：
a) 括号配平 + `new Function(expr)`；b) **作用域断言**：`for (const e of ...)` 必须在 `for (const h of ...)` **之前**闭合（否则 host 递归被套进匹配循环 → 退化成"有匹配才进影子根"，语法合法但永远读不到影子根内元素）；c) **端到端实跑**：`selector=.fuel-close` 应 ≥1 条、`selector=.user-menu-trigger` 应 1 条、`click` 后回读应归 0。
泛训：**「纯空白字符串」在 `||` 链里是 truthy** —— 任何 `a || b` 兜底先 `a.trim()` 再判。

7. 🆕 **自己写 `eval` 时两条禁令**：**别用页面 JS 的 `el.click()`**（`eval` 里含 `.click(` 会被写闸门当场拦；且穿透影子根拿到的可能是 SVG `<path>`，根本没这个方法）——要点击就用 `action=click`（协议层真实鼠标事件）。**别拿 `document.elementFromPoint` 探落点归属**——它**不穿透 shadowRoot**，只回 shadow host，会给出"看着点对了、其实没生效"的假象。

🔴 安全口径：CDP 能执行任意 JS = 能改该应用的一切、能读该页面任何内容。所以 `eval` 默认只读、写操作显式放行、`click` 走手册闸门。**这是权限设计，不是沙箱** —— 别拿它当安全边界。

## /ui/tree 与 /ui/readall 的截断提示（2026-09-27 上线，windows-mcp 唯一落地的借鉴）

- 触顶时输出 `"truncated":true` + `hint`（"树没走完，只返回前 N 个 —— 加大 max= (本端点上限 5000/2000) 或换更小的窗口"）；不触顶时输出与旧版**逐字节一致**（md5 对拍过），调用方按有无 `truncated` 分支即可。
- 🔴 **别再往 WalkLimited 上 CacheRequest / RuntimeId 去重** —— 2026-09-27 三次实现三次失败（Notepad 都能挂到 8s/20s 超时），已整体回滚。想重新试之前先读 openmem `f2bcc8b1` 的 A/B 数据与死因清单。
- 泛训：**"逐属性 COM 往返 = 慢"在本机 C# 服务上不成立**（实测 ~0.1ms 级/次）；性能改造先 A/B 对拍拿基线数字，别从别的技术栈（Python comtypes）照搬优化结论。

## 跨机远程协助：把 helper 当远程执行通道（2026-09-30 定稿，reasonix 实战）

场景：**另一台机（A 机）上的 agent 借本机 helper 操控本机** —— 改服务、跑命令、起 GUI、看真屏幕。SSH 进不去、或需要 **Session 1 + 管理员**时走这条（SSH 落 Session 0，控不了桌面）。

链路三段：A 机起临时 HTTP（一发一收）→ `GET :18800/app/run?path=powershell.exe&args=<-EncodedCommand>` → 本机 Session 1 里 `iwr` 拉脚本执行 → `Invoke-RestMethod -Method Post` 把结果回传 A 机。

🔴 **四条命门（每条都实测踩过，别再踩）**：
1. 脚本文件必须 **带 BOM 的 UTF-8**（`Set-Content -Encoding utf8BOM`）或**全 ASCII** —— helper 拉起的是 `powershell.exe`=**5.1**，无 BOM 的 UTF-8 中文注释被按 GBK 解码**直接破坏语法**，报错是一串 `??` 乱码，极易误判成"脚本没传过去"。
2. `app/run` 返回 `{"ok":true,"pid":0,"name":"","window":null}` **不代表失败** —— 实测多次 `pid:0` 的调用脚本完整跑完并回传了。**只信回传，不信返回值**，更别据此重复触发（重复执行副作用）。
3. A 机侧回传文件要**微秒精度命名**（`%H%M%S%f`）—— 按秒命名会同秒覆盖；等待时按 **CreationTime** 取新文件，别用"文件大小/LastWriteTime"筛（会读到上一轮的旧结果）。
4. 回传前**把所有值转成纯字符串** —— `Get-Content`/`Get-ChildItem` 的对象直接 `ConvertTo-Json` 会连带序列化出 PSDrive/Provider 树，实测炸出 **10MB 与 42MB** 报文，还有一次 6.7MB 直接把 `ConvertFrom-Json` 噎死。

用完必须收尾：A 机**关掉监听端口**（裸 HTTP 无鉴权，Tailscale 网内也别留）；本机临时文件只落 `%TEMP%`，不碰仓库/ProgramData。

细节、只读探针清单（`/health` `/monitors` `/shot` `/active`）、与 SSH / mstsc 的分工表 → `patterns/remote-assist.md`。相关 openmem：`b8cf00fb`（UU 排障全案，含本通道专段）、`ce4bb708`（UU 日志清理）。
