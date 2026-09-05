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
    [DllImport("user32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    // 置前: Windows 前台锁会拒绝后台进程的 SetForegroundWindow —
    // 三级策略: 直接置前 -> AttachThreadInput 挂前台输入队列再置前 -> 模拟 ALT 松开解锁再置前
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x80;

    // 光标处最顶层的普通可见窗口 (跳过自己/工具窗/最小化), 供截图"自动窗口检测"
    static IntPtr WindowFromPointEx(POINT p, IntPtr exclude)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr lp)
        {
            try
            {
                if (h == exclude || !IsWindowVisible(h)) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                if (r.Left < -30000) return true; // 最小化
                if (p.x >= r.Left && p.x < r.Right && p.y >= r.Top && p.y < r.Bottom)
                {
                    StringBuilder cn = new StringBuilder(64);
                    GetClassNameW(h, cn, 64);
                    string cname = cn.ToString();
                    if (cname == "Progman" || cname == "WorkerW") return true; // 桌面: 高亮全屏无意义
                    found = h; return false;
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static string WinActivate(IntPtr h)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        ShowWindow(h, SW_SHOW);
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
        Log("win activate: " + h + " ok=" + ok + " via=" + via);
        return "{\"ok\":true,\"activated\":" + (ok ? "true" : "false") + ",\"via\":\"" + via + "\"}";
    }

    static string WinShow(IntPtr h, int cmd, string name)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        bool ok = ShowWindow(h, cmd);
        return "{\"ok\":true,\"" + name + "\":" + (ok ? "true" : "false") + "}";
    }

    static string WinClose(IntPtr h)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        SendMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return "{\"ok\":true}";
    }

    static string WinMove(IntPtr h, int x, int y, int w, int hh)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        bool ok = MoveWindow(h, x, y, w, hh, true);
        return "{\"ok\":true,\"moved\":" + (ok ? "true" : "false") + "}";
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
                        string name = "clip_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".png";
                        string path = System.IO.Path.Combine(ShotDir, name);
                        img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        long bytes = new System.IO.FileInfo(path).Length;
                        result = "{\"ok\":true,\"type\":\"image\",\"file\":\"" + JsonEscape(path) +
                                 "\",\"url\":\"http://127.0.0.1:" + 18800 + "/img/" + JsonEscape(name) +
                                 "\",\"w\":" + img.Width + ",\"h\":" + img.Height + ",\"bytes\":" + bytes + "}";
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
            var all = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
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

    static System.Windows.Automation.AutomationElement UiElement(Dictionary<string, string> q)
    {
        string hwndStr = UiResolveHwnd(q);
        if (hwndStr == null) return null;
        IntPtr h = new IntPtr(long.Parse(hwndStr));
        var root = System.Windows.Automation.AutomationElement.FromHandle(h);
        // 按名称定位 (name= 优先于 i=): 一条命令直达 "点'保存'按钮" — 精确匹配优先, 否则包含匹配(忽略大小写)
        string nm = q.ContainsKey("name") ? q["name"] : "";
        if (nm != "")
        {
            string typeFilter = q.ContainsKey("type") ? q["type"] : "";
            var all = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
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
        var list = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        if (idx < 0 || idx >= list.Count) return null;
        return list[idx];
    }

    // 按名称查元素 (只查不点): name= 必填, type= 可选过滤; 返回全部匹配 {i,name,type,rect,enabled}
    static string UiFind(Dictionary<string, string> q)
    {
        try
        {
            string hwndStr = UiResolveHwnd(q);
            if (hwndStr == null) return "{\"ok\":false,\"error\":\"window not found\"}";
            IntPtr h = new IntPtr(long.Parse(hwndStr));
            var root = System.Windows.Automation.AutomationElement.FromHandle(h);
            string nm = q.ContainsKey("name") ? q["name"] : "";
            if (nm == "") return "{\"ok\":false,\"error\":\"need name\"}";
            string typeFilter = q.ContainsKey("type") ? q["type"] : "";
            var all = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            var items = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                string en = "";
                try { en = e.Current.Name ?? ""; } catch { }
                if (en.Length == 0 || en.IndexOf(nm, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string ct = "";
                try { ct = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { ct = "?"; }
                if (typeFilter != "" && !ct.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                bool enb = true; System.Windows.Rect r2 = new System.Windows.Rect(0, 0, 0, 0);
                try { enb = e.Current.IsEnabled; } catch { }
                try { r2 = e.Current.BoundingRectangle; } catch { }
                if (r2.X < -30000 || r2.Width < 0) r2 = new System.Windows.Rect(0, 0, 0, 0);
                items.Add("{\"i\":" + i + ",\"name\":\"" + JsonEscape(en) + "\",\"type\":\"" + JsonEscape(ct) +
                          "\",\"enabled\":" + (enb ? "true" : "false") +
                          ",\"rect\":{\"x\":" + (int)r2.X + ",\"y\":" + (int)r2.Y + ",\"w\":" + (int)r2.Width + ",\"h\":" + (int)r2.Height + "}}");
            }
            return "{\"ok\":true,\"name\":\"" + JsonEscape(nm) + "\",\"count\":" + items.Count + ",\"elements\":[" + string.Join(",", items.ToArray()) + "]}";
        }
        catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}"; }
    }

    // 设置编辑控件选区 (EM_SETSEL): title/name/i 定位控件, start/end=字符范围; 仅 Win32 Edit/RichEdit 系支持
    static string UiSelect(Dictionary<string, string> q)
    {
        try
        {
            var e = UiElement(q);
            if (e == null) return "{\"ok\":false,\"error\":\"element not found (bad i/name/window)\"}";
            int start = 0, end = 0;
            TryInt(q, "start", out start); TryInt(q, "end", out end);
            IntPtr ch = IntPtr.Zero;
            try { ch = new IntPtr(e.Current.NativeWindowHandle); } catch { }
            if (ch == IntPtr.Zero) return "{\"ok\":false,\"error\":\"element has no native handle (用 click 定起点 + keyboard_press shift+end 代替)\"}";
            SendMessage(ch, 0x00B1, (IntPtr)start, (IntPtr)end); // EM_SETSEL
            SendMessage(ch, 0x00B7, IntPtr.Zero, IntPtr.Zero);   // EM_SCROLLCARET 滚到光标可见
            string nm = "";
            try { nm = e.Current.Name ?? ""; } catch { }
            Log("ui select [" + start + "," + end + ") " + nm);
            return "{\"ok\":true,\"start\":" + start + ",\"end\":" + end + ",\"name\":\"" + JsonEscape(nm) + "\"}";
        }
        catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}"; }
    }

    static string UiClick(Dictionary<string, string> q)
    {
        try
        {
            var e = UiElement(q);
            if (e == null) return "{\"ok\":false,\"error\":\"element not found (bad index or window)\"}";
            string name = "";
            try { name = e.Current.Name ?? ""; } catch { }
            // 优先语义模式
            object pat;
            if (e.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out pat))
            { ((System.Windows.Automation.InvokePattern)pat).Invoke(); Log("ui click invoke: " + name); return "{\"ok\":true,\"via\":\"invoke\",\"name\":\"" + JsonEscape(name) + "\"}"; }
            if (e.TryGetCurrentPattern(System.Windows.Automation.TogglePattern.Pattern, out pat))
            { ((System.Windows.Automation.TogglePattern)pat).Toggle(); Log("ui click toggle: " + name); return "{\"ok\":true,\"via\":\"toggle\",\"name\":\"" + JsonEscape(name) + "\"}"; }
            if (e.TryGetCurrentPattern(System.Windows.Automation.ExpandCollapsePattern.Pattern, out pat))
            { ((System.Windows.Automation.ExpandCollapsePattern)pat).Expand(); Log("ui click expand: " + name); return "{\"ok\":true,\"via\":\"expand\",\"name\":\"" + JsonEscape(name) + "\"}"; }
            if (e.TryGetCurrentPattern(System.Windows.Automation.SelectionItemPattern.Pattern, out pat))
            { ((System.Windows.Automation.SelectionItemPattern)pat).Select(); Log("ui click select: " + name); return "{\"ok\":true,\"via\":\"select\",\"name\":\"" + JsonEscape(name) + "\"}"; }
            // 退坐标点击中心
            System.Windows.Rect r2 = e.Current.BoundingRectangle;
            int cx = (int)(r2.X + r2.Width / 2), cy = (int)(r2.Y + r2.Height / 2);
            SetCursorPos(cx, cy);
            Thread.Sleep(60);
            MouseClick("left", 1);
            Log("ui click coord: " + name + " @ " + cx + "," + cy);
            return "{\"ok\":true,\"via\":\"coord\",\"x\":" + cx + ",\"y\":" + cy + ",\"name\":\"" + JsonEscape(name) + "\"}";
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
                return "{\"ok\":true,\"via\":\"value\",\"len\":" + val.Length + "}";
            }
            return "{\"ok\":false,\"error\":\"element has no ValuePattern (不可直写, 用 keyboard/type)\"}";
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

    static string RecordStart(int x, int y, int w, int h, int fps)
    {
        if (recording) return "{\"ok\":false,\"error\":\"already recording\",\"file\":\"" + JsonEscape(recPath) + "\"}";
        if (w <= 0 || h <= 0) { var vs = VirtualScreen(); x = vs.X; y = vs.Y; w = vs.Width; h = vs.Height; }
        if (w % 2 != 0) w--;
        if (h % 2 != 0) h--;
        if (fps <= 0 || fps > 30) fps = 10;
        recRect = new Rectangle(x, y, w, h);
        recFps = fps;
        recPath = System.IO.Path.Combine(ShotDir, "rec_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4");
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
        return "{\"ok\":true,\"file\":\"" + JsonEscape(recPath) + "\",\"fps\":" + fps + "}";
    }

    static void RecordLoop()
    {
        int bw = recRect.Width, bh = recRect.Height;
        using (Bitmap frame = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
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
            var all = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
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
