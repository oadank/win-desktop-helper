using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// T2 自动化能力扩展: 窗口管理 / 鼠标按住拖拽 / 键按住 / 剪贴板写入 / UIA 元素树
// 与 shot-service.cs 同属 ShotService 类(partial), 共享 Log/FindWindowByTitle/KeyEvent/JsonEscape 等
// 编译需追加引用 (GAC): UIAutomationClient.dll + UIAutomationTypes.dll
partial class ShotService
{
    // ==================== 窗口管理 ====================
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9, SW_SHOW = 5;
    const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    // 置前: Windows 前台锁会拒绝后台进程的 SetForegroundWindow —
    // 三级策略: 直接置前 -> AttachThreadInput 挂前台输入队列再置前 -> 模拟 ALT 松开解锁再置前
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT pt, uint flags); // CWP_SKIPINVISIBLE|CWP_SKIPTRANSPARENT
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT pt);
    const uint CWP_SKIPINVISIBLE = 0x1, CWP_SKIPTRANSPARENT = 0x2;

    // 光标处的检测目标窗口 (跳过自己/工具窗/最小化/cloaked 幻影/桌面), 并下钻到最深层子窗口。
    // PixPin 同款灵敏度的关键 = 子窗口下钻: Electron/浏览器/IDE 的侧栏/正文/输入区都是子 HWND,
    // 只回顶层时整个应用一个大框 (实测 zcode 高亮全窗); 下钻后高亮贴到内容区。深度上限防病态嵌套。
    static IntPtr WindowFromPointEx(POINT p, IntPtr exclude)
    {
        IntPtr top = IntPtr.Zero;
        uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        EnumWindows(delegate (IntPtr h, IntPtr lp)
        {
            try
            {
                if (h == exclude || !IsWindowVisible(h)) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                uint wpid; GetWindowThreadProcessId(h, out wpid);
                if (wpid == myPid) return true; // 自家浮窗(结果/热键宿主)不参加检测
                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                if (r.Left < -30000) return true; // 最小化
                if (p.x >= r.Left && p.x < r.Right && p.y >= r.Top && p.y < r.Bottom)
                {
                    int cloaked;
                    if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true; // UWP 幻影窗: rect 在但不可见
                    StringBuilder cn = new StringBuilder(64);
                    GetClassNameW(h, cn, 64);
                    string cname = cn.ToString();
                    if (cname == "Progman" || cname == "WorkerW") return true; // 桌面: 高亮全屏无意义
                    top = h; return false;
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        if (top == IntPtr.Zero) return IntPtr.Zero;
        // 子窗口下钻: ChildWindowFromPointEx 的 pt 是父窗口客户区坐标, 每层都要 ScreenToClient
        IntPtr cur = top;
        for (int i = 0; i < 12; i++)
        {
            POINT cp = p; ScreenToClient(cur, ref cp);
            IntPtr child = ChildWindowFromPointEx(cur, cp, CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
            if (child == IntPtr.Zero || child == cur) break;
            RECT cr;
            if (!GetWindowRect(child, out cr) || cr.Right <= cr.Left || cr.Bottom <= cr.Top) break;
            cur = child;
        }
        return cur;
    }

    // 冻结修复用: 窗口放置状态
    [StructLayout(LayoutKind.Sequential)]
    struct WpRect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public uint length; public uint flags; public uint showCmd;
        public System.Drawing.Point ptMinPosition, ptMaxPosition; public WpRect rcNormalPosition;
    }
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr lprc, IntPtr hrgn, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLongPtr64(IntPtr h, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int nIndex, IntPtr dwNewLong);
    const int GWL_STYLE = -16;
    const long WS_VISIBLE = 0x10000000L, WS_EX_TRANSPARENT = 0x20L, WS_EX_NOACTIVATE = 0x08000000L;
    const uint RDW_INVALIDATE = 0x0001, RDW_UPDATENOW = 0x0100, RDW_ALLCHILDREN = 0x0080, RDW_FRAME = 0x0400;

    // 唤回窗口并真正解冻。外部硬 ShowWindow 显示 Electron 隐藏窗, 应用内部状态常常没同步:
    // 残留 WS_EX_TRANSPARENT(鼠标穿透, 看得见点不动) / WS_EX_NOACTIVATE / 缺 WS_VISIBLE / 放置状态还是隐藏。
    static string WinActivate(IntPtr h)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        bool wasHidden = !IsWindowVisible(h);
        bool wasIconic = IsIconic(h);
        var fixes = new List<string>();

        // 1) 清掉"看得见点不动"的样式残留
        try
        {
            long exv = GetWindowLongPtr64(h, GWL_EXSTYLE).ToInt64();
            long remove = 0;
            if ((exv & WS_EX_TRANSPARENT) != 0) { remove |= WS_EX_TRANSPARENT; fixes.Add("cleared_WS_EX_TRANSPARENT(鼠标穿透,点不动的主因)"); }
            if ((exv & WS_EX_NOACTIVATE) != 0) { remove |= WS_EX_NOACTIVATE; fixes.Add("cleared_WS_EX_NOACTIVATE(拒绝激活)"); }
            if (remove != 0) SetWindowLongPtr64(h, GWL_EXSTYLE, new IntPtr(exv & ~remove));
            long st = GetWindowLongPtr64(h, GWL_STYLE).ToInt64();
            if ((st & WS_VISIBLE) == 0) { SetWindowLongPtr64(h, GWL_STYLE, new IntPtr(st | WS_VISIBLE)); fixes.Add("added_WS_VISIBLE"); }
        }
        catch { }

        // 2) 用 SetWindowPlacement 恢复: 走应用自己的窗口过程, 比裸 ShowWindow 更容易让内部状态跟上
        if (wasHidden || wasIconic)
        {
            try
            {
                WINDOWPLACEMENT wp = new WINDOWPLACEMENT();
                wp.length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                if (GetWindowPlacement(h, ref wp)) { wp.showCmd = 1; wp.flags = 0; SetWindowPlacement(h, ref wp); fixes.Add("SetWindowPlacement(SW_SHOWNORMAL)"); }
            }
            catch { }
            ShowWindow(h, SW_RESTORE);
            ShowWindow(h, SW_SHOW);
        }
        // 3) 强制重绘解冻 (不重绘的话画面可能是旧帧, 像冻住)
        try { RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_FRAME); } catch { }

        bool ok = SetForegroundWindow(h);
        string via = "direct";
        if (!ok)
        {
            IntPtr fg = GetForegroundWindow();
            uint fgPid; uint fgTid = GetWindowThreadProcessId(fg, out fgPid);
            uint myTid = GetCurrentThreadId();
            if (fgTid != 0 && fgTid != myTid)
            {
                AttachThreadInput(myTid, fgTid, true);
                ok = SetForegroundWindow(h);
                AttachThreadInput(myTid, fgTid, false);
                via = "attach";
            }
        }
        if (!ok)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);           // ALT down: 解除前台锁
            ok = SetForegroundWindow(h);
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            via = "alt";
        }
        Log("win activate: " + h + " ok=" + ok + " via=" + via + " fixes=" + string.Join("|", fixes.ToArray()));
        Thread.Sleep(250);
        IntPtr nowFg = GetForegroundWindow();
        bool front = nowFg == h;
        string fixStr = fixes.Count > 0 ? ",\"fixes\":[\"" + string.Join("\",\"", fixes.ToArray()) + "\"]" : "";
        string warn = "";
        if (!front)
            warn = ",\"warn\":\"激活后前台窗口不是它(当前前台 hwnd=" + nowFg.ToInt64() + ") — 这次唤回是假的, 点它会没反应。换 tray_click 走应用自身恢复, 或关掉重开\"";
        else if (wasHidden)
            warn = ",\"warn\":\"这个窗口激活前是隐藏态。已清穿透样式+强制重绘, 若仍点不动: 用 tray_click 双击托盘图标让应用自己恢复, 或关闭重开(隐藏态唤回的应用内部状态可能没跟上)\"";
        return "{\"ok\":true,\"activated\":" + (ok ? "true" : "false") + ",\"via\":\"" + via + "\",\"foreground\":" + (front ? "true" : "false") + ",\"wasHidden\":" + (wasHidden ? "true" : "false") + fixStr + warn + "}";
    }

    static string WinShow(IntPtr h, int cmd, string name)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        bool ok = ShowWindow(h, cmd);
        return "{\"ok\":true,\"" + name + "\":" + (ok ? "true" : "false") + "}";
    }

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    static string WinClose(IntPtr h)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        if (!IsWindow(h)) return "{\"ok\":false,\"error\":\"invalid handle\"}"; // W3: 无效句柄曾报 closed:true
        // PostMessage 不等目标线程: SendMessage(WM_CLOSE) 会同步等待, 遇未保存对话框/忙窗口把 HTTP 挂 35s (实测)
        if (!PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) return "{\"ok\":false,\"error\":\"post failed\"}";
        // 校验 2s 内窗口真消失, 返回值反映真实结果
        for (int t = 0; t < 10; t++)
        {
            Thread.Sleep(200);
            if (!IsWindow(h) || !IsWindowVisible(h)) return "{\"ok\":true,\"closed\":true}";
        }
        Log("win close: window still alive (可能未保存对话框)");
        return "{\"ok\":true,\"closed\":false,\"hint\":\"window still alive - 可能有未保存对话框, 用 ui_find name=保存 定位处理\"}";
    }

    static string WinMove(IntPtr h, int x, int y, int w, int hh)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        if (!IsWindow(h)) return "{\"ok\":false,\"error\":\"invalid handle\"}";
        if (w < 50 || hh < 50) return "{\"ok\":false,\"error\":\"w/h must be >= 50\"}"; // W1: 零宽高曾被静默吞
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        bool clamped = false;
        if (x < vs.X) { x = vs.X; clamped = true; }
        if (y < vs.Y) { y = vs.Y; clamped = true; }
        if (x + w > vs.Right) { x = Math.Max(vs.X, vs.Right - w); clamped = true; }
        if (y + hh > vs.Bottom) { y = Math.Max(vs.Y, vs.Bottom - hh); clamped = true; } // W1: 负坐标 clamp 到可视区, 窗口不再飞丢
        bool iconic = false;
        try { iconic = IsIconic(h); } catch { }
        if (iconic) { ShowWindow(h, 9); Thread.Sleep(150); } // W2: 最小化窗口 SetWindowPos 无效, 先 restore 再 move
        bool ok = MoveWindow(h, x, y, w, hh, true);
        RECT rc; GetWindowRect(h, out rc);
        return "{\"ok\":true,\"moved\":" + (ok ? "true" : "false") + (clamped ? ",\"clamped\":true" : "") + (iconic ? ",\"restoredFirst\":true" : "") +
               ",\"rect\":{\"x\":" + rc.Left + ",\"y\":" + rc.Top + ",\"w\":" + (rc.Right - rc.Left) + ",\"h\":" + (rc.Bottom - rc.Top) + "}}";
    }

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

    // 窗口贴靠(分屏) —— 把 Win+方向键那套变成工具默认能力
    //   pos: left 左半屏 / right 右半屏 / top 上半 / bottom 下半
    //        topleft 左上 / topright 右上 / bottomleft 左下 / bottomright 右下 (四分之一屏)
    //        max 最大化 / min 最小化 / restore 还原
    //   monitor: 1..n 指定第几块屏 / next 下一块 / prev 上一块 (不给 = 窗口当前所在屏)
    // 半屏用 MoveWindow 直接算, 比模拟按键稳: 不受前台焦点限制, 多屏可精确指定, 且返回实际 rect 可验证
    static string WinSnap(IntPtr h, string pos, string mon, int cols, int col, int cspan, int rows, int row, int rspan)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        if (!IsWindow(h)) return "{\"ok\":false,\"error\":\"invalid handle: 窗口已关闭, 重新 list_apps 采样\"}";
        pos = (pos == "" ? "max" : pos).ToLowerInvariant();
        var screens = System.Windows.Forms.Screen.AllScreens;
        int monIdx = -1;
        if (mon != "")
        {
            int mi;
            if (int.TryParse(mon, out mi) && mi >= 1 && mi <= screens.Length) monIdx = mi - 1;
            else if (mon == "next" || mon == "prev")
            {
                int ci = Array.IndexOf(screens, System.Windows.Forms.Screen.FromHandle(h));
                if (ci < 0) ci = 0;
                monIdx = mon == "next" ? (ci + 1) % screens.Length : (ci - 1 + screens.Length) % screens.Length;
            }
            else return "{\"ok\":false,\"error\":\"monitor 无效: 填 1.." + screens.Length + " 或 next/prev (本机 " + screens.Length + " 块屏)\"}";
        }
        var sc = monIdx >= 0 ? screens[monIdx] : System.Windows.Forms.Screen.FromHandle(h);
        var wa = sc.WorkingArea;
        int x = wa.X, y = wa.Y, w = wa.Width, hh = wa.Height;
        int halfW = wa.Width / 2, restW = wa.Width - halfW, halfH = wa.Height / 2, restH = wa.Height - halfH;

        // 网格模式: cols/col/colspan + rows/row/rowspan —— 一套参数覆盖 Win11 Snap Layouts 全部布局 + 任意比例
        //   横三等分: cols=3 col=1|2|3      竖屏上中下: rows=3 row=1|2|3
        //   2/3 左:   cols=3 col=1 colspan=2        四等分: cols=2 rows=2 col/row 组合
        //   左半+右上: cols=2 col=1 / cols=4 col=3 rows=2 row=1
        bool gridMode = cols > 0 || col > 0 || cspan > 0 || rows > 0 || row > 0 || rspan > 0;
        if (gridMode)
        {
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;
            if (col < 1) col = 1;
            if (row < 1) row = 1;
            if (cspan < 1) cspan = 1;
            if (rspan < 1) rspan = 1;
            if (cols > 12 || rows > 12) return "{\"ok\":false,\"error\":\"cols/rows 最大 12\"}";
            if (col > cols) return "{\"ok\":false,\"error\":\"col 必须 <= cols (你填了 col=" + col + " cols=" + cols + ")\"}";
            if (row > rows) return "{\"ok\":false,\"error\":\"row 必须 <= rows (你填了 row=" + row + " rows=" + rows + ")\"}";
            if (col + cspan - 1 > cols) return "{\"ok\":false,\"error\":\"col+colspan-1 不能超过 cols\"}";
            if (row + rspan - 1 > rows) return "{\"ok\":false,\"error\":\"row+rowspan-1 不能超过 rows\"}";
            int cw = wa.Width / cols, ch = wa.Height / rows;
            int gx = wa.X + (col - 1) * cw;
            int gw = (col + cspan - 1 == cols) ? (wa.Width - (col - 1) * cw) : cspan * cw;
            int gy = wa.Y + (row - 1) * ch;
            int gh = (row + rspan - 1 == rows) ? (wa.Height - (row - 1) * ch) : rspan * ch;
            bool gi = false, gz = false;
            try { gi = IsIconic(h); } catch { }
            try { gz = IsZoomed(h); } catch { }
            if (gi || gz) { ShowWindow(h, SW_RESTORE); Thread.Sleep(150); }
            MoveWindow(h, gx, gy, gw, gh, true);
            Thread.Sleep(180);
            RECT grc; GetWindowRect(h, out grc);
            Log("win snap grid: " + h + " " + cols + "x" + rows + " cell " + col + "," + row + " span " + cspan + "x" + rspan);
            return "{\"ok\":true,\"pos\":\"grid\",\"mode\":\"move\",\"monitor\":" + (Array.IndexOf(screens, System.Windows.Forms.Screen.FromHandle(h)) + 1) +
                   ",\"monitors\":" + screens.Length +
                   ",\"grid\":{\"cols\":" + cols + ",\"col\":" + col + ",\"colspan\":" + cspan + ",\"rows\":" + rows + ",\"row\":" + row + ",\"rowspan\":" + rspan + "}" +
                   ",\"target\":{\"x\":" + gx + ",\"y\":" + gy + ",\"w\":" + gw + ",\"h\":" + gh + "}" +
                   ",\"rect\":{\"x\":" + grc.Left + ",\"y\":" + grc.Top + ",\"w\":" + (grc.Right - grc.Left) + ",\"h\":" + (grc.Bottom - grc.Top) + "}}";
        }

        string mode = "move";
        switch (pos)
        {
            case "left": w = halfW; break;
            case "right": x = wa.X + halfW; w = restW; break;
            case "top": hh = halfH; break;
            case "bottom": y = wa.Y + halfH; hh = restH; break;
            case "topleft": w = halfW; hh = halfH; break;
            case "topright": x = wa.X + halfW; w = restW; hh = halfH; break;
            case "bottomleft": w = halfW; y = wa.Y + halfH; hh = restH; break;
            case "bottomright": x = wa.X + halfW; w = restW; y = wa.Y + halfH; hh = restH; break;
            case "max": case "maximize": mode = "max"; break;
            case "min": case "minimize": mode = "min"; break;
            case "restore": mode = "restore"; break;
            case "sysleft": case "sysright": case "systop": case "sysbottom":
            case "systopleft": case "systopright": case "sysbottomleft": case "sysbottomright":
                // 真·系统 Snap Layouts 键盘流: Win+方向 → Esc 退出 Snap Assist
                // (老大: 不发 Esc 会拉着其它窗一起排)
                mode = "syskbd";
                break;
            default: return "{\"ok\":false,\"error\":\"pos 无效: left/right/top/bottom/topleft/topright/bottomleft/bottomright/max/min/restore/sysleft|sysright|systop|sysbottom|systopleft|systopright|sysbottomleft|sysbottomright (sys*=真系统吸附+Esc)\"}";
        }
        bool iconic = false, zoomed = false;
        try { iconic = IsIconic(h); } catch { }
        try { zoomed = IsZoomed(h); } catch { }
        if (mode == "min") ShowWindow(h, SW_MINIMIZE);
        else if (mode == "syskbd")
        {
            // 真·系统吸附: 先激活目标窗, 再 Win+方向; 结束必 Esc 关 Snap Assist
            if (iconic || zoomed) { ShowWindow(h, SW_RESTORE); Thread.Sleep(150); }
            SetForegroundWindow(h);
            Thread.Sleep(80);
            string vkArrow = "0x25"; // left
            if (pos == "sysright" || pos == "systopright" || pos == "sysbottomright") vkArrow = "0x27";
            else if (pos == "systop" || pos == "systopleft" || pos == "systopright") vkArrow = "0x26";
            else if (pos == "sysbottom" || pos == "sysbottomleft" || pos == "sysbottomright") vkArrow = "0x28";
            // Win+方向: 先按 Win, 再主键, 再松主键, 再松 Win
            keybd_event(0x5B, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(20);
            ushort av = (ushort)System.Convert.ToUInt16(vkArrow, 16);
            keybd_event((byte)av, 0, 0, UIntPtr.Zero);
            keybd_event((byte)av, 0, 2, UIntPtr.Zero);
            System.Threading.Thread.Sleep(120);
            keybd_event(0x5B, 0, 2, UIntPtr.Zero);
            System.Threading.Thread.Sleep(350);
            // Snap Assist 会弹出来选「另一个窗贴到另一半」——必须 Esc 否则会误贴其它窗
            keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero);
            System.Threading.Thread.Sleep(80);
            Thread.Sleep(180);
            RECT src; GetWindowRect(h, out src);
            int nowMonS = Array.IndexOf(screens, System.Windows.Forms.Screen.FromHandle(h)) + 1;
            Log("win snap syskbd: " + h + " pos=" + pos + " mon=" + nowMonS + " (Esc closed Snap Assist)");
            return "{\"ok\":true,\"pos\":\"" + pos + "\",\"mode\":\"syskbd\",\"monitor\":" + nowMonS + ",\"monitors\":" + screens.Length +
                   ",\"rect\":{\"x\":" + src.Left + ",\"y\":" + src.Top + ",\"w\":" + (src.Right - src.Left) + ",\"h\":" + (src.Bottom - src.Top) + "},\"note\":\"system snap + Esc\"}";
        }
        else
        {
            if (iconic || zoomed) { ShowWindow(h, SW_RESTORE); Thread.Sleep(150); } // 最小化/最大化状态下 MoveWindow 无效
            if (mode == "max") ShowWindow(h, SW_MAXIMIZE);
            else MoveWindow(h, x, y, w, hh, true);
        }
        Thread.Sleep(180);
        RECT rc; GetWindowRect(h, out rc);
        int nowMon = Array.IndexOf(screens, System.Windows.Forms.Screen.FromHandle(h)) + 1;
        Log("win snap: " + h + " pos=" + pos + " mon=" + nowMon);
        return "{\"ok\":true,\"pos\":\"" + pos + "\",\"mode\":\"" + mode + "\",\"monitor\":" + nowMon + ",\"monitors\":" + screens.Length +
               ",\"target\":{\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + hh + "}" +
               ",\"rect\":{\"x\":" + rc.Left + ",\"y\":" + rc.Top + ",\"w\":" + (rc.Right - rc.Left) + ",\"h\":" + (rc.Bottom - rc.Top) + "}}";
    }

    // 等待窗口出现 (轮询 FindWindowByTitle)
    static string WinWait(string title, int timeoutMs)
    {
        if (timeoutMs <= 0) timeoutMs = 10000;
        if (timeoutMs > 60000) timeoutMs = 60000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            IntPtr h = FindWindowByTitle(title);
            if (h != IntPtr.Zero)
            {
                RECT rc; GetWindowRect(h, out rc);
                return "{\"ok\":true,\"found\":true,\"waitedMs\":" + sw.ElapsedMilliseconds +
                       ",\"hwnd\":" + h.ToInt64() + ",\"rect\":{\"x\":" + rc.Left + ",\"y\":" + rc.Top +
                       ",\"w\":" + (rc.Right - rc.Left) + ",\"h\":" + (rc.Bottom - rc.Top) + "}}";
            }
            Thread.Sleep(200);
        }
        return "{\"ok\":true,\"found\":false,\"waitedMs\":" + timeoutMs + "}";
    }

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size); // attr 14 = DWMWA_CLOAKED

    // 应用列表: 可见顶层窗口按 Z 序 (排除 cloaked UWP/工具窗/无标题), front=是否前台 — 对标 computer-use list_apps
    static string AppList()
    {
        IntPtr fg = GetForegroundWindow();
        var list = new List<string>();
        EnumWindows(delegate (IntPtr h, IntPtr lp)
        {
            if (!IsWindowVisible(h) || !IsWindow(h)) return true;
            int cloaked;
            if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true; // 虚拟桌面隐藏/挂起的 UWP
            if (((long)GetWindowLong(h, -20) & 0x80) != 0) return true; // WS_EX_TOOLWINDOW
            StringBuilder sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
            string title = sb.ToString(); if (title.Length == 0) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            string proc = "";
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            RECT rc; GetWindowRect(h, out rc);
            list.Add("{\"hwnd\":" + h.ToInt64() + ",\"pid\":" + pid + ",\"process\":\"" + JsonEscape(proc) +
                     "\",\"title\":\"" + JsonEscape(title) + "\",\"front\":" + (h == fg ? "true" : "false") +
                     ",\"rect\":{\"x\":" + rc.Left + ",\"y\":" + rc.Top + ",\"w\":" + (rc.Right - rc.Left) + ",\"h\":" + (rc.Bottom - rc.Top) + "}}");
            return true;
        }, IntPtr.Zero);
        return "{\"ok\":true,\"count\":" + list.Count + ",\"apps\":[" + string.Join(",", list.ToArray()) + "]}";
    }

    // 按标题关键词枚举全部匹配窗口 (window_info 只回第一个, 这个回全部 — 对标 list_windows)
    static string WinListByTitle(string keyword)
    {
        var list = new List<string>();
        EnumWindows(delegate (IntPtr h, IntPtr lp)
        {
            if (!IsWindowVisible(h) || !IsWindow(h)) return true;
            StringBuilder sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
            string title = sb.ToString();
            if (title.Length == 0 || title.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            string proc = "";
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            RECT rc; GetWindowRect(h, out rc);
            list.Add("{\"hwnd\":" + h.ToInt64() + ",\"pid\":" + pid + ",\"process\":\"" + JsonEscape(proc) +
                     "\",\"title\":\"" + JsonEscape(title) +
                     "\",\"rect\":{\"x\":" + rc.Left + ",\"y\":" + rc.Top + ",\"w\":" + (rc.Right - rc.Left) + ",\"h\":" + (rc.Bottom - rc.Top) + "}}");
            return true;
        }, IntPtr.Zero);
        return "{\"ok\":true,\"title\":\"" + JsonEscape(keyword) + "\",\"count\":" + list.Count + ",\"windows\":[" + string.Join(",", list.ToArray()) + "]}";
    }

    // 按进程枚举可见窗口 (EnumWindows 过滤 pid)
    static string WinListByPid(int pid)
    {
        var list = new List<string>();
        EnumWindows(delegate (IntPtr h, IntPtr lp)
        {
            if (!IsWindowVisible(h)) return true;
            uint wp; GetWindowThreadProcessId(h, out wp);
            if ((int)wp != pid) return true;
            StringBuilder sb = new StringBuilder(256);
            GetWindowTextW(h, sb, 256);
            string title = sb.ToString();
            if (title.Length == 0) return true;
            RECT rc; GetWindowRect(h, out rc);
            list.Add("{\"title\":\"" + JsonEscape(title) + "\",\"hwnd\":" + h.ToInt64() +
                     ",\"rect\":{\"x\":" + rc.Left + ",\"y\":" + rc.Top + ",\"w\":" + (rc.Right - rc.Left) + ",\"h\":" + (rc.Bottom - rc.Top) + "}}");
            return true;
        }, IntPtr.Zero);
        return "{\"ok\":true,\"pid\":" + pid + ",\"count\":" + list.Count + ",\"windows\":[" + string.Join(",", list.ToArray()) + "]}";
    }

    // ==================== 鼠标按住/拖拽 / 光标位置 ====================
    static string MouseDownUp(string button, bool down)
    {
        uint flag;
        if (button == "right") flag = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
        else if (button == "middle") flag = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
        else flag = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
        mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
        return "{\"ok\":true,\"button\":\"" + button + "\",\"action\":\"" + (down ? "down" : "up") + "\"}";
    }

    // 拖拽一条龙: down -> 分步 move -> up
    static string MouseDrag(int x1, int y1, int x2, int y2, int ms)
    {
        if (ms < 50) ms = 50;
        if (ms > 5000) ms = 5000;
        int steps = Math.Max(6, Math.Min(60, ms / 16));
        SetCursorPos(x1, y1);
        Thread.Sleep(80);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        for (int i = 1; i <= steps; i++)
        {
            int x = x1 + (x2 - x1) * i / steps;
            int y = y1 + (y2 - y1) * i / steps;
            SetCursorPos(x, y);
            Thread.Sleep(ms / steps);
        }
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        return "{\"ok\":true,\"from\":{\"x\":" + x1 + ",\"y\":" + y1 + "},\"to\":{\"x\":" + x2 + ",\"y\":" + y2 + "}}";
    }

    static string MousePos()
    {
        POINT p;
        GetCursorPos(out p);
        return "{\"ok\":true,\"x\":" + p.x + ",\"y\":" + p.y + "}";
    }

    // ==================== 键按住 ====================
    static string KeyHold(string spec, int ms)
    {
        if (ms < 50) ms = 50;
        if (ms > 10000) ms = 10000;
        // 复用 PressCombo 的解析: 修饰符们 + 主键, 按住 ms 后释放
        string[] parts = spec.Split('+');
        ushort main = KeyToVk(parts[parts.Length - 1].Trim());
        if (main == 0) return "{\"ok\":false,\"error\":\"bad key: " + JsonEscape(spec) + "\"}";
        var mods = new List<ushort>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string k = parts[i].Trim().ToLowerInvariant();
            ushort m = 0;
            if (k == "shift") m = 0x10;
            else if (k == "ctrl" || k == "control") m = 0x11;
            else if (k == "alt") m = 0x12;
            else if (k == "win") m = 0x5B;
            if (m != 0) mods.Add(m);
        }
        foreach (ushort m in mods) KeyEvent(m, 0, 0);
        KeyEvent(main, 0, 0);
        Thread.Sleep(ms);
        KeyEvent(main, 0, KEYEVENTF_KEYUP);
        for (int i = mods.Count - 1; i >= 0; i--) KeyEvent(mods[i], 0, KEYEVENTF_KEYUP);
        return "{\"ok\":true,\"keys\":\"" + JsonEscape(spec) + "\",\"heldMs\":" + ms + "}";
    }

    // ==================== 剪贴板写入 (STA 线程安全) ====================
    static string ClipboardSetText(string text)
    {
        string err = null;
        Thread t = new Thread(new ThreadStart(delegate
        {
            try { Clipboard.SetText(text); }
            catch (Exception ex) { err = ex.Message; }
        }));
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(3000);
        if (err != null) return "{\"ok\":false,\"error\":\"" + JsonEscape(err) + "\"}";
        return "{\"ok\":true,\"chars\":" + text.Length + "}";
    }

    // 剪贴板图片统一入库: PNG 字节 MD5 作文件名 — 同图去重(3点采样漏检根治) + 重复读零落盘(轮询刷盘根治)
    static string SaveClipboardImage(Image img, out string hash)
    {
        hash = "";
        using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
        {
            img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            byte[] data = ms.ToArray();
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
                hash = BitConverter.ToString(md5.ComputeHash(data)).Replace("-", "");
            string path = System.IO.Path.Combine(ShotDir, "clip_" + hash + ".png");
            if (!System.IO.File.Exists(path)) { try { System.IO.File.WriteAllBytes(path, data); } catch (Exception ex) { Log("clip img save err: " + ex.Message); } }
            return path;
        }
    }

    // 直读当前剪贴板 (多格式): text / image(存PNG返回路径, AI用Read看图) / files(复制的文件路径列表)
    static string ClipboardGet()
    {
        string result = null;
        Thread t = new Thread(new ThreadStart(delegate
        {
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsImage())
                {
                    Image img = System.Windows.Forms.Clipboard.GetImage();
                    if (img != null)
                    {
                        string hash;
                        string path = SaveClipboardImage(img, out hash); // MD5 命名: 同图复用不刷盘
                        string name = System.IO.Path.GetFileName(path);
                        long bytes = new System.IO.FileInfo(path).Length;
                        result = "{\"ok\":true,\"type\":\"image\",\"file\":\"" + JsonEscape(path) +
                                 "\",\"url\":\"http://127.0.0.1:" + 18800 + "/img/" + JsonEscape(name) +
                                 "\",\"w\":" + img.Width + ",\"h\":" + img.Height + ",\"bytes\":" + bytes + ",\"md5\":\"" + hash + "\"}";
                        img.Dispose();
                    }
                }
                else if (System.Windows.Forms.Clipboard.ContainsFileDropList())
                {
                    var files = System.Windows.Forms.Clipboard.GetFileDropList();
                    var arr = new List<string>();
                    foreach (string f in files) arr.Add("\"" + JsonEscape(f) + "\"");
                    result = "{\"ok\":true,\"type\":\"files\",\"count\":" + arr.Count + ",\"files\":[" + string.Join(",", arr.ToArray()) + "]}";
                }
                else if (System.Windows.Forms.Clipboard.ContainsText())
                {
                    string txt = System.Windows.Forms.Clipboard.GetText();
                    result = "{\"ok\":true,\"type\":\"text\",\"chars\":" + txt.Length + ",\"text\":\"" + JsonEscape(txt) + "\"}";
                }
                else result = "{\"ok\":true,\"type\":\"empty\"}";
            }
            catch (Exception ex) { result = "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}"; }
        }));
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(3000);
        return result ?? "{\"ok\":false,\"error\":\"clipboard read timeout\"}";
    }

    // ==================== UI Automation 元素树 ====================
    // 端点:
    //   /ui/tree?title=记事本[&max=400]  -> [{i,type,name,enabled,rect,patterns}]
    //   /ui/click?title=xx&i=12          -> Invoke/Toggle/ExpandCollapse, 失败退坐标点击
    //   /ui/set?title=xx&i=12&value=xxx  -> ValuePattern.SetValue (输入框直写)
    //   /ui/read?title=xx&i=12           -> Name/Value/文本
    // 元素索引 = FindAll(Descendants) 平铺顺序, 供 tree -> click/set 引用

    // ==================== 大 DOM 防挂死 (workbuddy D6: Electron 窗口 FindAll 全树物化永久阻塞, max 无效) ====================
    // TreeWalker 逐节点计数即停: max 真正限制遍历成本; 路由层再包 UiCall 硬超时双保险
    static readonly System.Windows.Automation.TreeWalker UaWalker = System.Windows.Automation.TreeWalker.ControlViewWalker;

    static List<System.Windows.Automation.AutomationElement> WalkLimited(System.Windows.Automation.AutomationElement root, int max)
    {
        var list = new List<System.Windows.Automation.AutomationElement>();
        var stack = new Stack<System.Windows.Automation.AutomationElement>();
        PushChildrenReversed(root, stack);
        while (stack.Count > 0 && list.Count < max)
        {
            var e = stack.Pop();
            list.Add(e);
            PushChildrenReversed(e, stack);
        }
        return list;
    }

    static void PushChildrenReversed(System.Windows.Automation.AutomationElement parent, Stack<System.Windows.Automation.AutomationElement> stack)
    {
        var kids = new List<System.Windows.Automation.AutomationElement>();
        System.Windows.Automation.AutomationElement c = null;
        try { c = UaWalker.GetFirstChild(parent); } catch { }
        while (c != null && kids.Count < 2000)
        {
            kids.Add(c);
            try { c = UaWalker.GetNextSibling(c); } catch { c = null; }
        }
        for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
    }

    // UIA 调用硬超时: 超时返回降级提示 (后台线程 IsBackground, 进程退出不受影响)
    static readonly List<string> uiaLeaked = new List<string>(); // 超时未死线程登记 (tool@时间), /diag/threads 可查
    static int uiaLeakedCount { get { lock (uiaLeaked) return uiaLeaked.Count; } }

    static string UiCall(string toolName, Func<string> fn, int ms)
    {
        // E1 熔断: UIA COM 调用无法强杀, 泄漏线程会自旋烧 CPU — 攒够 3 个就拒绝一切新 ui 调用直到服务重启
        if (uiaLeakedCount >= 3)
        {
            Log("ui call fused: " + uiaLeakedCount + " leaked threads");
            return "{\"ok\":false,\"error\":\"UIA fused: " + uiaLeakedCount + " leaked worker threads (大DOM 超时不可杀)。请重启 shot-service.exe; 期间改用 /shot 截图+坐标操作\",\"leaked\":" + uiaLeakedCount + "}";
        }
        string outp = null;
        Thread th = new Thread(new ThreadStart(delegate
        {
            try { outp = fn(); }
            catch (Exception ex) { outp = "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}"; }
        }));
        th.IsBackground = true;
        th.Start();
        if (!th.Join(ms))
        {
            lock (uiaLeaked) uiaLeaked.Add(toolName + "@" + DateTime.Now.ToString("HH:mm:ss"));
            Log("uia timeout " + toolName + " >" + ms + "ms (大DOM?), leaked thread now " + uiaLeakedCount);
            return "{\"ok\":false,\"error\":\"UIA timeout " + ms + "ms - 疑似大DOM(Electron/聊天应用)。降级: /shot 截图+坐标操作, 或更小 max, 或 ui_find 精确 name\"}";
        }
        return outp ?? "{\"ok\":false,\"error\":\"uia internal\"}";
    }

    static string UiResolveHwnd(Dictionary<string, string> q)
    {
        if (q.ContainsKey("title"))
        {
            IntPtr h = FindWindowByTitle(q["title"]);
            return h.ToInt64() == 0 ? null : h.ToInt64().ToString();
        }
        if (q.ContainsKey("hwnd")) return q["hwnd"];
        return null;
    }

    static string UiTree(Dictionary<string, string> q)
    {
        try
        {
            string hwndStr = UiResolveHwnd(q);
            if (hwndStr == null) return "{\"ok\":false,\"error\":\"window not found\"}";
            IntPtr h = new IntPtr(long.Parse(hwndStr));
            var root = System.Windows.Automation.AutomationElement.FromHandle(h);
            int max = 400;
            if (q.ContainsKey("max")) { int v; if (int.TryParse(q["max"], out v) && v > 0 && v < 5000) max = v; }
            var all = WalkLimited(root, max + 1); // 计数即停 (D6: FindAll 全物化在大DOM永久阻塞)
            var items = new List<string>();
            int n = Math.Min(all.Count, max);
            for (int i = 0; i < n; i++)
            {
                var e = all[i];
                string ctl, name = "";
                try { ctl = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { ctl = "?"; }
                try { name = e.Current.Name ?? ""; } catch { }
                bool en = true; System.Windows.Rect r2 = new System.Windows.Rect(0, 0, 0, 0);
                try { en = e.Current.IsEnabled; } catch { }
                try { r2 = e.Current.BoundingRectangle; } catch { }
                if (r2.X < -30000 || r2.Width < 0) r2 = new System.Windows.Rect(0, 0, 0, 0); // 最小化/无矩形元素返回 -21亿, 归零
                // value: 编辑类控件读当前内容 (Edit/Document/ComboBox); focused: 是否持有键盘焦点
                string val = null; bool focused = false;
                if (ctl == "Edit" || ctl == "Document" || ctl == "ComboBox")
                {
                    try { object vp; if (e.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out vp)) { val = ((System.Windows.Automation.ValuePattern)vp).Current.Value; if (val != null && val.Length > 200) val = val.Substring(0, 200); } } catch { }
                }
                try { focused = e.Current.HasKeyboardFocus; } catch { }
                var pats = new List<string>();
                try
                {
                    foreach (var pat in e.GetSupportedPatterns())
                    {
                        string pn = pat.ProgrammaticName;
                        if (pn.EndsWith("PatternIdentifiers")) pn = pn.Substring(0, pn.Length - "PatternIdentifiers".Length);
                        else if (pn.EndsWith("Pattern")) pn = pn.Substring(0, pn.Length - "Pattern".Length);
                        pats.Add(pn.ToLowerInvariant());
                    }
                }
                catch { }
                if (name.Length > 80) name = name.Substring(0, 80);
                items.Add("{\"i\":" + i + ",\"type\":\"" + JsonEscape(ctl) + "\",\"name\":\"" + JsonEscape(name) +
                          "\",\"enabled\":" + (en ? "true" : "false") + ",\"focused\":" + (focused ? "true" : "false") +
                          ",\"value\":" + (val == null ? "null" : "\"" + JsonEscape(val) + "\"") +
                          ",\"rect\":{\"x\":" + (int)r2.X + ",\"y\":" + (int)r2.Y + ",\"w\":" + (int)r2.Width + ",\"h\":" + (int)r2.Height + "}" +
                          ",\"ref\":\"" + RefOf(e) + "\",\"pid\":" + PidOf(e) +
                          ",\"offscreen\":" + (UiaOffscreen(e) ? "true" : "false") +
                          ",\"patterns\":\"" + JsonEscape(string.Join(",", pats.ToArray())) + "\"}");
            }
            return "{\"ok\":true,\"hwnd\":" + h.ToInt64() + ",\"count\":" + n + (all.Count > n ? ",\"truncated\":true" : "") +
                   ",\"elements\":[" + string.Join(",", items.ToArray()) + "]}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}";
        }
    }

    // ===== 元素稳定引用 ref (对标 ZCode CUA 的 el.ref) =====
    // 索引 i 跨调用必漂移, 坐标靠模型目视必偏; ref 取 UIA RuntimeId —— 同一元素在窗口生命周期内不变。
    // 命中 ref 缓存直接复用元素对象, 不再重新遍历, 也就没有"点到隔壁"的可能。
    static Dictionary<string, System.Windows.Automation.AutomationElement> UiRefCache =
        new Dictionary<string, System.Windows.Automation.AutomationElement>();
    static object UiRefLock = new object();

    static string RefOf(System.Windows.Automation.AutomationElement e)
    {
        try
        {
            int[] rid = e.GetRuntimeId();
            if (rid != null && rid.Length > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < rid.Length; i++) { if (i > 0) sb.Append('.'); sb.Append(rid[i]); }
                string r = sb.ToString();
                lock (UiRefLock) { UiRefCache[r] = e; }
                return r;
            }
        }
        catch { }
        return "";
    }

    static int PidOf(System.Windows.Automation.AutomationElement e)
    {
        try { return e.Current.ProcessId; } catch { return 0; }
    }

    // 落点归属校验 (对标 ZCode CUA 的 assertClickElementOwnerPid): 坐标点击前先用 hit-test 看这个位置
    // 真正命中谁。命中元素不属于目标进程 = 目标被别的窗口盖住, 点了会落到别人身上 —— 直接拒绝, 不静默乱点。
    static string HitGuard(int x, int y, System.Windows.Automation.AutomationElement target, Dictionary<string, string> q)
    {
        if (q.ContainsKey("nohit") && q["nohit"] == "1") return "";
        int want = PidOf(target);
        System.Windows.Automation.AutomationElement hit = null;
        try { hit = System.Windows.Automation.AutomationElement.FromPoint(new System.Windows.Point(x, y)); } catch { }
        if (hit == null) return "";
        int hp = PidOf(hit);
        string hn = "";
        try { hn = hit.Current.Name ?? ""; } catch { }
        if (want <= 0 || hp <= 0 || hp == want) return "";
        return "{\"ok\":false,\"blocked\":true,\"error\":\"落点归属校验失败: 坐标 " + x + "," + y +
               " 实际命中 pid=" + hp + " 的元素 '" + JsonEscape(hn) + "', 目标元素属于 pid=" + want +
               " —— 目标被别的窗口盖住了, 这一下会点到别人身上(已拦下没点)。先 win_manage activate 把目标窗口置前, 再 ui_find 重新采样 ref\"}";
    }

    static System.Windows.Automation.AutomationElement UiElement(Dictionary<string, string> q)
    {
        // ref 优先: ui_find/ui_tree 返回的 ref 直达元素, 不需要 hwnd 也不需要重新遍历
        string refId = q.ContainsKey("ref") ? q["ref"] : "";
        if (refId != "")
        {
            lock (UiRefLock)
            {
                System.Windows.Automation.AutomationElement cached;
                if (UiRefCache.TryGetValue(refId, out cached) && cached != null)
                {
                    try { string _t = cached.Current.Name; return cached; }
                    catch { UiRefCache.Remove(refId); }
                }
            }
            return null; // ref 失效(元素被销毁/窗口重建) — 调用方要重新 ui_find
        }
        string hwndStr = UiResolveHwnd(q);
        if (hwndStr == null) return null;
        IntPtr h = new IntPtr(long.Parse(hwndStr));
        var root = System.Windows.Automation.AutomationElement.FromHandle(h);
        // 按名称定位 (name= 优先于 i=): 一条命令直达 "点'保存'按钮" — 精确匹配优先, 否则包含匹配(忽略大小写)
        string nm = q.ContainsKey("name") ? q["name"] : "";
        if (nm != "")
        {
            string typeFilter = q.ContainsKey("type") ? q["type"] : "";
            var all = WalkLimited(root, 1200); // E1: 服务端 FindFirst 也是全树物化(泄漏同源), 统一 walk 计数即停
            System.Windows.Automation.AutomationElement exact = null, contains = null;
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                string en = "";
                try { en = e.Current.Name ?? ""; } catch { }
                if (en.Length == 0) continue;
                if (typeFilter != "")
                {
                    string ct = "";
                    try { ct = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { }
                    if (!ct.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (en.Equals(nm, StringComparison.OrdinalIgnoreCase)) { exact = e; break; }
                if (contains == null && en.IndexOf(nm, StringComparison.OrdinalIgnoreCase) >= 0) contains = e;
            }
            return exact ?? contains;
        }
        int idx;
        if (!TryInt(q, "i", out idx)) return null;
        var list = WalkLimited(root, idx < 0 ? 1 : idx + 1); // 取到第 idx 个即停
        if (idx < 0 || idx >= list.Count) return null;
        return list[idx];
    }

    // 按名称/类型查元素 (只查不点): name= 与 type= 至少一个; 返回全部匹配 {i,name,type,rect,enabled}
    // 注意: i 是当次遍历序号, 仅本次响应内有效, 跨调用必须重新查 (UIA 树会变)
    static string UiFind(Dictionary<string, string> q)
    {
        try
        {
            string hwndStr = UiResolveHwnd(q);
            if (hwndStr == null) return "{\"ok\":false,\"error\":\"window not found\"}";
            IntPtr h = new IntPtr(long.Parse(hwndStr));
            var root = System.Windows.Automation.AutomationElement.FromHandle(h);
            string nm = q.ContainsKey("name") ? q["name"] : "";
            string typeFilter = q.ContainsKey("type") ? q["type"] : "";
            if (nm == "" && typeFilter == "") return "{\"ok\":false,\"error\":\"need name and/or type\"}";
            var all = WalkLimited(root, 1500); // E1: 唯一路径 = 计数即停遍历 (服务端 FindAll 全树物化会永久阻塞泄漏线程)
            var items = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                string en = "";
                try { en = e.Current.Name ?? ""; } catch { }
                if (nm != "" && en.IndexOf(nm, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string ct = "";
                try { ct = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { ct = "?"; }
                if (typeFilter != "" && !ct.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                bool enb = true; System.Windows.Rect r2 = new System.Windows.Rect(0, 0, 0, 0);
                try { enb = e.Current.IsEnabled; } catch { }
                try { r2 = e.Current.BoundingRectangle; } catch { }
                if (r2.X < -30000 || r2.Width < 0) r2 = new System.Windows.Rect(0, 0, 0, 0);
                items.Add("{\"i\":" + i + ",\"name\":\"" + JsonEscape(en) + "\",\"type\":\"" + JsonEscape(ct) +
                          "\",\"enabled\":" + (enb ? "true" : "false") +
                          ",\"rect\":{\"x\":" + (int)r2.X + ",\"y\":" + (int)r2.Y + ",\"w\":" + (int)r2.Width + ",\"h\":" + (int)r2.Height + "}" +
                          ",\"ref\":\"" + RefOf(e) + "\",\"pid\":" + PidOf(e) +
                          ",\"offscreen\":" + (UiaOffscreen(e) ? "true" : "false") + "}");
            }
            return "{\"ok\":true,\"name\":\"" + JsonEscape(nm) + "\",\"type\":\"" + JsonEscape(typeFilter) + "\",\"count\":" + items.Count + ",\"elements\":[" + string.Join(",", items.ToArray()) + "]}";
        }
        catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}"; }
    }

    // 设置编辑控件选区 (EM_SETSEL): title/name/i 定位控件, start/end=字符范围(必须都给, 非负); 仅 Win32 Edit/RichEdit 系支持
    // 越界 clamp 回 clamped:true; start>end 交换回 swapped:true; 相等=光标 collapsed:true — 杜绝假成功
    static string UiSelect(Dictionary<string, string> q)
    {
        try
        {
            var e = UiElement(q);
            if (e == null) return "{\"ok\":false,\"error\":\"element not found (bad i/name/window)\"}";
            int start = 0, end = 0;
            bool hs = q.ContainsKey("start") && int.TryParse(q["start"], out start);
            bool he = q.ContainsKey("end") && int.TryParse(q["end"], out end);
            if (!hs || !he) return "{\"ok\":false,\"error\":\"need both start and end (integers; equal values = collapsed caret)\"}";
            if (start < 0 || end < 0) return "{\"ok\":false,\"error\":\"start/end must be >= 0\"}";
            IntPtr ch = IntPtr.Zero;
            try { ch = new IntPtr(e.Current.NativeWindowHandle); } catch { }
            if (ch == IntPtr.Zero) return "{\"ok\":false,\"error\":\"element has no native handle (用 click 定起点 + click mods=shift 定终点代替)\"}";
            bool swapped = false, clamped = false;
            if (start > end) { int x = start; start = end; end = x; swapped = true; }
            int len = -1; // 文本长度 (Edit/Document 走 ValuePattern)
            try
            {
                object vp;
                if (e.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out vp))
                {
                    string v = ((System.Windows.Automation.ValuePattern)vp).Current.Value;
                    if (v != null) len = v.Length;
                }
            }
            catch { }
            if (len >= 0 && end > len) { end = len; clamped = true; }
            string elname = "";
            try { elname = e.Current.Name ?? ""; } catch { }
            if (start == end)
            {
                SendMessage(ch, 0x00B1, (IntPtr)start, (IntPtr)start); // EM_SETSEL 同位 = 光标定位
                SendMessage(ch, 0x00B7, IntPtr.Zero, IntPtr.Zero);
                return "{\"ok\":true,\"collapsed\":true,\"caret\":" + start + (clamped ? ",\"clamped\":true" : "") + ",\"name\":\"" + JsonEscape(elname) + "\"}";
            }
            SendMessage(ch, 0x00B1, (IntPtr)start, (IntPtr)end);
            SendMessage(ch, 0x00B7, IntPtr.Zero, IntPtr.Zero);
            Log("ui select [" + start + "," + end + ") " + elname);
            return "{\"ok\":true,\"start\":" + start + ",\"end\":" + end + (swapped ? ",\"swapped\":true" : "") + (clamped ? ",\"clamped\":true" : "") + ",\"name\":\"" + JsonEscape(elname) + "\"}";
        }
        catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}"; }
    }

    // 元素区域像素指纹(MD5): 点击前后对比, 解决 "点了但界面没变化" 的静默失败
    static string RegionHashOf(System.Windows.Automation.AutomationElement e)
    {
        try
        {
            var r = e.Current.BoundingRectangle;
            int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
            if (w <= 0 || h <= 0) return "";
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
                using (var ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    var md5 = System.Security.Cryptography.MD5.Create();
                    return BitConverter.ToString(md5.ComputeHash(ms.ToArray())).Replace("-", "");
                }
            }
        }
        catch { return ""; }
    }

    // 从元素向上找到真实 Win32 窗口, 拿屏幕坐标 rect (元素 rect 可能是窗口相对坐标, 如冻结态 ZCode/Electron)
    static bool WinRectOf(System.Windows.Automation.AutomationElement e, out int wx, out int wy, out int ww, out int wh)
    {
        wx = wy = ww = wh = 0;
        try
        {
            var cur = e;
            for (int i = 0; i < 30 && cur != null; i++)
            {
                int h = 0;
                try { h = (int)cur.Current.NativeWindowHandle; } catch { }
                if (h != 0)
                {
                    RECT r;
                    if (GetWindowRect((IntPtr)h, out r) && r.Right > r.Left && r.Bottom > r.Top)
                    { wx = r.Left; wy = r.Top; ww = r.Right - r.Left; wh = r.Bottom - r.Top; return true; }
                }
                var par = System.Windows.Automation.TreeWalker.ControlViewWalker.GetParent(cur);
                if (par == System.Windows.Automation.AutomationElement.RootElement || par == null) break;
                cur = par;
            }
        }
        catch { }
        return false;
    }

    // 整窗像素指纹: 对比区域是元素所在整窗中间 80%, 而不是按钮自身 — 按钮自身外观不变时(如切页签)区域对比会误报"没反应"
    static string WindowHashOf(System.Windows.Automation.AutomationElement e)
    {
        int wx, wy, ww, wh;
        if (!WinRectOf(e, out wx, out wy, out ww, out wh)) return RegionHashOf(e);
        int mx = wx + ww / 10, my = wy + wh / 10, mw = ww * 8 / 10, mh = wh * 8 / 10;
        if (mw <= 0 || mh <= 0) return RegionHashOf(e);
        try
        {
            using (var bmp = new Bitmap(mw, mh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(mx, my, 0, 0, new Size(mw, mh));
                using (var ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    var md5 = System.Security.Cryptography.MD5.Create();
                    return BitConverter.ToString(md5.ComputeHash(ms.ToArray())).Replace("-", "");
                }
            }
        }
        catch { return ""; }
    }

    // verify=1 时的附加返回: changed=点击前后界面是否真的变了
    static string VerifyExtra(System.Windows.Automation.AutomationElement e, string before)
    {
        if (before == "") return "";
        Thread.Sleep(500);
        string after = WindowHashOf(e);
        bool changed = after != "" && after != before;
        return ",\"verify\":{\"changed\":" + (changed ? "true" : "false") + ",\"before\":\"" + before + "\",\"after\":\"" + after + "\"}"
             + (changed ? "" : ",\"warn\":\"点了但界面像素没变(可能被遮挡/应用不响应 UIA/窗口没真激活/元素位置已漂移) — 改 mode=coord 真实鼠标点, 或先 win_manage activate 真正激活窗口, 再重采样确认元素还在原位\"");
    }

    // 元素中心若不在所属窗口 rect 内 = UIA 返回了窗口相对坐标(冻结态 Electron 等), 按窗口偏移平移到屏幕坐标
    static int AdjustToWindow(System.Windows.Automation.AutomationElement e, int cx, int cy, System.Windows.Rect elRect, out int outY)
    {
        outY = cy;
        int wx, wy, ww, wh;
        if (!WinRectOf(e, out wx, out wy, out ww, out wh) || ww < 10) return cx;
        if (cx >= wx - 2 && cx <= wx + ww + 2 && cy >= wy - 2 && cy <= wy + wh + 2) return cx; // 已在窗口内
        int nx = wx + (int)elRect.X + (int)elRect.Width / 2;
        int ny = wy + (int)elRect.Y + (int)elRect.Height / 2;
        if (nx >= wx - 2 && nx <= wx + ww + 2 && ny >= wy - 2 && ny <= wy + wh + 2)
        {
            Log("ui click coord adjusted: " + cx + "," + cy + " -> " + nx + "," + ny + " (窗口相对坐标)");
            outY = ny;
            return nx;
        }
        return cx; // 平移后也不在窗口内, 交给 HitGuard 拦
    }

    static bool UiaOffscreen(System.Windows.Automation.AutomationElement e)
    {
        try
        {
            object o = e.GetCurrentPropertyValue(System.Windows.Automation.AutomationElement.IsOffscreenProperty);
            if (o is bool) return (bool)o;
        }
        catch { }
        return false;
    }

    // 点击后校验"预期内容是否真的出现" —— 防"点了界面也变了, 但变得不对"
    // 实测事故: AI 点会话列表项报 ok 且 verify.changed=true, 但界面根本没进那个会话
    static string ExpectCheckJson(string expect, int pid, int waitMs)
    {
        if (string.IsNullOrEmpty(expect)) return "";
        Thread.Sleep(waitMs > 0 ? waitMs : 900);
        try
        {
            var root = System.Windows.Automation.AutomationElement.RootElement;
            var list = WalkLimited(root, 800);
            foreach (var e in list)
            {
                try
                {
                    if (pid > 0 && PidOf(e) != pid) continue;
                    string n = e.Current.Name ?? "";
                    if (n.Length == 0) continue;
                    if (n.IndexOf(expect, StringComparison.OrdinalIgnoreCase) >= 0)
                        return ",\"expect\":{\"found\":true,\"text\":\"" + JsonEscape(expect) + "\",\"matched\":\"" + JsonEscape(n.Length > 60 ? n.Substring(0, 60) : n) + "\"}";
                }
                catch { }
            }
        }
        catch { }
        return ",\"expect\":{\"found\":false,\"text\":\"" + JsonEscape(expect) + "\",\"hint\":\"点完没找到预期内容 — 大概率没点到或点了没生效。重新采样, 别硬往下走\"}";
    }

    // ui_click 包装: 点完按 expect= 校验预期内容是否出现, 结果并进返回 JSON
    static string UiClick(Dictionary<string, string> q)
    {
        string r = UiClickInner(q);
        string exp = q.ContainsKey("expect") ? q["expect"] : "";
        if (exp == "" || r.IndexOf("\"ok\":true") < 0) return r;
        int pid = 0;
        int k = r.IndexOf("\"pid\":");
        if (k >= 0)
        {
            string num = r.Substring(k + 6);
            int c = num.IndexOfAny(new char[] { ',', '}' });
            if (c > 0) int.TryParse(num.Substring(0, c), out pid);
        }
        if (pid == 0 && q.ContainsKey("hwnd"))
        {
            long hv = 0; long.TryParse(q["hwnd"], out hv);
            if (hv > 0)
            {
                IntPtr wh = new IntPtr(hv);
                if (IsWindow(wh)) { uint tp = 0; GetWindowThreadProcessId(wh, out tp); pid = (int)tp; }
            }
        }
        int k2 = r.LastIndexOf('}');
        if (k2 < 0) return r;
        return r.Substring(0, k2) + ExpectCheckJson(exp, pid, 900) + "}";
    }

    static string UiClickInner(Dictionary<string, string> q)
    {
        try
        {
            var e = UiElement(q);
            if (e == null) return "{\"ok\":false,\"error\":\"element not found (bad index or window)\"}";
            string name = "";
            try { name = e.Current.Name ?? ""; } catch { }
            string elRef = RefOf(e);
            int elPid = PidOf(e);
            // 可见性校验: 视口外的元素点了不生效(实测 AI 点会话列表里滚出视口的项, 返回 ok 但界面没进那个会话)
            bool force = q.ContainsKey("force") && (q["force"] == "1" || q["force"].ToLowerInvariant() == "true");
            if (!force)
            {
                bool off = UiaOffscreen(e);
                string geoWhy = "";
                try
                {
                    IntPtr wh = IntPtr.Zero;
                    if (q.ContainsKey("hwnd")) { long hv = 0; long.TryParse(q["hwnd"], out hv); if (hv > 0) wh = new IntPtr(hv); }
                    var rr = e.Current.BoundingRectangle;
                    if (wh != IntPtr.Zero && IsWindow(wh))
                    {
                        RECT wr; GetWindowRect(wh, out wr);
                        double ecx = rr.X + rr.Width / 2.0, ecy = rr.Y + rr.Height / 2.0;
                        if (ecx < wr.Left || ecx > wr.Right || ecy < wr.Top || ecy > wr.Bottom)
                            geoWhy = "元素中心(" + (int)ecx + "," + (int)ecy + ") 落在窗口矩形(" + wr.Left + "," + wr.Top + " -> " + wr.Right + "," + wr.Bottom + ") 之外";
                    }
                }
                catch { }
                if (off || geoWhy != "")
                    return "{\"ok\":false,\"error\":\"元素当前不在可视区, 点了不会生效 — " + JsonEscape(geoWhy != "" ? geoWhy : "UIA 报告 offscreen(可能滚出视口/被折叠)") + "。先滚动或展开让它可见再点; 确认是误判就传 force=1\",\"name\":\"" + JsonEscape(name) + "\",\"ref\":\"" + elRef + "\",\"offscreen\":true}";
            }
            // 名字校验: 请求 name 与点中的元素名对不上 = 点偏了(实测 i 漂移后点中别的控件还返回 ok)
            string reqName = q.ContainsKey("name") ? q["name"] : "";
            if (reqName != "" && name.IndexOf(reqName, StringComparison.OrdinalIgnoreCase) < 0)
                return "{\"ok\":false,\"error\":\"定位校验失败: 要点的 name='" + JsonEscape(reqName) + "', 实际点中的是 '" + JsonEscape(name) + "' (i 跨调用会漂移, 请改用 name= 定位)\"}";
            bool doVerify = q.ContainsKey("verify") && q["verify"] == "1";
            string hashBefore = doVerify ? WindowHashOf(e) : "";
            bool forceCoord = q.ContainsKey("mode") && q["mode"] == "coord";
            if (forceCoord)
            {
                System.Windows.Rect rf = e.Current.BoundingRectangle;
                int fx = (int)(rf.X + rf.Width / 2), fy = (int)(rf.Y + rf.Height / 2);
                fx = AdjustToWindow(e, fx, fy, rf, out fy);
                string guard = HitGuard(fx, fy, e, q);
                if (guard != "") return guard;
                SetCursorPos(fx, fy);
                Thread.Sleep(60);
                MouseClick("left", 1);
                Log("ui click coord(forced): " + name + " @ " + fx + "," + fy);
                return "{\"ok\":true,\"via\":\"coord\",\"forced\":true,\"x\":" + fx + ",\"y\":" + fy + ",\"name\":\"" + JsonEscape(name) + "\",\"ref\":\"" + elRef + "\",\"pid\":" + elPid + VerifyExtra(e, hashBefore) + "}";
            }
            // 优先语义模式
            object pat;
            if (e.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out pat))
            {
                ((System.Windows.Automation.InvokePattern)pat).Invoke();
                Log("ui click invoke: " + name);
                // 2026-09-07: 微信「进入微信」按钮 invoke 返回成功但界面毫无变化 — invoke 成功 ≠ 真的点了
                return "{\"ok\":true,\"via\":\"invoke\",\"name\":\"" + JsonEscape(name) + "\",\"ref\":\"" + elRef + "\",\"pid\":" + elPid + ",\"warn\":\"invoke 成功不代表界面已变化(部分应用如微信不响应 UIA Invoke)。若界面无变化, 用 ui_find 拿 rect 后 mouse_click 中心, 或 ui_click 传 mode=coord\"" + VerifyExtra(e, hashBefore) + "}";
            }
            if (e.TryGetCurrentPattern(System.Windows.Automation.TogglePattern.Pattern, out pat))
            { ((System.Windows.Automation.TogglePattern)pat).Toggle(); Log("ui click toggle: " + name); return "{\"ok\":true,\"via\":\"toggle\",\"name\":\"" + JsonEscape(name) + "\"}"; }
            if (e.TryGetCurrentPattern(System.Windows.Automation.ExpandCollapsePattern.Pattern, out pat))
            { ((System.Windows.Automation.ExpandCollapsePattern)pat).Expand(); Log("ui click expand: " + name); return "{\"ok\":true,\"via\":\"expand\",\"name\":\"" + JsonEscape(name) + "\"}"; }
            if (e.TryGetCurrentPattern(System.Windows.Automation.SelectionItemPattern.Pattern, out pat))
            { ((System.Windows.Automation.SelectionItemPattern)pat).Select(); Log("ui click select: " + name); return "{\"ok\":true,\"via\":\"select\",\"name\":\"" + JsonEscape(name) + "\"}"; }
            // 退坐标点击中心
            System.Windows.Rect r2 = e.Current.BoundingRectangle;
            int cx = (int)(r2.X + r2.Width / 2), cy = (int)(r2.Y + r2.Height / 2);
            cx = AdjustToWindow(e, cx, cy, r2, out cy);
            string guard2 = HitGuard(cx, cy, e, q);
            if (guard2 != "") return guard2;
            SetCursorPos(cx, cy);
            Thread.Sleep(60);
            MouseClick("left", 1);
            Log("ui click coord: " + name + " @ " + cx + "," + cy);
            return "{\"ok\":true,\"via\":\"coord\",\"x\":" + cx + ",\"y\":" + cy + ",\"name\":\"" + JsonEscape(name) + "\",\"ref\":\"" + elRef + "\",\"pid\":" + elPid + VerifyExtra(e, hashBefore) + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}";
        }
    }

    static string UiSet(Dictionary<string, string> q)
    {
        try
        {
            var e = UiElement(q);
            if (e == null) return "{\"ok\":false,\"error\":\"element not found (bad index or window)\"}";
            string val = q.ContainsKey("value") ? q["value"] : "";
            object pat;
            if (e.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out pat))
            {
                ((System.Windows.Automation.ValuePattern)pat).SetValue(val);
                // 假成功防线: React 受控组件会吞掉 SetValue(返回 ok 但内容没进), 必须读回验证
                Thread.Sleep(200);
                string got = "";
                try { got = ((System.Windows.Automation.ValuePattern)e.GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value ?? ""; } catch { }
                if (got != val)
                    return "{\"ok\":false,\"via\":\"value\",\"error\":\"SetValue 返回成功但读回不一致(写入 " + val.Length + " 字, 实际 " + got.Length + " 字) — 目标是受控输入框, 别用 ui_set, 改 clipboard_set + ui_click 聚焦 + keyboard_press ctrl+v + ui_read 验证\"}";
                return "{\"ok\":true,\"via\":\"value\",\"len\":" + val.Length + ",\"verified\":true}";
            }
            return "{\"ok\":false,\"error\":\"element has no ValuePattern (不可直写, 用 clipboard_set + ctrl+v)\"}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}";
        }
    }

    static string UiRead(Dictionary<string, string> q)
    {
        try
        {
            var e = UiElement(q);
            if (e == null) return "{\"ok\":false,\"error\":\"element not found (bad index or window)\"}";
            string name = "", val = "", cls = "", ctl = "";
            try { name = e.Current.Name ?? ""; } catch { }
            try { cls = e.Current.ClassName ?? ""; } catch { }
            try { ctl = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { }
            try
            {
                object pat;
                if (e.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out pat))
                    val = ((System.Windows.Automation.ValuePattern)pat).Current.Value ?? "";
            }
            catch { }
            if (val.Length > 4000) val = val.Substring(0, 4000);
            if (name.Length > 2000) name = name.Substring(0, 2000);
            return "{\"ok\":true,\"name\":\"" + JsonEscape(name) + "\",\"value\":\"" + JsonEscape(val) +
                   "\",\"class\":\"" + JsonEscape(cls) + "\",\"type\":\"" + JsonEscape(ctl) + "\"}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}";
        }
    }

    // ==================== 屏幕录制 (抓帧管道 -> ffmpeg -> MP4 h264) ====================
    static System.Diagnostics.Process recProc;
    static System.IO.StreamWriter recStdin;
    static Thread recThread;
    static volatile bool recording;
    static string recPath;
    static Rectangle recRect;
    static DateTime recStart;
    static int recFps;

    static readonly object recLock = new object();
    static string RecordStart(int x, int y, int w, int h, int fps)
    {
        HookRecExitStop(); // 进程退出时自动收尾录屏 (兜底: 不让 ffmpeg 孤儿/文件烂尾)
        bool fpsNorm = false, sizeClamped = false;
        lock (recLock) // R2: 检查+置位+启动整段互斥 (并发六连曾全回 ok 共用同一路径, ffmpeg 成孤儿)
        {
        if (recording) return "{\"ok\":false,\"error\":\"already recording\",\"file\":\"" + JsonEscape(recPath) + "\"}";
        // R1: 像素上限 4M, 超限回退虚拟屏 — 100000x100000(40GB Bitmap) 一条 URL 打挂进程
        bool wantFull = (w <= 0 || h <= 0);
        if (wantFull || (long)w * h > 4000000L)
        {
            var vs0 = VirtualScreen(); x = vs0.X; y = vs0.Y; w = vs0.Width; h = vs0.Height; sizeClamped = !wantFull; // 显式传超限才标 clamp, 默认全屏不算
        }
        if (w % 2 != 0) w--;
        if (h % 2 != 0) h--;
        if (fps <= 0 || fps > 30) { fps = 10; fpsNorm = true; }
        recRect = new Rectangle(x, y, w, h);
        recFps = fps;
        recPath = System.IO.Path.Combine(ShotDir, "rec_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".mp4"); // 毫秒防同名
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg",
                "-hide_banner -loglevel error -f rawvideo -pixel_format bgra -video_size " + w + "x" + h +
                " -framerate " + fps + " -i - -c:v libx264 -preset ultrafast -crf 26 -pix_fmt yuv420p -y \"" + recPath + "\"");
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.CreateNoWindow = true;
            recProc = System.Diagnostics.Process.Start(psi);
            recStdin = recProc.StandardInput;
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"ffmpeg start failed: " + JsonEscape(ex.Message) + "\"}";
        }
        recording = true;
        recStart = DateTime.Now;
        recThread = new Thread(new ThreadStart(RecordLoop));
        recThread.IsBackground = true;
        recThread.Start();
        Log("record start: " + recRect.ToString() + " fps=" + fps);
        return "{\"ok\":true,\"file\":\"" + JsonEscape(recPath) + "\",\"fps\":" + fps + (fpsNorm ? ",\"fpsNormalized\":true" : "") +
               ",\"video_size\":\"" + w + "x" + h + "\"" + (sizeClamped ? ",\"sizeClamped\":true" : "") + "}";
        }
    }

    static void RecordLoop()
    {
        int bw = recRect.Width, bh = recRect.Height;
        Bitmap frame0 = null;
        try { frame0 = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb); }
        catch (Exception ex) { Log("record alloc fail: " + ex.Message); lock (recLock) { recording = false; } try { if (recStdin != null) recStdin.Close(); } catch { } return; } // R1: OOM 不再 fail-fast 带走进程
        using (Bitmap frame = frame0)
        {
            byte[] row = new byte[bw * 4];
            while (recording)
            {
                try
                {
                    using (Graphics g = Graphics.FromImage(frame))
                        g.CopyFromScreen(recRect.X, recRect.Y, 0, 0, recRect.Size);
                    var bd = frame.LockBits(new Rectangle(0, 0, bw, bh), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        IntPtr ptr = bd.Scan0;
                        for (int y = 0; y < bh; y++)
                        {
                            Marshal.Copy(new IntPtr(ptr.ToInt64() + y * bd.Stride), row, 0, row.Length);
                            recStdin.BaseStream.Write(row, 0, row.Length);
                            recStdin.BaseStream.Flush();
                        }
                    }
                    finally { frame.UnlockBits(bd); }
                }
                catch (Exception ex) { Log("record frame err: " + ex.Message); break; }
                Thread.Sleep(1000 / recFps);
            }
        }
        try { recStdin.BaseStream.Flush(); recStdin.BaseStream.Close(); } catch { }
    }

    static string RecordStop()
    {
        if (!recording) return "{\"ok\":false,\"error\":\"not recording\"}";
        recording = false;
        if (recThread != null) recThread.Join(4000);
        try { recStdin.BaseStream.Close(); } catch { }
        bool exited = recProc.WaitForExit(15000);
        if (!exited) { try { recProc.Kill(); } catch { } }
        long bytes = 0;
        double dur = (DateTime.Now - recStart).TotalSeconds;
        try { var fi = new System.IO.FileInfo(recPath); bytes = fi.Length; } catch { }
        Log("record stop: " + recPath + " " + bytes + "B " + (int)dur + "s");
        recProc.Dispose(); recProc = null; recStdin = null; recThread = null;
        return "{\"ok\":true,\"file\":\"" + JsonEscape(recPath) + "\",\"bytes\":" + bytes + ",\"durationSec\":" + (int)dur + "}";
    }

    static string RecordStatus()
    {
        if (!recording) return "{\"ok\":true,\"recording\":false}";
        return "{\"ok\":true,\"recording\":true,\"file\":\"" + JsonEscape(recPath) + "\",\"elapsedSec\":" + (int)(DateTime.Now - recStart).TotalSeconds + ",\"fps\":" + recFps + "}";
    }

    // ---- 录屏可视化: 录制区域红框环 (画在区域外圈 4px, 不遮镜头不入镜; 点击穿透) ----
    static Form recBorder;

    sealed class ClickThroughBorderForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080  // WS_EX_TOOLWINDOW: 不进 Alt+Tab
                            | 0x00000020  // WS_EX_TRANSPARENT: 鼠标点击穿透
                            | 0x00080000  // WS_EX_LAYERED
                            | 0x08000000; // WS_EX_NOACTIVATE: 不抢焦点
                return cp;
            }
        }
    }

    static void ShowRecBorder()
    {
        CloseRecBorder();
        RunOnUi(delegate
        {
            try
            {
                Form f = new ClickThroughBorderForm();
                f.FormBorderStyle = FormBorderStyle.None;
                f.StartPosition = FormStartPosition.Manual;
                Rectangle b = recRect; b.Inflate(4, 4); // 红框环在录制区外圈
                f.Bounds = b;
                f.TopMost = true;
                f.ShowInTaskbar = false;
                f.BackColor = Color.FromArgb(235, 50, 50);
                System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath();
                gp.AddRectangle(new Rectangle(0, 0, b.Width, b.Height));
                gp.AddRectangle(new Rectangle(4, 4, b.Width - 8, b.Height - 8)); // 挖空中间, 只留 4px 红环
                f.Region = new Region(gp);
                f.Show();
                recBorder = f;
            }
            catch (Exception ex) { Log("rec border err: " + ex.Message); }
        });
    }

    // 回 UI 线程执行 (录屏窗体须在创建过消息循环的线程; 与 CaptureOverlay.RunOnHk 同款, 这里独立一份给 ShotService 静态方法用)
    static void RunOnUi(Action a)
    {
        try
        {
            Form hk = hkForm;
            if (hk != null && hk.IsHandleCreated) hk.BeginInvoke(new MethodInvoker(delegate { try { a(); } catch (Exception ex) { Log("hk ui err: " + ex.Message); } }));
            else { try { a(); } catch (Exception ex) { Log("hk ui fallback err: " + ex.Message); } }
        }
        catch { }
    }

    static void CloseRecBorder()
    {
        Form f = recBorder; recBorder = null;
        if (f == null || f.IsDisposed) return;
        try { f.BeginInvoke(new MethodInvoker(delegate { try { f.Close(); } catch { } })); }
        catch { try { f.Close(); } catch { } }
    }

    // 进程退出保护: 托盘"退出服务"/管理员重启 时若在录屏, 立即关 stdin 让 ffmpeg 收 EOF 封装落盘 (ProcessExit 仅 2s, 不能等 Join)
    static bool recExitHooked;
    public static void HookRecExitStop()
    {
        if (recExitHooked) return;
        recExitHooked = true;
        AppDomain.CurrentDomain.ProcessExit += delegate
        {
            try
            {
                if (!recording) return;
                recording = false;
                try { recStdin.BaseStream.Close(); } catch { }
                Log("record finalize on process exit: " + recPath);
            }
            catch { }
        };
    }

    // ==================== UIA 批量读值 ====================
    static string UiReadAll(Dictionary<string, string> q)
    {
        try
        {
            string hwndStr = UiResolveHwnd(q);
            if (hwndStr == null) return "{\"ok\":false,\"error\":\"window not found\"}";
            IntPtr h = new IntPtr(long.Parse(hwndStr));
            var root = System.Windows.Automation.AutomationElement.FromHandle(h);
            int max = 300;
            if (q.ContainsKey("max")) { int v; if (int.TryParse(q["max"], out v) && v > 0 && v < 2000) max = v; }
            var all = WalkLimited(root, max + 1);
            var items = new List<string>();
            int n = Math.Min(all.Count, max);
            for (int i = 0; i < n; i++)
            {
                var e = all[i];
                string name = ""; string val = ""; string ctl = "";
                bool en = true;
                try { name = e.Current.Name ?? ""; } catch { }
                try { ctl = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { }
                try { en = e.Current.IsEnabled; } catch { }
                try
                {
                    object pat;
                    if (e.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out pat))
                        val = ((System.Windows.Automation.ValuePattern)pat).Current.Value ?? "";
                }
                catch { }
                if (name.Length > 200) name = name.Substring(0, 200);
                if (val.Length > 1000) val = val.Substring(0, 1000);
                items.Add("{\"i\":" + i + ",\"type\":\"" + JsonEscape(ctl) + "\",\"name\":\"" + JsonEscape(name) + "\",\"value\":\"" + JsonEscape(val) + "\",\"enabled\":" + (en ? "true" : "false") + "}");
            }
            return "{\"ok\":true,\"hwnd\":" + h.ToInt64() + ",\"count\":" + n + (all.Count > n ? ",\"truncated\":true" : "") + ",\"elements\":[" + string.Join(",", items.ToArray()) + "]}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}";
        }
    }

    // ==================== 按需提权 (UAC 由用户确认, 不静默) ====================
    static string AppRunAs(string path, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(path, args == null ? "" : args);
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            System.Diagnostics.Process.Start(psi);
            Log("[runas] " + path + " " + args);
            return "{\"ok\":true,\"note\":\"elevated (user confirmed UAC)\"}";
        }
        catch (System.ComponentModel.Win32Exception wex)
        {
            return "{\"ok\":false,\"error\":\"user cancelled or denied UAC (code \" + wex.ErrorCode + \")\"}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}";
        }
    }

}
