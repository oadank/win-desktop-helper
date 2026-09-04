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

    // 置前 (稳妥三步: 恢复最小化 -> SHOW -> SetForegroundWindow)
    static string WinActivate(IntPtr h)
    {
        if (h == IntPtr.Zero) return "{\"ok\":false,\"error\":\"window not found\"}";
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        ShowWindow(h, SW_SHOW);
        bool ok = SetForegroundWindow(h);
        Log("win activate: " + h + " ok=" + ok);
        return "{\"ok\":true,\"activated\":" + (ok ? "true" : "false") + "}";
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
                          "\",\"enabled\":" + (en ? "true" : "false") +
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
        int idx;
        if (!TryInt(q, "i", out idx)) return null;
        var all = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        if (idx < 0 || idx >= all.Count) return null;
        return all[idx];
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
            MouseClick("left", false);
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
}
