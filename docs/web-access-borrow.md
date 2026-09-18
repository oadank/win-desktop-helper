# 从 eze-is/web-access 抄什么 —— win-desktop-helper 改造依据

> 来源：https://github.com/eze-is/web-access v2.5.4（8.9k star，SKILL.md 11KB + 5 个 Node 脚本，共 13 个文件）
> 抄的是**工程范式设计**，不是浏览器 CDP（我们走桌面级 UIA，路线不同）。
> 本文所有位置均为 `文件:行号`，web-access 侧来自实际读到的源码，win-desktop-helper 侧来自 v0.0.23（build 09-18 09:25 / pid 29244 / elevated true）源码实读。
> **纪律（抄自对方）**：只写验证过的事实。凡未执行验证的一律标 `[推断]`，不当结论用。

---

## 零、先看体检结果：我方现状底盘

| 项 | 事实 |
|---|---|
| 规模 | C# 主源 ≈11,600 行（shot-service 3498 / shot-capture 3078 / shot-automation 1589 / shot-pick 1407 / shot-longshot 756 / shot-config 484 / shot-ocr 412）+ `mcp-bridge.js` 812 行手写 stdio JSON-RPC，零依赖 |
| 形态 | .NET 4.8 WinForms 常驻托盘，`TcpListener` 只绑 127.0.0.1，**每请求新建线程、无池化无排队上限**（`shot-service.cs:2942-2963`）；计划任务 `WinDesktopHelper` 提权自启，**不是 nssm 服务** |
| 工具面 | bridge 注册 **40 个**；HTTP 侧另有 9 个未暴露（`/find_text` `/diag/threads` `/win/snapfill` `/check-update` `/update` `/app/exit` 等） |
| 手册 | SKILL.md **42,852 字符 / 774 行**（工具描述写"约 3K 字"，**是假的**）+ SKILL-HISTORY 23,415 字符 |
| 已有的强项 | `WalkLimited` 计数即停防挂死、`HitGuard` 落点归属校验、三级置前策略、长截图三带 SSD + 静止门 + 内缩避遮罩、UIPI 预检不假报 ok、`ui_set` 写后读回比对 —— **工程密度不比对方低，缺的是几处结构性的收口** |

---

## 一、能抄的（分三组，按能抄价值排）

### A 组：立刻抄，改了就有收益

| # | 它的做法 | 证据 | 我方现状（源码实据） |
|---|---|---|---|
| **A1** | **闲置资源租约回收**：自己创建的一律记账 `{id, lastAccessed}`，每次访问续期，60s 扫一遍，闲置超 15min 自动关闭；SIGINT/SIGTERM 时全清再退出 | `cdp-proxy.mjs:30-32` `250-276` `327` `660-671` | **全库 grep `TTL\|Idle\|Cleanup\|purge\|expire\|LastAccess` 只命中 1 处无关结构体字段**（`shot-service.cs:324`）。三处确认在漏：① `UiRefCache` 进程内字典缓存元素 ref，**无淘汰、无 TTL**（`shot-automation.cs:910-933`）② `pin_image` 贴图窗、截图/OCR 落盘、`record_start` 的 ffmpeg、`app_run` 起的进程，全无人收尸 ③ 截图目录已 77 张（health `shots:77`），只进不出 |
| **A2** | **失败分三态**：`exit 0`=继续 / `2`=必须问用户 / `1`=照 stdout 步骤自救，三种在 SKILL.md 里各自解释该干什么 | `check-deps.mjs:148` `163` `176` | 统一 `ok:false` + error 文本，**不区分"我能自修"和"得叫人"**。`/ui/*` 熔断后唯一出路是人肉 `taskkill` + `schtasks /run`（SKILL D8 自记），Agent 无从判断这是 `need_user` |
| **A3** | **破坏性变更一次做完，拒绝双路径**：旧写法当场 400 + `error`/`migration` 路径/`example` 三件套。FAQ 把理由说透："两条路径长期共存 → Agent 学不彻底、维护者两套都要测，**把架构债转成了认知债**" | `cdp-proxy.mjs:359-364` `397-403` / `migration-2.5.3.md:66` | **活标本就在我们代码里**：`mcp-bridge.js:507` 手工做 `maximize→max` 同义映射，补 bridge 与服务端 verb 不一致。另有静默失败实录：`keyboard_press` 的 `keys` 写成 `key` → 返回 `"keys":"undefined"` 照样"成功"。`tray_click` 坐标兜底"已废除"只写在手册文字里，代码里有没有残留分支没人保证 |
| **A4** | **根因挖到结构层**：URL 被截断的根因不是"忘 encode"，而是"用带语法的格式承载同语法的数据"→ 换传输通道，并明写"靠调用方守纪律治标不治本，Agent 偶尔忘记就翻车，还每次多花 token" | `migration-2.5.3.md:17-31` | 我们的同类问题解法停在纪律层："截图估坐标偏 150px、视觉模型偏 96px" → 写铁律"别让模型做算术"。方向对，**但没从工具面上砍掉这条路**：`/ocr` 明确不给 bbox（`shot-service.cs:445`），`/find_text` 未暴露 MCP 且只回"含不含"（`406-443`，代码自陈"改为把图丢给 agent 自己看"）→ UIA 一失效（Electron 大 DOM）**只剩让模型估坐标**，而这正是实测点不中的那条路 |
| **A5** | **修正必须回源头**：迁移 checklist 第 3 步——旧写法若来自某经验文件，**必须连源文件一起改，"不要只在当前调用上改"，否则下次复用再踩** | `migration-2.5.3.md:57` `69` | `update_skill` 实现是 **`fs.appendFileSync` 往 SKILL.md 尾部追加**（`mcp-bridge.js:765-773`）：无结构、无去重、无失效标记、无冲突仲裁。后果已写在手册里：`:693` 节标题自带"本条后段已被同日更晚的纠正节推翻"、`:722`"⚠️ 已被同日纠正节推翻，不要照抄这两条" —— **错误结论留在文件里靠人工贴警告**。另 `铁律0.1:29`（WorkBuddy 禁任何 `/ui/*`）与 `:451`（MiMo 可用 `ui_find(type=Edit)`）存在张力，无人仲裁 |
| **A6** | **不给自动迁移脚本，给 Agent 判断清单**：明确拒绝正则批量替换——"经验文件掺着说明注释，正则容易误伤；checklist 就是给 Agent 看着内容自己判断怎么改的，比脚本可靠" | `migration-2.5.3.md:68-69` | 工具签名变更没有"改造 checklist"这个产物。加一个工具要改 4 处（`shot-service.cs` switch + `McpToolsJson` + bridge 注册表 + `buildUrl` 路由，SKILL:125 自记），却没有单一真源 |

### B 组：设计范式

| # | 它的做法 | 证据 | 我方对位 |
|---|---|---|---|
| **B1** | **就绪等待分两层，并公开自我设限**：proxy 只保证文档层就绪，SKILL.md 与 API 文档都明写"`/new` 返回只代表基础加载完成，HTTP 200、`readyState=complete`、标题出现都**不算**完成标准"，必须再查目标内容，15s 观察窗**持续轮询** | `cdp-proxy.mjs:279-313` `368-370` / `cdp-api.md:46` | **我方 `expect=` 存在假阴性 bug 级缺陷**：`ExpectCheckJson`（`shot-automation.cs:1210-1233`）是 `Sleep(900)` 后**扫一次**，不是轮询到超时 → 慢渲染必然报 `found:false`。且 `verify/expect` **只挂在 `ui_click` 一个工具上**，`mouse_click`/`keyboard_*`/`ui_set`/`win_manage`/`app_run` 都不返回前后指纹。手册 `:742` 已有正确做法（"轮询截图直到特征文字出现，77.6s→19~25s 一次命中零重试"）**但那是外部脚本 `C:\D\opt\scripts\buddy_checkin.py` 手写的，不是服务能力** |
| **B2** | **`/health` 暴露内部账本**：不只回 alive，回 `connected/browser.id/sessions.size/managedTabs.size/chromePort`，供上层判断"已跑的实例是否正好是本次要的那个" | `cdp-proxy.mjs:333-344` / `check-deps.mjs:78-94` | `/health`（`shot-service.cs:1856-1861`）有 session/elevated/version/build/shots/uptime，**无托管资源账本**（几个贴图、几张临时图、有无录屏进行中）。也**不指明真 log 在哪** —— 这个缺失代价已实锤：手册记过**两次**"看错 log（仓库根那份是旧的，真跑的是 AppData 副本）得出完全相反的结论" |
| **B3** | 单实例幂等仲裁：不能 bind → 先探 `/health` → 健康实例就 `exit 0` 当自己成功了 | `cdp-proxy.mjs:632-652` | **~~缺口~~ 我方已有且更讲究**（`shot-service.cs:2717-2748`：普通实例撞管理员实例主动退出不接管；否则 take-over 杀旧进程并回报新 build）。**这条不抄**，只欠 B2 那半边：把健康状态说给 Agent 听 |
| **B4** | **探测零副作用**：查端口活着用裸 TCP connect 不用 WebSocket 握手，**因为握手会触发浏览器授权弹窗**，注释写明为何不用更"正规"的方法 | `browser-discovery.mjs:51-60` | 我们在用"截图"当状态断言：手册明写"验证窗口状态用 `screen_capture(region=x,y,1,1)` 裁一个像素（GetPixel 在 DWM 下不可信）" —— **重、且有落盘副作用**。缺一个不 activate、不改焦点、不落盘的纯状态查询端点 |
| **B5** | **决策与呈现分离**：`selectBrowser()` 只返回判别式事实 `{kind: ok\|ambiguous\|mismatch\|empty}` 不写文案，两处调用方各自决定怎么说；模块头注释直接写"不擅自降级" | `browser-discovery.mjs:1-10` `97-128` | 我方 C# handler 边判断边拼 JSON，同一份判定散落多处。已付过的学费：CDP 端口判定散在 `pickCdpApps` 表 + 快捷方式 + HKCU Run 键**三处必须同步改**（SKILL 2026-09-12 事故），错一处就静默降级 |
| **B6** | **不替用户拍板**：无持久偏好时，哪怕只检测到一个可用浏览器也照样问用户 | `browser-discovery.mjs:123-127` | 与我方人格铁律一致，但它落到代码分支（`ambiguous → exit 2`）。可照抄成机制 |
| **B7** | **偏好只有一处，单次覆盖不污染环境**：长期偏好 `config.env`（首启从模板复制、gitignored）；单次例外走 argv，**明写"不读 process.env"** | `browser-discovery.mjs:9-10` `62-79` / `check-deps.mjs:5-9` | 我们有 `shot-service.json` + 设置页 + 环境变量三处可生效。已付学费："区域截图热键是候选表先抢先得，实例间漂移 → 必须显式钉死配置" |
| **B8** | **子 Agent 共享一个 proxy、按 targetId 隔离 tab，无竞态** | `SKILL.md` 并行分治段 / `cdp-proxy.mjs:29-30` sessions Map | **我方熔断是全服务连坐**：一个窗口的 UIA 超时让所有窗口 `/ui/*` 一起死（`UiCall` 8s + 3 泄漏线程熔断，`shot-automation.cs:814`），且泄漏线程杀不掉只能重启 exe。缺 **per-window 隔离/锁**。另外桌面侧主动串行（`lsBusy` 单飞 `:296`、`recLock` 录屏互斥），多目标并行能力近乎空白 —— 手册里唯一的多目标批处理是 `fill=` 一次摆位，那是摆窗口不是并行干活 |

### C 组：给 Agent 看的输出怎么省 token（细节最见功力）

| # | 它的做法 | 证据 | 我方对位 |
|---|---|---|---|
| **C1** | 输出密度自适应：结果**只在真跨多来源时**才加 `@浏览器-profile` 标注，单来源不标 | `find-url.mjs:240-244` | `ui_tree` 一次 400 元素**全字段整块吐回**（含 `patterns` 字符串数组、value 截 200 字），无按需裁剪 |
| **C2** | 消灭分隔符歧义：字段用 `\|` 分隔，字段内的 `\|` 替换成全宽 `│` | `find-url.mjs:170-171` | JSON 输出无此问题，但 log 是纯文本行、无结构化字段 |
| **C3** | 返回"点到的到底是什么"：点击回 `{clicked,x,y,tag,text}`，`text` **截断 100 字**，给证据又不炸上下文 | `cdp-proxy.mjs:459` `494` `513` | `ui_click` 有 `via`+`name` 一致性校验（做得好），但 `mouse_click` 只回 `at{hwnd,pid,process,title,front}`，**不回落点元素的文字证据** |
| **C4** | 空结果不给"零条"了事：提前返回空并在结尾写清"为什么空 + 下一步改什么" | `find-url.mjs:107` `251-252` | 部分做到（`ui_set` 不一致时指路剪贴板方案，很好），但 `ui_find` 查不到时只回空 |
| **C5** | **帮助/清单从源码同源生成**：`--help` 是把本文件第 1-21 行注释去前缀打出来，用法与代码永不漂移 | `find-url.mjs:62` | **这条对我们是 P0 而非细节**：同一能力**三张清单互不一致** —— README 说 27 个工具 / bridge 实际 40 个 / C# `McpToolsJson` 约 33 个且陈旧（缺 `list_apps`/`ui_tree`/`ui_find`/`ui_select`/`longshot`/`app_restore`）。`shot-automation.cs:3454` 起那套内嵌 JSON 字符串拼接，静态读到 3470/3471/3473/3474/3477/3480/3485-3487/3490 **疑似缺右花括号** `[推断：仅静态比对，未执行验证]`。README 版本也陈旧（写 v0.0.18.1，实跑 v0.0.23）。**曾有事故**：`get_skill` 有 handler 有闸门但没进 TOOLS → 闸门逼 Agent 调一个不存在的工具，整条链死锁 |
| **C6** | 前置检查顺手吐出先验清单：末尾列出 `site-patterns/` 有哪些站，一次调用知道"哪些站我有笔记" | `check-deps.mjs:194-203` | **我方此处比它差，我上轮判"持平"判错了**：`get_skill` 默认吐**整份 42,852 字符**，`topic=` 无命中**回退完整版**省不掉，且闸门**按每轮对话重置** → **每一轮第一件事就烧 ~3 万字符**（SKILL:684-690 自记，连 `active_window` 这种只读工具都被拦） |
| **C7** | **经验按主体分文件** + frontmatter(`domain`/`aliases`/`updated`) + 统一三段（平台特征/有效模式/已知陷阱）；`match-site.mjs` 按 alias 正则命中就吐正文；写入门槛"只写验证过的事实"、"标注日期当提示不当保证" | `SKILL.md` 站点经验段 / `match-site.mjs:18-45` | **按应用的真值全在散文里，服务端不认**：Win+Z 格 1-3"自动化禁用"、L8 四均分选中 ZCode 后 assist 自动退出=系统级 bug、MiMo 可 `ui_find(type=Edit)`、WorkBuddy 禁 `/ui/*`、记事本 5 坑、Electron 黑屏判据=截图字节数（黑屏 22.8KB vs 正常 300~400KB）—— **没有 `process名 → 操作策略` 映射表**，全靠 Agent 关键词捞，捞不到就当没有 |
| **C8** | 锁住的库先复制到临时文件再查，临时名带 `pid+时间+随机` 防撞，`finally` 必删 | `find-url.mjs:134` `164-166` | 截图/OCR 产物按 MD5 命名去重（已修，好），但**机器生成物无 TTL、无清理**（见 A1）；仓库根散落 20+ 个 `_*_test.py` / `*.png` 混在源码里 |

### D 组：对方有、我们完全空白的两个纪律

- **D1 风险话术强制闸门**：它把"部分站点对自动化检测严格，存在封号风险，已内置防护但无法完全避免，Agent 继续操作即视为接受"写成**必须向用户展示、展示完才许干活**的话术。我方"敏感操作先问"只存在于 SKILL 口头，服务端**无危险动作识别、无 dry-run、无 undo、无审批钩子**（`/app/exit` 是唯一带 confirm 的端点）。
- **D2 派子 Agent 的写法纪律**：① 只写"要什么"别写步骤，过度限定会把主 Agent 的错误假设塞进子任务；② **别用暗示手段的动词**——写"搜索 X"会把子 Agent 锚死到搜索工具，写"获取/了解 X"才留出手段选择。这条对 DSH `subagent` 派活直接适用，我方无此纪律。

---

## 二、不抄的（以及它自己也不干净的地方）

| 项 | 为什么不抄 |
|---|---|
| 全端点**无鉴权** + `/screenshot?file=` 任意路径写 | 这是**我们的同类洞，不是它的优点**。见 P2-1 |
| 恢复步骤写死 `pkill -f cdp-proxy.mjs` | 纯 POSIX，Windows 上根本不成立 —— v2.5.4 仍这么写。**我们做错误指路必须按平台分支**，别照抄文本 |
| Proxy 常驻"不建议主动停止" | 因为停了要用户重新点浏览器授权。我们生命周期不同（计划任务提权），别搬"永不重启"结论 |
| 经验/子 Agent 纪律全靠 SKILL.md 文字自觉 | 机制层只有 `match-site.mjs` 一个查询工具。**我们有 `dsh-api-gate` 这类闸门插件的手段，就别退回纯文字纪律** —— 教训：2026-09-16 我方 agent 照样裸撞 api.github.com 吃 403，规矩写成文字就会被脱敏 |

---

## 三、改造清单（按"再拖债越大"排，不按好抄排）

### P0-1 超时链路对齐（**新发现，比多数"抄来的优点"更紧急**）
`mcp-bridge.js:478` 硬编码 `setTimeout(30000)` + DSH `mcp-servers.json` `toolCallTimeoutMs:30000`，而 `longshot` 默认 `timeout_ms=120000`、`ocr_image` 默认 `wait=60000` → **这些长操作经 MCP 调用必然先在 bridge/DSH 侧超时** `[推断：常量比对，未实测]`。UIA 8s 与工具 30s 之间也无协商。
**动作**：长操作超时改为按工具声明值向上传递（bridge 取 schema 里的 timeout 字段，DSH 侧 `toolCallTimeoutMs` 按工具覆盖）；先实测 `longshot`/`ocr_image` 经 MCP 是否真的必然超时，再定改法。**验收**：MCP 侧跑一次满 120s 的 longshot 能正常拿回 path。

### P0-2 单一真源 + 消灭静默兼容（抄 A3 A6 C5）
1. 工具清单**代码生成**：一份声明（名字/参数/HTTP 路径/verb 同义表）生成 bridge 注册表、`buildUrl`、`McpToolsJson`、README 工具表 —— 把"加一个工具改 4 处"降到 1 处，顺带根治三张清单漂移与 `McpToolsJson` 陈旧/疑似语法破损
2. **未知参数硬错**：参数名白名单校验，不认识的 key 直接 `ok:false` + `unknown_param` + `did_you_mean`（`key`→`keys`）+ `example` 正确调用
3. **废掉双路径**：`maximize→max` 这类手工同义映射收进声明表；`tray_click` 坐标兜底、`app_restore` 兼容保留，逐条确认代码里到底还有没有分支，废除的就删，不留"手册说废了代码还在"

### P0-3 资源租约与回收（抄 A1 + B2）
1. 托管表 `id → {kind: window|overlay|file|record|process, createdAt, lastTouched, ownerTool}`：`pin_image` 贴图窗、截图/OCR 落盘、`record` 的 ffmpeg、`app_run` 进程全登记，按 id 访问即续期
2. 定时扫：贴图窗超 TTL 自动关；机器生成图按 TTL 清（用户自己留的不动）；**给 `UiRefCache` 加淘汰**
3. 退出前统一清理；`/health` 补 `managed:{windows,overlays,tmpFiles,recording}` + **`logPath` 真身路径**（治"看错 log"）

### P0-4 把 `expect` 从一次性采样改成就绪轮询（抄 B1）
1. `ExpectCheckJson` 改：`Sleep(900)` 扫一次 → **轮询到超时**（可配 `timeout`，默认 8~15s），返回 `{found, waitedMs, samples}`
2. 新增 `wait_for(appear="目标文字" | disappear=..., timeout=, poll=, hwnd=)`：服务端轮询 UIA（大 DOM 走 `PropertyCondition` 精确过滤，**不许触发全树遍历**），**只回最终结果与采样摘要，不把每轮整棵树吐给 Agent** —— 省 token 大户
3. `verify/expect` 从 `ui_click` 独享扩到 `mouse_click`/`keyboard_press`/`ui_set`/`win_manage`/`app_run`
4. 学它把话说在前头：`app_run`/`/win/wait` 返回里明标"只保证窗口存在，不保证内容就绪，内容就绪用 `wait_for`"（抄 `cdp-api.md:46` 那句自我设限）
5. **顺手补 B4**：新增零副作用 `window_state(hwnd)` → `{visible,minimized,maximized,foreground,rect,responsiveMs}`，不 activate 不落盘，替掉"截 1 像素当断言"

### P1-1 手册与经验库重构（抄 C7 + C6 + A5 + C5）
1. `get_skill` **默认只返回索引**（工具速查 + app 清单 + 铁律标题列表），正文按 `app=` / `topic=` 精取；**无命中不再回退整份全文**，改为回"未命中 + 现有分片清单"
2. 闸门从"每轮重置"改成**每会话一次 + 按分片记录已读**（现在每轮烧 ~3 万字符，且只读工具也被拦）
3. 拆 `patterns/<process>.md`（`notepad` / `Weixin` / `ZCode` / `WorkBuddy` / `MiMo` / `msedge`），frontmatter `process`/`aliases`/`updated`/`uiPolicy`（`full` / `name-only` / `banned`），三段正文：**应用特征 / 已验证有效模式 / 已知陷阱**
4. **`uiPolicy` 进服务端**：`/ui/*` 入口先查该 hwnd 所属进程的策略，WorkBuddy 这类直接拒并指路 `tray_click`，**而不是等它烧 8s 超时把全服务拖进熔断**
5. `update_skill` 加 `app=` / `supersedes=` / `as_of=` 参数，**从"追加"改成"按 app 定位分片 + 标记被推翻条目"**，把 `:693` `:722` 那种人工 ⚠️ 警告变成机制字段
6. `get_skill` 末尾列已有 app 分片清单（抄 C6）
7. 写入门槛照抄两句原文进手册：*"只写经过验证的事实，不写未确认的猜测"*、*"经验内容标注发现日期，当作可能有效的提示而非保证正确的事实"*

### P1-2 UIA 隔离粒度：按窗口，别全服务连坐（抄 B8）
`UiCall` 熔断从全服务级改成 **per-process/per-window**；配线程池 + 排队上限替掉"每请求新建线程无上限"。

### P1-3 补 Electron 场景的机器可读定位通道（A4 的结构性解法，它没有、我们自己得补）
`/ocr` 返回 **bbox**（或换支持坐标输出的 OCR 后端）+ 暴露 `find_text(text=)` → 回元素/文字块中心坐标；有它之后，UIA 不可用的 Electron 窗口才不必"让模型估坐标"。**注意别拿它替代 UIA**：ref > name 仍是首选，这是兜底层。

### P2
| 项 | 动作 |
|---|---|
| 本地端口鉴权（**自己的洞**） | 仅绑 127.0.0.1 **零鉴权**：任意本机进程可写剪贴板、注入键鼠、`/app/run` 起程序。junction/硬链接/413 都已修（R8/R9 在案），缺的是调用方身份。方案：启动生成随机 token 写到 `%LOCALAPPDATA%` 下 ACL 仅当前用户的文件，bridge 读它带 `X-WDH-Token`；非回环一律拒 |
| **配置文件明文 key** | `shot-service.json` 明文存 OCR / 百度 / litellm key（打包已知的泄密源，走 `_pkg` 隔离只是绕过、没根治）→ 改走 Windows 凭据管理器或环境变量注入 |
| 危险动作闸门（抄 D1） | `ui_click`/`mouse_click` 命中"发送/删除/提交/支付"类控件名且本轮未确认 → 服务端拦，返回要转述给用户的大白话原话 + 2~3 个选项 |
| 观测性 | 结构化 log（当前 1.6MB 纯文本行）+ 按工具耗时/成功率统计 + `/diag` 扩充；`/health` 加 `logPath` |
| 环境自检 | Session 1 + 管理员 + 已登录三前提改为**开机自检主动告警**（现在 Session 0 只回 503、锁屏期间无提示失败、UIPI 只在被操作时才发现） |
| 派子 Agent 纪律（抄 D2） | 写进 DSH 人设：只写目标不写步骤；避免"搜索/抓取"这类暗示手段的动词 |

---

## 四、联网三层顺带盘一下能力对照

| 它的层 | 它的工具 | 我们对应 | 缺口 |
|---|---|---|---|
| 发现 | WebSearch | `web_search` | 无 |
| 静态 | WebFetch / curl / Jina（转 Markdown 省 token，20 RPM） | `web_fetch` / `pwsh` / `anysearch` | 无实质缺口；"正文转 Markdown 省 token"可在 `web_fetch` 前套一层，长文值得 |
| 浏览器 | CDP Proxy（带登录态、能读 DOM） | 桌面级 UIA 操作（同样带登录态）+ Edge 划词扩展 + **已有的 CDP 直读选区链**（按进程名查调试端口 MiMo 9222 / WorkBuddy 9229 / ZCode 9224，`shot-pick.cs`） | 我们**已有 CDP 能力**，只用在划词取词一件事上。要否扩成通用"读网页 DOM"工具面是独立决策；底子学费已交过（`/json/list` 混进 worker target 烧 6.6s → 必须按 type 过滤 + 350ms 预算 + 失败 target 60s 惩罚缓存） |
| 视频 | seek + screenshot 离散采帧 | `screen_capture` / `record_start` / `ocr_image` / `look_image` | **能力等价**（我们截帧走桌面截图）。两边都**不是"视频理解"**：无音轨、稀疏采样、DRM/Canvas 播放器截不到。"能看视频"是被说坏了 |

---

## 五、只干三件事

1. **P0-1 超时链路**：现有功能里有几个经 MCP 根本用不了，这不是优化是修 bug
2. **P0-2 单一真源 + 参数硬错**：改动面最小，直接消灭我们最常见的失败模式（静默假成功 → Agent 瞎重试 → 60 次 `window_info` 一次没点中那种事故）
3. **P0-4 `expect` 改轮询 + 新增 `wait_for`**：把手册 `:742` 那条已验证有效的"轮询到特征出现"从外部脚本升级成服务端能力，省 token 最狠，且顺手修掉 `expect` 的假阴性

第 4 名留给 **P1-1 的 `uiPolicy` 进服务端** —— 它同时解决三件我们自己的事：每轮 3 万字符的手册税、Electron 一调就把全服务熔断连坐、以及"禁 `/ui/*`"这条铁律至今只靠 Agent 自觉。

---

## 附：本清单两处我方结论的自我更正

1. **B3 单实例仲裁**：初判"缺口"，翻源码 `shot-service.cs:2717-2748` 后确认**已有且比它讲究**（普通实例不接管管理员实例），已就地标注，不列入改造。
2. **C6 手册抽取能力**：初判"我们有 `topic=`，持平"，**判错**。`topic=` 无命中回退整份全文 + 闸门每轮重置，实际比它的 `match-site.mjs` 差一档，已提到 P1-1。

---

## 实施状态（2026-09-19 第 3 轮收尾，全部结论均为实测）

| 项 | 状态 | 实测证据 |
|---|---|---|
| P0-1 超时链路对齐 | 🟡 桥侧完成，**上游未对齐** | 桥按工具单配预算(长截图150s/OCR130s)已生效；但 `C:\Users\oadan\.dsh\mcp-servers.json` 的 `toolCallTimeoutMs` 仍是 30000 —— 经 DSH 调用长活**仍会被上游掐断**。改它需重启 dsh-web(会断当前会话)，待老大点头。故 wait_for 的 timeout 刻意压在 25s。 |
| P0-2a 未知参数硬错 | ✅ 完成 | `textz` → '未知参数，你是不是想写 "text"'；另缺必填/类型错全在发出前拦下 |
| P0-2b 工具清单单一真源 | ❌ 未动 | C# 内嵌 `McpToolsJson`(~33 工具, 疑似 JSON 已坏)仍是第二真源，双路径别名 `maximize→max` 仍在 |
| P0-3 资源租约与回收 | ❌ 未动 | 贴图窗/临时截图/ffmpeg 子进程/UiRefCache 驱逐/`/health` 资源账本 全无 |
| P0-4 就绪轮询 + wait_for + window_state | ✅ 完成并上线 | 正向 `回收站` 84ms/1采样；负向 1625ms/**4采样**；不带定位 14ms 拒(旧版 7s 超时+白烧线程)；测后 `uia.leakedTotal=0` |
| P1-1 手册瘦身 + 按应用拆册 + uiPolicy 进服务端 | ✅ 完成 | 主手册 43,535→4,736 字；get_skill 默认 42,912→**4,803 字(降 88.8%)**；13 个分片；update_skill 支持 app/supersedes/体积守卫；UiaBan 使 WorkBuddy **13ms** 拦下 |
| P1-2 熔断按进程隔离 | ✅ 完成(部分) | 按进程 2 次上限 + 全局 8 兜底已实测；**线程池上限**未做(每连接一线程) |
| P1-3 OCR 坐标/暴露 find_text | ❌ 未动 | |
| P2 端口鉴权 / 明文 key / 危险动作闸门 / 结构化观测 / 环境自检 | ❌ 未动 | 仅 `/health` 提前给出了 logPath/exePath/uia 账本 |

**本轮追加实测推翻的旧结论(已同步进 openmem pinned 种子)**：① "get_skill 闸门每轮重置" 错，实为每会话一次；② "csc 被安全策略拦，必须 explorer.exe 绕道" 错，pwsh 内直接编译 exit=0；③ 计划任务真名 `WinDesktopHelper`，非 `dsh-shot-helper`。

**下一步顺序**：P0-2b 单一真源 → P0-3 资源租约 → 与老大确认是否改 DSH `toolCallTimeoutMs` 并择机重启 → P1-3 OCR → P2。

git: `3a49cd3`(瘦身前快照) → `c88818b` → `2a8600f`；回退分支 `pre-refactor-20260919`；运行目录留 `shot-service.exe.bak-<时间戳>`。
