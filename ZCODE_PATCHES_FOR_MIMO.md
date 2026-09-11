# ZCode 侧补丁清单（仓库重置后需 MiMo 加回的 zcode 侧改动）

> 基线 = 当前 clone（0b10692，9月9日推送状态）。以下全部是**未 push 就丢了**的 zcode 侧改动，
> 按 MiMo 重加自身补丁时一并加回。代码块可整段粘贴（zcode 会话记录逐字复现）。

---

## 补丁 1 — shot-service.cs：tray_click 键盘流防呆三件（Sep 9 考核实锤修复）

### 1a. FindTrayButton 加第三参 `allowTaskbarFallback`

签名改为：
```csharp
static System.Windows.Automation.AutomationElement FindTrayButton(System.Windows.Automation.AutomationElement scope, string name, bool allowTaskbarFallback)
```
- TrayClick 主路径调用改 `FindTrayButton(tray, name, false)`（禁任务栏按钮兜底），`via = "tray-icon"`
- mode=uiaclick 旧路径调用改 `FindTrayButton(fly, name, true)`（显式模式保留兜底）
- 函数体内非托盘区命中：`if (allowTaskbarFallback && fallback == null) fallback = el;`

背景：缩托盘应用的任务栏按钮仍在且同名（x≈619），老兜底摸到它=把窗点成最小化还报 ok（考核任务1实锤）。

### 1b. 新增 `KbdFocusMatch` + `FocusName24`（放在 FindTrayButton 前面）

```csharp
// Win+B 键盘流焦点判定: 名字含 name 且**跳过任务栏程序按钮陷阱位**。
// 陷阱: 窗口缩托盘后任务栏按钮仍存在(WorkBuddy x=619 同名), 焦点扫到就 Enter=隐藏/最小化切换而非唤回。
static string KbdFocusMatch(System.Windows.Automation.AutomationElement fe, string name)
{
    if (fe == null) return null;
    string n = ""; try { n = fe.Current.Name ?? ""; } catch { }
    if (n == "" || n.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) return null;
    try
    {
        string cls = fe.Current.ClassName ?? "";
        var r = fe.Current.BoundingRectangle;
        bool traySide = cls.StartsWith("SystemTray") || r.X > 1600;
        if (!traySide && r.Y > SystemInformation.VirtualScreen.Bottom - 80) return null;
    }
    catch { }
    return n;
}

static string FocusName24(System.Windows.Automation.AutomationElement fe)
{
    try { string s = fe.Current.Name; if (s == null) return ""; return s.Length > 24 ? s.Substring(0, 24) : s; }
    catch { return "?err"; }
}
```

### 1c. TrayClick 三段扫描循环全部换用 KbdFocusMatch

三段（Tab4 任务栏段 ←扫 / Shift+Tab 托盘段 →扫 / 溢出层 ←扫）里原来的
`if (fn2.IndexOf(name, ...) >= 0) { fFinalName = fn2; break; }`
全部替换为：
```csharp
string mHit = KbdFocusMatch(fe, name);
if (mHit != null) { fFinalName = mHit; break; }
```
（第一段保留 focusTrail 记录行；托盘段加 `if (step < 10) focusTrail.Append("T"+step+":["+FocusName24(fe)+"] ")`；溢出段同款用 "O" 前缀）

### 1d. 成功/失败日志 + via 改名

- 成功 Enter 后加：`Log("tray kbd ok: path=" + (scanPath == "" ? "taskbar段" : scanPath) + " name=[" + fFinalName + "]");`
- `via = "tray-icon"`（原 "taskbar"）
- 失败返回保持 ok:false 带焦点轨迹，**不许静默回退坐标点击**

### 1e. AppRestore 的 double 残留

```csharp
steps.Add("tray_click '" + trayName + "' Enter单击 -> " + TrayClick(trayName, "left", false)); // double=1 会 Enter×2 把刚显示的窗再藏回去
```

---

## 补丁 2 — shot-service.cs：mouse_click 落点回报 at + front=1 前台拒点

### 2a. 新增 LandJson（放 UipiCheck 前面）

```csharp
static string LandJson(IntPtr root, out bool front)
{
    front = false;
    if (root == IntPtr.Zero) return "{\"process\":\"\",\"title\":\"\",\"front\":false}";
    int pid = 0; GetWindowThreadProcessId(root, out pid);
    string proc = ""; try { proc = Process.GetProcessById(pid).ProcessName; } catch { }
    StringBuilder sb = new StringBuilder(256);
    string title = GetWindowTextW(root, sb, 256) > 0 ? sb.ToString() : "";
    front = root == GetForegroundWindow();
    return "{\"hwnd\":" + root.ToInt64() + ",\"pid\":" + pid + ",\"process\":\"" + JsonEscape(proc) + "\",\"title\":\"" + JsonEscape(title.Length > 60 ? title.Substring(0, 60) : title) + "\",\"front\":" + (front ? "true" : "false") + "}";
}
```

### 2b. /mouse/click 处理器接入（UIPI 预检之后）

```csharp
bool wantFront = q.ContainsKey("front") && q["front"] == "1";
bool landFront = false; string landJson = "";
if (hasXY)
{
    IntPtr landRoot = GetAncestor(WindowFromPoint(new System.Drawing.Point(x, y)), 2);
    if (landRoot == IntPtr.Zero) landRoot = WindowFromPoint(new System.Drawing.Point(x, y));
    landJson = LandJson(landRoot, out landFront);
}
if (uipi != null) { ...原样... }
else if (wantFront && hasXY && !landFront)
{
    code = 409; body = "{\"ok\":false,\"error\":\"落点窗口不是前台 —— 已拒点(front=1)…\",\"at\":" + landJson + "}";
}
else { ...原点击流程...，响应末尾加 (landJson != "" ? ",\"at\":" + landJson : "") }
```
背景：浏览器最小化时 7 连击盲点、win+方向打错窗——两案同源（对后台窗动手没人拦）。

### 2c. McpToolsJson 的 mouse_click 条目

description 加：返回 at=落点顶层窗口(process/title/front)，front=1 严格模式落点非前台直接拒点；inputSchema 加 front 属性。

---

## 补丁 3 — shot-pick.cs：剪贴板取词链 PickViaClipboard（Sep 10 夜，老大拍板的 STranslate 方案）

**注意已知边界**：ZCode(Chromium) 对合成 Ctrl+C 无视——keybd_event 版和 SendInput VK+扫描码版实测都 no change。这条链在 ZCode 不出字（静默正确），但在尊重合成输入的应用可用。作为 UIA 失败后的兜底保留。

### 3a. 新增函数（放 PickCaptureWork 前面）

```csharp
static string PickViaClipboard()
{
    string original = "";
    uint seq0 = 0;
    try { original = System.Windows.Forms.Clipboard.GetText() ?? ""; } catch { }
    try { seq0 = GetClipboardSequenceNumber(); } catch { }

    // 清残留修饰键（STranslate：不清=模拟复制失败主因）
    keybd_event(0xA2, 0, 2, UIntPtr.Zero); keybd_event(0xA3, 0, 2, UIntPtr.Zero);
    keybd_event(0xA4, 0, 2, UIntPtr.Zero); keybd_event(0xA5, 0, 2, UIntPtr.Zero);
    keybd_event(0x5B, 0, 2, UIntPtr.Zero); keybd_event(0x5C, 0, 2, UIntPtr.Zero);
    keybd_event(0xA0, 0, 2, UIntPtr.Zero); keybd_event(0xA1, 0, 2, UIntPtr.Zero);

    // Ctrl+C: VK+扫描码一起给（纯 VK 无扫描码会被 Chromium 无视）
    keybd_event(0x11, 0x1D, 0, UIntPtr.Zero);
    keybd_event(0x43, 0x2E, 0, UIntPtr.Zero);
    keybd_event(0x43, 0x2E, 2, UIntPtr.Zero);
    keybd_event(0x11, 0x1D, 2, UIntPtr.Zero);

    bool changed = false;
    int t0 = Environment.TickCount;
    while (Environment.TickCount - t0 < 500)
    {
        System.Threading.Thread.Sleep(10);
        try { if (GetClipboardSequenceNumber() != seq0) { changed = true; break; } } catch { }
    }
    if (changed) System.Threading.Thread.Sleep(30);
    string now = "";
    try { now = System.Windows.Forms.Clipboard.GetText() ?? ""; } catch { }
    if (changed || now != original || string.IsNullOrEmpty(original))
    {
        string t = (now ?? "").Trim();
        return t.Length <= 1 ? "" : t;
    }
    return "";
}
```

### 3b. PickCaptureWork click 分支接线（PickUiaDirect 之后、bbox/OCR 之前）

```csharp
if (string.IsNullOrWhiteSpace(text) && !PickIsTerminal(fgAt))
{
    int ct = Environment.TickCount;
    string clip = PickViaClipboard();
    if (!string.IsNullOrWhiteSpace(clip)) { text = clip; how = "clip"; }
    Log("pick(click): clip " + (Environment.TickCount - ct) + "ms chars=" + (text ?? "").Length +
        " | " + (string.IsNullOrWhiteSpace(text) ? "(no change)" : PickOneLine(text)));
}
```

红线：终端永不发 Ctrl+C（^C 中断）——上游 terminal skip 已挡，函数内再加一层。剪贴板**不还原**（老大拍板：划完能粘是特性）。

---

## 补丁 4 — SKILL.md

- 铁律 2 改写：「状态每次重新采样（桌面是共享的）」——人在同一台机器实时操作，每轮 /active+listall+截图重采样再出计划；状态不符只说"变了"禁编机制理论；焦点键发前必验 front=目标
- 铁律 6 更新：坐标兜底废除+陷阱跳过+诊断链（window_info not found → listall visible:false → tray_click → activate）+ 成功留 tray kbd ok 日志
- 踩坑表加「贴位首选=Win+方向键」行（发键前验 front、贴靠助手按 Esc、应用最小高钳制）
- 踩坑表加「点后台/最小化窗口=顶窗或落空」行（at.front / front=1）
- tray_click/mouse_click 工具描述与 SKILL 对齐

---

## 补丁 5 — mcp-bridge.js

- mouse_click：description 加 at/front 说明；inputSchema 加 `front: { type: 'number' }`
- tray_click：description 改为「托盘唤回(Win+B 键盘流)…Enter 单击…勿 double=1…找不到如实 ok:false 带轨迹」

---

## 已确认无需重加（9月9日基线里已有）

- R9-1 硬链接拦截（GetHardLinkCount）✓
- PressCombo / SendInput 基建 ✓
- pickBrowserNames 让位逻辑（基础版）✓
- /find_text 端点（9a6bf2a）✓

## 补丁 6 — Edge MV3 扩展 + /pick-inject（老大已批准移植，2026-09-11）

Edge 里划词出球/翻译选中的部分——老大已验收好用，**不随聊天窗划词搁置**，照原样移植回：
- `extension/` 目录整个（manifest.json / content.js / background.js / config.js）
- helper 侧 `POST /pick-inject` 端点（shot-pick.cs）+ **CORS 预检处理**（扩展 content script 直连需要 OPTIONS 204；MV3 background 一般不发 Origin 也要兜底）
- 提醒：content script 发 送 POST 到 127.0.0.1:18800 需 host_permissions；用户侧加载方式=开发者模式加载解压目录

## MiMo 自身要重加的（它知道，列这里防遗漏）

- 聊天窗取词链全部（UIA 直查+CacheRequest、WinOCR、高亮抠图、常驻 UIA FocusChanged 订阅、空球退役、紧凑点、探针数据）——**这批搁置**：老大判定截图+掩码+OCR 拼运气思路不对，等参考成熟开源方案重新设计后再动
- MCP_XTEST_UIA_WARMUP.md / MCP_XTEST_PICK_FINAL.md / MCP_XTEST_RUNTIME10.md 测试报告
- _scratch/ 收纳、ZCODE_HANDOFF 刷新

---

## 补丁 4 详单 — SKILL.md 丢失内容全文（直接粘回对应位置）

### 4.1 铁律 2 整条替换为（共享桌面重采样，zcode 9/9）

```markdown
2. **状态每次重新采样（桌面是共享的）**：句柄会变（微信实测 920778→1968942）、下标会漂、窗口会被盖住关闭；**更要命的是人也在同一台机器上实时操作**（藏窗进托盘/切焦点/关窗——实测 WorkBuddy"自己缩托盘"三次全是老大手操，AI 编了"自动收起"理论被当场打脸），两次调用之间的任何记忆都可能已过期。每轮开始必须先看当前状态（/active + listall + 截图）再出计划，禁止拿上轮记忆当输入
```

### 4.2 铁律 6 整条替换为（tray 让位+诊断链，zcode 9/9）

```markdown
6. **"看得见但点不动" / "压根没窗口" → `tray_click(name=应用名, double=0)`**：实现=**Win+B 键盘流**（Win+B 聚焦托盘 → Tab 切区段 → ←/→ 扫描主区+溢出层全部图标 → Enter 单击；图标被隐藏时自动展开溢出层继续扫）。**坐标兜底已废除(2026-09-09 考核实锤)**：缩托盘应用的**任务栏按钮仍在且同名**，旧 fallback 摸到它=把窗点成最小化还报 ok:true；现在扫描自动跳过"底部任务栏带内非托盘侧"焦点元素，成功留日志 `tray kbd ok: path=…`，真找不到**如实 ok:false 带三段焦点轨迹**（Electron 托盘名偶发读空 → 重试一次；名字不对用 tray_list 核对）。诊断链：`window_info not found` → `win/listall?pid=` 看 `visible:false`（=缩托盘；`win/restore` 只管最小化管不了隐藏）→ `tray_click` 唤回 → `activate` 置前。`win_manage activate` 唤回的 Electron 窗经常冻结，只当置前手段不当恢复手段；应用真退了 → `app_run` 再启
```

### 4.3 踩坑速查表加两行（zcode 9/10）

```markdown
| **贴位首选=Win+方向键(原生吸附)** | `keyboard_press(keys="win+left")` 吸左半（win+right 右半）；接 `win+up` 升左上 1/4（再按=最大化；win+down 反向）。**铁则：Win+方向作用于当前焦点窗，发键前必须 /active 验 front=目标**（实测打错窗改了别人布局）；弹出的**"贴靠助手"会抢焦点**——**按 Esc 关掉再发下一键（老大指路：取消类交互一律 Esc 优先，点"空白处"有误点风险）**；应用最小高度连原生吸附也钳（1/4=690 被 WorkBuddy 钳成 751），属应用约束非工具问题 |
| **点后台/最小化窗口=顶窗或落空** | mouse_click 返回 `at`=落点顶层窗口(process/title/front)，**at.front=false 说明点在后台窗上**（浏览器被最小化时 7 连点全落空的实案）；必须命中的点击加 `front=1`（落点非前台直接 409 拒点）。win+方向键不走此校验，发前仍须 /active 验 front |
```

### 4.4 workbuddy 的「记事本全链实测坑」（2026-09-09，workbuddy 9/9 写回，4 条全文）

```markdown
1. **粘贴文本到记事本：禁用 win_manage activate 置前**。activate 返回 `via:alt`（Alt 键置前），会把焦点带去菜单栏，随后 `ctrl+v` 落不进文档（存出 0 字节空文件，标题保持「无标题」）。正确做法：`mouse_click` 点文档 Edit/Document 中心聚焦，再 `clipboard_set`+`ctrl+v`。验证粘贴：选中全文 `ctrl+a` → `ctrl+c` → `clipboard_get` 读回，标题变 `*首行内容 - Notepad`（星号=未保存）即证明落地。
2. **Win11 记事本「另存为」文件名框：ui_set 是假成功**。`ui_set(ref=文件名Edit, value=完整路径)` 会返回 `verified:true`，但那是只校验了 Edit 控件的 ValuePattern，对话框内部并不读它——保存时仍用默认名（实测存成 `无标题.txt`）。正确做法：点文件名框聚焦 → `ctrl+a` 清空 → `clipboard_set(完整路径)` → `ctrl+v` 真写入；`ui_read` 该 Edit 的 `value` 可确证。
3. **保存按钮点击：UIA invoke 与 verify 截图都不可信**。`ui_click(ref=保存, verify=1)` 走 invoke 返回 `changed:false`（假成功）；改 `mode=coord` 真实鼠标点仍可能 `changed:false` 且 `after:空`——因为对话框截图机制取不到图（before 哈希还会和上一次相同）。**以磁盘文件是否出现/字节数正确为权威判据**，别信 verify 字段。
4. **贴靠助手必弹且延迟**：`win+right`/`win+down` 吸附后「贴靠助手」(explorer 进程) 常延迟弹出抢焦点，`active_window` 才看得到。每次发方向键后都复查 `active_window`，若变「贴靠助手」就 `esc` 关掉再发下一键。
```
