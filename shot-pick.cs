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

    // ---- I-BEAM 光标门卫 (思路自 STranslate MIT; 句柄比对在 Win11 失效, 2026-09-11 改形状识别) ----
    // 实锤(探针): 记事本工字形光标句柄=0x10003, 标准 IDC_IBEAM=0x10005 —— 应用自定义句柄比对永远 false,
    // 5 连误挡(venv/effort)。改判"蒙版里存在高度≥70%图高的连续实心竖带": 工字形(任何实现)必有这条竖线,
    // 箭头/手形/沙漏没有。结果按句柄缓存(每句柄只算一次位图), 零热路径开销。
    static IntPtr pickIBeamCursor;      // 标准 IDC_IBEAM 句柄(快路径)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PICKCURSORINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hCursor;
        public long pt;               // POINT (int x,int y) 合并避免同名结构冲突
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetCursorInfo(ref PICKCURSORINFO pci);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
    static readonly IntPtr IDC_IBEAM = new IntPtr(32513);   // MAKEINTRESOURCE(IDC_IBEAM)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PICKICONINFO
    {
        public int fIcon;
        public uint xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetIconInfo(IntPtr hIcon, ref PICKICONINFO piconinfo);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern int GetObjectW(IntPtr h, int c, out PICKBITMAP bm);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern int GetBitmapBits(IntPtr hBitmap, int cb, byte[] lpvBits);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PICKBITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }
    static readonly System.Collections.Hashtable pickCursorShapeCache = new System.Collections.Hashtable();   // hCursor -> bool(isIBeam)

    static bool PickIsIBeamCursor()
    {
        try
        {
            PICKCURSORINFO ci = new PICKCURSORINFO();
            ci.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PICKCURSORINFO));
            if (!GetCursorInfo(ref ci)) return false;
            if (ci.hCursor == IntPtr.Zero) return false;
            if (ci.hCursor == pickIBeamCursor) return true;     // 标准句柄快路径
            lock (pickCursorShapeCache)
            {
                if (pickCursorShapeCache.ContainsKey(ci.hCursor)) return (bool)pickCursorShapeCache[ci.hCursor];
                bool isIbeam = PickCursorLooksIBeam(ci.hCursor);
                pickCursorShapeCache[ci.hCursor] = isIbeam;
                if (!isIbeam) Log("pick gate: cursor " + ci.hCursor.ToInt64() + " = non-ibeam shape");
                else Log("pick gate: cursor " + ci.hCursor.ToInt64() + " = ibeam shape (custom)");
                return isIbeam;
            }
        }
        catch { return false; }
    }

    // 蒙版列轮廓: 任一列的 set 位高度 ≥ 70% 蒙版高 = 存在实心竖带 = 工字形
    static bool PickCursorLooksIBeam(IntPtr hCursor)
    {
        try
        {
            PICKICONINFO ii = new PICKICONINFO();
            if (!GetIconInfo(hCursor, ref ii)) return false;
            try
            {
                PICKBITMAP bm;
                if (GetObjectW(ii.hbmMask, System.Runtime.InteropServices.Marshal.SizeOf(typeof(PICKBITMAP)), out bm) == 0 || bm.bmWidth <= 0)
                    return false;
                int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
                int rowBytes = ((w + 15) / 16) * 2;             // 蒙版按 WORD 对齐
                if (h % 2 == 0 && bm.bmHeight > 0) { /* AND+XOR 双面: 取上半 AND 面也行; 直接全读更稳 */ }
                byte[] buf = new byte[rowBytes * h];
                if (GetBitmapBits(ii.hbmMask, buf.Length, buf) == 0) return false;
                int fullCols = 0;
                for (int x = 0; x < w; x++)
                {
                    int set = 0;
                    for (int y = 0; y < h; y++)
                        if ((buf[y * rowBytes + x / 8] & (0x80 >> (x % 8))) != 0) set++;
                    if (set >= h * 7 / 10) fullCols++;
                }
                return fullCols >= 3;                            // 竖带至少 3 列宽(1px 线也有 AA 邻列)
            }
            finally
            {
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
        }
        catch { return false; }
    }
    static bool pickSeenIBeam;          // 本次手势期间见过工字形(DOWN 记初始, MOVE 累积, UP 判定后清) —— 钩子线程独占(门卫只管拖选路径), 勿在他线程写

    // 双击判定用系统值 (STranslate): 手感与系统一致, 不硬编码 450ms/12px
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDoubleClickTime();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);
    const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;
    static Control pickSync;
    static bool pickDownFlag;
    static int pickX0, pickY0;
    static string pickSel = "";
    static int pickBusy = 0;
    static int pickLastX, pickLastY;
    static Form pickDot, pickCard;
    static int pickSlowUntil = -10000;   // UIA 慢目标退避deadline(TickCount): 窗口内不发单击取词
    static int pickRClickUntil = -10000; // 右键让路窗口: 右键按下后短暂暂停单击取词(不和右键菜单抢目标UI线程)

    // ---- 生命周期: 专属 STA 线程(剪贴板要求) ----
    static void PickInit()
    {
        try
        {
            pickIBeamCursor = LoadCursor(IntPtr.Zero, IDC_IBEAM);   // I-beam 门卫: 缓存工字形光标句柄
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
    static void PickOnMouse(int msg, int x, int y, int wheelDelta = 0)
    {
        if (pickEnabled != 1) return;
        try
        {
            if (msg == PICK_DOWN || msg == PICK_UP || msg == PICK_RBUTTONDOWN ||
                msg == PICK_RBUTTONUP || msg == PICK_WHEEL || msg == PICK_MOVE)
                pickUserAct = Environment.TickCount;
            if (msg == PICK_RBUTTONDOWN)
            { pickRClickUntil = Environment.TickCount + 1500; return; }   // 右键让路: 1.5s 内不发起单击取词
            if (msg == PICK_WHEEL)
            {
                // 悬浮窗永远免激活没焦点, 子控件收不到 WM_MOUSEWHEEL —— 滚轮由钩子喂给卡片正文
                if (PickHit(PickCardRect, x, y))
                {
                    int wd = wheelDelta; Control sw = pickSync;
                    if (sw != null && sw.IsHandleCreated)
                        sw.BeginInvoke(new MethodInvoker(delegate { PickCardWheel(x, y, wd); }));
                }
                return;
            }
            if (msg == PICK_DOWN)
            {
                pickDownFlag = true; pickX0 = x; pickY0 = y;
                pickSeenIBeam = PickIsIBeamCursor();          // I-beam 门卫: DOWN 记初始
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
                if (pickDownFlag) pickSeenIBeam |= PickIsIBeamCursor();   // 手势期间累积
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
                if (PickNear(PickDotRect, ux, uy, PICK_TOL_MISS) && !pickExpanded)
                {
                    s.BeginInvoke(new MethodInvoker(delegate { PickDotActivated(); }));
                    return;
                }
                // 已经有选词元素在屏上(点/工具条/卡片): 点空白 = 收起元素。
                // (2026-09-11 老大拍板修: 旧版收起后连事件一起吞=双击第一下死在这、第二下配不上对=
                //  "一次出一不出"的根因。现在收起之后这一下照样进双击判定: 第一下=关元素+记锚点,
                //  第二下配对取词。同 pickSync 线程 FIFO, dismiss 恒先跑, PickHandleAsync 里
                //  card/bar 判空已过。1A 砍了单击取词后本分支零 UIA 零副作用。)
                if (PickDotRect != null || pickBarWin != null || pickCard != null)
                {
                    Log("pick click: dismiss+chain (element onscreen) pos=" + ux + "," + uy);
                    s.BeginInvoke(new MethodInvoker(delegate { PickDismiss(); }));
                    s.BeginInvoke(new MethodInvoker(delegate { PickHandleAsync(ux, uy, true, 0, 0); }));
                    return;
                }
                if (PickNear(PickBarRect, ux, uy, PICK_TOL) || PickNear(PickCardRect, ux, uy, PICK_TOL) ||
                    PickNear(PickAskRect, ux, uy, PICK_TOL)) return;   // 贴着元素: 不动它
                // 这一下是"顺手关掉提问框"(DOWN 时它还开着): 不再二次触发取词
                if (askJustClosed) return;
                // 单击别处: 也弹悬浮球(见 PickHandleAsync click 模式 —— 只读 UIA 现有选区, 绝不发 Ctrl+C)
                Log("pick click: pass->dblclick-judge pos=" + ux + "," + uy);
                s.BeginInvoke(new MethodInvoker(delegate { PickHandleAsync(ux, uy, true, 0, 0); }));
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
            // UP 时刻不追加查询(GetCursorInfo 在 UP 瞬间常已切回箭头=误挡, 实测 venv/effort 被挡) ——
            // 只用 DOWN+MOVE 累积值, 与 STranslate 一致(它 UP 时不查, 用 _hasSeenIBeam 累积)
            bool wasTextGesture = pickSeenIBeam; pickSeenIBeam = false;         // 取走即清(下一手势重新记)
            if (!wasTextGesture)
            {
                // I-beam 门卫: 整个手势期间光标从未变工字形 = 不是在选文本(滑杆/按钮/画布)
                Log("pick: gesture not on text (no I-beam) — silent skip");
                return;
            }
            // 出点改为"取词成功才出"(OCR 作废后 UIA 16ms 级, 无需预出; 预出点+完成后重建 = 双跳+空点闪)
            s.BeginInvoke(new MethodInvoker(delegate { PickHandle(ux, uy, pickX0, pickY0); }));
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

    const int PICK_TOL = 16;  // 悬浮球只有 26px, 手抖一点就 miss —— 容差内当作点中
    const int PICK_TOL_MISS = 40; // 偏到 40px 内仍算"想点小点" -> 直接展开菜单, 绝不掉进取词路径(UIA 对慢目标能挂 700ms+ = 点快了的"卡")

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

    static void PickHandle(int x, int y, int x0, int y0) { PickHandleAsync(x, y, false, x0, y0); }

    // ★ 取词绝不能在 UI 线程跑 (2026-09-07 老大: "刚开始跟手, 然后延迟很大, 最后消失了像崩溃")
    //   UIA FromPoint/GetSelection 是跨进程 COM 调用, 目标程序(Chromium/记事本)正忙时会挂几秒到几十秒;
    //   剪贴板法更是直接 Thread.Sleep 轮询。这两个都发生在悬浮窗 UI 线程上的话,
    //   驱动拖动的 Timer 就无法触发 → 拖动滞后、心跳积压、末尾一次性收起 = 看着像卡死。
    //   日志实锤: 19:56:20 点「问AI」, 19:56:49 才出卡片 —— UI 线程被取词占了 29 秒。
    // 这里在 UI 线程只做"取快照 + 丢后台", 取词全在 ThreadPool, 拿到词再 BeginInvoke 回来画小点。
    static readonly int pickOwnPid = System.Diagnostics.Process.GetCurrentProcess().Id;
    // 前台窗口选取: 若前台是我们自己的悬浮窗(点/条/卡片偶发占前台), 用鼠标正下方的顶层窗替代——
    // 否则 CDP 表按前台进程查端口=查到自己头上(port=0 静默跳 CDP), 症状=时灵时不灵(实锤 2026-09-11 交替失败)
    static IntPtr PickFgWindow(int x, int y)
    {
        IntPtr fg = GetForegroundWindow();
        try
        {
            uint pid; GetWindowThreadProcessId(fg, out pid);
            if ((int)pid != pickOwnPid) return fg;
            IntPtr w = WindowFromPoint(new Point(x, y));
            if (w != IntPtr.Zero) { IntPtr root = GetAncestor(w, 2); if (root != IntPtr.Zero) w = root; }
            if (w != IntPtr.Zero && w != fg) { Log("pick fg=self -> using point window hwnd=" + w); return w; }
        }
        catch { }
        return fg;
    }

    static void PickHandleAsync(int x, int y, bool click, int x0, int y0)
    {
        if (Interlocked.Exchange(ref pickBusy, 1) == 1) return;
        try
        {
            if (click && (pickCard != null || pickBarWin != null)) { Interlocked.Exchange(ref pickBusy, 0); return; }
            IntPtr fgAt = PickFgWindow(x, y);
            if (click)
            {
                // 限流: 单击取词是跨进程 UIA COM, 乱点会连环发起拖慢全系统。
                // 但**双击选词**必须放行: 双击间隔 <300ms 且同位置 —— 吞掉它 = "双击没点"(老大实测),
                // 之后点别处才把旧选区读出来, 点乱冒。只有"不同位置的快速连点"才节流。
                int now = Environment.TickCount;
                // 1A(老大裁决): 单击选词砍掉 —— 只有"系统双击时限内同位置"= 双击选词才取词。
                // 双击判定用系统值(STranslate): GetDoubleClickTime + SM_C*DOUBLECLK, 手感与系统一致。
                // 双击不需要 I-beam 豁免: 门卫本来就只管拖选路径(isClick 分支直进这里, 钩子端不读该旗标),
                // 双击自身已有系统时限+同位强约束。(2026-09-11 评审揪出曾在此写 pickSeenIBeam=true "豁免":
                //  对本击 no-op, 且 UI 线程写钩子线程旗标=调度错位时污染下一轮拖选门卫, 已删)
                int dcW = Math.Max(1, GetSystemMetrics(SM_CXDOUBLECLK));
                int dcH = Math.Max(1, GetSystemMetrics(SM_CYDOUBLECLK));
                uint dcT = GetDoubleClickTime();
                bool dblClick = (now - pickLastClickCap >= 0 && now - pickLastClickCap < (int)dcT) &&
                                Math.Abs(x - pickLastClickX) * 2 <= dcW && Math.Abs(y - pickLastClickY) * 2 <= dcH;
                if (!dblClick)
                {
                    int gapDbg = now - pickLastClickCap;
                    bool samePos = Math.Abs(x - pickLastClickX) * 2 <= dcW && Math.Abs(y - pickLastClickY) * 2 <= dcH;
                    pickLastClickCap = now; pickLastClickX = x; pickLastClickY = y;
                    Log("pick click: single 记锚点 gap=" + gapDbg + "ms/窗口" + dcT + "ms samePos=" + samePos);
                    Interlocked.Exchange(ref pickBusy, 0); return;
                }
                pickLastClickCap = now; pickLastClickX = x; pickLastClickY = y;
                if (now < pickRClickUntil) { Interlocked.Exchange(ref pickBusy, 0); return; }   // 右键让路窗口内: 不取词
                if (now < pickSlowUntil) { Interlocked.Exchange(ref pickBusy, 0); return; }     // 慢目标退避窗口内: 不取词
                // 目标窗口挂死(UI 未响应)时 UIA 调用会阻塞很久 —— 直接跳过
                if (fgAt != IntPtr.Zero && IsHungAppWindow(fgAt))
                { Interlocked.Exchange(ref pickBusy, 0); Log("pick(click): target hung, skip"); return; }
            }
            int actBase = pickUserAct;
            if (!click) pickSel = "";   // 新一轮划选: 清上一轮残留, 文字到位前激活小点=未就绪
            ThreadPool.QueueUserWorkItem(delegate { PickCaptureWork(x, y, click, fgAt, actBase, x0, y0); });
        }
        catch (Exception ex) { Interlocked.Exchange(ref pickBusy, 0); Log("pick dispatch err: " + ex.Message); }
    }
    static int pickLastClickCap = -10000;
    static int pickLastClickX, pickLastClickY;   // 上次单击取词位置(判双击: 同位连点放行节流)

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsHungAppWindow(IntPtr hwnd);

    // ---- 2026-09-08 正式修复: 浏览器(Chromium 系)目标识别 ----
    // 实锤(老大 A/B 实验: 关划词后右键秒开): 跨进程 UIA 调用会同步霸占浏览器 UI 线程,
    // 单击取词的三连调用(FromPoint+RangeFromPoint+ExpandToEnclosingUnit)是右键卡 7 秒主凶。
    // 策略(老大拍板): 浏览器**单击取词一律不发**; 划选取词保留但只有一次 UIA 机会
    // (300ms 预算, 慢就退避 60s), 且浏览器**绝不走剪贴板兜底**(不发全局 Ctrl+C)。
    static readonly Dictionary<uint, object[]> pickBrowserCache = new Dictionary<uint, object[]>();   // pid -> [isBrowser, tick]
    static readonly string[] pickBrowserNames = { "msedge", "msedge_beta", "msedge_dev", "msedgewebview2", "chrome", "chrome_sx", "chromium", "firefox", "brave", "opera", "opera_gx", "vivaldi", "qqbrowser", "360se", "360chrome", "maxthon", "sogouexplorer" };
    // 终端黑名单 (2026-09-08 老大实测: PowerShell 里选中文本被自动取消 + 移动窗口就蹦 ^C):
    // conhost 的 UIA provider 被外部查询选区(GetSelection)时会走内部复制路径 = 等于替用户按了一次 Ctrl+C
    // (有选区→复制并清掉选区; 无选区→^C 中断命令)。终端一律不发起 UIA, 终端划词放弃。
    static readonly string[] pickTerminalNames = { "conhost", "windowsterminal", "openconsole", "powershell", "pwsh", "cmd", "wt", "wezterm", "alacritty", "hyper" };
    static readonly System.Collections.Hashtable pickTermCache = new System.Collections.Hashtable();

    static bool PickIsTerminal(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return false;
            lock (pickTermCache)
            {
                object[] c; int now = Environment.TickCount;
                if (pickTermCache.ContainsKey(pid))
                {
                    c = (object[])pickTermCache[pid];
                    if (now - (int)c[1] < 300000) return (bool)c[0];
                }
                bool isTerm = false;
                string n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                foreach (string t in pickTerminalNames) if (n == t) { isTerm = true; break; }
                pickTermCache[pid] = new object[] { isTerm, Environment.TickCount };
                return isTerm;
            }
        }
        catch { return false; }
    }
    static bool PickIsBrowser(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return false;
            lock (pickBrowserCache)
            {
                object[] c; int now = Environment.TickCount;
                if (pickBrowserCache.TryGetValue(pid, out c) && now - (int)c[1] < 300000) return (bool)c[0];
            }
            bool isBrowser = false;
            string n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            foreach (string b in pickBrowserNames) if (n == b) { isBrowser = true; break; }
            lock (pickBrowserCache) { pickBrowserCache[pid] = new object[] { isBrowser, Environment.TickCount }; }
            return isBrowser;
        }
        catch { return false; }
    }

    // ---- 取词 0: CDP 直读（2026-09-11 方案D，老大拍板）----
    // Electron 应用带 --remote-debugging-port 启动（快捷方式已加：MiMo=9222/WorkBuddy=9223/ZCode=9224），
    // DevTools 协议对每个 page/iframe target 发 Runtime.evaluate 读 getSelection —— 真选区、
    // 毫秒级、零 UIA 零按键零剪贴板。文本走 base64 往返避开 JSON 转义（页面 btoa / C# FromBase64）。
    // 空/口没开/超时 = 返回 ""，调用方落回 UIA/剪贴板老链。127.0.0.1 上口没开=连接拒绝<5ms，不拖节奏。
    // 红线不受影响：终端/浏览器不在映射表，天然不碰。
    static readonly string[][] pickCdpApps = new string[][] {
        new string[]{ "xiaomi mimo", "9222" },
        new string[]{ "workbuddy",   "9223" },
        new string[]{ "zcode",       "9224" },
    };
    static readonly Dictionary<uint, object[]> pickCdpCache = new Dictionary<uint, object[]>();  // pid -> [port, tick]
    static readonly Dictionary<string, int> pickCdpPenalty = new Dictionary<string, int>();      // wsUrl -> 惩罚截止 tick(死/慢 target 60s 内跳过)

    static int PickCdpPort(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return 0;
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return 0;
            lock (pickCdpCache)
            {
                object[] c; int now = Environment.TickCount;
                if (pickCdpCache.TryGetValue(pid, out c) && now - (int)c[1] < 300000) return (int)c[0];
            }
            int port = 0;
            string n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            foreach (string[] a in pickCdpApps) if (n == a[0]) { port = int.Parse(a[1]); break; }
            lock (pickCdpCache) { pickCdpCache[pid] = new object[] { port, Environment.TickCount }; }
            return port;
        }
        catch { return 0; }
    }

    static string CdpHttpGet(string url, int timeoutMs)
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET"; req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.Proxy = null;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (System.IO.StreamReader sr = new System.IO.StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    // 读选区表达式: ①document 选区 ②activeElement 输入框选区(getSelection 看不见 textarea/input)
    // ③同进程 iframe 的选区(主 frame 看不见子 frame, 穿透 contentDocument, 跨域自动跳过)。
    // 全单引号避免 JSON 转义; 结果走 btoa 避开 JSON 编码。
    static readonly string pickCdpExpr =
        "btoa(unescape(encodeURIComponent((function(){var t='';" +
        "try{if(window.getSelection&&getSelection())t=getSelection().toString()||'';}catch(e){}" +
        "if(!t){try{var a=document.activeElement;" +
        "if(a&&(a.tagName==='TEXTAREA'||a.tagName==='INPUT')&&typeof a.selectionStart==='number'&&a.selectionEnd!==a.selectionStart)t=a.value.slice(a.selectionStart,a.selectionEnd);}catch(e){}}" +
        "if(!t){try{var fs=document.querySelectorAll('iframe');" +
        "for(var i=0;i<fs.length;i++){var d=null;try{d=fs[i].contentDocument;}catch(e){}" +
        "if(d&&d.getSelection){var s2=d.getSelection().toString()||'';if(s2){t=s2;break;}}}}catch(e){}}" +
        "return t;})())))";

    // 单 target: 连 ws → evaluate 选区表达式的 base64 → 解码。
    // 返回: null=连接/超时失败(记 60s 惩罚), ""=读干净但没选区, 其他=选中文本
    static string CdpEvalSelection(string wsUrl)
    {
        try
        {
            using (System.Net.WebSockets.ClientWebSocket ws = new System.Net.WebSockets.ClientWebSocket())
            {
                using (System.Threading.CancellationTokenSource ctsC = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
                { ws.ConnectAsync(new Uri(wsUrl), ctsC.Token).GetAwaiter().GetResult(); }
                byte[] msg = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":\"" + pickCdpExpr + "\",\"returnByValue\":true}}");
                using (System.Threading.CancellationTokenSource ctsS = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
                { ws.SendAsync(new ArraySegment<byte>(msg), System.Net.WebSockets.WebSocketMessageType.Text, true, ctsS.Token).GetAwaiter().GetResult(); }
                byte[] rx = new byte[65536];
                System.Net.WebSockets.WebSocketReceiveResult rr;
                using (System.Threading.CancellationTokenSource ctsR = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
                { rr = ws.ReceiveAsync(new ArraySegment<byte>(rx), ctsR.Token).GetAwaiter().GetResult(); }
                string s = Encoding.UTF8.GetString(rx, 0, rr.Count);
                System.Text.RegularExpressions.Match m =
                    System.Text.RegularExpressions.Regex.Match(s, "\"value\"\\s*:\\s*\"([A-Za-z0-9+/=]+)\"");
                if (!m.Success) return "";
                return Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            }
        }
        catch
        {
            lock (pickCdpPenalty) { pickCdpPenalty[wsUrl] = Environment.TickCount + 60000; }
            return null;
        }
    }

    // 入口: 目标是 CDP 应用 → 遍历 page/iframe targets，先读到非空选区即返回
    static string PickViaCdp(IntPtr fgAt)
    {
        int port = PickCdpPort(fgAt);
        if (port == 0) return "";
        int t0 = Environment.TickCount;
        try
        {
            string list = CdpHttpGet("http://127.0.0.1:" + port + "/json/list", 600);
            if (list.Length == 0)
            {
                Log("pick cdp: /json/list fail port=" + port + " " + (Environment.TickCount - t0) + "ms (app 没带调试口启动?)");
                return "";
            }
            // 只扫 type=page/iframe 的 target: ZCode 这类还挂 4 个 worker(无 window/getSelection, 可能不回包), 按 URL 前缀滤不掉——
            // worker 的 ws 路径同样是 /devtools/page/, 必须看 "type" 字段
            List<string> urls = new List<string>();
            foreach (System.Text.RegularExpressions.Match em in System.Text.RegularExpressions.Regex.Matches(list, "\\{[^{}]*\\}"))
            {
                string entry = em.Value;
                if (!System.Text.RegularExpressions.Regex.IsMatch(entry, "\"type\"\\s*:\\s*\"(page|iframe)\"")) continue;
                System.Text.RegularExpressions.Match wm = System.Text.RegularExpressions.Regex.Match(entry, "\"webSocketDebuggerUrl\"\\s*:\\s*\"(ws://[^\"]+)\"");
                if (wm.Success) urls.Add(wm.Groups[1].Value);
            }
            string got = CdpSweep(urls, t0);
            if (string.IsNullOrWhiteSpace(got))
            {
                // 选区落定晚于读取的兜底: 双击选词在 mouseup 后一瞬才进 DOM, 80ms 后重扫
                Thread.Sleep(80);
                got = CdpSweep(urls, t0);
            }
            if (!string.IsNullOrWhiteSpace(got))
            {
                Log("pick cdp: " + (Environment.TickCount - t0) + "ms port=" + port + " chars=" + got.Length + " | " + PickOneLine(got));
                return got;
            }
            Log("pick cdp: " + (Environment.TickCount - t0) + "ms port=" + port + " targets=" + urls.Count + " sel empty(含重试)");
            return "";
        }
        catch (Exception ex) { Log("pick cdp err: " + ex.Message); return ""; }
    }

    // 全局预算 350ms: 死 target 各自 250ms 超时会把取词拖到秒级(实测 5 targets 烧 6.6s, pickBusy 锁死吞掉后续双击=成功率腰斩)
    static string CdpSweep(List<string> urls, int t0)
    {
        foreach (string u in urls)
        {
            lock (pickCdpPenalty)
            {
                int until;
                if (pickCdpPenalty.TryGetValue(u, out until) && Environment.TickCount < until) continue;
            }
            if (Environment.TickCount - t0 > 350) break;
            string got = CdpEvalSelection(u);
            if (!string.IsNullOrEmpty(got)) return got;
        }
        return "";
    }

    // click=true = 用户只是**单击**(没有划选)。2026-09-07 老大要的"点击时也弹出悬浮球"。
    // 只允许无副作用取词: ①UIA 拿光标所在的那个词(Word 单元) ②退一步读现有选区。
    // **绝不发 Ctrl+C**: 单击不产生选区, 这时全局复制 = 把用户刚点中的输入框里的东西/别处内容当"词"抓走。
    // ---- 取词 2: 剪贴板链（2026-09-11 老大拍板，STranslate MIT 参考 _ref_clipboardhelper.cs）----
    // UIA 读不到选区的场景(Electron 无障碍树懒加载)用模拟 Ctrl+C 读真选区。
    // 姿势: 快照(文本+剪贴板序列号) → 清残留修饰键 → SendInput Ctrl+C → 10ms 轮询序列号 ≤500ms →
    // 变了等 30ms → 读文本。判据: 序列号变 ∥ 文本变 ∥ 原剪贴板空 → 出字; 否则返回空=静默放弃。
    // 红线: 调用方必须已排除终端(conhost GetSelection 有内部复制副作用+^C 中断)。
    // 剪贴板不还原(老大拍板): 划完剪贴板=选中的文本, 「划完能粘」是特性。
    static string PickViaClipboard()
    {
        string original = "";
        uint seq0 = 0;
        try { original = System.Windows.Forms.Clipboard.GetText() ?? ""; } catch { }
        try { seq0 = GetClipboardSequenceNumber(); } catch { }

        // 清残留修饰键(STranslate 注释: 不清=模拟复制失败主因): L/R Ctrl、Alt、Win、Shift 全 KeyUp
        keybd_event(0xA2, 0, 2, UIntPtr.Zero); keybd_event(0xA3, 0, 2, UIntPtr.Zero);
        keybd_event(0xA4, 0, 2, UIntPtr.Zero); keybd_event(0xA5, 0, 2, UIntPtr.Zero);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero); keybd_event(0x5C, 0, 2, UIntPtr.Zero);
        keybd_event(0xA0, 0, 2, UIntPtr.Zero); keybd_event(0xA1, 0, 2, UIntPtr.Zero);
        keybd_event(0x10, 0, 2, UIntPtr.Zero);

        // Ctrl+C: VK+扫描码一起给(纯 VK 无扫描码会被 Chromium 无视——旧版零成功根因)
        keybd_event(0x11, 0x1D, 0, UIntPtr.Zero);            // Ctrl down
        keybd_event(0x43, 0x2E, 0, UIntPtr.Zero);            // C down
        keybd_event(0x43, 0x2E, 2, UIntPtr.Zero);            // C up
        keybd_event(0x11, 0x1D, 2, UIntPtr.Zero);            // Ctrl up

        bool changed = false;
        int t0 = Environment.TickCount;
        while (Environment.TickCount - t0 < 500)
        {
            System.Threading.Thread.Sleep(10);
            try { if (GetClipboardSequenceNumber() != seq0) { changed = true; break; } } catch { }
        }
        if (changed) System.Threading.Thread.Sleep(30);      // 内容稳定
        string now = "";
        try { now = System.Windows.Forms.Clipboard.GetText() ?? ""; } catch { }
        if (changed || now != original || string.IsNullOrEmpty(original))
        {
            string t = (now ?? "").Trim();
            return t.Length <= 1 ? "" : t;                   // 单字符当没取到
        }
        return "";                                           // 剪贴板没变 = 复制失败, 静默
    }

    static void PickCaptureWork(int x, int y, bool click, IntPtr fgAt, int actBase, int x0, int y0)
    {
        string text = "", how = "";
        int t0 = Environment.TickCount;
        try
        {
            // 诊断行: 失败时一眼看出前台到底是谁/CDP 端口解析成什么(交替失败=前台被自家悬浮窗占用的实锤路径)
            {
                string fpn = "?"; int fgp = 0;
                try { uint fp; GetWindowThreadProcessId(fgAt, out fp); fgp = (int)fp; fpn = System.Diagnostics.Process.GetProcessById((int)fp).ProcessName; } catch { }
                Log("pick(" + (click ? "click" : "drag") + ") fg=[" + fpn + "] pid=" + fgp + " cdp=" + PickCdpPort(fgAt) + " pt=" + x + "," + y);
            }
            if (click)
            {
                // 浏览器: 单击/双击取词归扩展(pick-inject) —— 原生不发; 终端: 红线不发
                if (PickIsBrowser(fgAt)) { Log("pick(click): browser target, skipped"); return; }
                if (PickIsTerminal(fgAt)) { Log("pick(click): terminal target, skipped"); return; }
                // CDP 直读优先(方案D): Electron 调试口毫秒级读真选区; 空=落回 UIA(聊天输入框选区 getSelection 看不到, 还得靠 UIA)
                try { string cdp = PickViaCdp(fgAt); if (!string.IsNullOrWhiteSpace(cdp)) { text = cdp; how = "cdp"; } } catch { }
                int uiaCost = 0;
                if (string.IsNullOrWhiteSpace(text))
                {
                    int tU = Environment.TickCount;
                    try { text = PickWordAtPoint(x, y); if (!string.IsNullOrWhiteSpace(text)) how = "uia词"; } catch { }
                    if (string.IsNullOrWhiteSpace(text))
                    { try { text = PickTextUia(x, y); if (!string.IsNullOrWhiteSpace(text)) how = "uia选区"; } catch { } }
                    uiaCost = Environment.TickCount - tU;
                }
                // 剪贴板链 (2026-09-11 老大拍板 STranslate 方案, _ref_clipboardhelper.cs):
                // Electron 聊天窗 UIA 读选区时灵时不灵(无障碍树懒加载) → 模拟 Ctrl+C 读真选区。
                // 判据: 序列号变 ∥ 文本变 ∥ 原剪贴板空 → 出字; 否则静默。不还原=划完能粘(老大拍板)。
                if (string.IsNullOrWhiteSpace(text))
                {
                    string clip = PickViaClipboard();
                    if (!string.IsNullOrWhiteSpace(clip)) { text = clip; how = "clip"; }
                    Log("pick(click): clip " + (Environment.TickCount - t0) + "ms | " + (string.IsNullOrWhiteSpace(clip) ? "(no change)" : PickOneLine(clip)));
                }
                // 退避只看 UIA 耗时: CDP+clip 全落空的正常 miss ~900ms 不该触发 30s 禁言(那是 UIA 挂死场景的保险)
                if (uiaCost > 700) { pickSlowUntil = Environment.TickCount + 30000; Log("pick(click): slow UIA target " + uiaCost + "ms, backoff 30s"); }
                if (string.IsNullOrWhiteSpace(text)) return;   // 取不到字 = 不出点(空点已废, 老大不满意"出的点没内容")
                if (text.Trim().Length > 200) text = text.Substring(0, 200);
            }
            else
            {
                // 拖选: UIA 优先(本地应用), 剪贴板兜底(Electron/聊天类)。
                // OCR 永久退出取词链(老大 2026-09-11 终审: 识别的全是错误, 不可靠)。
                // 浏览器划选归 Edge 扩展所有 —— 原生让位。
                bool terminal = PickIsTerminal(fgAt);
                bool browser = PickIsBrowser(fgAt);
                if (!terminal && !browser)
                {
                    // CDP 直读优先(方案D): Electron 拖选=文档级选区, getSelection 必有; 空才落 UIA/剪贴板
                    try { string cdp = PickViaCdp(fgAt); if (!string.IsNullOrWhiteSpace(cdp)) { text = cdp; how = "cdp"; } } catch { }
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        try { text = PickTextUia(x, y); if (!string.IsNullOrWhiteSpace(text)) how = "uia"; } catch { }
                    }
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        string clip = PickViaClipboard();
                        if (!string.IsNullOrWhiteSpace(clip)) { text = clip; how = "clip"; }
                        Log("pick(drag): clip fallback | " + (string.IsNullOrWhiteSpace(clip) ? "(no change)" : PickOneLine(clip)));
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                // 全链(UIA+剪贴板)拿不到字 = 静默放弃, 不出空点(老大: "出的点没内容"=浪费信任)
                Log("pick: text empty (" + (Environment.TickCount - t0) + "ms) via " + how + " — silent give-up, no ball");
                return;
            }
            text = text.Trim();
            if (text.Length > 2000) text = text.Substring(0, 2000);
            pickSel = text;
            pickLastX = x; pickLastY = y;
            pickShownAt = Environment.TickCount;
            Log("pick" + (click ? "(click)" : "") + ": " + text.Length + " chars via " + how +
                " in " + (Environment.TickCount - t0) + "ms | " + PickOneLine(text));
            Control s = pickSync;
            // 取词成功 → 出点(唯一出点路径; 双击=click 分支也算)
            if (s != null && s.IsHandleCreated)
                s.BeginInvoke(new MethodInvoker(delegate { ShowPickDot(x, y); }));
        }
        catch (Exception ex) { Log("pick capture err: " + ex.Message); }
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

    // ---- 取词 3: OCR 选区截图 (2026-09-08 主力, 老大终审: Ctrl+C 抢剪贴板方案作废) ----
    // 划选矩形直接 GDI 截屏 → 2x 放大 → OCR。零按键注入、零剪贴板占用、零副作用,
    // 浏览器/终端/图片/PDF 全局生效。baidu 高精度为主(provider 配置复用截图 OCR), Ollama 兜底。
    static string PickOcrRect(int x0, int y0, int x1, int y1)
    {
        try
        {
            int pad = 4;
            int lx = Math.Min(x0, x1) - pad, ly = Math.Min(y0, y1) - pad;
            int w = Math.Abs(x1 - x0) + pad * 2, h = Math.Abs(y1 - y0) + pad * 2;
            Rectangle vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (lx < vs.X) { w -= vs.X - lx; lx = vs.X; }
            if (ly < vs.Y) { h -= vs.Y - ly; ly = vs.Y; }
            if (lx + w > vs.X + vs.Width) w = vs.X + vs.Width - lx;
            if (ly + h > vs.Y + vs.Height) h = vs.Y + vs.Height - ly;
            if (w < 8 || h < 6) return "";
            using (Bitmap raw = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(raw))
                    g.CopyFromScreen(lx, ly, 0, 0, new Size(w, h));
                // 2x 放大: 划选的网页/终端小字, 放大后 OCR 精度明显提升
                using (Bitmap big = new Bitmap(w * 2, h * 2))
                {
                    using (Graphics g2 = Graphics.FromImage(big))
                    {
                        g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g2.DrawImage(raw, 0, 0, w * 2, h * 2);
                    }
                    var task = OcrProvider().RecognizeAsync(big);
                    if (!task.Wait(9000)) { Log("pick ocr: timeout 9s"); return ""; }
                    return (task.Result ?? "").Trim();
                }
            }
        }
        catch (Exception ex) { Log("pick ocr err: " + ex.Message); return ""; }
    }

    // Edge MV3 扩展 POST /pick-inject 入口: 只收文字, 球贴光标(方案A, 零 DPI 换算)
    // 返回 null=成功; 非 null=错误信息
    static string PickInject(string text)
    {
        try
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return "empty text";
            if (pickEnabled != 1) return "pick disabled";
            if (text.Length > 2000) text = text.Substring(0, 2000);
            POINT p;
            if (!GetCursorPos(out p)) return "GetCursorPos failed";
            pickSel = text;
            pickLastX = p.x; pickLastY = p.y;
            pickShownAt = Environment.TickCount;
            pickBusy = 0; // 扩展路径无 OCR 等待
            Control s = pickSync;
            if (s == null || !s.IsHandleCreated) return "pick UI not ready";
            s.BeginInvoke(new MethodInvoker(delegate { ShowPickDot(p.x, p.y); }));
            Log("pick-inject: " + text.Length + " chars @ " + p.x + "," + p.y + " | " + PickOneLine(text));
            return null;
        }
        catch (Exception ex) { Log("pick-inject err: " + ex.Message); return ex.Message; }
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
                    // 2A(老大裁决): hover 不再自动展开, 只响应点击; 记时间仅为诊断
                    if (pickHoverSince < 0) pickHoverSince = Environment.TickCount;
                }
                else pickHoverSince = -1;
                // 提问框开着(用户正在打字)时不许超时收起整组; 划选文字未就绪(OCR 推理中)也不收点
                if (!pickOverDot && pickAskWin == null && pickSel.Length > 0 && Environment.TickCount - pickShownAt > 4000) PickDismiss();
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
    // 钩子喂来的滚轮 -> 卡片正文滚动(标题栏上不滚)
    static void PickCardWheel(int x, int y, int delta)
    {
        PickCardForm c = pickCard as PickCardForm;
        int[] r = PickCardRect;
        if (c == null || r == null || !c.IsHandleCreated) return;
        if (y - r[1] <= PickCardForm.HEAD_H) return;
        c.ScrollBody(delta);
    }

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
        // 文字还在后台取词(OCR)没回来: 提示稍候, 不做动作不关菜单
        if (string.IsNullOrEmpty(pickSel))
        { Log("pick action " + act + ": text not ready yet"); TrayNotify("还在取词", "文字识别中，一两秒后再点"); return; }
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
                // 提示词三层累加(2026-09-07 老大定稿): 内置人设(写死) + 用户偏好(设置, 可空) + 本次要求(弹框, 可空)
                // 冲突仲裁写进人设: 本次要求 > 用户偏好 > 基础规则; 偏好为空/无意义直接忽略 —— 谁乱填都不会把 AI 带偏
                string extra = (Cfg("pick.askPrompt", "") ?? "").Trim();
                string persona =
                    "你是用户的桌面划词助手。用户划选了下面的内容，先判断它是什么类型，再按对应方式回答：\n" +
                    "- 英文单词/句子 → 给中文翻译；单词再附词性和一个例句\n" +
                    "- 术语/概念 → 一句话讲清是什么 + 一个例子\n" +
                    "- 报错/警告信息 → 最可能的原因 + 怎么修\n" +
                    "- 代码 → 这段代码干什么、有没有坑\n" +
                    "- 型号/数字 → 它是什么、关键参数\n" +
                    "- 其他 → 一句话总结要点\n" +
                    "要求：中文、150 字以内、直接说结论；不要复述原文；不要\"好的\"\"以下是\"这类开场白。\n" +
                    "优先级规则：【本次要求】>【用户偏好】> 上面的基础规则；若【用户偏好】为空或只是无意义的话，直接忽略它。";
                string p = persona + "\n\n";
                if (extra.Length > 0) p += "【用户偏好】" + extra + "\n\n";
                p += "【本次要求】" + (question.Length > 0 ? question : "按基础规则智能解释下面的内容") + "\n\n";
                p += "【划选内容】\n" + text;
                string prompt = p;
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
