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
    const string APP_VERSION = "0.0.18.1";
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
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);   // Win10 1703+; -4 = PerMonitorV2
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);               // 2 = per-monitor
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags); // flags=2 PW_RENDERFULLCONTENT: 拍 DirectComposition/D2D 硬件层 (Win8.1+)
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

    // 分级匹配: 标题完全相等 > 标题前缀 > 标题包含 > 仅按进程名; 可选 process 过滤
    // 2026-09-07 修: 旧版纯 IndexOf 包含匹配, title="微信" 命中 Edge 标签页标题(含"微信"二字) 实测误伤
    static IntPtr FindWindowByTitle(string keyword, string process)
    {
        if (keyword == "" && process == "") return IntPtr.Zero;
        System.Collections.Generic.List<IntPtr> exact = new System.Collections.Generic.List<IntPtr>();
        System.Collections.Generic.List<IntPtr> starts = new System.Collections.Generic.List<IntPtr>();
        System.Collections.Generic.List<IntPtr> contains = new System.Collections.Generic.List<IntPtr>();
        System.Collections.Generic.List<IntPtr> procOnly = new System.Collections.Generic.List<IntPtr>();
        string pnNeed = process;
        if (pnNeed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) pnNeed = pnNeed.Substring(0, pnNeed.Length - 4);
        EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            if (!IsWindowVisible(h)) return true;
            string t = WindowTitle(h);
            uint pid; GetWindowThreadProcessId(h, out pid);
            string pn = "";
            try { pn = Process.GetProcessById((int)pid).ProcessName; } catch { }
            if (pnNeed != "" && !string.Equals(pn, pnNeed, StringComparison.OrdinalIgnoreCase)) return true;
            if (pnNeed != "" && keyword == "") { procOnly.Add(h); return true; }
            if (keyword != "" && string.Equals(t, keyword, StringComparison.OrdinalIgnoreCase)) { exact.Add(h); return true; }
            if (keyword != "" && t.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) { starts.Add(h); return true; }
            if (keyword != "" && t.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) { contains.Add(h); return true; }
            return true;
        }, IntPtr.Zero);
        if (exact.Count > 0) return exact[0];
        if (starts.Count > 0) return starts[0];
        if (contains.Count > 0) return contains[0];
        if (procOnly.Count > 0) return procOnly[0];
        return IntPtr.Zero;
    }

    static IntPtr FindWindowByTitle(string keyword) { return FindWindowByTitle(keyword, ""); }

    static string WindowJsonByTitle(string keyword) { return WindowJsonByTitle(keyword, ""); }

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

    // 窗口截图: PrintWindow+PW_RENDERFULLCONTENT 让窗口自绘进 DC (能拍到 DirectComposition/D2D 内容,
    // CopyFromScreen 拍不到 — 实测 Win11 记事本正文区黑屏)。中心区若全黑(某些应用 PrintWindow 黑屏)回退 CopyFromScreen
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(IntPtr h, StringBuilder sb, uint len, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)] static extern bool CloseHandleW(IntPtr h); // kernel32 导出名是 CloseHandle (W 后缀不存在→EntryPointNotFound 静默吞进 catch)
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(IntPtr h, out BY_HANDLE_FILE_INFORMATION info);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIME 用 long 会被 Pack=8 对齐插 padding → nNumberOfLinks 读错位 (硬链接漏检根因)
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    // R9-1: NTFS 硬链接无 ReparsePoint 属性位、路径也在白名单内 — 唯一可靠信号是 link count>1 (正常截图产物恒为 1)
    static int GetHardLinkCount(string path)
    {
        try
        {
            IntPtr h = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero); // GENERIC_READ (纯 READ_ATTRIBUTES 在紧 ACL 下 err=5) + share READ; BACKUP_SEMANTICS 开文件需特权→恒失败 (实测 linkcount=-1 根因)
            if (h == new IntPtr(-1)) { Log("[hl] open fail err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + " path=" + path); return -1; }
            try
            {
                BY_HANDLE_FILE_INFORMATION fi;
                bool gi = GetFileInformationByHandle(h, out fi);
                if (!gi) Log("[hl] getinfo fail err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return gi ? (int)fi.nNumberOfLinks : -1;
            }
            finally { CloseHandleW(h); }
        }
        catch (Exception ex) { Log("[hl] exception: " + ex.GetType().Name + ": " + ex.Message); return -1; }
    }

    // R8-1: 打开句柄取"解析后真实路径"(junction/symlink/reparse 全部还原), 必须落在截图目录内。
    // 纯字符串 StartsWith(GetFullPath) 不解析 reparse, 目录内建 junction 指向外部即可绕过 (workbuddy R8 PoC)
    // (备用工具函数: 取句柄级最终路径; 注意对 junction 不解析目标, R8-1 已改用逐段 reparse 检查)
    static string ResolveFinalPath(string p)
    {
        try
        {
            IntPtr h = CreateFileW(p, 0x80000000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero); // 文件版 GENERIC_READ; 目录失败再退 BACKUP_SEMANTICS
            if (h == new IntPtr(-1))
            {
                h = CreateFileW(p, 0x8000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero); // 目录分支: BACKUP_SEMANTICS
                if (h == new IntPtr(-1)) return null;
            }
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 2); // FILE_NAME_RESOLVED_BIT: 解析 junction (workbuddy 勘误采纳)
                if (n == 0 || n >= sb.Capacity) return null;
                string r = sb.ToString();
                if (r.StartsWith("\\\\?\\")) r = r.Substring(4);
                return r;
            }
            finally { CloseHandleW(h); }
        }
        catch { return null; }
    }

    static bool SafeShotPath(string path, out string real, out string why)
    {
        real = null; why = null;
        if (string.IsNullOrEmpty(path)) { why = "need path"; return false; }
        string full;
        try { full = System.IO.Path.GetFullPath(path); } catch { why = "bad path"; return false; }
        if (!System.IO.File.Exists(full)) { why = "file not found"; return false; }
        int hlc = GetHardLinkCount(full); Log("[safe] linkcount=" + hlc + " for " + full);
        if (hlc > 1) { why = "hardlink (nNumberOfLinks>1) 拒绝"; return false; } // R9-1: 硬链接无 reparse 位/路径合法, 唯一信号=link count
        string root = System.IO.Path.GetFullPath(ShotDir).TrimEnd('\\');
        if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) { why = "path outside screenshots dir (安全限制)"; return false; }
        // R8-1 实战修正: GetFinalPathNameByHandle 对 junction 不跟随(返回原路径, 实测绕过) → 逐段查 reparse 属性,
        // 文件本体或任一中间目录是 junction/符号链接即拒 (字符串前缀 + reparse 双断)
        try
        {
            if ((System.IO.File.GetAttributes(full) & System.IO.FileAttributes.ReparsePoint) != 0) { why = "文件是符号链接/junction, 拒绝"; return false; }
            string dir = System.IO.Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && dir.Length >= root.Length && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if ((new System.IO.DirectoryInfo(dir).Attributes & System.IO.FileAttributes.ReparsePoint) != 0) { why = "路径中间目录含 junction/符号链接, 拒绝"; return false; }
                string parent = System.IO.Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }
        catch (Exception ex) { why = "reparse check fail: " + ex.Message; return false; }
        real = full;
        return true;
    }

    // /ocr?path=<png>&wait=1 — 后台 OCR (复用 OcrProvider/qwen3-vl): 截图文件进、文本出, agent 无 UI 依赖
    static string OcrFile(string path, int waitMs)
    {
        try
        {
            string realP, whyP;
            if (!SafeShotPath(path, out realP, out whyP)) return "{\"ok\":false,\"error\":\"" + JsonEscape(whyP) + "\"}";
            Bitmap bmp;
            using (Bitmap src = new Bitmap(realP)) bmp = new Bitmap(src); // 拷出释放文件句柄
            string text = null; string err = null;
            try
            {
                var task = OcrProvider().RecognizeAsync(bmp);
                int wms = waitMs > 0 ? waitMs : 60000;
                if (!task.Wait(wms)) { bmp.Dispose(); return "{\"ok\":false,\"error\":\"OCR timeout (模型冷启动加载慢?)\",\"retryable\":true,\"waitedMs\":" + wms + "}"; }
                text = task.Result;
            }
            catch (Exception ex) { err = (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
            bmp.Dispose();
            if (err != null) return "{\"ok\":false,\"error\":\"" + JsonEscape(err) + "\"}";
            return "{\"ok\":true,\"chars\":" + (text ?? "").Length + ",\"text\":\"" + JsonEscape(text ?? "") + "\"}";
        }
        catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}"; }
    }

    // 服务启动后后台预热 Ollama OCR 模型 (X2: 冷启动首包慢导致调用方"第一次必超时")
    static void OcrWarmup()
    {
        Thread th = new Thread(new ThreadStart(delegate
        {
            try
            {
                System.Threading.Thread.Sleep(4000);
                string ep = Cfg("ocr.endpoint", "http://127.0.0.1:11434/api/generate");
                using (var wc = new System.Net.WebClient())
                {
                    wc.Encoding = System.Text.Encoding.UTF8;
                    wc.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
                    string m = ep.Contains("11434") ? "qwen3-vl:4b-instruct" : "";
                    wc.UploadString(ep, "{\"model\":\"" + m + "\",\"prompt\":\"hi\",\"stream\":false}");
                }
                Log("ocr warmup done");
            }
            catch (Exception ex) { Log("ocr warmup skip: " + ex.Message); }
        }));
        th.IsBackground = true;
        th.Start();
    }

    // /pin?path=<png>&x=&y= — 贴图到桌面 (agent 把图钉到用户屏幕上, 与截图工具条贴图同一实现)
    static string PinFile(string path, int x, int y)
    {
        try
        {
            string realP, whyP;
            if (!SafeShotPath(path, out realP, out whyP)) return "{\"ok\":false,\"error\":\"" + JsonEscape(whyP) + "\"}";
            Bitmap img;
            using (Bitmap src = new Bitmap(realP)) img = new Bitmap(src);
            var vs = SystemInformation.VirtualScreen;
            if (x == -1 || y == -1) { x = vs.X + vs.Width / 2 - img.Width / 2; y = vs.Y + vs.Height / 2 - img.Height / 2; }
            Rectangle r = new Rectangle(x, y, img.Width, img.Height);
            int px = x, py = y;
            Form hk = hkForm;
            if (hk == null || !hk.IsHandleCreated) { img.Dispose(); return "{\"ok\":false,\"error\":\"UI thread not ready, retry later\"}"; }
            try
            {
                hk.BeginInvoke((Action)delegate
                {
                    try { PinForm p = new PinForm(img, r); p.Show(); }
                    catch (Exception ex) { img.Dispose(); Log("pin err: " + ex.Message); }
                });
            }
            catch (Exception ex) { img.Dispose(); return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}"; }
            return "{\"ok\":true,\"rect\":{\"x\":" + px + ",\"y\":" + py + ",\"w\":" + img.Width + ",\"h\":" + img.Height + "},\"hint\":\"已钉到桌面: 左键拖动/滚轮缩放/双击关闭/右键菜单\"}";
        }
        catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}"; }
    }

    static string DoShotWindow(IntPtr h)
    {
        RECT rc; GetWindowRect(h, out rc);
        int w = rc.Right - rc.Left, ht = rc.Bottom - rc.Top;
        if (w <= 0 || ht <= 0) return null;
        using (Bitmap bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(h, hdc, 2); }
                finally { g.ReleaseHdc(hdc); }
            }
            // 中心 5 点采样: 全黑 → PrintWindow 没画出内容, 回退桌面拷贝
            bool allBlack = true;
            int[] sx = { w / 2, w / 3, w * 2 / 3, w / 4, w * 3 / 4 };
            int[] sy = { ht / 2, ht / 3, ht * 2 / 3, ht / 4, ht * 3 / 4 };
            foreach (int px in sx) { foreach (int py in sy) { if (bmp.GetPixel(px, py).GetBrightness() > 0.05f) { allBlack = false; break; } } if (!allBlack) break; }
            if (allBlack && IsWindowVisible(h))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(rc.Left, rc.Top, 0, 0, new Size(w, ht));
                Log("shot window: printwindow black, fallback copyfromscreen");
            }
            string name = "shot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".png";
            string path = Path.Combine(ShotDir, name);
            bmp.Save(path, ImageFormat.Png);
            return path;
        }
    }

    static string WindowJsonByTitle(string keyword, string process)
    {
        IntPtr h = FindWindowByTitle(keyword, process);
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

    // ---- 托盘图标点击 (Electron 托盘应用窗口失踪时的主恢复手段) ----
    static string TrayClick(string name, string button, bool dbl)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.RootElement;
            var tray = root.FindFirst(System.Windows.Automation.TreeScope.Children,
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            var hit = FindTrayButton(tray, name);
            string via = "taskbar";
            if (hit == null)
            {
                var chev = FindTrayButton(tray, "隐藏的图标");
                if (chev == null) return "{\"ok\":false,\"error\":\"tray icon not found in taskbar, overflow chevron not found either\"}";
                var r0 = chev.Current.BoundingRectangle;
                MouseMove((int)(r0.X + r0.Width / 2), (int)(r0.Y + r0.Height / 2));
                System.Threading.Thread.Sleep(150); MouseClick("left", 1);
                System.Threading.Thread.Sleep(500);
                var of = root.FindFirst(System.Windows.Automation.TreeScope.Children,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "NotifyIconOverflowWindow"));
                hit = FindTrayButton(of, name);
                via = "overflow";
            }
            if (hit == null) return "{\"ok\":false,\"error\":\"tray icon not found: 主区和溢出区都找过\"}";
            var rc = hit.Current.BoundingRectangle;
            int cx = (int)(rc.X + rc.Width / 2), cy = (int)(rc.Y + rc.Height / 2);
            MouseMove(cx, cy); System.Threading.Thread.Sleep(120);
            MouseClick(button == "" ? "left" : button, dbl ? 2 : 1);
            return "{\"ok\":true,\"found\":true,\"via\":\"" + via + "\",\"rect\":{\"x\":" + (int)rc.X + ",\"y\":" + (int)rc.Y + ",\"w\":" + (int)rc.Width + ",\"h\":" + (int)rc.Height + "},\"clicked\":\"" + (dbl ? "double" : button) + "\"}";
        }
        catch (Exception e) { return "{\"ok\":false,\"error\":\"" + JsonEscape(e.Message) + "\"}"; }
    }

    static System.Windows.Automation.AutomationElement FindTrayButton(System.Windows.Automation.AutomationElement scope, string name)
    {
        if (scope == null || name == null || name == "") return null;
        foreach (var el in WalkLimited(scope, 500))
        {
            try
            {
                string n = el.Current.Name;
                if (!string.IsNullOrEmpty(n) && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return el;
            }
            catch { }
        }
        return null;
    }

    // ---- 全量窗口枚举 (含隐藏/最小化/托盘化窗口; list_apps 只列可见窗口, 找不到窗口时用这个) ----
    static string WinListAll(uint pidFilter)
    {
        var rows = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pidFilter > 0 && pid != pidFilter) return true;
            if (GetAncestor(h, 2) != h) return true;
            var sbT = new StringBuilder(256);
            string title = GetWindowTextW(h, sbT, 256) > 0 ? sbT.ToString() : "";
            var sbC = new StringBuilder(128);
            string cls = GetClassNameW(h, sbC, 128) > 0 ? sbC.ToString() : "";
            bool vis = IsWindowVisible(h);
            bool iconic = IsIconic(h);
            RECT r; if (!GetWindowRect(h, out r)) { r = new RECT(); }
            string pname = "";
            try { var p = System.Diagnostics.Process.GetProcessById((int)pid); pname = p.ProcessName; } catch { }
            rows.Add("{\"hwnd\":" + h.ToInt64() + ",\"pid\":" + pid + ",\"process\":\"" + JsonEscape(pname) + "\",\"title\":\"" + JsonEscape(title) + "\",\"class\":\"" + JsonEscape(cls) + "\",\"visible\":" + (vis ? "true" : "false") + ",\"minimized\":" + (iconic ? "true" : "false") + ",\"rect\":{\"x\":" + r.Left + ",\"y\":" + r.Top + ",\"w\":" + (r.Right - r.Left) + ",\"h\":" + (r.Bottom - r.Top) + "}}");
            return true;
        }, IntPtr.Zero);
        return "{\"ok\":true,\"count\":" + rows.Count + ",\"windows\":[" + string.Join(",", rows) + "]}";
    }

    // ---- 动: 鼠标 ----
    static void MouseMove(int x, int y) { SetCursorPos(x, y); }

    static void MouseClick(string button, int times)
    {
        uint down = MOUSEEVENTF_LEFTDOWN, up = MOUSEEVENTF_LEFTUP;
        if (button == "right") { down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; }
        else if (button == "middle") { down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; }
        for (int i = 0; i < times; i++)
        {
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // 修饰键按下/松开 (点击时按住 shift/ctrl 等) — mods 如 "shift"/"ctrl+shift"
    static byte[] ModsVks(string mods)
    {
        if (string.IsNullOrEmpty(mods)) return new byte[0];
        var l = new List<byte>();
        foreach (string k in mods.Split('+'))
        {
            string kk = k.Trim().ToLowerInvariant();
            if (kk == "shift") l.Add(0x10);
            else if (kk == "ctrl" || kk == "control") l.Add(0x11);
            else if (kk == "alt") l.Add(0x12);
            else if (kk == "win") l.Add(0x5B);
        }
        return l.ToArray();
    }

    static void MouseScroll(int delta) { mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), UIntPtr.Zero); }

    // 运行程序/打开(ShellExecute: 支持 exe/快捷方式/URL); 须在 Session 1 才有用户可见界面
    static string AppRun(string path, string args) { return AppRun(path, args, 0, ""); }

    // waitMs>0: 找到窗口后继续等到它稳定(rect 连续 3 次采样不变, 且句柄存活)再返回;
    //           句柄中途失效(多进程应用如微信会换进程换窗)会重新 diff 找新窗口 — 2026-09-07 实测坑
    // procFilter: 只认该进程名的窗口 (如 Weixin), 忽略启动器/其它进程弹出的过渡窗
    static string AppRun(string path, string args, int waitMs, string procFilter)
    {
        // 启动前快照可见顶级窗口; 启动后轮询找新窗口 — Store 应用启动器 pid ≠ 窗口进程, 必须按窗口 diff 找 (实测坑)
        HashSet<long> before = new HashSet<long>();
        EnumWindows(delegate(IntPtr h, IntPtr lp) { if (IsWindowVisible(h) && IsWindow(h)) before.Add(h.ToInt64()); return true; }, IntPtr.Zero);
        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = path;
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        psi.UseShellExecute = true;
        // ShellExecute 必须在 STA 线程: MTA 线程无 message pump, DDE 等待超时把 Process.Start 挂 30s+ (实测 app_run 卡 40s, 连 cmd 都卡)
        Process p = null; string startErr = null;
        Thread st = new Thread(new ThreadStart(delegate
        {
            try { p = Process.Start(psi); }
            catch (Exception ex) { startErr = ex.Message; }
        }));
        st.SetApartmentState(ApartmentState.STA);
        st.IsBackground = true;
        st.Start();
        st.Join(5000);
        if (startErr != null) return "{\"ok\":false,\"error\":\"" + JsonEscape(startErr) + "\"}";
        string runPid, runName;
        try { runPid = p.Id.ToString(); runName = JsonEscape(p.ProcessName); }
        catch { runPid = "0"; runName = ""; } // 启动器秒退 (Store 应用) 或启动超时
        string winJson = "null";
        IntPtr lastHwnd = IntPtr.Zero;
        for (int t = 0; t < 25; t++) // 最多等 2.5s, 找到新窗口提前结束
        {
            System.Threading.Thread.Sleep(100);
            IntPtr found = IntPtr.Zero; string ftitle = ""; uint fpid = 0;
            EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                if (IsWindowVisible(h) && IsWindow(h) && !before.Contains(h.ToInt64()))
                {
                    StringBuilder sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
                    if (sb.Length > 0)
                    {
                        ftitle = sb.ToString(); GetWindowThreadProcessId(h, out fpid); found = h; return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                string proc = "";
                try { proc = Process.GetProcessById((int)fpid).ProcessName; } catch { }
                if (procFilter != "" && !string.Equals(proc, procFilter, StringComparison.OrdinalIgnoreCase)) continue; // 进程名不符, 继续等
                winJson = "{\"hwnd\":" + found.ToInt64() + ",\"pid\":" + fpid + ",\"title\":\"" + JsonEscape(ftitle) + "\",\"process\":\"" + JsonEscape(proc) + "\"}";
                lastHwnd = found;
                break;
            }
        }
        bool stable = false; int waitedMs = 0;
        if (waitMs > 0)
        {
            int deadline = Environment.TickCount + waitMs;
            int sameCount = 0; int lastL = 0, lastT = 0, lastW = 0, lastH = 0;
            while (Environment.TickCount < deadline)
            {
                System.Threading.Thread.Sleep(200);
                // 句柄失效(应用换进程重建窗口) → 重新 diff 找新窗口, 别死守旧句柄
                if (lastHwnd == IntPtr.Zero || !IsWindow(lastHwnd))
                {
                    IntPtr nf = IntPtr.Zero; string ntitle = ""; uint npid = 0;
                    EnumWindows(delegate(IntPtr h, IntPtr lp)
                    {
                        if (IsWindowVisible(h) && IsWindow(h) && !before.Contains(h.ToInt64()))
                        {
                            StringBuilder sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
                            if (sb.Length > 0)
                            {
                                uint tp; GetWindowThreadProcessId(h, out tp);
                                string pn = ""; try { pn = Process.GetProcessById((int)tp).ProcessName; } catch { }
                                if (procFilter == "" || string.Equals(pn, procFilter, StringComparison.OrdinalIgnoreCase))
                                { ntitle = sb.ToString(); npid = tp; nf = h; return false; }
                            }
                        }
                        return true;
                    }, IntPtr.Zero);
                    if (nf != IntPtr.Zero)
                    {
                        string pn2 = ""; try { pn2 = Process.GetProcessById((int)npid).ProcessName; } catch { }
                        lastHwnd = nf;
                        winJson = "{\"hwnd\":" + nf.ToInt64() + ",\"pid\":" + npid + ",\"title\":\"" + JsonEscape(ntitle) + "\",\"process\":\"" + JsonEscape(pn2) + "\"}";
                        sameCount = 0;
                    }
                    continue;
                }
                RECT sr; GetWindowRect(lastHwnd, out sr);
                int cl = sr.Left, ct = sr.Top, cw = sr.Right - sr.Left, ch = sr.Bottom - sr.Top;
                if (cl == lastL && ct == lastT && cw == lastW && ch == lastH) sameCount++; else sameCount = 0;
                lastL = cl; lastT = ct; lastW = cw; lastH = ch;
                if (sameCount >= 2 && cw > 0 && ch > 0) { stable = true; break; } // 连续 3 次采样一致
            }
            waitedMs = waitMs - Math.Max(0, deadline - Environment.TickCount);
        }
        // 返回前刷新窗口终态: 首匹配可能是 Store 启动器 splash(标题如"记事本"), 真实窗标题("无标题 - Notepad")/pid 以其为准
        if (lastHwnd != IntPtr.Zero && IsWindow(lastHwnd))
        {
            StringBuilder rf = new StringBuilder(256); GetWindowTextW(lastHwnd, rf, 256);
            uint rpid = 0; GetWindowThreadProcessId(lastHwnd, out rpid);
            string rproc = ""; try { rproc = Process.GetProcessById((int)rpid).ProcessName; } catch { }
            RECT fr; GetWindowRect(lastHwnd, out fr);
            winJson = "{\"hwnd\":" + lastHwnd.ToInt64() + ",\"pid\":" + rpid + ",\"title\":\"" + JsonEscape(rf.ToString()) + "\",\"process\":\"" + JsonEscape(rproc) + "\",\"rect\":{\"x\":" + fr.Left + ",\"y\":" + fr.Top + ",\"w\":" + (fr.Right - fr.Left) + ",\"h\":" + (fr.Bottom - fr.Top) + "}}";
        }
        return "{\"ok\":true,\"pid\":" + runPid + ",\"name\":\"" + runName + "\",\"session\":" + Process.GetCurrentProcess().SessionId + ",\"window\":" + winJson + (waitMs > 0 ? ",\"stable\":" + (stable ? "true" : "false") + ",\"waitedMs\":" + waitedMs : "") + "}";
    }

    // 按进程名找可见主窗(顶层 + 有标题)
    static IntPtr FindMainWinByProc(string proc)
    {
        if (string.IsNullOrEmpty(proc)) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr lp)
        {
            if (!IsWindow(h) || !IsWindowVisible(h)) return true;
            StringBuilder sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
            if (sb.Length == 0) return true;
            uint pid = 0; GetWindowThreadProcessId(h, out pid);
            string pn = "";
            try { pn = Process.GetProcessById((int)pid).ProcessName; } catch { }
            if (string.Equals(pn, proc, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static string JsonArr(System.Collections.Generic.List<string> l)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < l.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"" + JsonEscape(l[i]) + "\"");
        }
        return sb.ToString();
    }

    static long HwndFromWindowJson(string js)
    {
        int k = js.IndexOf("\"hwnd\":");
        if (k < 0) return 0;
        string num = js.Substring(k + 7);
        int comma = num.IndexOfAny(new char[] { ',', '}' });
        if (comma > 0) num = num.Substring(0, comma);
        long v = 0; long.TryParse(num, out v);
        return v;
    }

    // 深度恢复: 关窗 -> 托盘双击重开 -> 等窗口稳定 -> 贴回原位置(可选)
    // 解决两类顽疾: ①Electron 假激活/冻结(看得见但点不动) ②应用没窗口(缩托盘/任务栏隐藏区)
    // 一条命令做完, AI 不需要知道内部步骤
    static string AppRestore(string process, string title, long hwndIn, string snapPos, int waitMs)
    {
        var steps = new System.Collections.Generic.List<string>();
        if (waitMs <= 0) waitMs = 8000;

        IntPtr h = IntPtr.Zero;
        if (hwndIn > 0) { IntPtr t = new IntPtr(hwndIn); if (IsWindow(t)) h = t; }
        if (h == IntPtr.Zero) h = FindMainWinByProc(process);
        if (h == IntPtr.Zero && !string.IsNullOrEmpty(title))
        {
            long v = HwndFromWindowJson(WindowJsonByTitle(title, process));
            if (v > 0) h = new IntPtr(v);
        }

        int ox = 0, oy = 0, ow = 0, oh = 0; bool had = false;
        if (h != IntPtr.Zero)
        {
            RECT r0; GetWindowRect(h, out r0);
            ox = r0.Left; oy = r0.Top; ow = r0.Right - r0.Left; oh = r0.Bottom - r0.Top;
            if (ow > 0 && oh > 0) had = true;
            steps.Add("found hwnd=" + h.ToInt64() + " rect=" + ox + "," + oy + "," + ow + "," + oh);
            steps.Add("close -> " + WinClose(h));
            Thread.Sleep(900);
        }
        else steps.Add("no visible window (缩托盘/隐藏区)");

        string trayName = !string.IsNullOrEmpty(process) ? process : title;
        if (string.IsNullOrEmpty(trayName))
            return "{\"ok\":false,\"error\":\"need process= or title= or hwnd=\",\"steps\":[" + JsonArr(steps) + "]}";
        steps.Add("tray_click '" + trayName + "' double=1 -> " + TrayClick(trayName, "left", true));

        int deadline = Environment.TickCount + waitMs;
        IntPtr nh = IntPtr.Zero; bool stable = false;
        int sameCount = 0, ll = 0, lt = 0, lw = 0, lh2 = 0;
        while (Environment.TickCount < deadline)
        {
            Thread.Sleep(250);
            IntPtr c = FindMainWinByProc(process);
            if (c == IntPtr.Zero && !string.IsNullOrEmpty(title))
            {
                long v = HwndFromWindowJson(WindowJsonByTitle(title, process));
                if (v > 0) c = new IntPtr(v);
            }
            if (c != IntPtr.Zero)
            {
                nh = c;
                RECT rr; GetWindowRect(nh, out rr);
                int cl = rr.Left, ct = rr.Top, cw = rr.Right - rr.Left, chh = rr.Bottom - rr.Top;
                if (cl == ll && ct == lt && cw == lw && chh == lh2) sameCount++; else sameCount = 0;
                ll = cl; lt = ct; lw = cw; lh2 = chh;
                if (sameCount >= 2 && cw > 50 && chh > 50) { stable = true; break; }
            }
            else sameCount = 0;
        }
        int waitedMs = waitMs - Math.Max(0, deadline - Environment.TickCount);

        if (nh == IntPtr.Zero)
            return "{\"ok\":false,\"error\":\"关窗后没等到窗口重新出现 — 托盘名可能不对(用 tray_list 看真实名字), 或应用已真退出(用 app_run 带 wait+process 重启)\",\"trayName\":\"" + JsonEscape(trayName) + "\",\"waitedMs\":" + waitedMs + ",\"steps\":[" + JsonArr(steps) + "]}";

        if (!string.IsNullOrEmpty(snapPos)) steps.Add("snap " + snapPos + " -> " + WinSnap(nh, snapPos, "", 0, 0, 0, 0, 0, 0));
        else if (had) steps.Add("move back -> " + WinMove(nh, ox, oy, ow, oh));
        try { WinActivate(nh); steps.Add("activate"); } catch (Exception ex) { steps.Add("activate err: " + ex.Message); }

        RECT fr; GetWindowRect(nh, out fr);
        return "{\"ok\":true,\"hwnd\":" + nh.ToInt64() + ",\"stable\":" + (stable ? "true" : "false")
            + ",\"waitedMs\":" + waitedMs + ",\"restored\":" + (had ? "true" : "false")
            + ",\"rect\":{\"x\":" + fr.Left + ",\"y\":" + fr.Top + ",\"w\":" + (fr.Right - fr.Left) + ",\"h\":" + (fr.Bottom - fr.Top) + "}"
            + ",\"hint\":\"已走 close->托盘双击重开(唯一可靠恢复路径)。窗口应为可点击状态; 若 ui_click 仍 verify.changed=false, 把原始返回贴出来别瞎试\""
            + ",\"steps\":[" + JsonArr(steps) + "]}";
    }

    // 当前前台窗口简报 (type/press 响应附带, 让调用方自查打到了哪个窗口 — 盲打事故防线)
    static string FrontBriefJson()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "{\"title\":\"\",\"process\":\"\"}";
        uint pid; GetWindowThreadProcessId(h, out pid);
        string proc = "";
        try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
        StringBuilder sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
        return "{\"title\":\"" + JsonEscape(sb.ToString()) + "\",\"process\":\"" + JsonEscape(proc) + "\"}";
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
            int wd0 = (int)wParam == 0x020A ? (short)((long)wParam >> 16) : 0;   // 滚轮 delta(高位字)
            PickOnMouse((int)wParam, ms.pt.x, ms.pt.y, wd0); // 划词检测: 必须在 volEnabled 判断之前, 音量关了划词也要能用 (回调内只记坐标, 1ms 内返回)
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
    static string lastClipImgHash = ""; // 剪贴板图片 MD5 (精确去重, 轮询防重复入库)
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
                if (pickBusy == 1) { Thread.Sleep(150); continue; } // 划词取词中: 跳过本轮, 别把 Ctrl+C 的临时内容记进剪贴板历史
                if (clipEnabled == 1 && Clipboard.ContainsImage())
                {
                    // 图片入历史: MD5 命名入库(同图去重精确到字节, 3采样点漏检已根治), 条目 "[图片] 路径" (AI 可 Read 该图/OCR/传多模态)
                    Image img = Clipboard.GetImage();
                    if (img != null)
                    {
                        string hash;
                        string path = SaveClipboardImage(img, out hash);
                        if (hash != "" && hash != lastClipImgHash)
                        {
                            lastClipImgHash = hash;
                            string entry = "[图片] " + path;
                            lock (clipLock)
                            {
                                clipHist.Remove(entry);
                                clipHist.Insert(0, entry);
                                while (clipHist.Count > clipMax) clipHist.RemoveAt(clipHist.Count - 1);
                            }
                            SaveClipHistory(); // 图片条目即时持久化 (此前只在文本复制时被顺带保存, 重启即丢)
                            Log("clip image captured " + img.Width + "x" + img.Height + " md5=" + (hash.Length > 8 ? hash.Substring(0, 8) : hash));
                        }
                        img.Dispose();
                    }
                }
                else if (clipEnabled == 1 && Clipboard.ContainsText())
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

    static void TypeText(string text) { TypeText(text, null); }
    // nl 默认 shift+enter: 记事本照常换行, 聊天/搜索框软换行不触发发送 (workbuddy 12:55 连发 4 条事故根治); nl="enter" 显式裸回车
    static void TypeText(string text, string nlMode)
    {
        bool soft = nlMode != "enter";
        text = text.Replace("\r\n", "\n");
        foreach (char c in text)
        {
            if (c == '\n')
            {
                if (soft) { KeyEvent(0x10, 0, 0); KeyEvent(0x0D, 0, 0); KeyEvent(0x0D, 0, KEYEVENTF_KEYUP); KeyEvent(0x10, 0, KEYEVENTF_KEYUP); }
                else { KeyEvent(0x0D, 0, 0); KeyEvent(0x0D, 0, KEYEVENTF_KEYUP); }
                continue;
            }
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

    static string JsonEscape(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t"); }

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

            bool headerOverflow = got >= buf.Length && req.IndexOf("\r\n\r\n") < 0; // R8-2: 16KB 截断不再静默假成功
            bool needUserSession = path.StartsWith("/mouse") || path.StartsWith("/keyboard") || path == "/shot" || path.StartsWith("/app") || path == "/open-repo" || path.StartsWith("/record") || path.StartsWith("/ui") || path.StartsWith("/win");
            bool control = path.StartsWith("/mouse") || path.StartsWith("/keyboard");
            int code = 200;
            string body = "";
            string logLine = target;

            try
            {
                if (headerOverflow)
                {
                    code = 413;
                    body = "{\"ok\":false,\"error\":\"request too large: URL 超过 16KB 已截断(不会静默部分生效)。长文本请分段传输或先落盘再按 path 引用\"}";
                }
                else if (needUserSession && MySession == 0)
                {
                    code = 503; body = "{\"ok\":false,\"error\":\"running in session 0, cannot access user desktop\"}";
                }
                else if (path == "/health")
                {
                    body = "{\"ok\":true,\"pid\":" + Process.GetCurrentProcess().Id + ",\"session\":" + MySession +
                           ",\"shots\":" + ShotCount + ",\"uptimeSec\":" + (int)(DateTime.Now - StartTime).TotalSeconds +
                           ",\"elevated\":" + (IsElevated() ? "true" : "false") +
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
                else if (path == "/pick-config")
                {
                    // 划词悬浮球配置: GET 查状态; ?enabled=0|1 开关; ?askModel/?askEndpoint/?askKey 改「问AI」后端; ?askPrompt 附加提示词(| 表示清空)
                    // 写回 shot-service.json (设置页同一份配置, 保存即两边可见)
                    int pen = -1;
                    if (q.ContainsKey("enabled")) { int v; if (int.TryParse(q["enabled"], out v)) pen = v == 1 ? 1 : 0; }
                    string sEp = q.ContainsKey("askEndpoint") ? q["askEndpoint"] : "";
                    string sKey = q.ContainsKey("askKey") ? q["askKey"] : "";
                    string sModel = q.ContainsKey("askModel") ? q["askModel"] : "";
                    string sPrompt = q.ContainsKey("askPrompt") ? q["askPrompt"] : "";
                    body = PickConfig(pen, sEp, sKey, sModel, sPrompt, 1);
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
                else if (path == "/apps") { body = AppList(); Log("[apps] list"); }
                else if (path == "/ocr")
                {
                    if (!q.ContainsKey("path")) { code = 400; body = "{\"ok\":false,\"error\":\"need path\"}"; }
                    else { int wm = 0; TryInt(q, "wait", out wm); body = OcrFile(q["path"], wm); Log("[ocr] " + q["path"]); }
                }
                else if (path == "/pin")
                {
                    if (!q.ContainsKey("path")) { code = 400; body = "{\"ok\":false,\"error\":\"need path\"}"; }
                    else
                    {
                        int px = -1, py = -1; TryInt(q, "x", out px); TryInt(q, "y", out py);
                        body = PinFile(q["path"], px, py); Log("[pin] " + q["path"]);
                    }
                }
                else if (path == "/diag/threads")
                {
                    string[] lt; lock (uiaLeaked) lt = uiaLeaked.ToArray();
                    body = "{\"ok\":true,\"uiaLeaked\":" + lt.Length + ",\"fused\":" + (lt.Length >= 3 ? "true" : "false") + ",\"entries\":[" + string.Join(",", Array.ConvertAll(lt, x => "\"" + JsonEscape(x) + "\"")) + "],\"hint\":\"泄漏线程来自 UIA 大DOM 超时(无法强杀); fused=true 时所有 /ui/* 将拒绝直到重启\"}";
                }
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
                    if (!q.ContainsKey("title") && !q.ContainsKey("process")) { code = 400; body = "{\"ok\":false,\"error\":\"need title or process\"}"; }
                    else
                    {
                        string wj = WindowJsonByTitle(q.ContainsKey("title") ? q["title"] : "", q.ContainsKey("process") ? q["process"] : "");
                        if (wj == null) { code = 404; body = "{\"ok\":false,\"error\":\"window not found\"}"; }
                        else body = wj;
                    }
                }
                else if (path == "/monitors") { body = MonitorsJson(); }
                else if (path == "/shot")
                {
                    Rectangle r = VirtualScreen();
                    bool handled = false; // window 模式走 PrintWindow 专用路径
                    if (q.ContainsKey("window"))
                    {
                        IntPtr h = FindWindowByTitle(q["window"]);
                        if (h == IntPtr.Zero) { code = 404; body = "{\"ok\":false,\"error\":\"window not found\"}"; }
                        else
                        {
                            string fp = DoShotWindow(h); // PrintWindow 拍硬件层, 黑图回退桌面拷贝
                            if (fp == null) { code = 500; body = "{\"ok\":false,\"error\":\"window rect invalid\"}"; }
                            else
                            {
                                Interlocked.Increment(ref ShotCount);
                                body = "{\"ok\":true,\"file\":\"" + JsonEscape(fp) + "\",\"url\":\"http://127.0.0.1:" + PORT + "/img/" + Uri.EscapeDataString(Path.GetFileName(fp)) + "\"}";
                            }
                            Log("[shot] window " + q["window"]);
                        }
                        handled = true;
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
                    if (!handled && code == 200)
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
                    bool triple = q.ContainsKey("triple") && q["triple"] == "1";
                    string mods = q.ContainsKey("mods") ? q["mods"].ToLowerInvariant() : "";
                    // UIPI 预检: 目标窗口若是管理员权限而自己是普通权限, 点击会被系统静默丢弃 → 直接拦下报错, 不假报 ok
                    string uipi = UipiCheck(hasXY ? WindowFromPoint(new System.Drawing.Point(x, y)) : GetForegroundWindow());
                    if (uipi != null) { code = 409; body = uipi; Log("[ctrl] mouse click BLOCKED by uipi"); }
                    else
                    {
                        if (hasXY) MouseMove(x, y);
                        byte[] mvks = ModsVks(mods);
                        foreach (byte vk in mvks) keybd_event(vk, 0, 0, UIntPtr.Zero); // 按住修饰键
                        MouseClick(button, triple ? 3 : (dbl ? 2 : 1));
                        for (int i = mvks.Length - 1; i >= 0; i--) keybd_event(mvks[i], 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP 逆序松开
                        body = "{\"ok\":true,\"button\":\"" + button + "\"" + (dbl ? ",\"double\":true" : "") + (triple ? ",\"triple\":true" : "") +
                               (mods != "" ? ",\"mods\":\"" + JsonEscape(mods) + "\"" : "") +
                               (hasXY ? ",\"x\":" + x + ",\"y\":" + y : "") + "}";
                        Log("[ctrl] mouse click " + button + (triple ? " triple" : (dbl ? " dbl" : "")) + (mods != "" ? " mods=" + mods : "") + (hasXY ? " @ " + x + "," + y : ""));
                    }
                }
                else if (path == "/mouse/scroll")
                {
                    int d;
                    if (!TryInt(q, "delta", out d)) { code = 400; body = "{\"ok\":false,\"error\":\"need delta\"}"; }
                    else
                    {
                        int sxp = 0, syp = 0;
                        bool hasPt = TryInt(q, "x", out sxp) && TryInt(q, "y", out syp);
                        if (hasPt) MouseMove(sxp, syp); // 滚轮作用于光标处, 先移到目标
                        MouseScroll(d);
                        body = "{\"ok\":true,\"delta\":" + d + (hasPt ? ",\"x\":" + sxp + ",\"y\":" + syp : "") + "}"; Log("[ctrl] scroll " + d + (hasPt ? " @ " + sxp + "," + syp : ""));
                    }
                }
                else if (path == "/keyboard/type")
                {
                    if (!q.ContainsKey("text")) { code = 400; body = "{\"ok\":false,\"error\":\"need text\"}"; }
                    else
                    {
                        string text = q["text"];
                        if (text.Length > 2000) { code = 400; body = "{\"ok\":false,\"error\":\"text too long (max 2000)\"}"; }
                        else
                        {
                            string uipi = UipiCheck(GetForegroundWindow());
                            if (uipi != null) { code = 409; body = uipi; Log("[ctrl] type BLOCKED by uipi"); }
                            else
                            {
                                int nl = 0; foreach (char cc in text) if (cc == '\n') nl++; TypeText(text, q.ContainsKey("nl") ? q["nl"] : ""); body = "{\"ok\":true,\"chars\":" + text.Length + ",\"newlines\":" + nl + (nl > 0 ? ",\"warn\":\"newlines sent as Shift+Enter (soft newline, no submit). pass nl=enter for real Enter; for exact multi-line paste prefer clipboard_set+ctrl+v\"" : "") + ",\"front\":" + FrontBriefJson() + "}"; Log("[ctrl] type " + text.Length + " chars" + (nl > 0 ? " (" + nl + " newlines!)" : ""));
                            }
                        }
                    }
                }
                else if (path == "/keyboard/press")
                {
                    if (!q.ContainsKey("keys")) { code = 400; body = "{\"ok\":false,\"error\":\"need keys\"}"; }
                    else
                    {
                        string uipi = UipiCheck(GetForegroundWindow());
                        if (uipi != null) { code = 409; body = uipi; Log("[ctrl] press BLOCKED by uipi (" + q["keys"] + ")"); }
                        else { PressCombo(q["keys"]); body = "{\"ok\":true,\"keys\":\"" + JsonEscape(q["keys"]) + "\",\"front\":" + FrontBriefJson() + "}"; Log("[ctrl] press " + q["keys"]); }
                    }
                }
                else if (path == "/app/run")
                {
                    if (!q.ContainsKey("path")) { code = 400; body = "{\"ok\":false,\"error\":\"need path\"}"; }
                    else
                    {
                        try { int aw = 0; TryInt(q, "wait", out aw); body = AppRun(q["path"], q.ContainsKey("args") ? q["args"] : "", aw, q.ContainsKey("process") ? q["process"] : ""); Log("[run] " + q["path"] + (aw > 0 ? " wait=" + aw : "")); }
                        catch (Exception ex) { code = 500; body = "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}"; }
                    }
                }
                else if (path == "/app/restore")
                {
                    long rh = 0; int rw = 0;
                    if (q.ContainsKey("hwnd")) long.TryParse(q["hwnd"], out rh);
                    if (q.ContainsKey("wait")) int.TryParse(q["wait"], out rw);
                    try
                    {
                        body = AppRestore(q.ContainsKey("process") ? q["process"] : "", q.ContainsKey("title") ? q["title"] : "", rh, q.ContainsKey("snap") ? q["snap"] : "", rw);
                        Log("[app_restore] " + body);
                    }
                    catch (Exception ex) { code = 500; body = "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}"; }
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
                    else if (verb == "listall")
                    {
                        int lp = 0; TryInt(q, "pid", out lp);
                        body = WinListAll((uint)Math.Max(0, lp));
                    }
                    else if (verb == "list")
                    {
                        int lp = 0; TryInt(q, "pid", out lp);
                        if (lp > 0) body = WinListByPid(lp);
                        else if (q.ContainsKey("title")) body = WinListByTitle(q["title"]);
                        else body = AppList();
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
                        else if (verb == "snap")
                        {
                            int sc = 0, sl = 0, ss = 0, sr = 0, srw = 0, srs = 0;
                            TryInt(q, "cols", out sc); TryInt(q, "col", out sl); TryInt(q, "colspan", out ss);
                            TryInt(q, "rows", out sr); TryInt(q, "row", out srw); TryInt(q, "rowspan", out srs);
                            body = WinSnap(wh, q.ContainsKey("pos") ? q["pos"] : "", q.ContainsKey("monitor") ? q["monitor"] : "", sc, sl, ss, sr, srw, srs);
                        }
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
                else if (path == "/tray/click")
                {
                    if (!q.ContainsKey("name") || q["name"] == "") { code = 400; body = "{\"ok\":false,\"error\":\"need name\"}"; }
                    else
                    {
                        string btn = q.ContainsKey("button") ? q["button"] : "left";
                        int dv = 0; TryInt(q, "double", out dv);
                        body = TrayClick(q["name"], btn, dv == 1);
                    }
                    Log("[tray] " + target);
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
                else if (path == "/clipboard/get") { body = ClipboardGet(); Log("[ctrl] clipboard get"); }
                else if (path == "/clipboard/set")
                {
                    if (!q.ContainsKey("text")) { code = 400; body = "{\"ok\":false,\"error\":\"need text\"}"; }
                    else
                    {
                        string ct = q["text"]; bool crn = false;
                        // 默认 \r\n→\n: 聊天框粘贴遇 \r(回车) 同样触发连发 (12:55 事故); keep_cr=1 保留原样
                        if (!(q.ContainsKey("keep_cr") && q["keep_cr"] == "1") && ct.IndexOf('\r') >= 0)
                        { ct = ct.Replace("\r\n", "\n").Replace("\r", "\n"); crn = true; }
                        body = ClipboardSetText(ct);
                        if (crn) body = body.Substring(0, body.Length - 1) + ",\"crNormalized\":true}";
                        Log("[ctrl] clipboard set " + ct.Length + " chars" + (crn ? " (CR normalized)" : ""));
                    }
                }
                else if (path == "/ui/tree") { body = UiCall("tree", delegate { return UiTree(q); }, 8000); Log("[ui] tree " + target); }
                else if (path == "/ui/click") { body = UiCall("click", delegate { return UiClick(q); }, 8000); Log("[ui] click " + target); }
                else if (path == "/ui/find") { body = UiCall("find", delegate { return UiFind(q); }, 8000); Log("[ui] find " + target); }
                else if (path == "/ui/select") { body = UiCall("select", delegate { return UiSelect(q); }, 8000); Log("[ui] select " + target); }
                else if (path == "/ui/set") { body = UiCall("set", delegate { return UiSet(q); }, 8000); Log("[ui] set " + target); }
                else if (path == "/ui/read") { body = UiCall("read", delegate { return UiRead(q); }, 8000); Log("[ui] read " + target); }
                else if (path == "/ui/readall") { body = UiCall("readall", delegate { return UiReadAll(q); }, 8000); Log("[ui] readall " + target); }
                else if (path == "/record/start")
                {
                    int rx = 0, ry = 0, rw = 0, rh = 0, rf = 10;
                    TryInt(q, "x", out rx); TryInt(q, "y", out ry); TryInt(q, "w", out rw); TryInt(q, "h", out rh); TryInt(q, "fps", out rf);
                    body = RecordStart(rx, ry, rw, rh, rf); // RecordStart 内部: w/h<=0 用全屏, fps 越界用默认
                    if (body.Contains("\"ok\":true")) ShowRecBorder(); // agent 发起的录制也给用户红框可见性
                    Log("[rec] start " + target);
                }
                else if (path == "/record/stop") { body = RecordStop(); CaptureOverlay.CloseRecordHud(); CloseRecBorder(); Log("[rec] stop"); }
                else if (path == "/record/status") { body = RecordStatus(); }
                else if (path == "/app/exit")
                {
                    // 优雅退出: 录制中会先触发 ProcessExit 钩子关 stdin 让 ffmpeg 落盘 (需显式 confirm 防误触)
                    if (!q.ContainsKey("confirm") || q["confirm"] != "1") { code = 400; body = "{\"ok\":false,\"error\":\"need confirm=1\"}"; }
                    else { Log("app exit requested via http"); body = "{\"ok\":true,\"bye\":true}"; new Thread(new ThreadStart(delegate { Thread.Sleep(300); Environment.Exit(0); })) { IsBackground = true }.Start(); }
                }
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
            int pe; if (int.TryParse(Cfg("pick.enabled", "1"), out pe)) pickEnabled = pe == 1 ? 1 : 0;
            Log("runtime settings: dir=" + ShotDir + " clipMax=" + clipMax + " clipEnabled=" + clipEnabled +
                " vol[en=" + volEnabled + " step=" + volStep + " rev=" + volReverse + "]" +
                " pick[en=" + pickEnabled + " ask=" + Cfg("pick.askModel", "GwV4F") + "]");
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

    // 断言写日志监听器: Debug.Assert 失败只落 shot-service.log, 不弹框不挂 UI 线程
    sealed class AssertLogListener : System.Diagnostics.TraceListener
    {
        public override void Write(string message) { Log("ASSERT: " + message); }
        public override void WriteLine(string message) { Log("ASSERT: " + message); }
        public override void Fail(string message, string detailMessage)
        {
            Log("ASSERT FAIL: " + message + (string.IsNullOrEmpty(detailMessage) ? "" : " | " + detailMessage));
        }
    }

    // ==================== 自动提权 (2026-09-07) ====================
    // 起因: 目标机上常有管理员权限运行的应用(WorkBuddy/ZCode 等)。UIPI 会**静默丢弃**普通权限进程
    // 合成的键鼠输入 → 所有 mouse/keyboard 工具返回 ok 却毫无效果(右键菜单不弹/快捷键无反应/点击不换焦点)。
    // 所以本程序必须默认以管理员运行, 否则对管理员窗口等于残废。
    // 体验目标: 完全自动化 —— 用户登录后即以管理员常驻, 新客户装机也不需要任何手工操作。
    //   ① 计划任务已注册 → schtasks /run 静默拉起提权实例, 当前普通实例立即退出 (无 UAC)
    //   ② 任务不存在(首次运行) → runas 提权重启自己(本机 ConsentPromptBehaviorAdmin=0 静默; 客户机弹一次)
    //      提权实例负责注册 ONLOGON 最高权限任务 → 之后永久静默
    //   ③ 任务注册失败 → 退化写注册表 AppCompatFlags\Layers = RUNASADMIN (每次启动请求管理员, 本机仍静默)
    const string TASK_NAME = "WinDesktopHelper";

    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int retLen);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "OpenProcess")] static extern IntPtr OpenProcessLimited(uint access, bool inherit, int pid); // EntryPoint 必须显式指定, 否则找 OpenProcessLimited 这个不存在的导出
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] static extern bool CloseHandleX(IntPtr h);

    static bool TokenElevated(IntPtr hProc)
    {
        IntPtr t;
        if (!OpenProcessToken(hProc, 0x0008, out t)) return false;
        try
        {
            int e = 0, len = 0;
            IntPtr buf = Marshal.AllocHGlobal(4);
            bool ok = false;
            try { ok = GetTokenInformation(t, 20, buf, 4, out len); if (ok) e = Marshal.ReadInt32(buf); }
            finally { Marshal.FreeHGlobal(buf); }
            return ok && e != 0;
        }
        finally { try { CloseHandleX(t); } catch { } }
    }
    static bool IsElevated()
    {
        try { return TokenElevated(Process.GetCurrentProcess().Handle); } catch { return false; }
    }
    // 判断别的进程是否管理员: 打不开句柄(权限不足/已退出)一律返回 false — 只用来做"已有一个提权实例就别抢"的保守判断
    static bool IsProcessElevated(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcessLimited(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return false;
            return TokenElevated(h);
        }
        catch { return false; }
        finally { if (h != IntPtr.Zero) try { CloseHandleX(h); } catch { } }
    }

    static bool RunSchtasks(string args, out string outText)
    {
        outText = "";
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                outText = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
        }
        catch (Exception ex) { outText = ex.Message; return false; }
    }
    static bool TaskExists() { string o; return RunSchtasks("/query /tn \"" + TASK_NAME + "\"", out o); }
    static bool TaskCreate(string exe)
    {
        string o;
        bool ok = RunSchtasks("/create /tn \"" + TASK_NAME + "\" /tr \"\\\"" + exe + "\\\"\" /sc ONLOGON /rl HIGHEST /f", out o);
        Log("schtasks create -> " + ok + (ok ? "" : (" : " + o.Trim().Replace("\r", " ").Replace("\n", " "))));
        return ok;
    }
    // 兜底: 注册表兼容性层 RUNASADMIN (进程每次启动都会请求管理员, 本机策略=0 时静默)
    static bool MarkRunAsAdmin(string exe)
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
            {
                if (k == null) return false;
                k.SetValue(exe, "RUNASADMIN", Microsoft.Win32.RegistryValueKind.String);
                Log("fallback: AppCompatFlags Layers RUNASADMIN set for " + exe);
                return true;
            }
        }
        catch (Exception ex) { Log("MarkRunAsAdmin err: " + ex.Message); return false; }
    }
    static bool RelaunchAsAdmin(string[] args)
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            string a = "";
            if (args != null) foreach (string s in args) if (!string.IsNullOrEmpty(s)) a += (a == "" ? "" : " ") + "\"" + s + "\"";
            psi.Arguments = a;
            Process.Start(psi);
            Log("relaunch as admin requested (verb=runas)");
            return true;
        }
        catch (Exception ex) { Log("relaunch as admin failed: " + ex.Message); return false; }
    }

    // 输入拦截预检: 目标窗口是管理员权限、而自己是普通权限 → 系统会把合成输入**静默丢掉**,
    // 不检测的话工具会返回 ok 而实际什么都没发生(2026-09-07 实测: 右键开始菜单/Win+X/Alt+F4 全废却全报 ok)
    static string UipiCheck(IntPtr targetHwnd)
    {
        if (IsElevated()) return null;                 // 自己是管理员, 任何窗口都能操作
        if (targetHwnd == IntPtr.Zero) return null;
        int pid = 0;
        try
        {
            GetWindowThreadProcessId(targetHwnd, out pid);
            if (pid <= 0 || pid == Process.GetCurrentProcess().Id) return null;
            if (!IsProcessElevated(pid)) return null;
        }
        catch { return null; }
        return "{\"ok\":false,\"error\":\"uipi blocked: 目标窗口(pid=" + pid + ")以管理员权限运行, 而本程序是普通权限 —— 系统会把合成的键鼠输入静默丢弃(工具会假报 ok 但毫无效果)\"" +
               ",\"fix\":\"以管理员重启 shot-service: 结束进程后从资源管理器运行一次(程序会自动提权), 或命令行 schtasks /run /tn WinDesktopHelper\"" +
               ",\"hint\":\"本程序默认应自动以管理员常驻; 出现本错误说明提权失败(如 UAC 被拒绝)\"}";
    }
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point p);

    [STAThread]
    public static void Main(string[] args)
    {
        // DPI 感知必须最先设: 任何窗口创建之后再设会静默失败 (时好时坏的根源)。
        // 优先 Per-Monitor V2 (不绑定启动时机, 任何时刻启动都按当前显示器真实 DPI 渲染)。
        // (2026-09-07 踩坑: 改为开机计划任务提权启动后, 启动太早, 旧的 SetProcessDPIAware 绑错 DPI,
        //  整个进程所有窗口被系统拉大——托盘菜单/设置窗全都巨大。PMv2 一刀根治)
        try
        {
            if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) throw new InvalidOperationException("pmv2 rejected");
        }
        catch
        {
            try { if (SetProcessDpiAwareness(2) != 0) throw new InvalidOperationException("shcore rejected"); }
            catch { try { SetProcessDPIAware(); } catch { } }
        }

        // 断言不再弹模态框 (服务弹 Debug.Assert 框会挂死整个 UI 线程, 还读不到内容) — 全部改写日志
        try
        {
            System.Diagnostics.Trace.AutoFlush = true;
            System.Diagnostics.Trace.Listeners.Clear();
            System.Diagnostics.Trace.Listeners.Add(new AssertLogListener());
        }
        catch { }
        // 构建指纹: exe 文件的修改时间+大小 = 用户编译时刻。跑的是不是刚编的, 一眼可验 (堵"改了没变化"坑)
        string build = BuildStamp();
        bool allowTray = true, watchMode = false;
        foreach (string a in args)
        {
            string x = (a ?? "").ToLowerInvariant();
            if (x == "-notray") allowTray = false;
            else if (x == "-watch") watchMode = true;
        }

        // ---- 自动提权: 必须以管理员运行, 否则对管理员窗口(WorkBuddy/ZCode 等)的键鼠输入会被 UIPI 静默丢弃 ----
        bool elevated = IsElevated();
        string selfExe = "";
        try { selfExe = Process.GetCurrentProcess().MainModule.FileName; } catch { }
        if (!elevated)
        {
            if (!string.IsNullOrEmpty(selfExe) && TaskExists())
            {
                // 计划任务已注册 → 静默拉起提权实例(无 UAC), 当前普通实例退出
                // 自愈: 若 90s 内已 /run 过却仍是普通权限, 说明任务机制在本机失效(被禁用/组策略限制)
                //        → 自动退化成注册表 RUNASADMIN 再重启(老大给 WorkBuddy/ZCode 提权用的就是这招)
                long last = 0;
                try
                {
                    using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\WinDesktopHelper"))
                    { if (rk != null) long.TryParse((rk.GetValue("LastTaskRun") ?? "0").ToString(), out last); }
                }
                catch { }
                long nowTicks = DateTime.Now.Ticks;
                bool ranRecently = last > 0 && (nowTicks - last) < TimeSpan.TicksPerSecond * 90;
                if (!ranRecently)
                {
                    try
                    {
                        using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\WinDesktopHelper"))
                        { if (rk != null) rk.SetValue("LastTaskRun", nowTicks.ToString(), Microsoft.Win32.RegistryValueKind.String); }
                    }
                    catch { }
                    Log("not elevated & task exists -> schtasks /run (silent elevate), 本实例退出");
                    string so; RunSchtasks("/run /tn \"" + TASK_NAME + "\"", out so);
                    return;
                }
                Log("schtasks /run 已试过但仍未提权 -> 任务机制失效, 退化 RUNASADMIN 并重启");
                MarkRunAsAdmin(selfExe);
                if (RelaunchAsAdmin(args)) return;
            }
            Log("not elevated & no task -> relaunch as admin (首次一次 UAC; 本机 ConsentPromptBehaviorAdmin=0 静默)");
            if (RelaunchAsAdmin(args)) return;
            Log("WARNING: 提权失败, 降级为普通权限运行 —— 对管理员窗口的键鼠操作会被系统静默丢弃");
        }
        else if (!string.IsNullOrEmpty(selfExe) && !TaskExists())
        {
            // 已提权但任务未注册(首次) → 注册 ONLOGON 最高权限任务, 之后登录即静默管理员常驻; 失败则退化注册表 RUNASADMIN
            if (!TaskCreate(selfExe)) MarkRunAsAdmin(selfExe);
        }

        // 单实例互斥: 已有实例则自动顶替(结束旧进程后接管) — 堵死"忘了 Stop-Process, 新 exe 静默退出,
        // 用户跑的还是旧代码"这个反复踩坑的部署漏洞 (2026-09-05)
        bool createdNew;
        try
        {
            instanceMutex = new Mutex(true, MUTEX_NAME, out createdNew);
            if (!createdNew)
            {
                // 已有一个管理员实例在跑、而自己是普通实例 → 别抢(抢了会把提权实例 kill 掉, 反而降级)
                bool otherElevated = false;
                try
                {
                    foreach (Process p in Process.GetProcessesByName("shot-service"))
                    {
                        if (p.Id == Process.GetCurrentProcess().Id) continue;
                        if (IsProcessElevated(p.Id)) { otherElevated = true; break; }
                    }
                }
                catch { }
                if (otherElevated && !elevated) { Log("已有一个管理员实例在运行, 本普通实例退出(不接管, 避免把提权实例顶掉)"); return; }
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

        // DPI 感知已在 Main 最顶设置 (必须在任何窗口创建之前, 见文件头说明)
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
            PickInit(); // 划词悬浮球(豆包替代): 专属 STA 线程, 剪贴板取词需要 STA
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
            OcrWarmup(); // X2: 后台预热, 首次 /ocr 不再冷启动超时
            Log("clipboard watcher started");
        }
        catch (Exception ex) { Log("clip watcher thread err: " + ex.Message); }

        if (allowTray)
        {
            try { InitTray(); } catch (Exception ex) { Log("InitTray FAILED (服务继续可用, 只是没托盘): " + ex.Message); }
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
                    string wj = WindowJsonByTitle(McpParam(a, "title"), McpParam(a, "process"));
                    return wj == null ? McpText("{\"ok\":false,\"error\":\"window not found\"}", true) : McpText(wj, false);
                }
                case "active_window": return McpText(ActiveWindowJson(), false);
                case "monitors": return McpText(MonitorsJson(), false);
                case "mouse_move": { int x = McpParamInt(a, "x"), y = McpParamInt(a, "y"); MouseMove(x, y); return McpText("{\"ok\":true,\"x\":" + x + ",\"y\":" + y + "}", false); }
                case "mouse_click":
                {
                    string button = McpParam(a, "button"); if (button == "") button = "left";
                    bool dbl = McpParam(a, "double") == "1";
                    bool triple = McpParam(a, "triple") == "1";
                    string mods = McpParam(a, "mods");
                    if (McpParam(a, "x") != "" && McpParam(a, "y") != "") MouseMove(McpParamInt(a, "x"), McpParamInt(a, "y"));
                    byte[] mvks = ModsVks(mods);
                    foreach (byte vk in mvks) keybd_event(vk, 0, 0, UIntPtr.Zero);
                    MouseClick(button, triple ? 3 : (dbl ? 2 : 1));
                    for (int i = mvks.Length - 1; i >= 0; i--) keybd_event(mvks[i], 0, 2, UIntPtr.Zero);
                    return McpText("{\"ok\":true,\"button\":\"" + button + "\"" + (dbl ? ",\"double\":true" : "") + (triple ? ",\"triple\":true" : "") + (mods != "" ? ",\"mods\":\"" + JsonEscape(mods) + "\"" : "") + "}", false);
                }
                case "mouse_scroll":
                {
                    int d = McpParamInt(a, "delta");
                    string mx = McpParam(a, "x"), my = McpParam(a, "y");
                    if (mx != "" && my != "") MouseMove(int.Parse(mx), int.Parse(my));
                    MouseScroll(d);
                    return McpText("{\"ok\":true,\"delta\":" + d + (mx != "" ? ",\"x\":" + mx + ",\"y\":" + my : "") + "}", false);
                }
                case "keyboard_type": { string t = McpParam(a, "text"); int mnl = 0; foreach (char cc in t) if (cc == '\n') mnl++; TypeText(t, McpParam(a, "nl")); return McpText("{\"ok\":true,\"chars\":" + t.Length + ",\"newlines\":" + mnl + ",\"front\":" + FrontBriefJson() + "}", false); }
                case "keyboard_press": { string k = McpParam(a, "keys"); PressCombo(k); return McpText("{\"ok\":true,\"keys\":\"" + JsonEscape(k) + "\"}", false); }
                case "app_run":
                {
                    int waitMs = 0; int.TryParse(McpParam(a, "wait"), out waitMs);
                    return McpText(AppRun(McpParam(a, "path"), McpParam(a, "args"), waitMs, McpParam(a, "process")), false);
                }
                case "app_restore":
                {
                    long rh2 = 0; int rw2 = 0;
                    long.TryParse(McpParam(a, "hwnd"), out rh2);
                    int.TryParse(McpParam(a, "wait"), out rw2);
                    return McpText(AppRestore(McpParam(a, "process"), McpParam(a, "title"), rh2, McpParam(a, "snap"), rw2), false);
                }
                case "taskbar_volume":
                {
                    // 任务栏滚轮调音量: enabled=0|1(开关) step=音量步进 reverse=1 反向; 不带参会查询状态
                    if (McpParam(a, "enabled") == "0" || McpParam(a, "enabled") == "1") volEnabled = McpParam(a, "enabled") == "1" ? 1 : 0;
                    if (McpParam(a, "step") != "") { int v; if (int.TryParse(McpParam(a, "step"), out v) && v >= 1 && v <= 20) volStep = v; }
                    if (McpParam(a, "reverse") == "1") volReverse = 1;
                    if (McpParam(a, "reverse") == "0") volReverse = 0;
                    return McpText("{\"ok\":true,\"enabled\":" + volEnabled + ",\"reverse\":" + volReverse + ",\"step\":" + volStep + ",\"taskbarWnds\":" + taskbarWnds.Length + "}", false);
                }
                case "pick_config":
                {
                    // 划词悬浮球: enabled=0|1 开关(立即生效); askEndpoint/askKey/askModel 改「问AI」后端; askPrompt 附加提示词; 不带参=查询
                    // 写回 shot-service.json 持久化(设置页同步可见)。任一参数留空=不改动该项
                    int pen = -1; string ev = McpParam(a, "enabled");
                    if (ev == "0") pen = 0; else if (ev == "1") pen = 1;
                    return McpText(PickConfig(pen, McpParam(a, "askEndpoint"), McpParam(a, "askKey"), McpParam(a, "askModel"), McpParam(a, "askPrompt"), 1), false);
                }
                case "ocr_image": return McpText(OcrFile(McpParam(a, "path"), McpParamInt(a, "wait")), false);
                case "pin_image":
                {
                    int px = -1, py = -1; int.TryParse(McpParam(a, "x"), out px); int.TryParse(McpParam(a, "y"), out py);
                    return McpText(PinFile(McpParam(a, "path"), px, py), false);
                }
                case "clipboard_get": return McpText(ClipboardGet(), false);
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
                    if (verb == "listall")
                    {
                        int lpid = McpParamInt(a, "pid");
                        return McpText(WinListAll((uint)Math.Max(0, lpid)), false);
                    }
                    if (verb == "list")
                    {
                        int lp = McpParamInt(a, "pid");
                        if (lp > 0) return McpText(WinListByPid(lp), false);
                        string kw = McpParam(a, "title");
                        if (kw != "") return McpText(WinListByTitle(kw), false);
                        return McpText(AppList(), false);
                    }
                    IntPtr wh = IntPtr.Zero;
                    long hv = 0; long.TryParse(McpParam(a, "hwnd"), out hv);
                    if (hv > 0)
                    {
                        wh = new IntPtr(hv);
                        if (!IsWindow(wh)) return McpText("{\"ok\":false,\"error\":\"hwnd invalid: 窗口已关闭或句柄已失效, 请重新 list_apps 采样\"}", true);
                    }
                    else
                    {
                        string ttl2 = McpParam(a, "title");
                        if (ttl2 == "") return McpText("{\"ok\":false,\"error\":\"need hwnd or title: 推荐 hwnd(list_apps 采样), title 会变且可能误匹配\"}", true);
                        wh = FindWindowByTitle(ttl2);
                    }
                    if (wh == IntPtr.Zero) return McpText("{\"ok\":false,\"error\":\"window not found: 标题没匹配到, 建议改用 hwnd(list_apps 采样)\"}", true);
                    if (verb == "activate") return McpText(WinActivate(wh), false);
                    if (verb == "max") return McpText(WinShow(wh, SW_MAXIMIZE, "maximized"), false);
                    if (verb == "min") return McpText(WinShow(wh, SW_MINIMIZE, "minimized"), false);
                    if (verb == "restore") return McpText(WinShow(wh, SW_RESTORE, "restored"), false);
                    if (verb == "close") return McpText(WinClose(wh), false);
                    if (verb == "snap") return McpText(WinSnap(wh, McpParam(a, "pos"), McpParam(a, "monitor"),
                        McpParamInt(a, "cols"), McpParamInt(a, "col"), McpParamInt(a, "colspan"),
                        McpParamInt(a, "rows"), McpParamInt(a, "row"), McpParamInt(a, "rowspan")), false);
                    if (verb == "move") return McpText(WinMove(wh, McpParamInt(a, "x"), McpParamInt(a, "y"), McpParamInt(a, "w"), McpParamInt(a, "h")), false);
                    return McpText("unknown verb (activate/max/min/restore/close/move/wait/list)", true);
                }
                case "tray_click":
                    return McpText(TrayClick(McpParam(a, "name"), McpParam(a, "button") == "" ? "left" : McpParam(a, "button"),
                        McpParam(a, "double") == "1" || McpParam(a, "double") == "true"), false);
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
                    return McpText(UiCall("tree", delegate { return UiTree(q2); }, 8000), false);
                }
                case "ui_click":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "i", "name", "type", "ref", "mode", "verify", "nohit", "expect", "force" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiCall("click", delegate { return UiClick(q2); }, 8000), false);
                }
                case "ui_find":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "name", "type" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiCall("find", delegate { return UiFind(q2); }, 8000), false);
                }
                case "ui_select":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "i", "name", "start", "end" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiCall("select", delegate { return UiSelect(q2); }, 8000), false);
                }
                case "ui_set":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "i", "name", "value" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiCall("set", delegate { return UiSet(q2); }, 8000), false);
                }
                case "ui_read":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "i", "name" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiCall("read", delegate { return UiRead(q2); }, 8000), false);
                }
                case "ui_readall":
                {
                    Dictionary<string, string> q2 = new Dictionary<string, string>();
                    foreach (var kv in new[] { "title", "hwnd", "max" }) { string v = McpParam(a, kv); if (v != "") q2[kv] = v; }
                    return McpText(UiCall("readall", delegate { return UiReadAll(q2); }, 8000), false);
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
            "{\"name\":\"window_info\",\"description\":\"按窗口标题/进程名查询窗口 {hwnd,title,process,rect}，操作前定位用。优先级: 标题全等>标题前缀>标题包含>仅进程名。警告: title 模糊匹配会误伤(如 title=微信 命中含该词的浏览器标签页), 建议同时给 process 或先用 list_apps 拿 hwnd。查不到返回 ok:false\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"process\":{\"type\":\"string\",\"description\":\"进程名过滤, 如 Weixin / msedge, 忽略大小写可带 .exe\"}}}}," +
            "{\"name\":\"active_window\",\"description\":\"获取当前活动窗口信息 {title,process,rect}\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"monitors\",\"description\":\"列出显示器元数据（分辨率/主屏/设备名）\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"mouse_move\",\"description\":\"移动鼠标到物理像素坐标\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\"]}}," +
            "{\"name\":\"mouse_click\",\"description\":\"点击。button=left|right|middle，double=1 双击，triple=1 三击选整行(坐标务必行内 rect.x+20 以上, 左边缘2px触发全选实测坑)，mods=shift/ctrl/alt 按住修饰键点击(选范围/多选)\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"button\":{\"type\":\"string\"},\"double\":{\"type\":\"number\"},\"triple\":{\"type\":\"number\"},\"mods\":{\"type\":\"string\"}}}}," +
            "{\"name\":\"mouse_scroll\",\"description\":\"滚轮：正数=向上滚，负数=向下滚（典型 ±120/格）。可选 x,y 先移动到目标坐标再滚\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"delta\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}},\"required\":[\"delta\"]}}," +
            "{\"name\":\"keyboard_type\",\"description\":\"向当前聚焦输入框打字。中文/emoji 直接支持（Unicode 事件，不依赖输入法）。≤2000 字符\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"text\":{\"type\":\"string\"},\"nl\":{\"type\":\"string\"}},\"required\":[\"text\"]}}," +
            "{\"name\":\"keyboard_press\",\"description\":\"按组合键，如 ctrl+shift+a / enter / alt+f4 / win / ctrl+s\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"keys\":{\"type\":\"string\"}},\"required\":[\"keys\"]}}," +
            "{\"name\":\"app_run\",\"description\":\"运行程序/打开（exe/快捷方式/URL）。GUI 会在用户桌面可见。多进程应用(微信/Electron)启动后会换进程换窗, 返回的 hwnd 可能是过渡态: 建议 wait=3000 + process=进程名, 服务端会等窗口 rect 稳定后再返回并带 stable 标记\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\"},\"args\":{\"type\":\"string\"},\"wait\":{\"type\":\"number\",\"description\":\"找到窗口后额外等待稳定的毫秒数(建议 3000), 0=不等待(旧行为)\"},\"process\":{\"type\":\"string\",\"description\":\"只认该进程名的窗口, 如 Weixin\"}},\"required\":[\"path\"]}}," +
            "{\"name\":\"taskbar_volume\",\"description\":\"任务栏滚轮调音量（常驻功能）。enabled=0/1 开关，step=每次滚轮音量变化百分比(1-20,默认2)，reverse=1 反向(滚轮上=减小)。不带参返回当前状态。\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"enabled\":{\"type\":\"number\"},\"step\":{\"type\":\"number\"},\"reverse\":{\"type\":\"number\"}}}}," +
            "{\"name\":\"pick_config\",\"description\":\"划词悬浮球配置。enabled=0/1 开关划词功能(立即生效并持久化); askEndpoint/askKey/askModel 改「问AI」后端(默认 litellm :4000 / GwV4F); askPrompt 问AI附加提示词(定制回答风格, 传 | 清空回默认)。不带参返回当前状态。翻译引擎在设置页「翻译」节配置。\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"enabled\":{\"type\":\"number\"},\"askEndpoint\":{\"type\":\"string\"},\"askKey\":{\"type\":\"string\"},\"askModel\":{\"type\":\"string\"},\"askPrompt\":{\"type\":\"string\"}}}}," +
            "{\"name\":\"clipboard_history\",\"description\":\"读取剪贴板历史（常驻监听，最多50条，最新在前）。limit=返回条数(可选)。给AI复用刚复制的内容。\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"limit\":{\"type\":\"number\"}}}}," +
            "{\"name\":\"win_manage\",\"description\":\"窗口管理。verb=activate|max|min|restore|close|move|wait|list。activate置前台(先解除最小化)；move需x,y,w,h；wait轮询等title窗口出现(timeout毫秒,上限60s)；list按pid列窗口\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"verb\":{\"type\":\"string\"},\"hwnd\":{\"type\":\"number\",\"description\":\"窗口句柄, 优先于 title (list_apps 采样, 最可靠)\"},\"title\":{\"type\":\"string\"},\"pid\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"w\":{\"type\":\"number\"},\"h\":{\"type\":\"number\"},\"timeout\":{\"type\":\"number\"}},\"required\":[\"verb\"]}," +
            "{\"name\":\"mouse_down\",\"description\":\"按住鼠标键不松。button=left(默认)/right/middle。与mouse_up配对可自定义拖拽\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"button\":{\"type\":\"string\"}}}," +
            "{\"name\":\"mouse_up\",\"description\":\"松开鼠标键。button=left(默认)/right/middle\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"button\":{\"type\":\"string\"}}}," +
            "{\"name\":\"mouse_drag\",\"description\":\"左键拖拽一条龙: 从x1,y1按住平滑拖到x2,y2再松开。ms=总时长毫秒(默认300)\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"x1\":{\"type\":\"number\"},\"y1\":{\"type\":\"number\"},\"x2\":{\"type\":\"number\"},\"y2\":{\"type\":\"number\"},\"ms\":{\"type\":\"number\"}},\"required\":[\"x1\",\"y1\",\"x2\",\"y2\"]}," +
            "{\"name\":\"mouse_pos\",\"description\":\"查当前鼠标光标物理像素坐标\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}," +
            "{\"name\":\"keyboard_hold\",\"description\":\"按住组合键ms毫秒再松开(如按住win拖窗口)。keys格式同keyboard_press\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"keys\":{\"type\":\"string\"},\"ms\":{\"type\":\"number\"}},\"required\":[\"keys\"]}," +
            "{\"name\":\"clipboard_set\",\"description\":\"写文本到剪贴板(替代手动复制)\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}," +
            "{\"name\":\"clipboard_get\",\"description\":\"直读当前剪贴板(多格式): text=文本/image=PNG路径+md5(Read看图/OCR通道)/files=文件路径列表\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}" +
            "{\"name\":\"ocr_image\",\"description\":\"对截图文件跑OCR(qwen3-vl本地)。path=PNG(须截图目录内), 返回chars+text; 可选wait毫秒\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\"},\"wait\":{\"type\":\"number\"}},\"required\":[\"path\"]}}" +
            "{\"name\":\"pin_image\",\"description\":\"图片文件钉到桌面(贴图窗,同截图工具条贴图): 左键拖动/滚轮缩放/双击关闭。path=PNG, x/y缺省居中\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}},\"required\":[\"path\"]}}" +
            "{\"name\":\"ui_readall\",\"description\":\"批量读整棵元素树的 Name/Value(输入框内容)/类型 — 找输入框里的值/页面文本时用这个, 比 ui_read 逐个快。title=窗口标题, max=上限(默认300)\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"hwnd\":{\"type\":\"number\"},\"max\":{\"type\":\"number\"}}}," +
            "{\"name\":\"record_start\",\"description\":\"开始录屏(抓屏管道喂ffmpeg出MP4 h264)。x,y,w,h=区域(默认全屏), fps=帧率(默认10,上限30)。无音频。用 record_stop 结束\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"w\":{\"type\":\"number\"},\"h\":{\"type\":\"number\"},\"fps\":{\"type\":\"number\"}}}}," +
            "{\"name\":\"record_stop\",\"description\":\"停止录屏, 返回 MP4 文件路径\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"record_status\",\"description\":\"查录屏状态(是否在录/已录秒数/文件)\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"app_runas\",\"description\":\"以管理员运行程序(触发UAC, 用户点确认才执行; AI无法静默提权)。path=程序, args=参数\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\"},\"args\":{\"type\":\"string\"}},\"required\":[\"path\"]}}," +
            "{\"name\":\"ui_click\",\"description\":\"按ui_tree索引语义点击元素(Invoke/Toggle/Expand/Select优先,无模式退坐标中心)。title=窗口标题,i=元素索引\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"hwnd\":{\"type\":\"number\"},\"i\":{\"type\":\"number\"}},\"required\":[\"i\"]}," +
            "{\"name\":\"ui_set\",\"description\":\"按索引直接写输入框值(ValuePattern,不走键盘输入法)。title=窗口标题,i=索引,value=文本\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"hwnd\":{\"type\":\"number\"},\"i\":{\"type\":\"number\"},\"value\":{\"type\":\"string\"}},\"required\":[\"i\",\"value\"]}," +
            "{\"name\":\"ui_read\",\"description\":\"按索引读元素Name/Value/类名/类型(比OCR准)。title=窗口标题,i=索引\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"hwnd\":{\"type\":\"number\"},\"i\":{\"type\":\"number\"}},\"required\":[\"i\"]}," +
            "{\"name\":\"get_skill\",\"description\":\"【必须先调用】获取本服务 SKILL 操作手册（铁律/避坑/流程）。所有工具首次调用前强制先读本 SKILL，否则报错。踩坑必须 update_skill 写回，禁止只写记忆。\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}}," +
            "{\"name\":\"update_skill\",\"description\":\"【踩坑必写】把新踩坑经验写回共享 SKILL.md（全体 agent 共享，立即生效）。title=小节标题，entry=markdown 正文\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"title\":{\"type\":\"string\"},\"entry\":{\"type\":\"string\"}},\"required\":[\"title\",\"entry\"]}}," +
            "{\"name\":\"tray_click\",\"description\":\"点击系统托盘/任务栏图标(托盘应用窗口失踪时用它唤回主窗)。name=图标名含糊匹配, button=left/right, double=1 双击(多数托盘应用双击开主窗)。点击后重新 window_info(process=...) 或 win_manage listall 验证窗口是否出现\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"name\":{\"type\":\"string\"},\"button\":{\"type\":\"string\"},\"double\":{\"type\":\"number\"}},\"required\":[\"name\"]}}" +
            "]";
    }

    // ---- 托盘（默认显示；右键菜单；隐藏后重启服务恢复） ----
    // NotifyIcon.Text 有 63 字符硬上限, 超了直接抛异常 -> 整个托盘初始化失败 -> 服务起不来 (2026-09-07 踩过: 加了【管理员】标记就超了)
    // 所以这里统一截断, 宁可少显示几个字符也绝不让它把服务带崩
    static string TrayTip()
    {
        string s = "Win Desktop Helper v" + APP_VERSION + (IsElevated() ? "【管理员】" : "【普通权限】") + "\nbuild " + BuildStamp() + " | :18800";
        return s.Length > 62 ? s.Substring(0, 62) : s;
    }
    static void InitTray()
    {
        TrayIcon = new NotifyIcon();
        TrayIcon.Icon = BuildIcon();
        TrayIcon.Text = TrayTip(); // NotifyIcon.Text 超 63 字符会抛异常崩掉托盘初始化, 统一走 TrayTip() 截断
        TrayIcon.Visible = true;
        ContextMenuStrip menu = new ContextMenuStrip();
        // 版本行带构建指纹: 打开菜单一眼确认跑的是不是刚编的 exe (部署自验证)
        menu.Items.Add("v" + APP_VERSION + "  " + (IsElevated() ? "管理员" : "普通权限(受限)") + "  build " + BuildStampShort(), null, null).Enabled = false; // 只读版本+权限显示 (短: 菜单不拉宽; 完整信息悬停图标看 tooltip)

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
        // 剪贴板历史入口: 只有热键太隐蔽(用户找不到/忘了键位), 菜单里给个显眼入口, 并显示当前实际生效的热键
        menu.Items.Add("剪贴板历史 (" + (clipHotkeyName == "" ? "Ctrl+Alt+V" : clipHotkeyName) + ")", null, delegate
        {
            try { ShowClipHistory(); } catch (Exception ex) { Log("clip history menu err: " + ex.Message); }
        });

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

        // 录屏停止入口 (退出程序不再等于录屏失控: 录制中 ProcessExit 兜底落盘; 这里给显式停止)
        ToolStripMenuItem mRecStop = new ToolStripMenuItem("⏹ 停止录制", null, delegate
        {
            try
            {
                string r = RecordStop();
                CaptureOverlay.CloseRecordHud();
                CloseRecBorder();
                Log("record stop via tray: " + r);
                string file = "";
                int p1 = r.IndexOf("\"file\":\"");
                if (p1 >= 0) { int p2 = r.IndexOf("\"", p1 + 9); file = r.Substring(p1 + 9, p2 - p1 - 9); }
                TrayNotify("录屏已保存", string.IsNullOrEmpty(file) ? r : file);
            }
            catch (Exception ex) { Log("tray rec stop err: " + ex.Message); }
        });
        menu.Items.Add(mRecStop);
        menu.Opening += delegate { mRecStop.Enabled = recording; }; // 打开菜单时按录制状态亮/灰

        menu.Items.Add("隐藏托盘图标", null, delegate { TrayIcon.Visible = false; Log("tray hidden (restart service to show again)"); });
        menu.Items.Add("退出服务", null, delegate { Log("tray exit requested"); Environment.Exit(0); });
        TrayIcon.ContextMenuStrip = menu;
        TrayIcon.DoubleClick += delegate
        {
            try { string fp = DoShot(VirtualScreen()); Log("tray dblclick shot: " + fp); Clipboard.SetText(fp); }
            catch (Exception ex) { Log("tray dblclick err: " + ex.Message); }
        };
        // 启动气泡报构建指纹: 用户每次启动肉眼确认"跑的是不是刚编的" (部署自验证)
        TrayNotify("已启动 v" + APP_VERSION + (IsElevated() ? "（管理员）" : "（普通权限，受限）"), "build " + BuildStamp() + " — 与编译时间一致即新代码生效");
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