// shot-service — 自建 Session 1 多模态助手桥 (HTTP, 127.0.0.1:18800)
// 用途: Session 0 的任何 agent 通过 HTTP 请求, 让运行在用户会话(Session 1)的本服务:
//   看: 截图(全屏/区域/窗口/显示器) + 活动窗口信息 + 显示器元数据
//   动: 鼠标移动/点击/滚轮 + 键盘输入(含中文)/组合键 — T1 Safe Computer Control (Level 1)
// 绕开 Session 0 无显示/无输入通道的内核限制 (GDI 直截黑屏, SendInput 无目标)
// 编译 (系统自带 .NET Framework 4.8, C#5 语法):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ ^
//     /out:shot-service.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll shot-service.cs
//   (改码前必须先 Stop-Process shot-service, 否则 exe 被锁 CS0016)
// API:
//   GET /health                      存活/会话检查
//   -- 看 --
//   GET /shot?region=all             全部屏幕(虚拟屏并集) [默认]
//   GET /shot?screen=0               指定显示器(下标)
//   GET /shot?x=0&y=0&w=800&h=600    任意矩形(物理像素)
//   GET /shot?window=标题关键词       按窗口标题截取窗口区域
//   GET /active                      当前活动窗口: {title, process, rect}
//   GET /monitors                    显示器元数据: [{index,bounds,primary,device}]
//   -- 动 (Level 1 低风险, 需当前会话=Session 1) --
//   GET /mouse/move?x=100&y=200      移动鼠标(物理像素)
//   GET /mouse/click?x=&y=&button=left|right|middle&double=0|1   点击(可带坐标)
//   GET /mouse/scroll?delta=120      滚轮(正上负下, 典型±120)
//   GET /keyboard/type?text=...      键盘输入(URL编码, 支持中文/emoji/换行)
//   GET /keyboard/press?keys=ctrl+shift+a   组合键(修饰符: ctrl/shift/alt/win)
// 保存: <用户图片目录>\Screenshots\shot_yyyy-MM-dd_HH-mm-ss-fff.png (可用环境变量 WDH_SHOT_DIR 覆盖)
// 自启: HKCU\...\Run\shot-service + shot-watcher(崩溃自愈) + 计划任务 dsh-shot-helper(手动拉起)
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public class ShotService
{
    const int PORT = 18800;
    const string APP_VERSION = "0.0.5";
    const string REPO_URL = "https://github.com/oadank/win-desktop-helper";
    const string LATEST_API = "https://api.github.com/repos/oadank/win-desktop-helper/releases/latest";
    const string MUTEX_NAME = @"Global\WinDesktopHelper"; // 单实例互斥(跨会话, 防双进程)
    static Mutex instanceMutex;
    static readonly string ShotDir = Environment.GetEnvironmentVariable("WDH_SHOT_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot-service.log");
    static int MySession = Process.GetCurrentProcess().SessionId;
    static int ShotCount = 0;
    static DateTime StartTime = DateTime.Now;
    static NotifyIcon TrayIcon;

    // ---- Win32 ----
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback cb, IntPtr lp);
    delegate bool Callback(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    const uint MOUSEEVENTF_WHEEL = 0x0800;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion U;
    }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData;
        public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    static void Log(string msg) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg + "\r\n"); } catch { } }

    // ---- 看: 屏幕/窗口 ----
    static Rectangle VirtualScreen()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxR = int.MinValue, maxB = int.MinValue;
        foreach (Screen s in Screen.AllScreens)
        {
            if (s.Bounds.X < minX) minX = s.Bounds.X;
            if (s.Bounds.Y < minY) minY = s.Bounds.Y;
            if (s.Bounds.Right > maxR) maxR = s.Bounds.Right;
            if (s.Bounds.Bottom > maxB) maxB = s.Bounds.Bottom;
        }
        return new Rectangle(minX, minY, maxR - minX, maxB - minY);
    }

    static IntPtr FindWindowByTitle(string keyword)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            if (!IsWindowVisible(h)) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            if (sb.ToString().IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static string WindowTitle(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(1024);
        GetWindowTextW(h, sb, 1024);
        return sb.ToString();
    }

    static string DoShot(Rectangle r)
    {
        using (Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height));
            string name = "shot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".png";
            string path = Path.Combine(ShotDir, name);
            bmp.Save(path, ImageFormat.Png);
            return path;
        }
    }

    static string WindowJsonByTitle(string keyword)
    {
        IntPtr h = FindWindowByTitle(keyword);
        if (h == IntPtr.Zero) return null;
        RECT rc; GetWindowRect(h, out rc);
        uint pid; GetWindowThreadProcessId(h, out pid);
        string proc = "";
        try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
        return "{\"ok\":true,\"hwnd\":" + h.ToInt64() + ",\"title\":\"" + JsonEscape(WindowTitle(h)) +
               "\",\"process\":\"" + JsonEscape(proc) + "\",\"rect\":{\"x\":" + rc.Left + ",\"y\":" + rc.Top +
               ",\"w\":" + (rc.Right - rc.Left) + ",\"h\":" + (rc.Bottom - rc.Top) + "}}";
    }

    static string ActiveWindowJson()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "{\"ok\":true,\"hwnd\":0,\"title\":\"\",\"process\":\"\",\"rect\":{\"x\":0,\"y\":0,\"w\":0,\"h\":0}}";
        RECT rc; GetWindowRect(h, out rc);
        uint pid; GetWindowThreadProcessId(h, out pid);
        string proc = "";
        try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
        return "{\"ok\":true,\"hwnd\":" + h.ToInt64() + ",\"title\":\"" + JsonEscape(WindowTitle(h)) +
               "\",\"process\":\"" + JsonEscape(proc) + "\",\"rect\":{\"x\":" + rc.Left + ",\"y\":" + rc.Top +
               ",\"w\":" + (rc.Right - rc.Left) + ",\"h\":" + (rc.Bottom - rc.Top) + "}}";
    }

    static string MonitorsJson()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"ok\":true,\"count\":").Append(Screen.AllScreens.Length).Append(",\"screens\":[");
        for (int i = 0; i < Screen.AllScreens.Length; i++)
        {
            Screen s = Screen.AllScreens[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"index\":").Append(i)
              .Append(",\"bounds\":{\"x\":").Append(s.Bounds.X).Append(",\"y\":").Append(s.Bounds.Y)
              .Append(",\"w\":").Append(s.Bounds.Width).Append(",\"h\":").Append(s.Bounds.Height).Append("}")
              .Append(",\"primary\":").Append(s.Primary ? "true" : "false")
              .Append(",\"device\":\"").Append(JsonEscape(s.DeviceName)).Append("\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ---- 动: 鼠标 ----
    static void MouseMove(int x, int y) { SetCursorPos(x, y); }

    static void MouseClick(string button, bool dbl)
    {
        uint down = MOUSEEVENTF_LEFTDOWN, up = MOUSEEVENTF_LEFTUP;
        if (button == "right") { down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; }
        else if (button == "middle") { down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; }
        int times = dbl ? 2 : 1;
        for (int i = 0; i < times; i++)
        {
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }
    }

    static void MouseScroll(int delta) { mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), UIntPtr.Zero); }

    // 运行程序/打开(ShellExecute: 支持 exe/快捷方式/URL); 须在 Session 1 才有用户可见界面
    static string AppRun(string path, string args)
    {
        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = path;
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        psi.UseShellExecute = true;
        Process p = Process.Start(psi);
        return "{\"ok\":true,\"pid\":" + p.Id + ",\"name\":\"" + JsonEscape(p.ProcessName) + "\",\"session\":" + Process.GetCurrentProcess().SessionId + "}";
    }

    // ---- 动: 键盘 ----
    static void KeyEvent(ushort vk, ushort scan, uint flags)
    {
        INPUT[] ins = new INPUT[1];
        ins[0].type = INPUT_KEYBOARD;
        ins[0].U.ki.wVk = vk;
        ins[0].U.ki.wScan = scan;
        ins[0].U.ki.dwFlags = flags;
        SendInput(1, ins, Marshal.SizeOf(typeof(INPUT)));
    }

    static void TypeText(string text)
    {
        text = text.Replace("\r\n", "\n");
        foreach (char c in text)
        {
            if (c == '\n') { KeyEvent(0x0D, 0, 0); KeyEvent(0x0D, 0, KEYEVENTF_KEYUP); continue; } // Enter
            if (c == '\t') { KeyEvent(0x09, 0, 0); KeyEvent(0x09, 0, KEYEVENTF_KEYUP); continue; } // Tab
            // Unicode 直发(中文/emoji 不依赖输入法)
            KeyEvent(0, (ushort)c, KEYEVENTF_UNICODE);
            KeyEvent(0, (ushort)c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }
    }

    static ushort KeyToVk(string k)
    {
        k = k.ToLowerInvariant();
        switch (k)
        {
            case "enter": case "return": return 0x0D;
            case "tab": return 0x09;
            case "esc": case "escape": return 0x1B;
            case "space": return 0x20;
            case "backspace": case "bs": return 0x08;
            case "delete": case "del": return 0x2E;
            case "insert": case "ins": return 0x2D;
            case "home": return 0x24; case "end": return 0x23;
            case "pageup": case "pgup": return 0x21; case "pagedown": case "pgdn": return 0x22;
            case "up": return 0x26; case "down": return 0x28; case "left": return 0x25; case "right": return 0x27;
            case "printscreen": case "prtsc": return 0x2C;
            case "capslock": return 0x14;
            case "win": return 0x5B; case "menu": return 0x5D;
            case "shift": return 0x10; case "ctrl": case "control": return 0x11; case "alt": return 0x12;
        }
        if (k.Length == 1)
        {
            char c = k[0];
            if (c >= 'a' && c <= 'z') return (ushort)(c - 'a' + 0x41);
            if (c >= 'A' && c <= 'Z') return (ushort)(c - 'A' + 0x41);
            if (c >= '0' && c <= '9') return (ushort)(c - '0' + 0x30);
            switch (c)
            {
                case '.': return 0xBE; case ',': return 0xBC; case '/': return 0xBF;
                case '\\': return 0xDC; case '-': return 0xBD; case '=': return 0xBB;
                case ';': return 0xBA; case '\'': return 0xDE; case '[': return 0xDB;
                case ']': return 0xDD; case '`': return 0xC0;
            }
        }
        if (k.StartsWith("f") && k.Length <= 3)
        {
            int n; if (int.TryParse(k.Substring(1), out n) && n >= 1 && n <= 24) return (ushort)(0x6F + n);
        }
        return 0;
    }

    static void PressCombo(string spec)
    {
        string[] parts = spec.Split('+');
        ushort main = KeyToVk(parts[parts.Length - 1].Trim());
        List<ushort> mods = new List<ushort>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string k = parts[i].Trim().ToLowerInvariant();
            ushort m = 0;
            if (k == "shift") m = 0x10;
            else if (k == "ctrl" || k == "control") m = 0x11;
            else if (k == "alt") m = 0x12;
            else if (k == "win") m = 0x5B;
            else if (k == "menu") m = 0x5D;
            if (m != 0) mods.Add(m);
        }
        if (main == 0) return;
        foreach (ushort m in mods) KeyEvent(m, 0, 0);
        KeyEvent(main, 0, 0);
        KeyEvent(main, 0, KEYEVENTF_KEYUP);
        for (int i = mods.Count - 1; i >= 0; i--) KeyEvent(mods[i], 0, KEYEVENTF_KEYUP);
    }

    static string JsonEscape(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }

    // 从 query 读 int, 失败返回 false
    static bool TryInt(Dictionary<string, string> q, string key, out int v) { return int.TryParse((q.ContainsKey(key) ? q[key] : ""), out v); }

    // ---- HTTP ----
    static void Handle(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 5000;
            NetworkStream ns = client.GetStream();
            byte[] buf = new byte[16384];
            int got = 0;
            string req = "";
            while (got < buf.Length)
            {
                int n = ns.Read(buf, got, buf.Length - got);
                if (n <= 0) break;
                got += n;
                req = Encoding.ASCII.GetString(buf, 0, got);
                if (req.Contains("\r\n\r\n")) break;
            }
            string[] lines = req.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] parts = (lines.Length > 0 ? lines[0] : "").Split(' ');
            string target = parts.Length > 1 ? parts[1] : "/";
            string path = target.Split('?')[0];
            string query = target.Contains("?") ? target.Substring(target.IndexOf('?') + 1) : "";
            Dictionary<string, string> q = new Dictionary<string, string>();
            foreach (string kv in query.Split('&'))
            {
                if (kv.Length == 0) continue;
                string[] k = kv.Split('=');
                string key = Uri.UnescapeDataString(k[0]);
                string val = (k.Length > 1) ? Uri.UnescapeDataString(k[1]) : "";
                q[key] = val;
            }

            bool needUserSession = path.StartsWith("/mouse") || path.StartsWith("/keyboard") || path == "/shot" || path.StartsWith("/app") || path == "/open-repo";
            bool control = path.StartsWith("/mouse") || path.StartsWith("/keyboard");
            int code = 200;
            string body = "";
            string logLine = target;

            try
            {
                if (needUserSession && MySession == 0)
                {
                    code = 503; body = "{\"ok\":false,\"error\":\"running in session 0, cannot access user desktop\"}";
                }
                else if (path == "/health")
                {
                    body = "{\"ok\":true,\"pid\":" + Process.GetCurrentProcess().Id + ",\"session\":" + MySession +
                           ",\"shots\":" + ShotCount + ",\"uptimeSec\":" + (int)(DateTime.Now - StartTime).TotalSeconds + ",\"version\":\"2.0\",\"appVersion\":\"" + APP_VERSION + "\"}";
                }
                else if (path == "/active") { body = ActiveWindowJson(); }
                else if (path.StartsWith("/img/"))
                {
                    // 托管 Screenshots 目录下的图片: /img/<文件名> → PNG 字节
                    // 用途: DSH 对话里助手消息的 Markdown 图片必须是绝对 http(s) 地址才渲染,
                    //       截图后用此端点提供 http://127.0.0.1:PORT/img/xxx.png 给 agent 引用
                    string fname = target.Substring("/img/".Length);
                    fname = Path.GetFileName(fname); // 防目录穿越
                    string fp = Path.Combine(ShotDir, fname);
                    if (File.Exists(fp))
                    {
                        byte[] imgBytes = File.ReadAllBytes(fp);
                        string ext = Path.GetExtension(fp).ToLowerInvariant();
                        string mime = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : ext == ".gif" ? "image/gif" : ext == ".webp" ? "image/webp" : "image/png";
                        using (var fs = client.GetStream())
                        {
                            string imgHead = "HTTP/1.1 200 OK\r\nContent-Type: " + mime + "\r\nContent-Length: " + imgBytes.Length + "\r\nCache-Control: max-age=3600\r\nConnection: close\r\n\r\n";
                            byte[] imgHb = Encoding.ASCII.GetBytes(imgHead);
                            fs.Write(imgHb, 0, imgHb.Length);
                            fs.Write(imgBytes, 0, imgBytes.Length);
                            fs.Flush();
                        }
                        return; // 已手写响应, 不走公共 JSON 响应
                    }
                    else { code = 404; body = "{\"ok\":false,\"error\":\"image not found\"}"; }
                }
                else if (path == "/guide")
                {
                    string guide = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SKILL.md");
                    if (File.Exists(guide)) body = File.ReadAllText(guide, Encoding.UTF8);
                    else { code = 404; body = "{\"ok\":false,\"error\":\"SKILL.md not found\"}"; }
                }
                else if (path == "/check-update")
                {
                    string v = LatestVersion();
                    bool upd = v != null && IsNewerVersion(v);
                    body = "{\"ok\":true,\"current\":\"" + APP_VERSION + "\",\"latest\":\"" + (v == null ? "unknown" : v) + "\",\"update\":" + (upd ? "true" : "false") + ",\"repo\":\"" + REPO_URL + "\"}";
                }
                else if (path == "/update")
                {
                    Thread t = new Thread(new ThreadStart(DoUpdateSilent));
                    t.IsBackground = true;
                    t.Start();
                    body = "{\"ok\":true,\"msg\":\"update started (silent)\"}";
                }
                else if (path == "/open-repo")
                {
                    try { Process.Start(REPO_URL); body = "{\"ok\":true}"; }
                    catch (Exception ex) { code = 500; body = "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}"; }
                }
                else if (path == "/window")
                {
                    if (!q.ContainsKey("title")) { code = 400; body = "{\"ok\":false,\"error\":\"need title\"}"; }
                    else
                    {
                        string wj = WindowJsonByTitle(q["title"]);
                        if (wj == null) { code = 404; body = "{\"ok\":false,\"error\":\"window not found\"}"; }
                        else body = wj;
                    }
                }
                else if (path == "/monitors") { body = MonitorsJson(); }
                else if (path == "/shot")
                {
                    Rectangle r = VirtualScreen();
                    if (q.ContainsKey("window"))
                    {
                        IntPtr h = FindWindowByTitle(q["window"]);
                        if (h == IntPtr.Zero) { code = 404; body = "{\"ok\":false,\"error\":\"window not found\"}"; }
                        else { RECT rc; GetWindowRect(h, out rc); r = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top); }
                    }
                    else if (q.ContainsKey("x") && q.ContainsKey("y") && q.ContainsKey("w") && q.ContainsKey("h"))
                    {
                        int x, y, w, hh;
                        int.TryParse(q["x"], out x); int.TryParse(q["y"], out y);
                        int.TryParse(q["w"], out w); int.TryParse(q["h"], out hh);
                        r = new Rectangle(x, y, w, hh);
                    }
                    else if (q.ContainsKey("screen"))
                    {
                        int idx; int.TryParse(q["screen"], out idx);
                        if (idx >= 0 && idx < Screen.AllScreens.Length) r = Screen.AllScreens[idx].Bounds;
                    }
                    if (code == 200)
                    {
                        string fp = DoShot(r);
                        FileInfo fi = new FileInfo(fp);
                        Interlocked.Increment(ref ShotCount);
                        // url 字段: 供 DSH 助手消息用 Markdown 图片语法渲染 (http 绝对地址才显示)
                        string imgUrl = "http://127.0.0.1:" + PORT + "/img/" + Uri.EscapeDataString(Path.GetFileName(fp));
                        body = "{\"ok\":true,\"file\":\"" + JsonEscape(fp) + "\",\"url\":\"" + imgUrl + "\",\"width\":" + r.Width + ",\"height\":" + r.Height +
                               ",\"bytes\":" + fi.Length + ",\"region\":{\"x\":" + r.X + ",\"y\":" + r.Y + ",\"w\":" + r.Width + ",\"h\":" + r.Height + "}}";
                    }
                }
                else if (path == "/mouse/move")
                {
                    int x, y;
                    if (!TryInt(q, "x", out x) || !TryInt(q, "y", out y)) { code = 400; body = "{\"ok\":false,\"error\":\"need x,y\"}"; }
                    else { MouseMove(x, y); body = "{\"ok\":true,\"x\":" + x + ",\"y\":" + y + "}"; Log("[ctrl] mouse move " + x + "," + y); }
                }
                else if (path == "/mouse/click")
                {
                    int x = 0, y = 0; bool hasXY = TryInt(q, "x", out x) && TryInt(q, "y", out y);
                    string button = q.ContainsKey("button") ? q["button"].ToLowerInvariant() : "left";
                    bool dbl = q.ContainsKey("double") && q["double"] == "1";
                    if (hasXY) MouseMove(x, y);
                    MouseClick(button, dbl);
                    body = "{\"ok\":true,\"button\":\"" + button + "\",\"double\":" + (dbl ? "true" : "false") +
                           (hasXY ? ",\"x\":" + x + ",\"y\":" + y : "") + "}";
                    Log("[ctrl] mouse click " + button + (dbl ? " dbl" : "") + (hasXY ? " @ " + x + "," + y : ""));
                }
                else if (path == "/mouse/scroll")
                {
                    int d;
                    if (!TryInt(q, "delta", out d)) { code = 400; body = "{\"ok\":false,\"error\":\"need delta\"}"; }
                    else { MouseScroll(d); body = "{\"ok\":true,\"delta\":" + d + "}"; Log("[ctrl] scroll " + d); }
                }
                else if (path == "/keyboard/type")
                {
                    if (!q.ContainsKey("text")) { code = 400; body = "{\"ok\":false,\"error\":\"need text\"}"; }
                    else
                    {
                        string text = q["text"];
                        if (text.Length > 2000) { code = 400; body = "{\"ok\":false,\"error\":\"text too long (max 2000)\"}"; }
                        else { TypeText(text); body = "{\"ok\":true,\"chars\":" + text.Length + "}"; Log("[ctrl] type " + text.Length + " chars"); }
                    }
                }
                else if (path == "/keyboard/press")
                {
                    if (!q.ContainsKey("keys")) { code = 400; body = "{\"ok\":false,\"error\":\"need keys\"}"; }
                    else { PressCombo(q["keys"]); body = "{\"ok\":true,\"keys\":\"" + JsonEscape(q["keys"]) + "\"}"; Log("[ctrl] press " + q["keys"]); }
                }
                else if (path == "/app/run")
                {
                    if (!q.ContainsKey("path")) { code = 400; body = "{\"ok\":false,\"error\":\"need path\"}"; }
                    else
                    {
                        try { body = AppRun(q["path"], q.ContainsKey("args") ? q["args"] : ""); Log("[run] " + q["path"]); }
                        catch (Exception ex) { code = 500; body = "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}"; }
                    }
                }
                else { code = 404; body = "{\"ok\":false,\"error\":\"not found\"}"; }
            }
            catch (Exception ex) { code = 500; body = "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}"; Log("handler err: " + ex.Message); }

            if (code == 200 && body == "") { code = 404; body = "{\"ok\":false,\"error\":\"not found\"}"; }
            string reason = code == 200 ? "OK" : code == 404 ? "Not Found" : code == 400 ? "Bad Request" : code == 500 ? "Internal Server Error" : "Service Unavailable";
            byte[] resp = Encoding.UTF8.GetBytes(body);
            string contentType = path == "/guide" ? "text/plain; charset=utf-8" : "application/json; charset=utf-8";
            string head = "HTTP/1.1 " + code + " " + reason + "\r\n" +
                          "Content-Type: " + contentType + "\r\n" +
                          "Content-Length: " + resp.Length + "\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            ns.Write(resp, 0, resp.Length);
            ns.Flush();
            Log("req " + code + " " + (control ? "[ctrl]" : "") + logLine);
        }
        catch (Exception ex) { Log("handle err: " + ex.Message); }
        finally { client.Close(); }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // 单实例互斥: 已有实例则直接退出 (防 Session 0/1 双进程抢端口)
        bool createdNew;
        try
        {
            instanceMutex = new Mutex(true, MUTEX_NAME, out createdNew);
            if (!createdNew) { Log("another instance running, exit"); return; }
        }
        catch (Exception ex) { Log("mutex err: " + ex.Message); }

        bool allowTray = true, watchMode = false;
        foreach (string a in args)
        {
            string x = (a ?? "").ToLowerInvariant();
            if (x == "-notray") allowTray = false;
            else if (x == "-watch") watchMode = true;
        }
        try { SetProcessDPIAware(); } catch { }
        // .NET 4.8 默认 TLS1.0, GitHub API 需 TLS1.2 (否则更新检测失败)
        try { System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12; } catch { }
        Log("shot-service v3 session=" + MySession + " pid=" + Process.GetCurrentProcess().Id +
            " mode=" + (watchMode ? "watch" : "service") + (allowTray ? " tray=on" : " tray=off"));
        try { if (!Directory.Exists(ShotDir)) Directory.CreateDirectory(ShotDir); } catch { }

        if (watchMode) { RunWatch(); return; }    // 自愈守护 (替代 shot-watcher.exe)

        Thread srv = new Thread(new ThreadStart(ServerLoop));
        srv.IsBackground = true;
        srv.Start();

        if (allowTray)
        {
            InitTray();
            Thread upChk = new Thread(new ThreadStart(CheckUpdateSilent));
            upChk.IsBackground = true;
            upChk.Start();
            Application.Run();
        }
        else { while (true) Thread.Sleep(60000); }
    }

    static void ServerLoop()
    {
        TcpListener listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, PORT);
            listener.Start();
            Log("listening on 127.0.0.1:" + PORT);
        }
        catch (Exception ex) { Log("listen failed: " + ex.Message); return; }
        while (true)
        {
            try
            {
                TcpClient c = listener.AcceptTcpClient();
                Thread t = new Thread(new ParameterizedThreadStart(delegate(object o) { Handle((TcpClient)o); }));
                t.IsBackground = true;
                t.Start(c);
            }
            catch (Exception ex) { Log("accept err: " + ex.Message); Thread.Sleep(500); }
        }
    }

    // ---- 更新检测 / 项目主页 ----
    static string GetToken()
    {
        string tk = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(tk))
        {
            string tf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wdh-gh-token");
            if (File.Exists(tf)) tk = File.ReadAllText(tf).Trim();
        }
        return tk;
    }

    // API 客户端: 带 User-Agent; 有 token 则带认证(访问 private 仓库)
    static WebClient NewApiClient()
    {
        var wc = new WebClient();
        wc.Headers.Add("User-Agent", "win-desktop-helper/" + APP_VERSION);
        string tk = GetToken();
        if (!string.IsNullOrEmpty(tk)) wc.Headers.Add("Authorization", "token " + tk);
        return wc;
    }

    // 下载 release asset (GitHub 官方姿势: 先带认证拿 302 Location, 再不带认证跟随下载, 避免 Authorization 泄漏到 CDN)
    static void DownloadAsset(long assetId, string dest)
    {
        string api = "https://api.github.com/repos/oadank/win-desktop-helper/releases/assets/" + assetId;
        string token = GetToken();
        var req1 = (HttpWebRequest)WebRequest.Create(api);
        req1.UserAgent = "win-desktop-helper/" + APP_VERSION;
        req1.Accept = "application/octet-stream";
        req1.AllowAutoRedirect = false;
        if (!string.IsNullOrEmpty(token)) req1.Headers["Authorization"] = "token " + token;
        req1.Timeout = 60000;
        string location = null;
        using (var resp1 = (HttpWebResponse)req1.GetResponse())
        {
            int code = (int)resp1.StatusCode;
            if (code == 302 || code == 301) location = resp1.Headers["Location"];
            else if (code == 200) { using (var fs = File.Create(dest)) resp1.GetResponseStream().CopyTo(fs); return; } // 直接 200 (罕见)
        }
        if (string.IsNullOrEmpty(location)) throw new Exception("asset redirect missing");
        // 第二步: 用 WebClient 跟随 Location 下载 (实测稳定, 不带任何认证头)
        using (var wc2 = new WebClient())
        {
            wc2.Headers.Add("User-Agent", "win-desktop-helper/" + APP_VERSION);
            wc2.DownloadFile(location, dest);
        }
    }

    static string LatestVersion()
    {
        try
        {
            using (var wc = NewApiClient())
            {
                string json = wc.DownloadString(LATEST_API);
                int i = json.IndexOf("\"tag_name\":");
                if (i >= 0)
                {
                    int s = json.IndexOf('"', i + 11);
                    int e = json.IndexOf('"', s + 1);
                    if (s >= 0 && e > s) return json.Substring(s + 1, e - s - 1);
                }
            }
        }
        catch (Exception ex) { Log("update check err: " + ex.Message); }
        return null;
    }

    static bool IsNewerVersion(string remote)
    {
        string r = (remote ?? "").TrimStart('v', 'V');
        string[] rp = r.Split('.');
        string[] lp = APP_VERSION.Split('.');
        for (int i = 0; i < Math.Max(rp.Length, lp.Length); i++)
        {
            int rv = 0, lv = 0;
            if (i < rp.Length) int.TryParse(rp[i], out rv);
            if (i < lp.Length) int.TryParse(lp[i], out lv);
            if (rv != lv) return rv > lv;
        }
        return false;
    }

    // 启动时静默检查: 有新版 → 自动静默更新
    static void CheckUpdateSilent()
    {
        string v = LatestVersion();
        if (v != null && IsNewerVersion(v))
        {
            try { TrayIcon.ShowBalloonTip(6000, "发现新版本 v" + v.TrimStart('v', 'V'), "正在自动静默更新，完成后自动恢复...", ToolTipIcon.Info); } catch { }
            Thread.Sleep(2500);
            DoUpdateSilent();
        }
    }

    // 自动静默更新: 下载最新 setup 并静默安装到当前目录（保持路径不变，装完由 watcher 拉起新版）
    static int updatingFlag = 0; // 防重入(启动自动检查与手动触发可能并发)
    static void DoUpdateSilent()
    {
        if (Interlocked.Exchange(ref updatingFlag, 1) == 1) return;
        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), "wdh-update-setup.exe");
            using (var wc = NewApiClient())
            {
                string json = wc.DownloadString(LATEST_API);
                long assetId = 0;
                int ia = json.IndexOf("\"assets\":[");
                if (ia >= 0)
                {
                    int j = json.IndexOf("\"id\":", ia);
                    if (j >= 0)
                    {
                        int s = j + 5;
                        while (s < json.Length && !char.IsDigit(json[s])) s++;
                        int e = s;
                        while (e < json.Length && char.IsDigit(json[e])) e++;
                        if (e > s) long.TryParse(json.Substring(s, e - s), out assetId);
                    }
                }
                if (assetId == 0) { Log("update: no asset id found"); return; }
                DownloadAsset(assetId, tmp);
            }
            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\') + "\\";
            // 停止自愈守护, 避免安装期间 watcher 拉起旧版占用 exe
            try { foreach (var p in Process.GetProcessesByName("shot-watcher")) { try { p.Kill(); } catch { } } } catch { }
            Process.Start(tmp, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"" + dir + "\"");
            Log("update: silent installer launched, target=" + dir);
            // 本进程退出以释放 exe 占用(CloseApplications 对无窗口 winexe 不可靠);
            // 装完 iss 的 [Run] 会拉起 watcher -> 新服务恢复
            Thread.Sleep(2500);
            Environment.Exit(0);
        }
        catch (Exception ex) { Log("update err: " + ex.Message); }
    }

    // ---- 模式: -watch 自愈守护 (替代独立 shot-watcher.exe) ----
    static bool ServiceAlive()
    {
        try { using (TcpClient c = new TcpClient("127.0.0.1", PORT)) { return true; } }
        catch { }
        return false;
    }

    static void RunWatch()
    {
        DateTime last = DateTime.MinValue;
        while (true)
        {
            if (!ServiceAlive() && (DateTime.Now - last).TotalMilliseconds > 15000)
            {
                try { Process.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot-service.exe")); last = DateTime.Now; Log("watch: relaunch shot-service"); }
                catch (Exception ex) { Log("watch relaunch fail: " + ex.Message); }
            }
            Thread.Sleep(30000);
        }
    }

    // ---- 模式: -mcp MCP stdio server (与 mcp-bridge.js 等价, 一个 exe 内置, 无需 node) ----
    // winexe 无控制台, .NET Console 不可用 → 用原生句柄读写 stdin/stdout
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle); // -10=stdin -11=stdout
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(IntPtr h, byte[] buf, uint n, out uint read, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool WriteFile(IntPtr h, byte[] buf, uint n, out uint written, IntPtr ov);

    static string McpParam(Dictionary<string, object> a, string key) { object v; return (a != null && a.TryGetValue(key, out v) && v != null) ? v.ToString() : ""; }
    static int McpParamInt(Dictionary<string, object> a, string key) { int n; int.TryParse(McpParam(a, key), out n); return n; }

    static void RunMcp()
    {
        IntPtr hIn = GetStdHandle(-10), hOut = GetStdHandle(-11);
        byte[] buf = new byte[65536];
        StringBuilder pending = new StringBuilder();
        bool skillRead = false;
        while (true)
        {
            uint read;
            if (!ReadFile(hIn, buf, (uint)buf.Length, out read, IntPtr.Zero) || read == 0) break;
            pending.Append(Encoding.UTF8.GetString(buf, 0, (int)read));
            string s = pending.ToString();
            pending.Clear();
            int nl;
            while ((nl = s.IndexOf('\n')) >= 0)
            {
                string line = s.Substring(0, nl).TrimEnd('\r');
                s = s.Substring(nl + 1);
                if (line.Trim().Length == 0) continue;
                string resp = McpHandle(line, ref skillRead);
                if (resp != null)
                {
                    byte[] ob = Encoding.UTF8.GetBytes(resp + "\n");
                    uint w; WriteFile(hOut, ob, (uint)ob.Length, out w, IntPtr.Zero);
                }
            }
            pending.Append(s);
        }
    }

    static string McpHandle(string line, ref bool skillRead)
    {
        try
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var msg = ser.Deserialize<Dictionary<string, object>>(line);
            object idObj; msg.TryGetValue("id", out idObj);
            string id = idObj == null ? "null" : idObj.ToString();
            string method = msg.ContainsKey("method") ? msg["method"].ToString() : "";
            Dictionary<string, object> prms = (msg.ContainsKey("params") && msg["params"] is Dictionary<string, object>)
                ? (Dictionary<string, object>)msg["params"] : new Dictionary<string, object>();

            if (method == "initialize")
                return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{\"tools\":{\"listChanged\":false}},\"serverInfo\":{\"name\":\"win-desktop-helper\",\"version\":\"3.0\"}}";
            if (method == "ping") return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}";
            if (method == "notifications/initialized" || method == "notifications/cancelled") return null;
            if (method == "tools/list")
                return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":" + McpToolsJson() + "}}";
            if (method == "tools/call")
            {
                string name = prms.ContainsKey("name") ? prms["name"].ToString() : "";
                Dictionary<string, object> args = (prms.ContainsKey("arguments") && prms["arguments"] is Dictionary<string, object>)
                    ? (Dictionary<string, object>)prms["arguments"] : new Dictionary<string, object>();
                return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + McpCall(name, args, ref skillRead) + "}";
            }
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32601,\"message\":\"method not found\"}}";
        }
        catch (Exception ex) { Log("mcp parse err: " + ex.Message); return null; }
    }

    static string McpText(string s, bool isError) { return "{\"content\":[{\"type\":\"text\",\"text\":\"" + JsonEscape(s) + "\"}],\"isError\":" + (isError ? "true" : "false") + "}"; }

    static string McpCall(string name, Dictionary<string, object> a, ref bool skillRead)
    {
        if (name == "get_skill")
        {
            skillRead = true;
            string fp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SKILL.md");
            string txt = File.Exists(fp) ? File.ReadAllText(fp, Encoding.UTF8) : "(SKILL.md 缺失，无法读取操作手册)";
            return McpText(txt + "\n\n—— 请遵守以上 SKILL 纪律。执行中若踩坑，务必用 update_skill 把经验写回 SKILL.md（全体 agent 共享），不要只写进自己的记忆。", false);
        }
        if (name == "update_skill")
        {
            try
            {
                string fp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SKILL.md");
                string t = McpParam(a, "title"); if (t == "") t = "经验补充";
                File.AppendAllText(fp, "\n## " + t + "\n\n" + McpParam(a, "entry") + "\n", Encoding.UTF8);
                return McpText("已写入共享 SKILL.md: " + fp, false);
            }
            catch (Exception ex) { return McpText("写入失败: " + ex.Message, true); }
        }
        if (!skillRead) return McpText("⚠️ 本服务强制要求：首次操作前必须先调用 get_skill 获取 SKILL 操作手册与安全纪律。请先调用 get_skill 再重试。踩坑后用 update_skill 写回共享 SKILL.md。", true);
        try
        {
            switch (name)
            {
                case "screen_capture":
                {
                    Rectangle r = VirtualScreen();
                    if (McpParam(a, "screen") != "") { int idx = McpParamInt(a, "screen"); if (idx >= 0 && idx < Screen.AllScreens.Length) r = Screen.AllScreens[idx].Bounds; }
                    else if (McpParam(a, "x") != "" && McpParam(a, "y") != "" && McpParam(a, "w") != "" && McpParam(a, "h") != "") r = new Rectangle(McpParamInt(a, "x"), McpParamInt(a, "y"), McpParamInt(a, "w"), McpParamInt(a, "h"));
                    else if (McpParam(a, "window") != "")
                    {
                        IntPtr h = FindWindowByTitle(McpParam(a, "window"));
                        if (h == IntPtr.Zero) return McpText("{\"ok\":false,\"error\":\"window not found\"}", true);
                        RECT rc; GetWindowRect(h, out rc); r = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
                    }
                    string fp = DoShot(r);
                    FileInfo fi = new FileInfo(fp);
                    return McpText("{\"ok\":true,\"file\":\"" + JsonEscape(fp) + "\",\"width\":" + r.Width + ",\"height\":" + r.Height + ",\"bytes\":" + fi.Length + "}", false);
                }
                case "window_info":
                {
                    string wj = WindowJsonByTitle(McpParam(a, "title"));
                    return wj == null ? McpText("{\"ok\":false,\"error\":\"window not found\"}", true) : McpText(wj, false);
                }
                case "active_window": return McpText(ActiveWindowJson(), false);
                case "monitors": return McpText(MonitorsJson(), false);
                case "mouse_move": { int x = McpParamInt(a, "x"), y = McpParamInt(a, "y"); MouseMove(x, y); return McpText("{\"ok\":true,\"x\":" + x + ",\"y\":" + y + "}", false); }
                case "mouse_click":
                {
                    string button = McpParam(a, "button"); if (button == "") button = "left";
                    bool dbl = McpParam(a, "double") == "1";
                    if (McpParam(a, "x") != "" && McpParam(a, "y") != "") MouseMove(McpParamInt(a, "x"), McpParamInt(a, "y"));
                    MouseClick(button, dbl);
                    return McpText("{\"ok\":true,\"button\":\"" + button + "\",\"double\":" + (dbl ? "true" : "false") + "}", false);
                }
                case "mouse_scroll": { int d = McpParamInt(a, "delta"); MouseScroll(d); return McpText("{\"ok\":true,\"delta\":" + d + "}", false); }
                case "keyboard_type": { string t = McpParam(a, "text"); TypeText(t); return McpText("{\"ok\":true,\"chars\":" + t.Length + "}", false); }
                case "keyboard_press": { string k = McpParam(a, "keys"); PressCombo(k); return McpText("{\"ok\":true,\"keys\":\"" + JsonEscape(k) + "\"}", false); }
                case "app_run": { return McpText(AppRun(McpParam(a, "path"), McpParam(a, "args")), false); }
                default: return McpText("unknown tool: " + name, true);
            }
        }
        catch (Exception ex) { return McpText("error: " + ex.Message, true); }
    }

    static string McpToolsJson()
    {
        return "[" +
            "{\"name\":\"screen_capture\",\"description\":\"截取用户桌面指定区域，返回 PNG 文件路径。region=all 全屏(默认)；screen=0 指定显示器；x,y,w,h 任意矩形；window=窗口标题关键词\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"region\":{\"type\":\"string\"},\"screen\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"w\":{\"type\":\"number\"},\"h\":{\"type\":\"number\"},\"window\":{\"type\":\"string\"}}}}," +
            "{\"name\":\"window_info\",\"description\":\"按窗口标题关键词查询窗口信息 {hwnd,title,process,rect}，操作前定位用。查不到返回 ok:false\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"}},\"required\":[\"title\"]}}," +
            "{\"name\":\"active_window\",\"description\":\"获取当前活动窗口信息 {title,process,rect}\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"monitors\",\"description\":\"列出显示器元数据（分辨率/主屏/设备名）\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"mouse_move\",\"description\":\"移动鼠标到物理像素坐标\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\"]}}," +
            "{\"name\":\"mouse_click\",\"description\":\"点击（带坐标先移动再点）。button=left|right|middle，double=1 双击\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"button\":{\"type\":\"string\"},\"double\":{\"type\":\"number\"}}}}," +
            "{\"name\":\"mouse_scroll\",\"description\":\"滚轮：正数=向上滚，负数=向下滚（典型 ±120）\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"delta\":{\"type\":\"number\"}},\"required\":[\"delta\"]}}," +
            "{\"name\":\"keyboard_type\",\"description\":\"向当前聚焦输入框打字。中文/emoji 直接支持（Unicode 事件，不依赖输入法）。≤2000 字符\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}}," +
            "{\"name\":\"keyboard_press\",\"description\":\"按组合键，如 ctrl+shift+a / enter / alt+f4 / win / ctrl+s\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"keys\":{\"type\":\"string\"}},\"required\":[\"keys\"]}}," +
            "{\"name\":\"app_run\",\"description\":\"运行程序/打开（exe/快捷方式/URL）。GUI 会在用户桌面可见\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\"},\"args\":{\"type\":\"string\"}},\"required\":[\"path\"]}}," +
            "{\"name\":\"get_skill\",\"description\":\"【必须先调用】获取本服务 SKILL 操作手册（铁律/避坑/流程）。所有工具首次调用前强制先读本 SKILL，否则报错。踩坑必须 update_skill 写回，禁止只写记忆。\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"update_skill\",\"description\":\"【踩坑必写】把新踩坑经验写回共享 SKILL.md（全体 agent 共享，立即生效）。title=小节标题，entry=markdown 正文\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"entry\":{\"type\":\"string\"}},\"required\":[\"title\",\"entry\"]}}" +
            "]";
    }

    // ---- 托盘（默认显示；右键菜单；隐藏后重启服务恢复） ----
    static void InitTray()
    {
        TrayIcon = new NotifyIcon();
        TrayIcon.Icon = BuildIcon();
        TrayIcon.Text = "Win Desktop Helper\nSession " + MySession + " | :18800";
        TrayIcon.Visible = true;
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Win Desktop Helper  v" + APP_VERSION, null, null).Enabled = false; // 只读版本显示
        menu.Items.Add("立即截图(全屏+复制路径)", null, delegate
        {
            try { string fp = DoShot(VirtualScreen()); Log("tray shot: " + fp); Clipboard.SetText(fp); TrayIcon.ShowBalloonTip(1500, "Win Desktop Helper", "已截图: " + fp, ToolTipIcon.Info); }
            catch (Exception ex) { Log("tray shot err: " + ex.Message); }
        });
        menu.Items.Add("打开截图目录", null, delegate { try { if (Directory.Exists(ShotDir)) Process.Start("explorer.exe", "\"" + ShotDir + "\""); } catch { } });
        menu.Items.Add("打开日志", null, delegate { try { if (File.Exists(LogPath)) Process.Start("notepad.exe", "\"" + LogPath + "\""); } catch { } });
        menu.Items.Add("复制 MCP 接入配置", null, delegate
        {
            try
            {
                string bridge = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp-bridge.js").Replace("\\", "/");
                string txt = "【Win Desktop Helper MCP 接入配置】\n\n" +
                    "[Claude Desktop] claude_desktop_config.json 的 mcpServers 加:\n" +
                    "  \"win-desktop-helper\": { \"command\": \"node\", \"args\": [\"" + bridge + "\"] }\n\n" +
                    "[DSH] ~/.dsh/mcp-servers.json 的 servers 数组加:\n" +
                    "  { \"id\":\"win-desktop-helper\", \"serverName\":\"win-desktop-helper\", \"transport\":\"stdio\",\n" +
                    "    \"command\":\"node\", \"args\":[\"" + bridge + "\"], \"enabled\":true }\n" +
                    "    （若 DSH 要求绝对路径，把 command 改成 node.exe 全路径，如 C:/Program Files/nodejs/node.exe）\n\n" +
                    "[通用] command=node, args=[" + bridge + "]";
                Clipboard.SetText(txt);
                TrayIcon.ShowBalloonTip(2000, "已复制 MCP 配置", "粘贴到 Claude Desktop / DSH mcp-servers.json 即可接入", ToolTipIcon.Info);
            }
            catch (Exception ex) { Log("tray mcp cfg err: " + ex.Message); }
        });
        menu.Items.Add("检查更新", null, delegate
        {
            try
            {
                string v = LatestVersion();
                if (v == null) MessageBox.Show("无法连接 GitHub，请检查网络", "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (IsNewerVersion(v)) { MessageBox.Show("发现新版本 v" + v.TrimStart('v', 'V') + "，正在自动静默更新...", "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Information); Thread.Sleep(800); DoUpdateSilent(); }
                else MessageBox.Show("已是最新版本 v" + APP_VERSION, "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show("检查失败: " + ex.Message, "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        });
        menu.Items.Add("下载并安装更新", null, delegate { DoUpdateSilent(); });
        menu.Items.Add("项目主页 (GitHub)", null, delegate { try { Process.Start(REPO_URL); } catch { } });
        menu.Items.Add("-");
        menu.Items.Add("隐藏托盘图标", null, delegate { TrayIcon.Visible = false; Log("tray hidden (restart service to show again)"); });
        menu.Items.Add("退出服务", null, delegate { Log("tray exit requested (watcher will relaunch)"); Environment.Exit(0); });
        TrayIcon.ContextMenuStrip = menu;
        TrayIcon.DoubleClick += delegate
        {
            try { string fp = DoShot(VirtualScreen()); Log("tray dblclick shot: " + fp); Clipboard.SetText(fp); }
            catch (Exception ex) { Log("tray dblclick err: " + ex.Message); }
        };
        Log("tray icon ready");
    }

    // 代码绘制托盘图标: 深蓝圆底 + 白色镜头圈 + 青色核心
    static Icon BuildIcon()
    {
        Bitmap bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(45, 90, 210))) g.FillEllipse(bg, 2, 2, 28, 28);
            using (Pen rim = new Pen(Color.White, 3f)) g.DrawEllipse(rim, 7, 7, 18, 18);
            using (SolidBrush core = new SolidBrush(Color.FromArgb(120, 220, 255))) g.FillEllipse(core, 13, 13, 6, 6);
        }
        IntPtr h = bmp.GetHicon();
        Icon ic = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        bmp.Dispose();
        return ic;
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}