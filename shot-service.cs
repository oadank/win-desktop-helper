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
//   GET /taskbar-volume[?enabled=0|1&step=N&reverse=1]   任务栏滚轮调音量(常驻, 查状态/开关/步进/反向)
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

public partial class ShotService
{
    const int PORT = 18800;
    const string APP_VERSION = "0.0.18";
    const string REPO_URL = "https://github.com/oadank/win-desktop-helper";
    // 最新版本检查: 走 releases/latest 的 302 重定向读 Location 尾部 tag — 零 GitHub API 调用零限流(60次/小时)
    const string LATEST_URL = REPO_URL + "/releases/latest";
    const string MUTEX_NAME = @"Global\WinDesktopHelper"; // 单实例互斥(跨会话, 防双进程)
    static Mutex instanceMutex;
    static string ShotDir = Environment.GetEnvironmentVariable("WDH_SHOT_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shot-service.log");
    static int MySession = Process.GetCurrentProcess().SessionId;
    static int ShotCount = 0;
    static DateTime StartTime = DateTime.Now;
    static NotifyIcon TrayIcon;

    // ---- Win32 ----
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback cb, IntPtr lp);
    delegate bool Callback(IntPtr h, IntPtr lp);
    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
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

    // 构建指纹: 本进程 exe 的修改时间+大小 (= csc 写出 exe 的时刻)。日志/托盘/气泡/health 都带它,
    // 用户肉眼比对"编译时间 vs 运行时间"即可确认跑的是不是刚编的
    static string BuildStamp()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            FileInfo fi = new FileInfo(exe);
            return fi.LastWriteTime.ToString("MM-dd HH:mm") + " " + (fi.Length / 1024) + "KB";
        }
        catch { return "unknown"; }
    }
    static string BuildStampShort()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            return new FileInfo(exe).LastWriteTime.ToString("MM-dd HH:mm");
        }
        catch { return "?"; }
    }

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

    // ---- 任务栏滚轮调音量 (Taskbar Wheel Volume, 常驻) ----
    // 机制: WH_MOUSE_LL 低级鼠标钩子 + 光标在任务栏(Shell_TrayWnd/Shell_SecondaryTrayWnd)矩形内
    //       + 拦截 WM_MOUSEWHEEL + 模拟系统音量键 (VK_VOLUME_UP/DOWN, 弹原生音量 OSD)
    // 与 Windhawk taskbar-volume-control 同款体验, 免注入免 COM, Win10/11 通用
    const int WH_MOUSE_LL = 14;
    const int WM_MOUSEWHEEL = 0x020A;
    const int WM_MBUTTONDOWN = 0x0207;
    const int HC_ACTION = 0;
    const ushort VK_VOLUME_UP = 0xAF, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_MUTE = 0xAD;
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    static IntPtr volHook;
    static LowLevelMouseProc volHookProc;
    static volatile int volEnabled = 1;   // 1=启用
    static volatile int volReverse = 0;   // 1=滚轮上=减小音量
    static volatile int volStep = 2;      // 每次滚轮一格音量变化 (%)
    static volatile IntPtr[] taskbarWnds = new IntPtr[0]; // 原子数组快照: 刷新线程整体替换, 回调无锁读
    static DateTime lastTaskbarScan = DateTime.MinValue;
    static DateTime lastHookDiag = DateTime.MinValue;
    static long volTriggers = 0;   // 钩子触发计数(离屏验证用)
    static long volCalls = 0;      // 钩子回调总次数(诊断: 回调是否进入)
    static long volLastWheelPtX = -9999, volLastWheelPtY = -9999;  // 最近一次滚轮事件坐标(内存探针, 零I/O)
    static long volLastWheelHit = -1;   // 最近一次任务栏判定结果
    static long volLastWheelTicks = 0;  // 最近滚轮事件时间戳
    static Form hkForm;            // 热键窗引用: heal 线程经它 Invoke 回消息循环线程重装钩子

    static IntPtr VolumeHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // ⚠️ LowLevelHooksTimeout 本机=1ms: 回调内禁止任何 I/O/枚举/日志, 否则超时被静默拔钩
        Interlocked.Increment(ref volCalls); // 纯内存计数, 1ms 内必完成 — 诊断: 回调是否被调用
        try
        {
            if (nCode != HC_ACTION) return CallNextHookEx(volHook, nCode, wParam, lParam);
            MSLLHOOKSTRUCT ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
            if (volEnabled != 1) return CallNextHookEx(volHook, nCode, wParam, lParam);
            if ((int)wParam == WM_MBUTTONDOWN && IsPointOnTaskbar(ms.pt))
            {
                KeyEvent(VK_VOLUME_MUTE, 0, 0);
                KeyEvent(VK_VOLUME_MUTE, 0, KEYEVENTF_KEYUP);
                Interlocked.Increment(ref volTriggers);
                return (IntPtr)1; // 拦截, 不让任务栏处理中键
            }
            if ((int)wParam == WM_MOUSEWHEEL)
            {
                // 探针: 记录滚轮事件坐标与判定(纯内存, 允许在 1ms 回调内)
                volLastWheelPtX = ms.pt.x; volLastWheelPtY = ms.pt.y;
                volLastWheelTicks = Environment.TickCount;
                if (IsPointOnTaskbar(ms.pt)) volLastWheelHit = 1;
                else volLastWheelHit = 0;
            }
            if ((int)wParam == WM_MOUSEWHEEL && IsPointOnTaskbar(ms.pt))
            {
                int delta = (short)((ms.mouseData >> 16) & 0xFFFF); // 高16位=滚轮刻度(正=上)
                if (delta != 0)
                {
                    bool up = delta > 0;
                    if (volReverse == 1) up = !up;
                    int clicks = volStep / 2;
                    if (clicks < 1) clicks = 1;
                    for (int i = 0; i < clicks; i++)
                    {
                        KeyEvent(up ? VK_VOLUME_UP : VK_VOLUME_DOWN, 0, 0);
                        KeyEvent(up ? VK_VOLUME_UP : VK_VOLUME_DOWN, 0, KEYEVENTF_KEYUP);
                    }
                    Interlocked.Increment(ref volTriggers);
                    return (IntPtr)1; // 拦截: 阻止任务栏默认滚动行为
                }
            }
        }
        catch { } // 静默: 回调里绝对不能抛/写日志
        return CallNextHookEx(volHook, nCode, wParam, lParam);
    }

    static bool IsPointOnTaskbar(POINT pt)
    {
        // 注意: 本函数在低级钩子回调中被高频调用, 必须轻量(回调超时会静默拔钩!)
        // 窗口列表是原子数组快照(刷新线程整体替换), 回调里只做 GetWindowRect 判位
        IntPtr[] wnds = taskbarWnds;
        for (int i = 0; i < wnds.Length; i++)
        {
            IntPtr h = wnds[i];
            if (h == IntPtr.Zero) continue;
            RECT r;
            if (GetWindowRect(h, out r) && pt.x >= r.Left && pt.x <= r.Right && pt.y >= r.Top && pt.y <= r.Bottom) return true;
        }
        return false;
    }

    // 任务栏窗口列表维护: 只在这里枚举(安装/定时线程调用, 不在钩子回调里!)
    static void RefreshTaskbarWindows()
    {
        List<IntPtr> fresh = new List<IntPtr>();
        IntPtr main = FindWindowW("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) fresh.Add(main);
        try
        {
            EnumWindows(delegate(IntPtr wh, IntPtr lp)
            {
                StringBuilder sb = new StringBuilder(64);
                if (GetClassNameW(wh, sb, 64) > 0 && sb.ToString() == "Shell_SecondaryTrayWnd") fresh.Add(wh);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        taskbarWnds = fresh.ToArray(); // 原子替换快照
    }

    // 任务栏窗口列表定期刷新线程: 排除任务栏重启/分辨率变化导致句柄失效 (WM_DISPLAYCHANGE 之外的兜底)
    static void TaskbarRefreshLoop()
    {
        while (true)
        {
            Thread.Sleep(5000);
            try { RefreshTaskbarWindows(); } catch { }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    const int WM_NCLBUTTONDOWN = 0xA1;

    // ---- 剪贴板历史 (Ctrl+Win+V, 常驻) ----
    // 后台 STA 线程轮询剪贴板文本 → 去重入历史(最新在前, 上限 50);
    // 热键窗口接收 WM_HOTKEY 弹历史列表, 双击/回车粘贴(设剪贴板+激活原窗口+模拟 Ctrl+V)
    const int MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    const int WM_HOTKEY = 0x0312;
    const int HOTKEY_ID = 0x5712; // 'W'+'V' 记号
    static int clipMax = 50;    // 设置页可配 (剪贴板最大条数)
    static int clipEnabled = 1; // 设置页可配: 0 = 暂停剪贴板监听
    static readonly List<string> clipHist = new List<string>();
    static readonly object clipLock = new object();
    static string lastClipText = "";
    static string clipHotkeyName = "";   // 实际注册成功的组合(候选自动降级)
    static Form clipHistWin;             // 当前打开的剪贴板历史窗(单例: 再按热键=关闭, 不叠窗)
    static readonly string ClipStorePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clipboard-history.json");

    // ---- 剪贴板历史持久化: 每行一条 JSON 字符串(完整转义), UTF-8 ----
    static string ClipStoreEscape(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
    static string ClipStoreUnescape(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                if (n == 'r') sb.Append('\r');
                else if (n == 'n') sb.Append('\n');
                else if (n == '\\') sb.Append('\\');
                else if (n == '"') sb.Append('"');
                else { sb.Append('\\'); sb.Append(n); }
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
    static void SaveClipHistory()
    {
        try
        {
            List<string> snap;
            lock (clipLock) { snap = new List<string>(clipHist); }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < snap.Count; i++)
                sb.Append("\"").Append(ClipStoreEscape(snap[i])).Append("\"\r\n");
            File.WriteAllText(ClipStorePath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) { Log("clip save err: " + ex.Message); }
    }
    static void LoadClipHistory()
    {
        try
        {
            if (!File.Exists(ClipStorePath)) return;
            string[] lines = File.ReadAllLines(ClipStorePath, Encoding.UTF8);
            lock (clipLock)
            {
                clipHist.Clear();
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i].Trim();
                    if (l.Length >= 2 && l[0] == '"' && l[l.Length - 1] == '"')
                    {
                        string v = ClipStoreUnescape(l.Substring(1, l.Length - 2));
                        if (!string.IsNullOrEmpty(v) && !clipHist.Contains(v)) clipHist.Add(v);
                    }
                }
                if (clipHist.Count > clipMax) clipHist.RemoveRange(clipMax, clipHist.Count - clipMax);
            }
            Log("clip history loaded: " + clipHist.Count + " items");
        }
        catch (Exception ex) { Log("clip load err: " + ex.Message); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    static void ClipWatcherLoop()
    {
        while (true)
        {
            try
            {
                if (clipEnabled == 1 && Clipboard.ContainsText())
                {
                    string t = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(t) && t != lastClipText)
                    {
                        lastClipText = t;
                        lock (clipLock)
                        {
                            clipHist.Remove(t);
                            clipHist.Insert(0, t);
                            while (clipHist.Count > clipMax) clipHist.RemoveAt(clipHist.Count - 1);
                        }
                        SaveClipHistory(); // 新记录落盘持久化
                    }
                }
            }
            catch (Exception ex) { Log("clip watcher err: " + ex.Message); }
            Thread.Sleep(400);
        }
    }

    static string ClipDisplay(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 80 ? s.Substring(0, 77) + "..." : s;
    }

    // 弹历史窗口: 在钩子线程调用(同 STA 消息循环), 单击=复制 双击=粘贴 右键=菜单, Enter/Esc 键盘
    // 单例: 已开窗口再按热键 = 关闭(普通人的开关习惯), 绝不叠多个窗口
    static void ShowClipHistory()
    {
        try
        {
            Form existing = clipHistWin;
            if (existing != null && !existing.IsDisposed)
            {
                try { existing.Close(); } catch { }
                clipHistWin = null;
                return;
            }
            List<string> snaps;
            lock (clipLock) { snaps = new List<string>(clipHist); }
            if (snaps.Count == 0) { TrayNotify("剪贴板历史", "还没有记录，复制点东西再按 " + (clipHotkeyName == "" ? "热键" : clipHotkeyName)); return; }
            IntPtr prevFg = GetForegroundWindow();

            Form f = new Form();
            clipHistWin = f; // 登记单例, 防热键连按竞态叠窗
            f.Text = "剪贴板历史";
            f.FormBorderStyle = FormBorderStyle.None;      // 无系统边框 → 没有巨大最小化/最大化/关闭按钮
            f.BackColor = Color.FromArgb(35, 36, 40);
            f.StartPosition = FormStartPosition.CenterScreen;
            f.TopMost = true;
            f.ShowInTaskbar = false;
            f.Size = new Size(600, 430);

            // ---- 自绘标题栏: 小标题 + 小关闭 ×, 按住可拖动窗口 ----
            Panel title = new Panel();
            title.Dock = DockStyle.Top;
            title.Height = 36;
            title.BackColor = Color.FromArgb(22, 23, 26);
            Label tl = new Label();
            tl.Text = "剪贴板历史 (" + snaps.Count + ")   ·   单击复制  双击粘贴  右键菜单  Esc 关闭";
            tl.ForeColor = Color.FromArgb(190, 195, 200);
            tl.Font = new Font("Microsoft YaHei UI", 9f);
            tl.AutoSize = false;
            tl.Dock = DockStyle.Fill;
            tl.TextAlign = ContentAlignment.MiddleLeft;
            tl.Padding = new Padding(12, 0, 0, 0);
            Button bx = new Button();
            bx.Text = "×";
            bx.FlatStyle = FlatStyle.Flat;
            bx.FlatAppearance.BorderSize = 0;
            bx.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 60, 60);
            bx.BackColor = Color.Transparent;
            bx.ForeColor = Color.FromArgb(210, 215, 220);
            bx.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            bx.Size = new Size(34, 36);
            bx.Dock = DockStyle.Right;
            bx.Click += delegate { f.Close(); };
            title.Controls.Add(tl);
            title.Controls.Add(bx);
            // 标题栏拖拽移动窗口 (WM_NCLBUTTONDOWN + HTCAPTION)
            title.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(f.Handle, (uint)WM_NCLBUTTONDOWN, (IntPtr)2, IntPtr.Zero); // 2=HTCAPTION
                }
            };
            tl.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(f.Handle, (uint)WM_NCLBUTTONDOWN, (IntPtr)2, IntPtr.Zero);
                }
            };
            f.Controls.Add(title);

            // ---- 列表: 深色 + 自绘(选中高亮蓝条, 交替行色) ----
            ListBox lb = new ListBox();
            lb.Dock = DockStyle.Fill;
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.ItemHeight = 30;
            lb.BackColor = Color.FromArgb(35, 36, 40);
            lb.ForeColor = Color.FromArgb(225, 228, 232);
            lb.BorderStyle = BorderStyle.None;
            lb.Font = new Font("Microsoft YaHei UI", 9.5f);
            lb.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                Color bg = sel ? Color.FromArgb(45, 100, 175) : (e.Index % 2 == 0 ? Color.FromArgb(35, 36, 40) : Color.FromArgb(31, 32, 36));
                using (SolidBrush b2 = new SolidBrush(bg)) e.Graphics.FillRectangle(b2, e.Bounds);
                string txt = lb.Items[e.Index].ToString();
                Color numColor = sel ? Color.FromArgb(190, 215, 255) : Color.FromArgb(110, 120, 135);
                Color txtColor = sel ? Color.White : Color.FromArgb(225, 228, 232);
                Rectangle nb = e.Bounds; nb.Offset(10, 0); nb.Width = 44;
                TextRenderer.DrawText(e.Graphics, txt.Length > 3 ? txt.Substring(0, 4) : txt, lb.Font, nb, numColor, TextFormatFlags.VerticalCenter);
                Rectangle tb = e.Bounds; tb.Offset(58, 0); tb.Width -= 66;
                TextRenderer.DrawText(e.Graphics, txt.Length > 4 ? txt.Substring(4) : "", lb.Font, tb, txtColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            for (int i = 0; i < snaps.Count; i++) lb.Items.Add("[" + i + "] " + ClipDisplay(snaps[i]));
            lb.SelectedIndex = 0;
            f.Controls.Add(lb);
            lb.BringToFront();

            // ---- 底部状态条 ----
            Panel status = new Panel();
            status.Dock = DockStyle.Bottom;
            status.Height = 28;
            status.BackColor = Color.FromArgb(22, 23, 26);
            Label sl = new Label();
            sl.Text = "热键 " + (clipHotkeyName == "" ? "?" : clipHotkeyName) + "   ·   单击=复制到剪贴板   双击=粘贴   右键=删除/清空";
            sl.ForeColor = Color.FromArgb(130, 138, 148);
            sl.Font = new Font("Microsoft YaHei UI", 8.5f);
            sl.Dock = DockStyle.Fill;
            sl.TextAlign = ContentAlignment.MiddleLeft;
            sl.Padding = new Padding(12, 0, 0, 0);
            status.Controls.Add(sl);
            f.Controls.Add(status);

            // ---- 交互 ----
            Action copyAction = delegate
            {
                int idx = lb.SelectedIndex;
                string pick = (idx >= 0 && idx < snaps.Count) ? snaps[idx] : null;
                if (pick == null) return;
                try { Clipboard.SetText(pick); }
                catch (Exception ex) { Log("clip set err: " + ex.Message); }
                sl.Text = "✓ 已复制第 " + idx + " 条 (" + ClipDisplay(pick).Length + " 字)，去目标窗口按 Ctrl+V";
            };
            Action pasteAction = delegate
            {
                int idx = lb.SelectedIndex;
                string pick = (idx >= 0 && idx < snaps.Count) ? snaps[idx] : null;
                if (pick == null) return;
                try { Clipboard.SetText(pick); } catch (Exception ex) { Log("clip set err: " + ex.Message); }
                f.Close();
                try
                {
                    if (prevFg != IntPtr.Zero) SetForegroundWindow(prevFg);
                    System.Threading.Thread.Sleep(120);
                    PressCombo("ctrl+v");
                }
                catch (Exception ex) { Log("paste err: " + ex.Message); }
            };
            lb.Click += delegate { copyAction(); };                       // 单击 = 复制
            lb.DoubleClick += delegate { pasteAction(); };                // 双击 = 复制+粘贴
            lb.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyData == Keys.Enter) pasteAction();               // Enter = 粘贴
                else if (e.KeyData == Keys.Escape) f.Close();             // Esc = 关闭
            };
            f.KeyPreview = true;
            f.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyData == Keys.Escape) f.Close(); };

            // 右键菜单: 复制 / 粘贴 / 删除此项 / 清空全部
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = Color.FromArgb(40, 41, 46);
            menu.ForeColor = Color.FromArgb(225, 228, 232);
            menu.Items.Add("复制此项", null, delegate { copyAction(); });
            menu.Items.Add("粘贴此项", null, delegate { pasteAction(); });
            menu.Items.Add("删除此项", null, delegate
            {
                int idx = lb.SelectedIndex;
                if (idx >= 0 && idx < snaps.Count)
                {
                    lock (clipLock) clipHist.Remove(snaps[idx]);
                    lb.Items.RemoveAt(idx);
                    SaveClipHistory(); // 删除后落盘
                    if (lb.Items.Count > 0) lb.SelectedIndex = Math.Min(idx, lb.Items.Count - 1);
                    else f.Close();
                }
            });
            menu.Items.Add("清空全部", null, delegate
            {
                lock (clipLock) { clipHist.Clear(); lastClipText = ""; }
                SaveClipHistory(); // 清空后落盘
                f.Close();
            });
            lb.ContextMenuStrip = menu;
            lb.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    int idx = lb.IndexFromPoint(e.Location);
                    if (idx >= 0 && idx < lb.Items.Count) lb.SelectedIndex = idx;
                }
            };

            // 窗口关闭时清单例引用(下次热键可重新打开)
            f.FormClosed += delegate { if (clipHistWin == f) clipHistWin = null; };

            f.ShowDialog();
        }
        catch (Exception ex) { Log("clip hist err: " + ex.Message); }
    }

    static void TrayNotify(string title, string msg)
    {
        try { if (TrayIcon != null) TrayIcon.ShowBalloonTip(2500, title, msg, ToolTipIcon.Info); } catch { }
    }

    // 热键接收窗口: 注册 Ctrl+Win+V, 收到 WM_HOTKEY 弹历史; 收到 WM_DISPLAYCHANGE 立即刷新任务栏(分辨率切换)
    class HotkeyForm : Form
    {
        public HotkeyForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Size = new Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-100, -100);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID)
            {
                ShowClipHistory();
                return;
            }
            if (m.Msg == WM_HOTKEY && (int)m.WParam == SHOT_HOTKEY_ID)
            {
                ShowCaptureOverlay();
                return;
            }
            if (m.Msg == WM_HOTKEY && (int)m.WParam == FULLSHOT_HOTKEY_ID)
            {
                ThreadPool.QueueUserWorkItem(delegate { DoFullScreenShot(); });
                return;
            }
            if (m.Msg == WM_HOTKEY && (int)m.WParam == PIN_HOTKEY_ID)
            {
                DoPinFromClipboard();
                return;
            }
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                // 分辨率/显示器变更: 任务栏窗口句柄与矩形都会变, 立即重建列表, 否则滚轮/中键失效
                try { RefreshTaskbarWindows(); Log("display change, taskbar refreshed: " + taskbarWnds.Length + " wnds"); }
                catch { }
            }
            base.WndProc(ref m);
        }
    }
    const int WM_DISPLAYCHANGE = 0x007E;

    static Control hookSync; // 钩子线程的同步控件: 自愈重装经它 Invoke 回钩子线程 (回调必须在安装线程的消息循环里被调用)

    // 鼠标钩子独立线程入口 (拖卡根治): WH_MOUSE_LL 回调在安装线程同步执行 —
    // 若与遮罩 UI 同线程, 拖框时每帧重绘会阻塞回调 → 鼠标指针/事件全部排队 → 拖动巨卡 (实测复现)。
    // 专用线程 + 自己的消息循环: UI 再忙也不影响鼠标。
    static void InstallMouseHook()
    {
        try
        {
            hookSync = new Control();
            hookSync.CreateControl(); // 消息循环所在线程的句柄, 供 Invoke
            volHookProc = new LowLevelMouseProc(VolumeHookProc);
            volHook = SetWindowsHookEx(WH_MOUSE_LL, volHookProc, GetModuleHandle(null), 0);
            RefreshTaskbarWindows();
            Log("mouse hook installed on dedicated thread: handle=" + volHook + " taskbarWnds=" + taskbarWnds.Length);
            // 任务栏窗口列表兜底刷新线程(5s): WM_DISPLAYCHANGE 之外的双保险
            Thread rfr = new Thread(new ThreadStart(TaskbarRefreshLoop));
            rfr.IsBackground = true;
            rfr.Start();
            // 钩子自愈: 30s 心跳, 经 hookSync Invoke 回本线程安全重装
            // ⚠️ 绝不能在无消息循环的线程重装钩子 (回调会永远不被调用, 实测踩坑)
            Thread heal = new Thread(new ThreadStart(VolumeHookHealLoop));
            heal.IsBackground = true;
            heal.Start();
            Log("volume hook heal loop started");
            Application.Run(); // 钩子线程消息循环: 驱动钩子回调 + hookSync.Invoke
        }
        catch (Exception ex) { Log("mouse hook install err: " + ex.Message); }
    }

    // 钩子自愈: 低级钩子可被系统静默拔除(回调超时/系统压力) — 定期经钩子线程重装 + 刷新任务栏窗口快照
    static void VolumeHookHealLoop()
    {
        while (true)
        {
            Thread.Sleep(30000);
            try
            {
                // 任务栏窗口句柄/矩形定期刷新(分辨率变更/explorer 重启自适应)
                RefreshTaskbarWindows();
                // 重装钩子必须回到钩子线程执行 — 通过钩子线程同步控件 Invoke
                Control hs = hookSync;
                if (hs != null && hs.IsHandleCreated)
                {
                    hs.Invoke(new MethodInvoker(delegate
                    {
                        try
                        {
                            if (volHook != IntPtr.Zero) UnhookWindowsHookEx(volHook);
                            volHookProc = new LowLevelMouseProc(VolumeHookProc);
                            volHook = SetWindowsHookEx(WH_MOUSE_LL, volHookProc, GetModuleHandle(null), 0);
                            Log("volume hook healed: handle=" + volHook + " taskbarWnds=" + taskbarWnds.Length);
                        }
                        catch (Exception ex) { Log("volume hook heal err: " + ex.Message); }
                    }));
                }
            }
            catch { } // 钩子线程可能未就绪/已退出, 静默等下一轮
        }
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

            bool needUserSession = path.StartsWith("/mouse") || path.StartsWith("/keyboard") || path == "/shot" || path.StartsWith("/app") || path == "/open-repo" || path.StartsWith("/record") || path.StartsWith("/ui") || path.StartsWith("/win");
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
                           ",\"shots\":" + ShotCount + ",\"uptimeSec\":" + (int)(DateTime.Now - StartTime).TotalSeconds +
                           ",\"version\":\"" + APP_VERSION + "\",\"build\":\"" + BuildStamp() + "\"}";
                }
                else if (path == "/taskbar-volume")
                {
                    // 任务栏滚轮调音量: GET 查状态; ?enabled=0|1 开关; ?step=N 步进; ?reverse=1 反向; ?refresh=1 强制刷新任务栏列表
                    if (q.ContainsKey("refresh") && q["refresh"] == "1") RefreshTaskbarWindows();
                    if (q.ContainsKey("enabled")) { int v; if (int.TryParse(q["enabled"], out v)) volEnabled = v == 1 ? 1 : 0; }
                    if (q.ContainsKey("reverse")) { int v; if (int.TryParse(q["reverse"], out v)) volReverse = v == 1 ? 1 : 0; }
                    if (q.ContainsKey("step")) { int v; if (int.TryParse(q["step"], out v) && v >= 1 && v <= 20) volStep = v; }
                    // 任务栏矩形诊断: 输出每个窗口的 rect, 验证判定坐标系
                    StringBuilder dr = new StringBuilder();
                    IntPtr[] wnds = taskbarWnds;
                    for (int i = 0; i < wnds.Length; i++)
                    {
                        RECT r;
                        if (GetWindowRect(wnds[i], out r))
                            dr.Append("[").Append(i).Append("]").Append(r.Left).Append(",").Append(r.Top).Append(",").Append(r.Right).Append(",").Append(r.Bottom).Append(" ");
                    }
                    body = "{\"ok\":true,\"enabled\":" + volEnabled + ",\"reverse\":" + volReverse + ",\"step\":" + volStep +
                           ",\"triggers\":" + Interlocked.Read(ref volTriggers) + ",\"calls\":" + Interlocked.Read(ref volCalls) + ",\"taskbarWnds\":" + taskbarWnds.Length + ",\"rects\":\"" + dr.ToString() + "\",\"wheel\":{\"pt\":" + Interlocked.Read(ref volLastWheelPtX) + "," + Interlocked.Read(ref volLastWheelPtY) + ",\"hit\":" + Interlocked.Read(ref volLastWheelHit) + ",\"tick\":" + Interlocked.Read(ref volLastWheelTicks) + "},\"hook\":\"" + volHook + "\"}";
                }
                else if (path == "/clipboard/history")
                {
                    // 剪贴板历史: GET 返回最近 N 条(默认全部, ?limit=N 截断)。给 AI 查询/复用粘贴内容
                    int lim = 0;
                    if (q.ContainsKey("limit")) int.TryParse(q["limit"], out lim);
                    List<string> snaps;
                    lock (clipLock) { snaps = new List<string>(clipHist); }
                    if (lim > 0 && lim < snaps.Count) snaps = snaps.GetRange(0, lim);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\"ok\":true,\"count\":").Append(snaps.Count).Append(",\"items\":[");
                    for (int i = 0; i < snaps.Count; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.Append("{\"index\":").Append(i).Append(",\"text\":\"").Append(JsonEscape(snaps[i])).Append("\"}");
                    }
                    sb.Append("]}");
                    body = sb.ToString();
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
                    Thread t = new Thread(() => DoUpdateSilent(true));
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
                // ---- T2 自动化扩展: 窗口管理 / 鼠标扩展 / 键按住 / 剪贴板 / UIA (实现在 shot-automation.cs) ----
                else if (path.StartsWith("/win/"))
                {
                    string verb = path.Substring(5);
                    if (verb == "wait")
                    {
                        int tmo = 10000;
                        if (q.ContainsKey("timeout")) { int v; if (int.TryParse(q["timeout"], out v) && v > 0) tmo = v; }
                        body = WinWait(q.ContainsKey("title") ? q["title"] : "", tmo);
                    }
                    else if (verb == "list")
                    {
                        int lp = 0; TryInt(q, "pid", out lp);
                        body = WinListByPid(lp);
                    }
                    else
                    {
                        IntPtr wh = IntPtr.Zero;
                        if (q.ContainsKey("title")) wh = FindWindowByTitle(q["title"]);
                        else if (q.ContainsKey("hwnd")) { try { wh = new IntPtr(long.Parse(q["hwnd"])); } catch { } }
                        if (wh == IntPtr.Zero) { code = 404; body = "{\"ok\":false,\"error\":\"window not found\"}"; }
                        else if (verb == "activate") body = WinActivate(wh);
                        else if (verb == "max") body = WinShow(wh, SW_MAXIMIZE, "maximized");
                        else if (verb == "min") body = WinShow(wh, SW_MINIMIZE, "minimized");
                        else if (verb == "restore") body = WinShow(wh, SW_RESTORE, "restored");
                        else if (verb == "close") body = WinClose(wh);
                        else if (verb == "move")
                        {
                            int mx, my, mw, mh;
                            if (TryInt(q, "x", out mx) && TryInt(q, "y", out my) && TryInt(q, "w", out mw) && TryInt(q, "h", out mh))
                                body = WinMove(wh, mx, my, mw, mh);
                            else { code = 400; body = "{\"ok\":false,\"error\":\"need x,y,w,h\"}"; }
                        }
                        else { code = 404; body = "{\"ok\":false,\"error\":\"unknown verb\"}"; }
                    }
                    Log("[win] " + target);
                }
                else if (path == "/mouse/down" || path == "/mouse/up")
                {
                    string btn = q.ContainsKey("button") ? q["button"].ToLowerInvariant() : "left";
                    body = MouseDownUp(btn, path == "/mouse/down");
                    Log("[ctrl] mouse " + (path == "/mouse/down" ? "down " : "up ") + btn);
                }
                else if (path == "/mouse/drag")
                {
                    int x1, y1, x2, y2, ms;
                    if (TryInt(q, "x1", out x1) && TryInt(q, "y1", out y1) && TryInt(q, "x2", out x2) && TryInt(q, "y2", out y2))
                    {
                        if (!TryInt(q, "ms", out ms)) ms = 300;
                        body = MouseDrag(x1, y1, x2, y2, ms);
                        Log("[ctrl] drag " + x1 + "," + y1 + " -> " + x2 + "," + y2);
                    }
                    else { code = 400; body = "{\"ok\":false,\"error\":\"need x1,y1,x2,y2\"}"; }
                }
                else if (path == "/mouse/pos") { body = MousePos(); }
                else if (path == "/keyboard/hold")
                {
                    if (!q.ContainsKey("keys")) { code = 400; body = "{\"ok\":false,\"error\":\"need keys\"}"; }
                    else
                    {
                        int ms; if (!TryInt(q, "ms", out ms)) ms = 300;
                        body = KeyHold(q["keys"], ms);
                        Log("[ctrl] hold " + q["keys"] + " " + ms + "ms");
                    }
                }
                else if (path == "/clipboard/set")
                {
                    if (!q.ContainsKey("text")) { code = 400; body = "{\"ok\":false,\"error\":\"need text\"}"; }
                    else { body = ClipboardSetText(q["text"]); Log("[ctrl] clipboard set " + q["text"].Length + " chars"); }
                }
                else if (path == "/ui/tree") { body = UiTree(q); Log("[ui] tree " + target); }
                else if (path == "/ui/click") { body = UiClick(q); Log("[ui] click " + target); }
                else if (path == "/ui/set") { body = UiSet(q); Log("[ui] set " + target); }
                else if (path == "/ui/read") { body = UiRead(q); Log("[ui] read " + target); }
                else if (path == "/ui/readall") { body = UiReadAll(q); Log("[ui] readall " + target); }
                else if (path == "/record/start")
                {
                    int rx = 0, ry = 0, rw = 0, rh = 0, rf = 10;
                    TryInt(q, "x", out rx); TryInt(q, "y", out ry); TryInt(q, "w", out rw); TryInt(q, "h", out rh); TryInt(q, "fps", out rf);
                    body = RecordStart(rx, ry, rw, rh, rf); // RecordStart 内部: w/h<=0 用全屏, fps 越界用默认
                    Log("[rec] start " + target);
                }
                else if (path == "/record/stop") { body = RecordStop(); Log("[rec] stop"); }
                else if (path == "/record/status") { body = RecordStatus(); }
                else if (path == "/app/runas")
                {
                    if (!q.ContainsKey("path")) { code = 400; body = "{\"ok\":false,\"error\":\"need path (UAC 由用户确认)\"}"; }
                    else { body = AppRunAs(q["path"], q.ContainsKey("args") ? q["args"] : ""); Log("[runas] " + q["path"]); }
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

    // 启动时从 shot-service.json 加载运行时配置 (设置页写入; 热键类重启生效, 音量/条数此处为初值)
    static void LoadRuntimeSettings()
    {
        try
        {
            string dir = Cfg("capture.dir", "");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                dir = Environment.ExpandEnvironmentVariables(dir);
                if (dir.Length > 2) ShotDir = dir;
            }
            int cm; if (int.TryParse(Cfg("clipboard.max", "50"), out cm) && cm >= 5 && cm <= 500) clipMax = cm;
            int ce; if (int.TryParse(Cfg("clipboard.enabled", "1"), out ce)) clipEnabled = ce == 1 ? 1 : 0;
            int ve; if (int.TryParse(Cfg("volume.enabled", "1"), out ve)) volEnabled = ve == 1 ? 1 : 0;
            int vs; if (int.TryParse(Cfg("volume.step", "2"), out vs) && vs >= 1 && vs <= 20) volStep = vs;
            int vr; if (int.TryParse(Cfg("volume.reverse", "0"), out vr)) volReverse = vr == 1 ? 1 : 0;
            Log("runtime settings: dir=" + ShotDir + " clipMax=" + clipMax + " clipEnabled=" + clipEnabled +
                " vol[en=" + volEnabled + " step=" + volStep + " rev=" + volReverse + "]");
        }
        catch (Exception ex) { Log("load runtime settings err: " + ex.Message); }
    }

    // 解析热键串 "Ctrl+Alt+S" -> {mods, vk}; 非法返回 null
    static uint[] HotkeyParse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        try
        {
            uint mods = 0; ushort vk = 0;
            string[] parts = spec.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string k = parts[i].Trim();
                if (k.Length == 0) return null;
                string lk = k.ToLowerInvariant();
                if (lk == "ctrl" || lk == "control") mods |= MOD_CONTROL;
                else if (lk == "shift") mods |= MOD_SHIFT;
                else if (lk == "alt") mods |= MOD_ALT;
                else if (lk == "win") mods |= MOD_WIN;
                else
                {
                    if (i != parts.Length - 1) return null;
                    vk = KeyToVk(lk);
                    if (vk == 0) return null;
                }
            }
            if (vk == 0) return null;
            return new uint[] { mods, vk };
        }
        catch { return null; }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // 构建指纹: exe 文件的修改时间+大小 = 用户编译时刻。跑的是不是刚编的, 一眼可验 (堵"改了没变化"坑)
        string build = BuildStamp();
        bool allowTray = true, watchMode = false;
        foreach (string a in args)
        {
            string x = (a ?? "").ToLowerInvariant();
            if (x == "-notray") allowTray = false;
            else if (x == "-watch") watchMode = true;
        }

        // 单实例互斥: 已有实例则自动顶替(结束旧进程后接管) — 堵死"忘了 Stop-Process, 新 exe 静默退出,
        // 用户跑的还是旧代码"这个反复踩坑的部署漏洞 (2026-09-05)
        bool createdNew;
        try
        {
            instanceMutex = new Mutex(true, MUTEX_NAME, out createdNew);
            if (!createdNew)
            {
                Log("another instance running, auto take-over: killing old process (build of old=unknown)");
                try
                {
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    foreach (Process p in Process.GetProcessesByName("shot-service"))
                    {
                        if (p.Id == Process.GetCurrentProcess().Id) continue;
                        try { p.Kill(); Log("take-over: killed old pid=" + p.Id); } catch (Exception ex2) { Log("take-over: kill pid=" + p.Id + " err: " + ex2.Message); }
                    }
                }
                catch (Exception ex2) { Log("take-over enum err: " + ex2.Message); }
                bool took = false;
                for (int i = 0; i < 50 && !took; i++) // 最多等 5s 让旧进程退出/互斥释放
                {
                    Thread.Sleep(100);
                    try
                    {
                        if (instanceMutex != null) { try { instanceMutex.Dispose(); } catch { } }
                        instanceMutex = new Mutex(true, MUTEX_NAME, out createdNew);
                        took = createdNew;
                    }
                    catch { }
                }
                if (!took)
                {
                    Log("take-over FAILED: old instance still holds mutex, exiting");
                    if (allowTray && !watchMode)
                        MessageBox.Show("检测到旧实例仍在运行且无法自动结束。\n请手动结束 shot-service.exe 后再启动。", "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Log("take-over OK: new build " + build + " is now the running instance");
            }
        }
        catch (Exception ex) { Log("mutex err: " + ex.Message); }

        // 全局异常兜底: 托盘常驻程序任何 UI 异常只记日志+气泡, 绝不弹 .NET 崩溃框 (双击复制时序曾触发 ObjectDisposedException)
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Log("ui exception (caught): " + e.Exception.GetType().Name + ": " + e.Exception.Message + "\r\n" + e.Exception.StackTrace);
                try { TrayNotify("出错了(已拦截)", e.Exception.Message); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Exception ex = e.ExceptionObject as Exception;
                Log("fatal exception: " + (ex != null ? ex.GetType().Name + ": " + ex.Message : e.ExceptionObject));
            };
        }
        catch { }

        try { SetProcessDPIAware(); } catch { }
        // .NET 4.8 默认 TLS1.0, GitHub API 需 TLS1.2 (否则更新检测失败)
        try { System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12; } catch { }
        Log("shot-service v" + APP_VERSION + " build=" + build + " session=" + MySession + " pid=" + Process.GetCurrentProcess().Id +
            " mode=" + (watchMode ? "watch" : "service") + (allowTray ? " tray=on" : " tray=off"));
        LoadRuntimeSettings();
        try { if (!Directory.Exists(ShotDir)) Directory.CreateDirectory(ShotDir); } catch { }

        if (watchMode) { RunWatch(); return; }    // 自愈守护 (替代 shot-watcher.exe)

        Thread srv = new Thread(new ThreadStart(ServerLoop));
        srv.IsBackground = true;
        srv.Start();

        // 鼠标钩子独立线程 (拖卡根治): 回调在专用线程消息循环执行, 与截图遮罩 UI 线程彻底解耦
        try
        {
            Thread hookT = new Thread(new ThreadStart(InstallMouseHook));
            hookT.IsBackground = true;
            hookT.Start();
            Log("mouse hook thread started");
        }
        catch (Exception ex) { Log("mouse hook thread err: " + ex.Message); }

        // 热键窗 (UI 线程): 剪贴板历史热键 + 区域/全屏截图热键
        try
        {
            Thread volT = new Thread(new ThreadStart(delegate
            {
                try
                {
                    HotkeyForm hk = new HotkeyForm();
                    hkForm = hk;
                    hk.CreateControl();
                    // 候选组合自动降级: Ctrl+Win+V 是 Win11 系统"切换声音输出"自带键, 故 Ctrl+Alt+V 优先; 记录实际生效键
                    uint[][] cands = new uint[][]
                    {
                        new uint[] { MOD_CONTROL | MOD_ALT, 0x56 },   // Ctrl+Alt+V (优先, 用户确认空闲)
                        new uint[] { MOD_CONTROL | MOD_WIN, 0x56 },   // Ctrl+Win+V (Win11 系统占用中, 备选)
                        new uint[] { MOD_CONTROL | MOD_SHIFT, 0x56 }, // Ctrl+Shift+V
                        new uint[] { MOD_WIN, 0x56 },                 // Win+V (系统剪贴板历史)
                        new uint[] { MOD_CONTROL | MOD_WIN | MOD_ALT, 0x51 }, // Ctrl+Win+Alt+Q
                    };
                    string[] candNames = new string[] { "Ctrl+Alt+V", "Ctrl+Win+V", "Ctrl+Shift+V", "Win+V", "Ctrl+Win+Alt+Q" };
                    bool hkOk = false;
                    for (int ci = 0; ci < cands.Length; ci++)
                    {
                        if (RegisterHotKey(hk.Handle, HOTKEY_ID, cands[ci][0], cands[ci][1]))
                        {
                            clipHotkeyName = candNames[ci];
                            hkOk = true;
                            Log("clip hotkey registered: " + candNames[ci]);
                            break;
                        }
                    }
                    if (!hkOk) Log("clip hotkey register FAILED (all candidates busy)");
                    // 区域截图热键: 候选组合自动降级 (Win+Shift+S 被 Win11 系统截图占用, 故 Win+Shift+A 优先)
                    uint[][] scands;
                    string[] scanmes;
                    uint[] cfgShot = HotkeyParse(Cfg("capture.hotkeyRegion", ""));
                    if (cfgShot != null) { scands = new uint[][] { cfgShot }; scanmes = new string[] { Cfg("capture.hotkeyRegion", "") }; }
                    else
                    {
                        scands = new uint[][]
                        {
                            new uint[] { MOD_WIN | MOD_SHIFT, 0x41 },
                            new uint[] { MOD_CONTROL | MOD_SHIFT, 0x53 },
                            new uint[] { MOD_WIN | MOD_SHIFT, 0x53 },
                        };
                        scanmes = new string[] { "Win+Shift+A", "Ctrl+Shift+S", "Win+Shift+S" };
                    }
                    bool shOk = false;
                    for (int ci = 0; ci < scands.Length; ci++)
                    {
                        if (RegisterHotKey(hk.Handle, SHOT_HOTKEY_ID, scands[ci][0], scands[ci][1]))
                        {
                            shotHotkeyName = scanmes[ci];
                            shOk = true;
                            Log("shot hotkey registered: " + scanmes[ci]);
                            break;
                        }
                    }
                    if (!shOk) Log("shot hotkey register FAILED (all candidates busy)");
                    // 全屏截图热键 (Ctrl+Alt+A, 候选降级)
                    uint[][] fcands;
                    string[] fnames;
                    uint[] cfgFull = HotkeyParse(Cfg("capture.hotkeyFull", ""));
                    if (cfgFull != null) { fcands = new uint[][] { cfgFull }; fnames = new string[] { Cfg("capture.hotkeyFull", "") }; }
                    else
                    {
                        fcands = new uint[][]
                        {
                            new uint[] { MOD_CONTROL | MOD_ALT, 0x41 },
                            new uint[] { MOD_CONTROL | MOD_SHIFT, 0x41 },
                            new uint[] { MOD_CONTROL | MOD_ALT, 0x46 },
                        };
                        fnames = new string[] { "Ctrl+Alt+A", "Ctrl+Shift+A", "Ctrl+Alt+F" };
                    }
                    bool fOk = false;
                    for (int ci = 0; ci < fcands.Length; ci++)
                    {
                        if (RegisterHotKey(hk.Handle, FULLSHOT_HOTKEY_ID, fcands[ci][0], fcands[ci][1]))
                        {
                            fullShotHotkeyName = fnames[ci];
                            fOk = true;
                            Log("fullscreen shot hotkey registered: " + fnames[ci]);
                            break;
                        }
                    }
                    if (!fOk) Log("fullscreen shot hotkey register FAILED (all candidates busy)");
                    // 贴图热键 (PixPin F3 同款): 剪贴板图钉到桌面
                    uint[][] pcands;
                    string[] pnames;
                    uint[] cfgPin = HotkeyParse(Cfg("capture.hotkeyPin", ""));
                    if (cfgPin != null) { pcands = new uint[][] { cfgPin }; pnames = new string[] { Cfg("capture.hotkeyPin", "") }; }
                    else
                    {
                        pcands = new uint[][]
                        {
                            new uint[] { 0, 0x72 },
                            new uint[] { MOD_CONTROL | MOD_ALT, 0x50 },
                        };
                        pnames = new string[] { "F3", "Ctrl+Alt+P" };
                    }
                    bool pOk = false;
                    for (int ci = 0; ci < pcands.Length; ci++)
                    {
                        if (RegisterHotKey(hk.Handle, PIN_HOTKEY_ID, pcands[ci][0], pcands[ci][1]))
                        {
                            pinHotkeyName = pnames[ci];
                            pOk = true;
                            Log("pin hotkey registered: " + pnames[ci]);
                            break;
                        }
                    }
                    if (!pOk) Log("pin hotkey register FAILED (all candidates busy)");
                    Application.Run(hk);
                }
                catch (Exception ex) { Log("hotkey form err: " + ex.Message); Application.Run(); }
            }));
            volT.SetApartmentState(ApartmentState.STA);
            volT.IsBackground = true;
            volT.Start();
            Log("hotkey thread started");
        }
        catch (Exception ex) { Log("hotkey thread err: " + ex.Message); }

        // 剪贴板历史监听: 独立 STA 线程轮询(Clipboard 需 STA); 先加载持久化历史
        try
        {
            LoadClipHistory();
            Thread clipT = new Thread(new ThreadStart(ClipWatcherLoop));
            clipT.SetApartmentState(ApartmentState.STA);
            clipT.IsBackground = true;
            clipT.Start();
            Log("clipboard watcher started");
        }
        catch (Exception ex) { Log("clip watcher thread err: " + ex.Message); }

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
    // 最新版本: 请求 releases/latest 的 302 重定向, 从 Location 尾部取 tag (vX.Y.Z) — 零 API 调用零限流
    static string LatestVersion()
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(LATEST_URL);
            req.UserAgent = "win-desktop-helper/" + APP_VERSION;
            req.AllowAutoRedirect = false;
            req.Timeout = 30000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                string loc = resp.Headers["Location"];
                if (!string.IsNullOrEmpty(loc))
                {
                    string tag = loc.TrimEnd('/');
                    int i = tag.LastIndexOf('/');
                    if (i >= 0 && i < tag.Length - 1) return tag.Substring(i + 1);
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

    // 自动静默更新: 下载最新 setup, 用独立(detached)安装器替换自身并拉起新版。
    // 历史坑(几十个版本更新抽风根因): 旧实现让安装器 PrepareToInstall 用 taskkill /IM shot-service.exe /F /T 杀自身,
    //   但安装器是 shot-service 的子进程, /T 把整棵树(含安装器自己)一起杀 -> exe 永远替换不完 -> 死循环直到进程丢失。
    //   修复(双层):
    //   1) setup.iss 去掉 /T, 只杀 shot-service.exe 本体, 不再误杀安装器;
    //   2) 本函数: 下载后退出自身释放 exe 文件锁, 经由 cmd start 拉起 detached 安装器(不属于本进程树, 绝不会被误杀),
    //      安装器替换 exe 后由 iss [Run] 拉起新版; 并加 30 分钟失败冷却, 杜绝自动重试风暴。
    static int updatingFlag = 0; // 防重入(启动自动检查与手动触发可能并发)
    static DateTime ReadUpdateGuard()
    {
        try
        {
            string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wdh-update-guard.txt");
            if (File.Exists(f)) { long t; if (long.TryParse(File.ReadAllText(f).Trim(), out t)) return new DateTime(t, DateTimeKind.Utc); }
        }
        catch { }
        return DateTime.MinValue;
    }
    static void WriteUpdateGuard()
    {
        try
        {
            string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wdh-update-guard.txt");
            File.WriteAllText(f, DateTime.UtcNow.Ticks.ToString());
        }
        catch { }
    }
    static void DoUpdateSilent(bool isManual = false)
    {
        if (Interlocked.Exchange(ref updatingFlag, 1) == 1) return;
        try
        {
            string v = LatestVersion();
            if (string.IsNullOrEmpty(v)) { Log("update: latest lookup failed, skip"); return; }
            if (!IsNewerVersion(v)) { Log("update: already latest (" + APP_VERSION + "), skip"); return; } // 已最新不重装
            // 失败冷却: 自动更新近 30 分钟内已尝试过且仍不是新版 -> 不再自动重试, 避免死循环 (手动点击不受限)
            if (!isManual && (DateTime.UtcNow - ReadUpdateGuard()).TotalMinutes < 30)
            {
                Log("update: cooldown active, skip auto retry (use tray menu /update to force)"); return;
            }
            WriteUpdateGuard(); // 记录本次尝试, 即便失败也进入冷却, 防止刷屏式重试
            string ver = v.TrimStart('v', 'V');
            // 直链拼装: 文件名带版本号是发布约定 (setup.iss OutputBaseFilename=win-desktop-helper-setup-X.Y.Z)
            string url = REPO_URL + "/releases/download/" + v + "/win-desktop-helper-setup-" + ver + ".exe";
            string tmp = Path.Combine(Path.GetTempPath(), "wdh-update-setup.exe");
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "win-desktop-helper/" + APP_VERSION);
                wc.DownloadFile(url, tmp); // 直链普通 HTTPS, 跟随 CDN 302, 无认证无 API 限流
            }
            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            // 独立安装器脚本: 等文件锁释放 -> 静默安装(替换 exe) -> 自删。
            // 安装器由 cmd start 拉起, 不属于本进程树, 绝不会被 PrepareToInstall 的 taskkill 误杀; 替换完由 iss [Run] 拉起新版。
            string bat = Path.Combine(Path.GetTempPath(), "wdh-update-" + DateTime.Now.Ticks.ToString("x") + ".bat");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("timeout /t 3 /nobreak >nul 2>&1");
            sb.AppendLine("taskkill /F /IM shot-service.exe >nul 2>&1");
            sb.AppendLine("set \"INS=" + tmp + "\"");
            sb.AppendLine("set \"DIR=" + dir + "\"");
            sb.AppendLine("start \"\" /wait \"%INS%\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"%DIR%\"");
            sb.AppendLine("del /f /q \"%~f0\" >nul 2>&1");
            File.WriteAllText(bat, sb.ToString());
            try { foreach (var p in Process.GetProcessesByName("shot-watcher")) { try { p.Kill(); } catch { } } } catch { }
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"") { CreateNoWindow = true, UseShellExecute = false });
            Log("update: self-updater launched (detached installer -> " + dir + "), exiting self to release file lock");
            Thread.Sleep(500);
            Environment.Exit(0); // 退出自身释放 exe 锁, 安装器才能替换; 新版由 iss [Run] 拉起
        }
        catch (Exception ex) { Log("update err: " + ex.Message); }
        finally { Interlocked.Exchange(ref updatingFlag, 0); }
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
                return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{\"tools\":{\"listChanged\":false}},\"serverInfo\":{\"name\":\"win-desktop-helper\",\"version\":\"" + APP_VERSION + "\"}}";
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
                case "taskbar_volume":
                {
                    // 任务栏滚轮调音量: enabled=0|1(开关) step=音量步进 reverse=1 反向; 不带参会查询状态
                    if (McpParam(a, "enabled") == "0" || McpParam(a, "enabled") == "1") volEnabled = McpParam(a, "enabled") == "1" ? 1 : 0;
                    if (McpParam(a, "step") != "") { int v; if (int.TryParse(McpParam(a, "step"), out v) && v >= 1 && v <= 20) volStep = v; }
                    if (McpParam(a, "reverse") == "1") volReverse = 1;
                    if (McpParam(a, "reverse") == "0") volReverse = 0;
                    return McpText("{\"ok\":true,\"enabled\":" + volEnabled + ",\"reverse\":" + volReverse + ",\"step\":" + volStep + ",\"taskbarWnds\":" + taskbarWnds.Length + "}", false);
                }
                case "clipboard_history":
                {
                    int lim = McpParamInt(a, "limit");
                    List<string> snaps;
                    lock (clipLock) { snaps = new List<string>(clipHist); }
                    if (lim > 0 && lim < snaps.Count) snaps = snaps.GetRange(0, lim);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\"ok\":true,\"count\":").Append(snaps.Count).Append(",\"items\":[");
                    for (int i = 0; i < snaps.Count; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.Append("{\"index\":").Append(i).Append(",\"text\":\"").Append(JsonEscape(snaps[i])).Append("\"}");
                    }
                    sb.Append("]}");
                    return McpText(sb.ToString(), false);
                }
                // ---- T2 automation tools ----
                case "win_manage":
                {
                    string verb = McpParam(a, "verb");
                    if (verb == "wait")
                    {
                        int tmo = McpParamInt(a, "timeout"); if (tmo <= 0) tmo = 10000;
                        return McpText(WinWait(McpParam(a, "title"), tmo), false);
                    }
                    if (verb == "list") return McpText(WinListByPid(McpParamInt(a, "pid")), false);
                    IntPtr wh = IntPtr.Zero;
                    string ttl = McpParam(a, "title");
                    if (ttl != "") wh = FindWindowByTitle(ttl);
                    if (wh == IntPtr.Zero) return McpText("{\"ok\":false,\"error\":\"window not found\"}", true);
                    if (verb == "activate") return McpText(WinActivate(wh), false);
                    if (verb == "max") return McpText(WinShow(wh, SW_MAXIMIZE, "maximized"), false);
                    if (verb == "min") return McpText(WinShow(wh, SW_MINIMIZE, "minimized"), false);
                    if (verb == "restore") return McpText(WinShow(wh, SW_RESTORE, "restored"), false);
                    if (verb == "close") return McpText(WinClose(wh), false);
                    if (verb == "move") return McpText(WinMove(wh, McpParamInt(a, "x"), McpParamInt(a, "y"), McpParamInt(a, "w"), McpParamInt(a, "h")), false);
                    return McpText("unknown verb (activate/max/min/restore/close/move/wait/list)", true);
                }
                case "mouse_down": return McpText(MouseDownUp(McpParam(a, "button") == "" ? "left" : McpParam(a, "button"), true), false);
                case "mouse_up": return McpText(MouseDownUp(McpParam(a, "button") == "" ? "left" : McpParam(a, "button"), false), false);
                case "mouse_drag":
                    return McpText(MouseDrag(McpParamInt(a, "x1"), McpParamInt(a, "y1"), McpParamInt(a, "x2"), McpParamInt(a, "y2"),
                        McpParam(a, "ms") == "" ? 300 : McpParamInt(a, "ms")), false);
                case "mouse_pos": return McpText(MousePos(), false);
                case "keyboard_hold":
                {
                    int ms = McpParam(a, "ms") == "" ? 300 : McpParamInt(a, "ms");
                    return McpText(KeyHold(McpParam(a, "keys"), ms), false);
                }
                case "clipboard_set": return McpText(ClipboardSetText(McpParam(a, "text")), false);
                case "ui_tree":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "max" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiTree(q2), false);
                }
                case "ui_click":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "i" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiClick(q2), false);
                }
                case "ui_set":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "i", "value" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiSet(q2), false);
                }
                case "ui_read":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "i" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiRead(q2), false);
                }
                case "ui_readall":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "max" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiReadAll(q2), false);
                }
                case "record_start":
                {
                    int rx = 0, ry = 0, rw = 0, rh = 0, rf = 10;
                    int.TryParse(McpParam(a, "x"), out rx); int.TryParse(McpParam(a, "y"), out ry);
                    int.TryParse(McpParam(a, "w"), out rw); int.TryParse(McpParam(a, "h"), out rh);
                    int.TryParse(McpParam(a, "fps"), out rf);
                    return McpText(RecordStart(rx, ry, rw, rh, rf), false);
                }
                case "record_stop": return McpText(RecordStop(), false);
                case "record_status": return McpText(RecordStatus(), false);
                case "app_runas": return McpText(AppRunAs(McpParam(a, "path"), McpParam(a, "args")), false);
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
            "{\"name\":\"taskbar_volume\",\"description\":\"任务栏滚轮调音量（常驻功能）。enabled=0/1 开关，step=每次滚轮音量变化百分比(1-20,默认2)，reverse=1 反向(滚轮上=减小)。不带参返回当前状态。\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"enabled\":{\"type\":\"number\"},\"step\":{\"type\":\"number\"},\"reverse\":{\"type\":\"number\"}}}}," +
            "{\"name\":\"clipboard_history\",\"description\":\"读取剪贴板历史（常驻监听，最多50条，最新在前）。limit=返回条数(可选)。给AI复用刚复制的内容。\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"limit\":{\"type\":\"number\"}}}}," +
            "{\"name\":\"win_manage\",\"description\":\"窗口管理。verb=activate|max|min|restore|close|move|wait|list。activate置前台(先解除最小化)；move需x,y,w,h；wait轮询等title窗口出现(timeout毫秒,上限60s)；list按pid列窗口\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"verb\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"},\"pid\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"w\":{\"type\":\"number\"},\"h\":{\"type\":\"number\"},\"timeout\":{\"type\":\"number\"}},\"required\":[\"verb\"]}," +
            "{\"name\":\"mouse_down\",\"description\":\"按住鼠标键不松。button=left(默认)/right/middle。与mouse_up配对可自定义拖拽\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"button\":{\"type\":\"string\"}}}," +
            "{\"name\":\"mouse_up\",\"description\":\"松开鼠标键。button=left(默认)/right/middle\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"button\":{\"type\":\"string\"}}}," +
            "{\"name\":\"mouse_drag\",\"description\":\"左键拖拽一条龙: 从x1,y1按住平滑拖到x2,y2再松开。ms=总时长毫秒(默认300)\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"x1\":{\"type\":\"number\"},\"y1\":{\"type\":\"number\"},\"x2\":{\"type\":\"number\"},\"y2\":{\"type\":\"number\"},\"ms\":{\"type\":\"number\"}},\"required\":[\"x1\",\"y1\",\"x2\",\"y2\"]}," +
            "{\"name\":\"mouse_pos\",\"description\":\"查当前鼠标光标物理像素坐标\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}," +
            "{\"name\":\"keyboard_hold\",\"description\":\"按住组合键ms毫秒再松开(如按住win拖窗口)。keys格式同keyboard_press\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"keys\":{\"type\":\"string\"},\"ms\":{\"type\":\"number\"}},\"required\":[\"keys\"]}," +
            "{\"name\":\"clipboard_set\",\"description\":\"写文本到剪贴板(替代手动复制)\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}," +
            "{\"name\":\"ui_readall\",\"description\":\"批量读整棵元素树的 Name/Value(输入框内容)/类型 — 找输入框里的值/页面文本时用这个, 比 ui_read 逐个快。title=窗口标题, max=上限(默认300)\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"hwnd\":{\"type\":\"number\"},\"max\":{\"type\":\"number\"}}}," +
            "{\"name\":\"record_start\",\"description\":\"开始录屏(抓屏管道喂ffmpeg出MP4 h264)。x,y,w,h=区域(默认全屏), fps=帧率(默认10,上限30)。无音频。用 record_stop 结束\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"w\":{\"type\":\"number\"},\"h\":{\"type\":\"number\"},\"fps\":{\"type\":\"number\"}}}}," +
            "{\"name\":\"record_stop\",\"description\":\"停止录屏, 返回 MP4 文件路径\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"record_status\",\"description\":\"查录屏状态(是否在录/已录秒数/文件)\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"app_runas\",\"description\":\"以管理员运行程序(触发UAC, 用户点确认才执行; AI无法静默提权)。path=程序, args=参数\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\"},\"args\":{\"type\":\"string\"}},\"required\":[\"path\"]}}," +
            "{\"name\":\"ui_click\",\"description\":\"按ui_tree索引语义点击元素(Invoke/Toggle/Expand/Select优先,无模式退坐标中心)。title=窗口标题,i=元素索引\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"hwnd\":{\"type\":\"number\"},\"i\":{\"type\":\"number\"}},\"required\":[\"i\"]}," +
            "{\"name\":\"ui_set\",\"description\":\"按索引直接写输入框值(ValuePattern,不走键盘输入法)。title=窗口标题,i=索引,value=文本\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"hwnd\":{\"type\":\"number\"},\"i\":{\"type\":\"number\"},\"value\":{\"type\":\"string\"}},\"required\":[\"i\",\"value\"]}," +
            "{\"name\":\"ui_read\",\"description\":\"按索引读元素Name/Value/类名/类型(比OCR准)。title=窗口标题,i=索引\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"hwnd\":{\"type\":\"number\"},\"i\":{\"type\":\"number\"}},\"required\":[\"i\"]}," +
            "{\"name\":\"get_skill\",\"description\":\"【必须先调用】获取本服务 SKILL 操作手册（铁律/避坑/流程）。所有工具首次调用前强制先读本 SKILL，否则报错。踩坑必须 update_skill 写回，禁止只写记忆。\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"update_skill\",\"description\":\"【踩坑必写】把新踩坑经验写回共享 SKILL.md（全体 agent 共享，立即生效）。title=小节标题，entry=markdown 正文\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"entry\":{\"type\":\"string\"}},\"required\":[\"title\",\"entry\"]}}" +
            "]";
    }

    // ---- 托盘（默认显示；右键菜单；隐藏后重启服务恢复） ----
    static void InitTray()
    {
        TrayIcon = new NotifyIcon();
        TrayIcon.Icon = BuildIcon();
        TrayIcon.Text = "Win Desktop Helper v" + APP_VERSION + "\nbuild " + BuildStamp() + " | :18800";
        TrayIcon.Visible = true;
        ContextMenuStrip menu = new ContextMenuStrip();
        // 版本行带构建指纹: 打开菜单一眼确认跑的是不是刚编的 exe (部署自验证)
        menu.Items.Add("v" + APP_VERSION + "  build " + BuildStampShort(), null, null).Enabled = false; // 只读版本显示 (短: 菜单不拉宽; 完整信息悬停图标看 tooltip)

        // 截图 二级菜单 (M1: 区域截图为核心能力, 折叠但置顶)
        ToolStripMenuItem mShot = new ToolStripMenuItem("截图");
        mShot.DropDownItems.Add("区域截图 (框选)", null, delegate { ShowCaptureOverlay(); });
        mShot.DropDownItems.Add("全屏截图", null, delegate
        {
            // 不弹气泡 (用户要求): 保存路径已复制到剪贴板, 打开截图目录即可见
            try { string fp = DoShot(VirtualScreen()); Log("tray shot: " + fp); Clipboard.SetText(fp); }
            catch (Exception ex) { Log("tray shot err: " + ex.Message); }
        });
        mShot.DropDownItems.Add("打开截图目录", null, delegate { try { if (Directory.Exists(ShotDir)) Process.Start("explorer.exe", "\"" + ShotDir + "\""); } catch { } });
        menu.Items.Add(mShot);

        // 工具 二级菜单 (MCP/更新/日志等低频项折叠)
        ToolStripMenuItem mTools = new ToolStripMenuItem("工具");
        mTools.DropDownItems.Add("检查更新", null, delegate
        {
            try
            {
                string v = LatestVersion();
                if (v == null) MessageBox.Show("无法连接 GitHub，请检查网络", "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (IsNewerVersion(v)) { MessageBox.Show("发现新版本 v" + v.TrimStart('v', 'V') + "，正在自动静默更新...", "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Information); Thread.Sleep(800); DoUpdateSilent(true); }
                else MessageBox.Show("已是最新版本 v" + APP_VERSION, "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show("检查失败: " + ex.Message, "Win Desktop Helper", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        });
        mTools.DropDownItems.Add("打开日志", null, delegate { try { if (File.Exists(LogPath)) Process.Start("notepad.exe", "\"" + LogPath + "\""); } catch { } });
        mTools.DropDownItems.Add("以管理员重启", null, delegate
        {
            try
            {
                // UAC 由用户确认; 确认后退出当前实例, 新实例以管理员拉起 (iss [Run] 的自启仍是普通权限)
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo(exe); psi.UseShellExecute = true; psi.Verb = "runas";
                Process.Start(psi);
                Log("elevate restart requested");
                Thread.Sleep(800);
                Environment.Exit(0);
            }
            catch (System.ComponentModel.Win32Exception) { TrayNotify("已取消", "未提升管理员权限, 服务保持普通权限运行"); }
            catch (Exception ex) { Log("elevate restart err: " + ex.Message); TrayNotify("重启失败", ex.Message); }
        });
        mTools.DropDownItems.Add("复制 MCP 接入配置", null, delegate
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
        mTools.DropDownItems.Add("项目主页 (GitHub)", null, delegate { try { Process.Start(REPO_URL); } catch { } });
        menu.Items.Add(mTools);

        // M3: 设置/百度翻译登录入口 (用户自助填 appid/key, 存 json 热生效)
        menu.Items.Add("设置...", null, delegate { try { ShowSettingsForm(); } catch (Exception ex) { Log("settings err: " + ex.Message); } });

        menu.Items.Add("隐藏托盘图标", null, delegate { TrayIcon.Visible = false; Log("tray hidden (restart service to show again)"); });
        menu.Items.Add("退出服务", null, delegate { Log("tray exit requested"); Environment.Exit(0); });
        TrayIcon.ContextMenuStrip = menu;
        TrayIcon.DoubleClick += delegate
        {
            try { string fp = DoShot(VirtualScreen()); Log("tray dblclick shot: " + fp); Clipboard.SetText(fp); }
            catch (Exception ex) { Log("tray dblclick err: " + ex.Message); }
        };
        // 启动气泡报构建指纹: 用户每次启动肉眼确认"跑的是不是刚编的" (部署自验证)
        TrayNotify("已启动 v" + APP_VERSION, "build " + BuildStamp() + " — 与编译时间一致即新代码生效");
        Log("tray icon ready, build=" + BuildStamp());
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