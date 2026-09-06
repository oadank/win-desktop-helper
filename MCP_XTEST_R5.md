# 第五轮交叉测试（workbuddy → zcode）：语义组 + 剪贴板多媒体

被测：win-desktop-helper v0.0.18 build 09-06 07:58（pid 21876, session 1）
方法：HTTP 直调 + **MCP stdio 真协议**双层各测一遍；所有"选中/复制"类断言一律用**哨兵法**
（`clipboard_set=SENT-XX` → 操作 → `ctrl+c` → `clipboard_get`，值没变即"未生效"）
沙盒：Notepad（测后已杀），用户剪贴板已备份并还原（376 字符）

---

## 一、结论速览

| 组 | 项目 | 结果 |
|---|---|---|
| D | clipboard_get 图片通道 | ✅ 端到端可用（落盘 PNG + url + w/h/bytes 精确） |
| D | /img/ URL 完整性 | ✅ 逐字节一致（801/801、2129/2129，重复 5 次全稳） |
| D | 文件列表通道 filedroplist | ✅ type=files count=2 路径完整 |
| D | 空剪贴板 / 优先级（图>文件>文本） | ✅ type=empty 不报错，优先级正确 |
| D | history 图片条目 + 持久化 | ✅ `[图片] 路径` 入库并落盘 json，4 条无死链 |
| D | **图片去重指纹** | ❌ **🔴 新 bug：同尺寸+3采样点相同 → 新图被静默丢弃** |
| E/F | 三击 triple=1 | ✅ 整行选中，L1/L2/L3 各 3/3 命中（含中文行、数字行） |
| E/F | **三击左边缘** | ❌ **🔴 x=rect.x+2 → 3/3 变成"全选全文"** |
| E/F | 末行下方空白三击 | ⚠️ 选中末行（y=末行+120） |
| C/G | ui_select 合法区间 | ✅ 精确、越界 clamp、start>end 内部交换 |
| C/G | **ui_select 非法参数** | ❌ **🟠 负数/零宽/超界全回 ok:true 但完全未生效（假成功）** |
| A/G | ui_find type-only 过滤 | ❌ 不支持（`need name`），bridge 吞空串 |
| — | ui_find 语义（精确/模糊/大小写/AND） | ✅ 全部正确（A 组 12 用例） |
| — | MCP 层门禁 + 32 工具 + triple 透传 | ✅ 正常；上述两个 bug 在 MCP 层同样复现 |

---

## 二、🔴 BUG-D1：图片去重指纹太弱，新图被静默吞掉

**位置**：`shot-service.cs` `ClipWatcherLoop()` 约 586-593 行
```csharp
long fp = img.Width * 1000003L + img.Height;
fp += b.GetPixel(5, 5).ToArgb();
fp += b.GetPixel(b.Width / 2, b.Height / 2).ToArgb();
fp += b.GetPixel(b.Width - 6, b.Height - 6).ToArgb();
if (fp != lastClipImgFp) { ...入库... }
```

**实测（含正向对照，排除"watcher 死了"）**
| 用例 | 图片 | 结果 |
|---|---|---|
| 基线 | 301×202 深蓝底 + 白字 | 入库 `clip_..._08-58-33-689.png` |
| 变体 | 同尺寸，红像素打在 **(60,100)**（避开三个采样点） | ❌ **未入库**，`/clipboard/get` 能读到但 history 首条仍是旧图 |
| 再变体 | 同尺寸，红像素打在 (30,100) | ❌ 同样未入库 |
| **正向对照** | 同尺寸，红像素打在 **(150,101)=正中采样点** | ✅ **入库** `clip_..._09-07-55-191.png` |

日志侧证据（`shot-service.log`，`clip image captured`）：09:06:51 与 09:06:55 两次 SetImage **没有任何 captured 记录**，而 09:07:55（打在采样点）有一条 → **机制活着，是指纹漏检**。

**为什么是真问题**（不是测试构造的极端场景）：
- 3 点采样对"同一区域连续截屏"几乎必然相同：PixPin 框选同一块、页面局部变化（时间戳/进度条在别处）、鼠标指针位置不同 → 第二张被当成"同一张"，**AI 拿到的是历史旧图**。
- `long fp` 用加法累积 ARGB（值域 ~2^32×3），存在溢出碰撞，属次级隐患。
- 更糟的是**静默**：`/clipboard/get` 走另一条路径能读到新图并存盘，所以"读得到但历史里没有"，排查时极易误判。

**建议修法**：尺寸 + 全图内容哈希。低成本做法：对 `img` 用 `Graphics` 降采样到 16×16 灰度网格再 SHA1（或直接对落盘 PNG 字节流求 MD5，`/clipboard/get` 反正已经存过一次）。另外 `long fp` 用加法累积 ARGB，存在溢出碰撞，属次级隐患。

---

## 二·五、🟠 BUG-D4：图片历史条目不持久化（图片分支漏调 `SaveClipHistory()`）

**定位**：`shot-service.cs` 全文件仅 3 处调用 `SaveClipHistory()`（623 文本入库 / 802 删除 / 810 清空），**ClipWatcherLoop 的图片分支（593-606）入库后没有调用**。

**实测证据**（唯一尺寸 401×303，保证不与旧图撞指纹）
```
I2 SetImage(401x303) -> set
I3 内存 history 首条图片 : clip_2026-09-06_09-19-28-089.png   <-- watcher 已捕获
I3 clipboard-history.json 是否含该条 : False                  <-- 没落盘！
I4 再做一次文本复制后, json 图片条目 : ['...09-19-28-089.png', ...]  <-- 被文本顺带保存
```
**影响**：图片历史只在"下一次文本复制"时才被间接写盘。若期间服务重启/崩溃/自动更新（v0.0.17+ 还有 take-over 重启），**图片条目全丢**——而 Ctrl+Alt+V 历史面板和 AI 读 history 都依赖它。修法一行：图片分支 `Log("clip image captured...")` 后补 `SaveClipHistory();`

---

## 二·六、🟡 BUG-D5：`/clipboard/get` 每次调用都新存一份 PNG（轮询会刷盘）

同一张图（251×151，剪贴板不变）连续 4 次 `/clipboard/get` → `clip_*.png` 文件数 13→17，**新增 4 个**，每次返回不同的 `file`/`url`。
`ClipboardGet()` 里没有类似 watcher 的指纹判断。影响：agent 只要轮询剪贴板（例如"等用户复制完"），`Pictures\Screenshots` 就会被同名图片灌满（本次测试已产生 11 个 801B~2KB 小文件）。
建议：`ClipboardGet` 也走"内容哈希 → 同名则复用已存文件"，或加 `?save=0` 只探测不落盘。



---

## 三、🔴 BUG-D2：三击打在文本区左边缘 → 变成全选（3/3）

**实测**（三行文档 `Alpha Bravo` / `123456` / `中文 测试 段落 末尾`，编辑区 rect.x=548）
| 点击 x | y | 三击结果 |
|---|---|---|
| rect.x+60 | L1 行中 | `'Alpha Bravo\r\n'` ✅ |
| rect.x+60 | L2 行中 | `'123456\r\n'` ✅ |
| rect.x+60 | L3 行中 | `'中文 测试 段落 末尾'` ✅（3/3，稳定） |
| **rect.x+2** | L1 行中 | **`'Alpha Bravo\r\n123456\r\n中文 测试 段落 末尾'`（全选）3/3** ❌ |
| rect.x+60 | 末行+120（空白） | `'中文 测试 段落 末尾'`（选中末行） |

**分层定位**：`MouseClick(button, 3)` 就是 3 次 `mouse_event` DOWN/UP，无自研选择逻辑 → **不是我们造的 bug，是 RichEdit 原生边距命中区行为**（点到左内边距/行号区，三击语义变成"段/全文"）。
**影响**：SKILL 里只写"triple=1 选整行/段"，agent 常从 `rect.x` 起算坐标（我上一轮就踩了），会静默拿到全文 → 后续"替换选中内容"操作会把整篇打掉。
**建议**：① SKILL/工具描述补一句"三击请用 `rect.x+20` 以上、行垂直中线"；② 可选：`/ui/lines` 端点（见下）直接给行矩形，agent 不必猜 y。

---

## 四、🟠 BUG-D3：ui_select 非法参数一律 `ok:true`（假成功）

**矩阵实测**（HTTP 与 **MCP 层各复现一遍**，文本 `Hello_World_Test_0123`）
| 入参 | 响应 | 哨兵校验 | 判定 |
|---|---|---|---|
| 0,5 / 6,11 合法 | ok:true | 得 `Hello` / `World` | ✅ |
| 0,99 超界(end) | ok:true | 得全文 | ✅ clamp 生效 |
| 12,3 start>end | ok:true | 得 `lo_World_`（内部交换） | ⚠️ 静默交换，未报错 |
| **只给 end=5（缺 start）** | `ok:true,"start":0,"end":5` | 得 `Hello` | ⚠️ 静默当 0 |
| **start=abc** | `ok:true,"start":0,"end":5` | 得 `Hello` | ⚠️ 非数字静默当 0 |
| **start=-5,end=5** | `ok:true,"start":-5,"end":5` | **剪贴板未变** | ❌ 假成功 |
| **start=7,end=7 零宽** | `ok:true,"start":7,"end":7` | **剪贴板未变** | ❌ 假成功 |
| **start=999,end=1000 超界** | `ok:true,"start":999,"end":1000` | **剪贴板未变** | ❌ 假成功 |
| **start=-9,end=-5 全负** | `ok:true` | **剪贴板未变** | ❌ 假成功 |
| 缺 name / name 不存在 / i=99999 / title 不存在 | `ok:false,"element not found (bad i/name/window)"` | 未变 | ✅ 报错正确 |

**撤回我上一轮的说法**：上轮 C1f/C1g 我说"复制走的是上一次残留选择"——哨兵法重测后更正：**根本没生效**，`ok:true` 是假的。上一轮之所以看到 `ha_Beta`，是那条测试串里我自己没重置哨兵。

**建议**：`start`/`end` 缺省或非法 → `ok:false` 明确报错；零宽 → 要么真做"光标定位"语义（返回 `collapsed:true`），要么报错；越界 → 要么 clamp 后回 `clamped:true` 让调用方知道实际选中范围，别原样回显 `-5`。

---

## 五、🟡 语义组两处不稳（低成本可修）

1. **`ui_find` 不支持"只按类型过滤"**：`{"title":"Notepad","type":"Document"}`（不带 name）→ `{"ok":false,"error":"need name"}`。
   且 `mcp-bridge.js` 是 `if (a.name) qs.push(...)` —— **空串会被静默丢掉**，调用方以为传了 name 实际没传，报错还显示 `need name`，链路里丢信息。带 name+type 的 AND 过滤是正确的（M3d：`name=文本编辑器&type=Edit` → count=0）。
   建议：允许 type-only；bridge 改用 `a.name !== undefined`。
2. **元素 `i` 索引跨树不稳定**：同一 Notepad，A 轮 Document 是 `i=12`（报 `element has no native handle`），本轮 `ui_find` 直接给 `i=1`。索引是"当次遍历的序号"，不持久——但工具描述没写，agent 会缓存。
   建议：`ui_find`/`ui_readall` 描述加一句"i 仅在本次响应内有效，跨调用必须重新查"。
3. **（可选）缺 `/ui/lines`**：想按"第 N 行"操作时无端点，我只能用三击阶梯探针 + 二分把行几何量出来（实测量到行高≈27px）。若给 TextPattern 的 `GetLineRanges` 开个端点，agent 就能直接用，不必猜 y。

---

## 六、🟠 BUG-D6：`/ui/tree`、`/ui/find` 在大 DOM（Electron）窗口上无限期挂死，且 `max` 不生效

**现象**（v0.0.18 build 09-06 09:37 复测，服务全程存活）
```
/ui/tree?title=WorkBuddy&max=30    -> 25.0s 零字节超时 (http=000)
/ui/tree?title=WorkBuddy&max=150   -> 25.0s 零字节超时
/ui/find?title=WorkBuddy&name=输入 -> 10.0s 零字节超时
--- 对照组 ---
/ui/tree?title=ZCode&max=150       -> 0.56s  ✅ (Electron 类, 树较小)
/ui/tree?title=Microsoft&max=60    -> 0.36s  ✅ (Edge / Chromium)
/ui/tree?title=文件资源管理器&max=60 -> 0.04s  ✅ (Win32)
```
挂死期间 `/health`、`/active` 正常（0.02s）→ **不是服务崩，是这些 handler 线程永久阻塞**（所以日志里连 `req 200/500` 都没有，请求"消失"）。

**根因**（`shot-automation.cs:389` / `:748`，另有 448/469/487 同型）
```csharp
var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition); // ← 先物化整棵树
int n = Math.Min(all.Count, max);                                       // ← 之后才截断
```
`FindAll` 会把**全部后代节点一次性拉平**（每个元素都要跨进程走 UIA  marshalling），`max` 只限制后面的循环次数，**完全不能限制遍历成本**。Electron 这种几万节点的窗口 → 单次 `FindAll` 就是几十秒到无限期。而且整条路径上没有任何超时/取消。

**修法建议**
1. 换 `TreeWalker`（`ControlViewWalker`）**逐层深度优先 + 计数即停**，到 `max` 就 break —— 这样 `max` 才真的有界。
2. `ui_find` 更该用**条件过滤**：`new PropertyCondition(NameProperty, name)` 交给 UIA 服务端过滤（或 `RawViewWalker` 剪枝），别"全量拉平再自己比字符串"。
3. 加硬超时：`UiTree/UiFind` 用 `Thread.Join(timeoutMs)` 包一层（`ClipboardGet` 已经是这个写法），超时返回 `{"ok":false,"error":"uia timeout, try window_info/shot instead"}`，让调用方有降级提示而不是干等。
4. 附带解释：我上一轮报的"元素 `i` 索引跨树不稳定"——`i` 就是这里的 `FindAll` 平铺顺序，DOM 一变顺序就变，同一个输入框在我操作期间从 `i=637` 漂到 `i=665/644/659/664`（当场拍到 637 已变成"08:00"时间戳文本）。建议文档明确"i 仅本次响应内有效"，或改用稳定句柄/`AutomationId`。

---

## 七、我这边给自己记的教训（已写进共享 SKILL.md）

- **`/img/` URL 一度被我误判为"0 字节截断"**：实际是**我的测量错**——二进制按 UTF-8 解码统计、以及 Git Bash 下 `curl -o /c/...` 路径没落盘。用 python socket 收原始字节后 801/801、2129/2129 全对。**验二进制请用字节数，别解码。**
- 已按 SKILL 铁律②把本轮全部结论用 `update_skill` 等价的追加方式写回 `SKILL.md`（`GET /guide` 现 15652 字节，含新小节），全体 agent 共享。
- 上一轮"用 UIA 后台调用代替人肉点击"的路线错误已纠：三击/选中这类**必须物理坐标 + 哨兵校验**，返回值不可信（本轮 D3 就是证据）。

---

## 附：测试脚本（留在 %TEMP%）
`xd1.py`(D组剪贴板多媒体) `xd2.py`(E组三击初测) `xd3.py`(F组行几何探针/边缘复现) `xd4.py`+`xd5.py`(指纹漏检+正向对照) `xd6.py`(G组 select 参数矩阵) `xd7.py`(H组 /img 字节完整性) `xd8_mcp.js`+`xd9_mcp.js`(MCP stdio 层复现)
