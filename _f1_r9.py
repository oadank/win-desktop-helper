# -*- coding: utf-8 -*-
# F1: 遮罩失焦自关 (防滞留+busy DoS); R9-1: 文件句柄 nNumberOfLinks>1 拒硬链接
p = 'shot-capture.cs'
s = open(p, 'rb').read().decode('utf-8')

# ---- F1: OnDeactivate ----
old = '''        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (IsDisposed) return;
            if (e.Button == MouseButtons.Right)'''
new = '''        // F1 (workbuddy R9): 遮罩失焦(被别进程抢焦点/UAC/通知) → Esc 打不到本窗, 热键 busy,ignore → 滞留全屏糊罩 DoS。
        // 修法: 失焦且前台属别进程 → 自动关闭; textMode(输入法候选窗夺焦)/自家浮窗(结果/录屏菜单/历史窗) 不触发
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            try
            {
                if (IsDisposed || textMode) return;
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Handle) return;
                uint pid; GetWindowThreadProcessId(fg, out pid);
                if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return; // 自家浮窗短暂夺焦
                Log("capture: focus stolen by pid " + pid + ", auto-close to avoid stuck overlay");
                CancelAll();
            }
            catch (Exception ex) { Log("deactivate err: " + ex.Message); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (IsDisposed) return;
            if (e.Button == MouseButtons.Right)'''
assert old in s, 'onmousedown anchor'
s = s.replace(old, new, 1)
open(p, 'wb').write(s.encode('utf-8'))
print('F1 patched')

# ---- R9-1: link count ----
p2 = 'shot-service.cs'
t = open(p2, 'rb').read().decode('utf-8')
old = '''    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandleW(IntPtr h);'''
new = '''    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandleW(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(IntPtr h, out BY_HANDLE_FILE_INFORMATION info);
    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    // R9-1: NTFS 硬链接无 ReparsePoint 属性位、GetFullPath 也看不出 (它真的"在"目录里) — 唯一可靠信号=link count>1
    static int GetHardLinkCount(string path)
    {
        try
        {
            IntPtr h = CreateFileW(path, 0x8000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h == new IntPtr(-1)) return -1;
            try
            {
                BY_HANDLE_FILE_INFORMATION fi;
                return GetFileInformationByHandle(h, out fi) ? (int)fi.nNumberOfLinks : -1;
            }
            finally { CloseHandleW(h); }
        }
        catch { return -1; }
    }'''
assert old in t, 'closehandle anchor'
t = t.replace(old, new, 1)

old = '''            string full;
            try { full = System.IO.Path.GetFullPath(path); } catch { why = "bad path"; return false; }
            if (!System.IO.File.Exists(full)) { why = "file not found"; return false; }'''
new = '''            string full;
            try { full = System.IO.Path.GetFullPath(path); } catch { why = "bad path"; return false; }
            if (!System.IO.File.Exists(full)) { why = "file not found"; return false; }
            if (GetHardLinkCount(full) > 1) { why = "hardlink (nNumberOfLinks>1) 拒绝"; return false; } // R9-1'''
assert old in t, 'safe path head'
t = t.replace(old, new, 1)
open(p2, 'wb').write(t.encode('utf-8'))
print('R9-1 patched')
