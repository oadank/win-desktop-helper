using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 划词悬浮球 (2026-09-07) — 取代豆包的"选中即弹小点"
// 交互契约 (老大定): 选中文字 → 光标旁出现小圆点; 鼠标**划过不展开**, 停上去计时 300ms 才展开工具条;
// 直接点小点 = 立即翻译出卡片。全程不抢焦点(所有窗体 ShowWithoutActivation + WS_EX_NOACTIVATE)。
//
// 取词两级降级:
//   1) UIA TextPattern.GetSelection() — 无副作用, 浏览器/Office/Explorer 可用
//   2) 剪贴板法 — 备份剪贴板 → Ctrl+C → 读 → 还原; 期间置 pickBusy 让剪贴板监听跳过(不污染历史)
// 钩子回调必须 1ms 内返回(本机 LowLevelHooksTimeout=1ms), 所以回调只记坐标, 真活在专属 STA 线程干。
partial class ShotService
{
    const int PICK_DOWN = 0x0201, PICK_UP = 0x0202, PICK_MOVE = 0x0200;   // DOWN / UP / MOUSEMOVE
    const int PICK_RBUTTONDOWN = 0x0204, PICK_RBUTTONUP = 0x0205, PICK_WHEEL = 0x020A;

    // GetForegroundWindow / GetWindowThreadProcessId 已在 shot-service.cs 声明, 此处复用

    // 用户物理输入活动: 剪贴板法发 Ctrl+C 前用它判断"用户是否已经去干别的了"
    static volatile int pickUserAct;   // TickCount of last physical mouse/button/wheel event seen by hook
    const int PICK_MOVE_MIN2 = 144;                   // 位移平方 <144(<12px) = 单击, 不算划选
    static int pickEnabled = 1;
    static Control pickSync;
    static bool pickDownFlag;
    static int pickX0, pickY0;
    static string pickSel = "";
    static int pickBusy = 0;
    static int pickLastX, pickLastY;
    static Form pickDot, pickCard;

    // ---- 生命周期: 专属 STA 线程(剪贴板要求) ----
    static void PickInit()
    {
        try
        {
            Thread t = new Thread(new ThreadStart(PickLoop));
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }
        catch (Exception ex) { Log("pick init err: " + ex.Message); }
    }

    static void PickLoop()
    {
        try
        {
            pickSync = new Control();
            pickSync.CreateControl();
            System.Windows.Forms.Timer tk = new System.Windows.Forms.Timer();
            tk.Interval = PICK_TICK;
            tk.Tick += delegate { PickTick(); };
            tk.Start();
            pickTimer = tk;
            Log("pick: STA picker thread ready (划词悬浮球已就绪)");
            Application.Run();
        }
        catch (Exception ex) { Log("pick loop err: " + ex.Message); }
    }

    // 鼠标钩子回调入口: 只记坐标 + 纯内存判定命中矩形, 立刻返回(1ms 限制)
    // 小点/工具条的 hover 与点击全部由这里驱动 —— WinForms 的 MouseEnter/MouseClick 在
    // WS_EX_NOACTIVATE|TOOLWINDOW 的免激活窗上实测收不到(点了没反应), 低级钩子能看到全局鼠标消息。
    static void PickOnMouse(int msg, int x, int y)
    {
        if (pickEnabled != 1) return;
        try
        {
            if (msg == PICK_DOWN || msg == PICK_UP || msg == PICK_RBUTTONDOWN ||
                msg == PICK_RBUTTONUP || msg == PICK_WHEEL || msg == PICK_MOVE)
                pickUserAct = Environment.TickCount;
            if (msg == PICK_DOWN)
            {
                pickDownFlag = true; pickX0 = x; pickY0 = y;
                pickDownOnDot = PickHit(PickDotRect, x, y);
                pickDownOnBar = PickHit(PickBarRect, x, y);
                pickDownOnCard = PickHit(PickCardRect, x, y);
                pickDownOnCardLive = pickDownOnCard;
                // 问AI 提问框(能拿焦点的正当窗): 框内点击=正常打字/编辑, 完全放行给 WinForms
                pickDownOnAsk = PickHit(PickAskRect, x, y);
                bool askWasOpen = PickAskRect != null;
                if (pickDownOnAsk) { pickDownOnAskLive = true; return; }
                if (pickDownOnCard)
                {
                    int dx0 = x, dy0 = y;
                    Control sd = pickSync;
                    if (sd != null && sd.IsHandleCreated)
                        sd.BeginInvoke(new MethodInvoker(delegate { PickCardDragStart(dx0, dy0); }));
                }
                // 2026-09-07 老大: "鼠标点击的时候别消失" —— 按下不再一律 dismiss。
                //   谁收掉交给 UP 判定: 划词会重画(ShowPickDot 内部先 dismiss), 单击只尝试取词;
                //   两者都没命中就让元素按 4s/60s 超时自然收起, 点别处不再"一碰就没"。
                if (askWasOpen) pickDownOnAskLive = true;   // 提问框刚被这一下关掉: 本轮手势不再触发二次取词
                return;
            }
            if (msg == PICK_MOVE)
            {
                if (pickDragging || pickDownOnCard) { pickCurX = x; pickCurY = y; }
                Interlocked.Increment(ref pickTrackSeen);
                return;
            }
            if (msg != PICK_UP) return;
            if (!pickDownFlag) return;
            pickDownFlag = false;
            // 本轮手势的"刚关掉提问框 / 刚在卡片上按住过"闩锁在这里一次性取走并清零。
            // 以前是留到下一次才清, 于是"点一下卡片正文/点一下关掉提问框"之后, 紧接着那次真正的
            // 划词会被误当成重复取词吞掉(要划第二次才出小点)。
            bool askJustClosed = pickDownOnAskLive; pickDownOnAskLive = false;
            bool cardWasTouched = pickDownOnCardLive; pickDownOnCardLive = false;
            int dx = x - pickX0, dy = y - pickY0;
            bool isClick = dx * dx + dy * dy < PICK_MOVE_MIN2;
            int ux = x, uy = y;
            Control s = pickSync;
            if (s == null || !s.IsHandleCreated) return;

            if (isClick)
            {
                // 提问框里单击 = 正常放光标打字, 不触发任何取词/展开
                if (pickDownOnAsk) { pickDownOnAsk = false; return; }
                if (pickDownOnDot) { s.BeginInvoke(new MethodInvoker(delegate { PickDotActivated(); })); return; }
                if (pickDownOnBar)
                {
                    string act = PickBarActionAt(ux, uy);
                    if (act.Length > 0) { s.BeginInvoke(new MethodInvoker(delegate { PickBarFire(act); })); return; }
                    return;   // 工具条空白处: 什么也不做(不 dismiss, 等 4s 超时)
                }
                if (pickDownOnCard) { int px0 = pickX0, py0 = pickY0; s.BeginInvoke(new MethodInvoker(delegate { PickCardUp(px0, py0, ux, uy); })); return; }
                // 差一点没点到小点/工具条(26px 的目标手抖就 miss, 老逻辑一 miss 就整组消失 =
                // 老大说的"鼠标点击的时候它消失"): 容差内当作点中小点 -> 展开工具条。
                if (PickNear(PickDotRect, ux, uy, PICK_TOL) && !pickExpanded)
                {
                    s.BeginInvoke(new MethodInvoker(delegate { PickDotActivated(); }));
                    return;
                }
                if (PickNear(PickBarRect, ux, uy, PICK_TOL) || PickNear(PickCardRect, ux, uy, PICK_TOL) ||
                    PickNear(PickAskRect, ux, uy, PICK_TOL)) return;   // 贴着元素: 不动它
                // 这一下是"顺手关掉提问框"(DOWN 时它还开着): 不再二次触发取词
                if (askJustClosed) return;
                // 单击别处: 也弹悬浮球(见 PickHandle click 模式 —— 只读 UIA 现有选区, 绝不发 Ctrl+C)
                s.BeginInvoke(new MethodInvoker(delegate { PickHandle(ux, uy, true); }));
                return;
            }
            if (pickDownOnCard)
            {
                int dx0 = pickX0, dy0 = pickY0, ux2 = ux, uy2 = uy;
                s.BeginInvoke(new MethodInvoker(delegate { PickCardUp(dx0, dy0, ux2, uy2); }));
                return;
            }
            if (pickDownOnAsk) { pickDownOnAsk = false; return; }               // 提问框里拖选文字: 正常编辑, 不触发取词
            if (askJustClosed) return;                                          // 提问框刚被这一下关掉: 本轮手势不取词
            if (cardWasTouched) return;                                         // 刚在卡片上按住过: 不重复取词
            s.BeginInvoke(new MethodInvoker(delegate { PickHandle(ux, uy); }));
        }
        catch { }
    }

    // ---- 命中矩形(物理像素, 由 UI 线程更新; 钩子线程只读) ----
    static int[] PickDotRect, PickBarRect, PickCardRect;
    static bool pickDownOnDot, pickDownOnBar, pickDownOnCard;
    static bool pickDownOnAsk, pickDownOnAskLive;   // 问AI 提问框(可获焦): 框内点击/划选不触发 dismiss 和二次取词
    static Form pickAskWin;
    static int[] PickAskRect;
    static volatile bool pickOverDot, pickOverBar, pickOverCard;
    static long pickTrackSeen;         // 钩子收到过的 MOVE 数(诊断: 真实拖动有没有 MOVE 进来)
    static int pickHoverSince = -1;    // 指针开始停在小点的 TickCount(-1=不在)
    static int pickShownAt;            // 当前元素显示时刻(自动收起计时起点)
    static bool pickExpanded;          // 本次划词已展开过工具条(防 UP+hover 双触发)
    static bool pickDownOnCardLive;    // 本次按下落在卡片上 -> 期间的拖选不再触发取词
    static int pickDragX, pickDragY;   // 卡片拖动: 抓取时 光标-窗口原点
    static int pickCurX, pickCurY;     // 拖动期间光标实时位置(钩子 MOVE 写, 心跳读)
    static bool pickDragging;
    static Form pickBarWin;
    static volatile bool pickCardBusy;
    const int PICK_TICK = 200;
    const int PICK_TICK_DRAG = 15;       // 拖动期间心跳加密到 15ms, 不然"一步一卡"不跟手
    static System.Windows.Forms.Timer pickTimer;
    static string pickBarActs = "";
    static int[] pickBarBtnXs;


    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    // Show() 之后必须显式置顶, 否则免激活窗会被下层/后激活的窗压住
    static void PickTopMost(Form f)
    {
        try
        {
            if (f == null || f.IsDisposed || !f.IsHandleCreated) return;
            SetWindowPos(f.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log("pick topmost err: " + ex.Message); }
    }

    // 每 4 轮(约 800ms)复查一次置顶, 防止别的程序抢置顶把卡片压下去
    static int pickRaiseTick;
    static void PickRaise()
    {
        if (++pickRaiseTick % 4 != 0) return;
        PickTopMost(pickDot);
        PickTopMost(pickBarWin);
        PickTopMost(pickCard);
    }

    static bool PickHit(int[] r, int x, int y)
    {
        if (r == null || r.Length != 4) return false;
        return x >= r[0] && x < r[2] && y >= r[1] && y < r[3];
    }

    const int PICK_TOL = 8;   // 悬浮球只有 26px, 手抖一点就 miss —— 容差内当作点中

    // 点是否落在矩形内或紧贴其四周(外扩 tol)
    static bool PickNear(int[] r, int x, int y, int tol)
    {
        if (r == null || r.Length != 4) return false;
        return x >= r[0] - tol && x < r[2] + tol && y >= r[1] - tol && y < r[3] + tol;
    }

    // 工具条按钮命中: 用 ShowPickBar 时记下的按钮 x 区间换算回物理坐标
    static int PickBarActionIndex(int x, int y)
    {
        int[] r = PickBarRect;
        if (r == null || x < r[0] || x >= r[2] || y < r[1] || y >= r[3]) return -1;
        int[] xs = pickBarBtnXs;
        if (xs == null) return -1;
        for (int i = 0; i + 1 < xs.Length; i += 2)
            if (x >= xs[i] && x < xs[i + 1]) return i / 2;
        return -1;
    }

    static string PickBarActionAt(int x, int y)
    {
        try
        {
            int i = PickBarActionIndex(x, y);
            if (i < 0) return "";
            string[] acts = (pickBarActs ?? "").Split(',');
            return i < acts.Length ? acts[i] : "";
        }
        catch { return ""; }
    }

    static void PickHandle(int x, int y) { PickHandle(x, y, false); }

    // click=true = 用户只是**单击**(没有划选)。2026-09-07 老大要的"点击时也弹出悬浮球"。
    // 只允许无副作用取词: ①UIA 拿光标所在的那个词(Word 单元) ②退一步读现有选区。
    // **绝不发 Ctrl+C**: 单击不产生选区, 这时全局复制 = 把用户刚点中的输入框里的东西/别处内容当"词"抓走。
    // 取不到词就什么都不动(元素交给 4s/60s 超时自然收起), 而不是"一碰就没"。
    static void PickHandle(int x, int y, bool click)
    {
        if (Interlocked.Exchange(ref pickBusy, 1) == 1) return;
        try
        {
            if (click)
            {
                // 结果卡片/工具条正开着: 用户可能还在看/按按钮, 别用新小点把它顶掉
                if (pickCard != null || pickBarWin != null) return;
                string w = "";
                try { w = PickWordAtPoint(x, y); } catch { }
                if (string.IsNullOrWhiteSpace(w)) { try { w = PickTextUia(x, y); } catch { } }
                if (string.IsNullOrWhiteSpace(w)) return;   // 单击空白处(桌面/图片): 不打扰
                w = w.Trim();
                if (w.Length > 200) w = w.Substring(0, 200);
                pickSel = w;
                pickLastX = x; pickLastY = y;
                pickShownAt = Environment.TickCount;
                Log("pick(click): " + w.Length + " chars | " + PickOneLine(w));
                ShowPickDot(x, y);
                return;
            }
            string text = "", how = "";
            int actBase = pickUserAct;
            IntPtr fgAt = GetForegroundWindow();
            try { text = PickTextUia(x, y); if (!string.IsNullOrWhiteSpace(text)) how = "uia"; } catch { }
            if (string.IsNullOrWhiteSpace(text))
            {
                // 剪贴板法会发一次全局 Ctrl+C —— 用户一旦移开指针/换窗, 这次复制就落到别处
                // (实测: 划完词顺手点到抖音评论区, 字被"粘贴"进输入框)。安全阀不满足就直接不试。
                if (!PickClipboardSafe(actBase, fgAt, x, y))
                    Log("pick: clipboard fallback skipped (user moved on) — 避免把词投进别的应用");
                else
                {
                    try { text = PickTextClipboard(actBase, fgAt); if (!string.IsNullOrWhiteSpace(text)) how = "clipboard"; } catch { }
                }
            }
            if (string.IsNullOrWhiteSpace(text)) { Log("pick: no text captured"); return; }
            text = text.Trim();
            if (text.Length > 2000) text = text.Substring(0, 2000);
            pickSel = text;
            pickLastX = x; pickLastY = y;
            pickShownAt = Environment.TickCount;
            Log("pick: " + text.Length + " chars via " + how + " | " + PickOneLine(text));
            ShowPickDot(x, y);
        }
        catch (Exception ex) { Log("pick handle err: " + ex.Message); }
        finally { Interlocked.Exchange(ref pickBusy, 0); }
    }

    // ---- 供 MCP / 设置页读写: 改内存 + 写回 json (立即生效且持久) ----
    // 任意参数传空字符串 = 不修改该项; 返回当前生效配置
    static string PickConfig(int enabled, string askEndpoint, string askKey, string askModel, int save)
    {
        return PickConfig(enabled, askEndpoint, askKey, askModel, "", save);
    }
    static string PickConfig(int enabled, string askEndpoint, string askKey, string askModel, string askPrompt, int save)
    {
        if (enabled >= 0) pickEnabled = enabled == 1 ? 1 : 0;
        try
        {
            Dictionary<string, string> d = LoadCfgDict();
            d["capture.dir"] = Cfg("capture.dir", "");
            d["capture.hotkeyRegion"] = Cfg("capture.hotkeyRegion", "");
            d["capture.hotkeyFull"] = Cfg("capture.hotkeyFull", "");
            d["capture.hotkeyPin"] = Cfg("capture.hotkeyPin", "");
            d["clipboard.enabled"] = Cfg("clipboard.enabled", "1");
            d["clipboard.max"] = Cfg("clipboard.max", "50");
            d["volume.enabled"] = Cfg("volume.enabled", "1");
            d["volume.step"] = Cfg("volume.step", "2");
            d["volume.reverse"] = Cfg("volume.reverse", "0");
            d["pick.enabled"] = pickEnabled.ToString();
            d["pick.askEndpoint"] = askEndpoint.Length > 0 ? askEndpoint : Cfg("pick.askEndpoint", "http://127.0.0.1:4000/chat/completions");
            d["pick.askKey"] = askKey.Length > 0 ? askKey : Cfg("pick.askKey", "sk-200418");
            d["pick.askModel"] = askModel.Length > 0 ? askModel : Cfg("pick.askModel", "GwV4F");
            // 附加提示词: 允许显式清空(传空格再 trim 为空)—— 与其余"空=不改"约定不同, 用前导 '|' 表示"清空为默认"
            if (askPrompt == "|") d["pick.askPrompt"] = "";
            else d["pick.askPrompt"] = askPrompt.Length > 0 ? askPrompt : Cfg("pick.askPrompt", "");
            if (save == 1) SaveCfgDict(d);
        }
        catch (Exception ex) { Log("pick config err: " + ex.Message); }
        Log("pick config: enabled=" + pickEnabled + " model=" + Cfg("pick.askModel", "GwV4F"));
        return "{\"ok\":true,\"enabled\":" + pickEnabled + ",\"askEndpoint\":\"" + JsonEscape(Cfg("pick.askEndpoint", "http://127.0.0.1:4000/chat/completions")) +
               "\",\"askModel\":\"" + JsonEscape(Cfg("pick.askModel", "GwV4F")) +
               "\",\"askKeySet\":" + (Cfg("pick.askKey", "").Length > 0 ? "true" : "false") +
               ",\"askPrompt\":\"" + JsonEscape(Cfg("pick.askPrompt", "")) +
               "\",\"saved\":" + save + "}";
    }

    static string PickOneLine(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
        return s.Length > 40 ? s.Substring(0, 40) + "…" : s;
    }

    // ---- 取词 1: UIA (无副作用) ----
    static string PickTextUia(int x, int y)
    {
        try
        {
            System.Windows.Automation.AutomationElement el =
                System.Windows.Automation.AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (el == null) return "";
            object pat;
            if (el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out pat))
            {
                var tp = pat as System.Windows.Automation.TextPattern;
                if (tp != null)
                {
                    var sel = tp.GetSelection();
                    if (sel != null && sel.Length > 0)
                    {
                        string s = sel[0].GetText(-1);
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
        }
        catch { }
        return "";
    }

    // ---- 取词 1b: UIA 光标所在处的"词"(单击用, 无副作用) ----
    // 单击不会产生选区, 所以只能问文本控件"我这个坐标上是什么词": RangeFromPoint 命中字符 ->
    // ExpandToEnclosingUnit(Word) 扩成整词。取不到就返回空(不猜、不去 Ctrl+C)。
    static string PickWordAtPoint(int x, int y)
    {
        try
        {
            System.Windows.Automation.AutomationElement el =
                System.Windows.Automation.AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (el == null) return "";
            object pat;
            if (!el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out pat)) return "";
            System.Windows.Automation.TextPattern tp =
                pat as System.Windows.Automation.TextPattern;
            if (tp == null) return "";
            System.Windows.Automation.Text.TextPatternRange r = tp.RangeFromPoint(new System.Windows.Point(x, y));
            if (r == null) return "";
            r.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Word);
            string s = r.GetText(-1);
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            return s.Length <= 1 ? "" : s;   // 单字符/标点当作没取到, 别为它弹球
        }
        catch { }
        return "";
    }

    // 剪贴板法前置检查: 焦点没换 + 指针还在原地(24px 内) + 用户没敲过鼠标
    static bool PickClipboardSafe(int actBase, IntPtr fgAt, int x, int y)
    {
        try
        {
            if (GetForegroundWindow() != fgAt) return false;
            if (pickUserAct != actBase)
            {
                POINT cur;
                if (!GetCursorPos(out cur)) return false;
                int dx = cur.x - x, dy = cur.y - y;
                if (dx * dx + dy * dy > 24 * 24) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // ---- 取词 2: 剪贴板法 (通用兜底, 短暂占用剪贴板后还原) ----
    static string PickTextClipboard(int actBase, IntPtr fgAt)
    {
        string result = "";
        IDataObject backup = null;
        try { backup = Clipboard.GetDataObject(); } catch { }
        try
        {
            Clipboard.Clear();
            KeyEvent(0x11, 0, 0);                    // Ctrl down
            KeyEvent(0x43, 0, 0);                    // C
            KeyEvent(0x43, 0, KEYEVENTF_KEYUP);
            KeyEvent(0x11, 0, KEYEVENTF_KEYUP);      // Ctrl up
            for (int i = 0; i < 25; i++)             // 最多等 500ms
            {
                Thread.Sleep(20);
                Application.DoEvents();              // 让消息泵转, 目标应用才能写入剪贴板
                // 期间用户换窗/挪指针 = 这次 Ctrl+C 可能已经投到他正在用的输入框 -> 立刻收手
                if (GetForegroundWindow() != fgAt) { Log("pick clipboard aborted: foreground changed"); break; }
                try { if (Clipboard.ContainsText()) { result = Clipboard.GetText(); break; } } catch { }
            }
        }
        catch (Exception ex) { Log("pick clipboard err: " + ex.Message); }
        finally
        {
            try { if (backup != null) Clipboard.SetDataObject(backup, true); } catch { }
        }
        return result ?? "";
    }

    // ---- 小点 / 工具条 / 卡片 ----
    static void ShowPickDot(int x, int y)
    {
        try
        {
            PickDismiss();
            pickHoverSince = -1;
            PickDotForm d = new PickDotForm();
            Point dloc = PickPlace(x, y, PickDotForm.DOT, PickDotForm.DOT);
            d.Location = dloc;
            d.Show();
            PickTopMost(d);
            pickDot = d;
            PickDotRect = new int[] { dloc.X, dloc.Y, dloc.X + PickDotForm.DOT, dloc.Y + PickDotForm.DOT };
            Log("pick dot shown @ " + dloc.X + "," + dloc.Y + " hwnd=" + d.Handle);
        }
        catch (Exception ex) { Log("pick dot err: " + ex.Message); }
    }

    // 心跳: hover 计时/拖动跟随/自动收起全在这里判(免激活窗收不到鼠标事件)
    // 拖动期间: 钩子 MOVE 在本机基本收不到(探针实锤 moves=0), pickCurX/Y 是陈旧值 ——
    // 必须直接 GetCursorPos 实时取, 且心跳加密到 15ms, 否则卡片"一步一卡"不跟手。
    static void PickTick()
    {
        try
        {
            PickRaise();
            POINT cur;
            if (GetCursorPos(out cur))
            {
                pickOverDot = PickHit(PickDotRect, cur.x, cur.y);
                pickOverBar = PickHit(PickBarRect, cur.x, cur.y);
                pickOverCard = PickHit(PickCardRect, cur.x, cur.y);
                if (pickDragging) PickCardDragMove(cur.x, cur.y);   // 实时光标坐标, 不依赖钩子
                if (pickTimer != null)
                {
                    int want = pickDragging ? PICK_TICK_DRAG : PICK_TICK;
                    if (pickTimer.Interval != want) pickTimer.Interval = want;
                }
            }
            Form d = pickDot;
            if (d != null && !d.IsDisposed)
            {
                if (pickOverDot)
                {
                    if (pickHoverSince < 0) pickHoverSince = Environment.TickCount;
                    if (Environment.TickCount - pickHoverSince >= 300) { PickDotActivated(); return; }
                }
                else pickHoverSince = -1;
                // 提问框开着(用户正在打字)时不许超时收起整组
                if (!pickOverDot && pickAskWin == null && Environment.TickCount - pickShownAt > 4000) PickDismiss();
            }
            Form b = pickBarWin;
            if (b != null && !b.IsDisposed && !pickOverBar && !pickDragging && pickAskWin == null && Environment.TickCount - pickShownAt > 4000) PickDismiss();
            Form c = pickCard as Form;
            // 卡片: 结果没回来(busy)或指针在上面或正在拖 都不收; 空闲 60s 才自动关
            if (c != null && !c.IsDisposed && !pickOverCard && !pickCardBusy && !pickDragging &&
                Environment.TickCount - pickShownAt > 60000) PickDismiss();
        }
        catch (Exception ex) { Log("pick tick err: " + ex.Message); }
    }

    // ---- 卡片拖动(钩子按住 + 心跳跟随; 免激活窗自己收不到拖拽消息) ----
    static void PickCardDragStart(int x, int y)
    {
        PickCardForm c = pickCard as PickCardForm;
        int[] r = PickCardRect;
        if (c == null || r == null) return;
        if (c.HitButton(x - r[0], y - r[1]) != PickCardForm.HIT_NONE) return;   // 按在按钮上: 不拖
        if (!c.HitDraggable(x - r[0], y - r[1])) return;   // 正文区不拖(留给选字/滚动)
        pickDragging = true;
        try { if (pickTimer != null) pickTimer.Interval = PICK_TICK_DRAG; } catch { }   // 起拖立刻加密心跳, 不等下一轮 200ms
        pickDragX = x - r[0]; pickDragY = y - r[1];
        Log("pick card drag start @grab " + (x - r[0]) + "," + (y - r[1]));
    }

    // 把卡片左上角落到 (光标-抓取偏移), 并限制在工作区内
    // 拖动路径每帧都会进这里: 用一次 SetWindowPos 同时完成移动+置顶(比 Location+单独置顶少一半窗口操作)
    static void PickCardMoveTo(int cx, int cy)
    {
        Form c = pickCard as Form;
        if (c == null || c.IsDisposed) return;
        Rectangle wa = Screen.FromPoint(new Point(cx, cy)).WorkingArea;
        int nx = cx - pickDragX, ny = cy - pickDragY;
        if (nx < wa.Left) nx = wa.Left;
        if (ny < wa.Top) ny = wa.Top;
        if (nx + PickCardForm.CARD_W > wa.Right) nx = wa.Right - PickCardForm.CARD_W;
        if (ny + PickCardForm.CARD_H > wa.Bottom) ny = wa.Bottom - PickCardForm.CARD_H;
        if (c.Left != nx || c.Top != ny)
        {
            try { SetWindowPos(c.Handle, HWND_TOPMOST, nx, ny, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE); }
            catch { try { c.Location = new Point(nx, ny); } catch { } }
        }
        PickCardRect = new int[] { nx, ny, nx + PickCardForm.CARD_W, ny + PickCardForm.CARD_H };
    }



    static void PickCardDragMove(int x, int y)
    {
        // 拖动中: 按住就跟随(物理拖动的实时反馈); 按键已释放则收尾
        if (!pickDownFlag) { pickDragging = false; return; }
        PickCardMoveTo(x, y);
    }

    // 停在小点 300ms 或小点被点击 -> 展开工具条
    static void PickDotActivated()
    {
        // 幂等: UP 的"点击"与心跳的"停够 300ms"可能都打进来, 只认第一次
        if (pickExpanded) return;
        pickExpanded = true;
        try
        {
            int[] r = PickDotRect;
            int cx = r != null ? (r[0] + r[2]) / 2 : pickLastX;
            int cy = r != null ? (r[1] + r[3]) / 2 : pickLastY;
            Log("pick dot activated");
            PickBarForm b = new PickBarForm();
            PickDismiss();
            Point loc = PickPlace(cx - PickDotForm.DOT / 2, cy - PickDotForm.DOT / 2, PickBarForm.BAR_W, PickBarForm.BAR_H);
            b.Location = loc;
            b.Show();
            PickTopMost(b);
            pickBarWin = b;
            pickShownAt = Environment.TickCount;
            pickBarActs = "translate,ask,copy";
            int[] rel = b.ActXs;
            int[] xs = new int[rel.Length];
            for (int i = 0; i < xs.Length; i++) xs[i] = loc.X + rel[i];
            pickBarBtnXs = xs;
            PickBarRect = new int[] { loc.X, loc.Y, loc.X + PickBarForm.BAR_W, loc.Y + PickBarForm.BAR_H };
            Log("pick bar shown @ " + loc.X + "," + loc.Y + " btns=" + xs[0] + "/" + xs[2] + "/" + xs[4]);
        }
        catch (Exception ex) { Log("pick expand err: " + ex.Message); }
    }

    static void PickBarFire(string act)
    {
        Log("pick action: " + act);
        PickDismiss();
        if (act == "translate") PickDoTranslate();
        else if (act == "ask") PickShowAsk();
        else if (act == "copy") { try { Clipboard.SetText(pickSel); } catch { } TrayNotify("已复制", PickOneLine(pickSel)); }
    }

    // ---- 问 AI 前先弹提问框(2026-09-07 老大要的: 每次可以问不同的话) ----
    // 全套悬浮窗里唯一允许抢焦点的窗(要打字)。回车=带这次的问题发问; 留空回车=按内置默认直接解释;
    // Esc/点外面=取消。关掉后把前台焦点还给用户原来的窗口。
    static IntPtr pickAskPrevFg;
    static void PickShowAsk()
    {
        try
        {
            PickAskPrevFg();
            PickAskForm a = new PickAskForm(pickSel);
            Point loc = PickPlace(pickLastX, pickLastY, PickAskForm.ASK_W, PickAskForm.ASK_H);
            a.Location = loc;
            a.Show();
            PickTopMost(a);
            pickAskWin = a;
            PickAskRect = new int[] { loc.X, loc.Y, loc.X + PickAskForm.ASK_W, loc.Y + PickAskForm.ASK_H };
            a.FormClosed += delegate { PickAskClosed(a); };
            Log("pick ask box shown @ " + loc.X + "," + loc.Y);
        }
        catch (Exception ex) { Log("pick ask err: " + ex.Message); }
    }
    static void PickAskPrevFg() { try { pickAskPrevFg = GetForegroundWindow(); } catch { } }
    static void PickAskClosed(PickAskForm a)
    {
        try
        {
            bool go = a.Confirmed; string q = a.Result;
            if (pickAskWin == (Form)a) { pickAskWin = null; PickAskRect = null; }
            pickDownOnAsk = false;
            if (go) PickDoAsk(q);
            else Log("pick ask cancelled");
            // 把前台还给用户原来的窗口(我们短暂抢过焦点打字)
            try { if (pickAskPrevFg != IntPtr.Zero) SetForegroundWindow(pickAskPrevFg); } catch { }
        }
        catch (Exception ex) { Log("pick ask closed err: " + ex.Message); }
    }

    // 卡片上松开鼠标: 点按钮就执行动作, 否则结束"按住标题栏/原文行"的拖动
    // downX/downY = 按下点(决定抓取偏移), upX/upY = 松开点(决定落位与按钮命中)
    static void PickCardUp(int downX, int downY, int upX, int upY)
    {
        PickCardForm c = pickCard as PickCardForm;
        int[] r = PickCardRect;
        if (c == null || r == null) { pickDragging = false; return; }
        int btn = c.HitButton(upX - r[0], upY - r[1]);
        if (btn != PickCardForm.HIT_NONE) { pickDragging = false; PickCardButton(c, btn); return; }
        if (!pickDragging) { pickDragging = false; return; }   // 没起拖(按在正文区) -> 什么都不做
        pickDragging = false;
        PickCardMoveTo(upX, upY);
        Log("pick card drag end -> " + (upX - pickDragX) + "," + (upY - pickDragY));
    }

    static void PickCardButton(PickCardForm c, int btn)
    {
        if (btn == PickCardForm.HIT_CLOSE) { PickDismiss(); return; }
        if (btn == PickCardForm.HIT_SWAP) { PickTranslateReverse(c); return; }
        if (btn == PickCardForm.HIT_COPY)
        {
            try { Clipboard.SetText(c.BodyText); TrayNotify("已复制", "结果已复制到剪贴板"); } catch { }
            return;
        }
        if (btn == PickCardForm.HIT_COPY_SRC)
        {
            try { Clipboard.SetText(pickSel); TrayNotify("已复制", "原文已复制到剪贴板"); } catch { }
        }
    }

    static void PickCardClick(int x, int y)
    {
        PickCardForm c = pickCard as PickCardForm;
        int[] r = PickCardRect;
        if (c == null || r == null) return;
        switch (c.HitButton(x - r[0], y - r[1]))
        {
            case PickCardForm.HIT_CLOSE: PickDismiss(); break;
            case PickCardForm.HIT_SWAP: PickTranslateReverse(c); break;
            case PickCardForm.HIT_COPY:
                try { Clipboard.SetText(c.BodyText); TrayNotify("已复制", "结果已复制到剪贴板"); } catch { }
                break;
            case PickCardForm.HIT_COPY_SRC:
                try { Clipboard.SetText(pickSel); TrayNotify("已复制", "原文已复制到剪贴板"); } catch { }
                break;
        }
    }

    static Point PickPlace(int x, int y, int w, int h)
    {
        Rectangle wa = Screen.FromPoint(new Point(x, y)).WorkingArea;
        int px = x + 14, py = y + 14;
        if (px + w > wa.Right) px = x - 14 - w;
        if (py + h > wa.Bottom) py = y - 14 - h;
        if (px < wa.Left) px = wa.Left + 4;
        if (py < wa.Top) py = wa.Top + 4;
        return new Point(px, py);
    }

    static void PickDismiss()
    {
        try { if (pickDot != null) { pickDot.Close(); pickDot = null; } } catch { }
        try { if (pickBarWin != null) { pickBarWin.Close(); pickBarWin = null; } } catch { }
        try { if (pickAskWin != null) { pickAskWin.Close(); pickAskWin = null; } } catch { }
        try { if (pickCard != null) { pickCard.Close(); pickCard = null; } } catch { }
        PickDotRect = null; PickBarRect = null; PickCardRect = null; PickAskRect = null; pickBarBtnXs = null;
        pickHoverSince = -1; pickDragging = false; pickExpanded = false; pickDownOnCardLive = false;
    }

    // ---- 翻译 ----
    // 自动判方向偶尔不合意(中英混排被判反), 所以卡片上有「反转翻译」: 拿当前结果按反方向再翻一次。
    static string pickTransTo = "";

    static void PickDoTranslate()
    {
        string text = pickSel;
        if (string.IsNullOrWhiteSpace(text)) return;
        pickTransTo = PickHasCJK(text) ? "en" : "zh";
        PickShowCard("翻译中…", text);
        PickTranslateAsync(text, pickTransTo, text, pickTransTo == "en" ? "中→英" : "英→中");
    }

    // 卡片「反转翻译」: 源 = 现在正文里的译文, 目标 = 与上次相反的语言; 原文行仍保留用户最初划的词
    static void PickTranslateReverse(PickCardForm c)
    {
        if (pickCardBusy) return;   // 上一次请求还没回来: 别叠加(此时正文还是原文)
        string body = c == null ? "" : c.BodyText;
        if (string.IsNullOrWhiteSpace(body)) return;
        string to = pickTransTo == "en" ? "zh" : "en";
        pickTransTo = to;
        string srcLine = string.IsNullOrWhiteSpace(pickSel) ? body : pickSel;
        c.SetTitle("反转中…");
        PickTranslateAsync(body, to, srcLine, to == "en" ? "中→英(反转)" : "英→中(反转)");
    }

    static void PickTranslateAsync(string text, string to, string srcLine, string label)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            string outText;
            try { outText = TranslateProvider().TranslateAsync(text, to).Result; }
            catch (Exception ex) { outText = "翻译失败: " + ex.Message; }
            PickSetCard("翻译 (" + label + ")", outText, srcLine);
        });
    }

    static bool PickHasCJK(string s)
    {
        int cjk = 0, latin = 0;
        foreach (char c in s)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
        }
        return cjk >= latin;
    }

    // ---- 问 AI (litellm 127.0.0.1:4000) ----
    // question = 本次在提问框里现输入的话(可空)。非空时优先按它问, 划选文本作为上下文附上;
    // 留空则走内置默认提示词(简明解释划选内容)。设置页全局附加提示词(askPrompt)两种情况都前置。
    static void PickDoAsk(string question)
    {
        string text = pickSel;
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(question)) return;
        question = (question ?? "").Trim();
        PickShowCard("AI 思考中…", text);
        ThreadPool.QueueUserWorkItem(delegate
        {
            string ans = "";
            try
            {
                string ep = Cfg("pick.askEndpoint", "http://127.0.0.1:4000/chat/completions");
                string key = Cfg("pick.askKey", "sk-200418");
                string model = Cfg("pick.askModel", "GwV4F");
                // 附加提示词(设置页「划词」/pick_config askPrompt 可配): 全局固定的风格/角色前缀
                string extra = (Cfg("pick.askPrompt", "") ?? "").Trim();
                string ctx = string.IsNullOrWhiteSpace(text) ? "" : "【划选内容】" + text;
                string prompt;
                if (question.Length > 0)
                    prompt = "请回答下面的问题。" + (ctx.Length > 0 ? ctx + "\n" : "") + "【问题】" + question;
                else
                    prompt = "简明回答下面内容（中文，不超过 300 字，直接给结论，不要复述问题）：\n\n" + text;
                if (extra.Length > 0) prompt = extra + "\n\n" + prompt;
                string json = "{\"model\":" + EscapeJson(model) + ",\"messages\":[{\"role\":\"user\",\"content\":" + EscapeJson(prompt) + "}],\"max_tokens\":800}";
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                    wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
                    string resp = wc.UploadString(ep, json);
                    ans = PickExtractContent(resp);
                }
            }
            catch (Exception ex) { ans = "AI 调用失败: " + ex.Message; }
            if (string.IsNullOrWhiteSpace(ans)) ans = "(空响应 — 检查 litellm 是否可达)";
            PickSetCard("AI 回答", ans, text);
        });
    }

    static void PickShowCard(string body, string src)
    {
        pickCardBusy = true;      // 请求已发出, 结果没回来前卡片不许自动收
        try
        {
            Control s = pickSync;
            if (s != null && s.IsHandleCreated)
                s.BeginInvoke(new MethodInvoker(delegate { PickShowCardInner("处理中", body, src); }));
        }
        catch (Exception ex) { Log("pick card err: " + ex.Message); }
    }

    static void PickSetCard(string title, string body, string src)
    {
        pickCardBusy = false;     // 结果到了: 允许自动收起
        try
        {
            Control s = pickSync;
            if (s != null && s.IsHandleCreated)
                s.BeginInvoke(new MethodInvoker(delegate { PickShowCardInner(title, body, src); }));
        }
        catch { }
    }

    static void PickShowCardInner(string title, string body, string src)
    {
        // pickCardBusy 由 PickShowCard(发请求) 置位 / PickSetCard(拿到结果) 清除,
        // 这里不能无脑清 —— busy 期间卡片不许被 60s 超时关掉
        if (pickCard == null || (pickCard as PickCardForm) == null) pickCardBusy = false;
        try { if (pickDot != null) { pickDot.Close(); pickDot = null; } } catch { }
        try { if (pickBarWin != null) { pickBarWin.Close(); pickBarWin = null; } } catch { }
        PickBarRect = null;
        PickCardForm c = pickCard as PickCardForm;
        if (c != null && !c.IsDisposed)
        {
            c.SetContent(title, body, src);
            pickShownAt = Environment.TickCount;
            PickTopMost(c);
            return;
        }
        try { if (pickCard != null) { pickCard.Close(); pickCard = null; } } catch { }
        try
        {
            c = new PickCardForm();
            c.SetContent(title, body, src);
            Point loc = PickPlace(pickLastX, pickLastY, PickCardForm.CARD_W, PickCardForm.CARD_H);
            c.Location = loc;
            c.Show();
            PickTopMost(c);
            pickCard = c;
            PickCardRect = new int[] { loc.X, loc.Y, loc.X + PickCardForm.CARD_W, loc.Y + PickCardForm.CARD_H };
            pickShownAt = Environment.TickCount;
            Log("pick card shown @ " + loc.X + "," + loc.Y);
        }
        catch (Exception ex) { Log("pick card show err: " + ex.Message); }
    }

    // 手抠 /chat/completions 的 content (不引第三方 JSON 库)
    static string PickExtractContent(string resp)
    {
        try
        {
            int i = resp.IndexOf("\"content\"");
            if (i < 0) return "";
            int c = resp.IndexOf(':', i + 9);
            if (c < 0) return "";
            int q1 = resp.IndexOf('"', c + 1);
            if (q1 < 0) return "";
            var sb = new StringBuilder();
            int k = q1 + 1;
            while (k < resp.Length)
            {
                char ch = resp[k];
                if (ch == '\\')
                {
                    if (k + 1 < resp.Length)
                    {
                        char nx = resp[k + 1];
                        if (nx == 'n') sb.Append('\n');
                        else if (nx == 't') sb.Append('\t');
                        else if (nx == 'r') sb.Append('\r');
                        else if (nx == 'u')
                        {
                            if (k + 5 < resp.Length)
                            {
                                try { sb.Append((char)Convert.ToInt32(resp.Substring(k + 2, 4), 16)); } catch { }
                                k += 4;
                            }
                        }
                        else sb.Append(nx);
                        k += 2; continue;
                    }
                    k++; continue;
                }
                if (ch == '"') break;
                sb.Append(ch); k++;
            }
            return sb.ToString();
        }
        catch { return ""; }
    }
}
