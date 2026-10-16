# 真实按键链路 + 卡键 + 剪贴板占用 体检
Add-Type -AssemblyName System.Windows.Forms
$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Kb {
  [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern IntPtr GetOpenClipboardWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr h);
  [DllImport("user32.dll")] public static extern bool CloseClipboard();
  public const int VK_CONTROL=0x11, VK_LCONTROL=0xA2, VK_RCONTROL=0xA3, VK_SHIFT=0x10, VK_MENU=0x12, VK_LWIN=0x5B;
  public const uint KEYUP=0x2, KEYDOWN=0x0;
  public static void ReleaseAll() {
    foreach (byte k in new byte[]{VK_CONTROL,VK_LCONTROL,VK_RCONTROL,VK_SHIFT,0xA0,0xA1,VK_MENU,0xA4,0xA5,VK_LWIN,0x5C})
      keybd_event(k, 0, KEYUP, UIntPtr.Zero);
  }
}
'@
Add-Type -TypeDefinition $src -ErrorAction Stop

Write-Host "=== 1. 修饰键状态 (1 位=按下) ==="
foreach ($k in @('VK_CONTROL','VK_LCONTROL','VK_RCONTROL','VK_SHIFT','VK_MENU','VK_LWIN')) {
  $code = switch ($k) {
    'VK_CONTROL' { 0x11 } 'VK_LCONTROL' { 0xA2 } 'VK_RCONTROL' { 0xA3 }
    'VK_SHIFT' { 0x10 } 'VK_MENU' { 0x12 } 'VK_LWIN' { 0x5B }
  }
  $st = [Kb]::GetKeyState($code)
  $down = ($st -band 0x8000) -ne 0
  Write-Host "$k down=$down state=$st"
}

Write-Host "=== 2. 强制松开所有修饰键 ==="
[Kb]::ReleaseAll()
Start-Sleep -Milliseconds 150
Write-Host ("Ctrl still down? " + (([Kb]::GetKeyState(0x11) -band 0x8000) -ne 0))

Write-Host "=== 3. 剪贴板占用者 ==="
$h = [Kb]::GetOpenClipboardWindow()
Write-Host "openWnd=$h"
if ($h -ne [IntPtr]::Zero) {
  $p = 0
  [void][Kb]::GetWindowThreadProcessId($h, [ref]$p)
  $n = "?"
  try { $n = (Get-Process -Id $p -ErrorAction Stop).ProcessName } catch {}
  Write-Host "  holder pid=$p proc=$n"
}

Write-Host "=== 4. 真实按键: 记事本里 Ctrl+A / Ctrl+C / Ctrl+V ==="
$p = Start-Process notepad -PassThru
Start-Sleep -Seconds 1
# 激活记事本
$p.Refresh()
Add-Type -Name N -Namespace U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);'
[U.N]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
# 输入测试文字
[System.Windows.Forms.SendKeys]::SendWait("KEYTEST-{F12}")
Start-Sleep -Milliseconds 200
# Ctrl+A 全选, Ctrl+C 复制
[System.Windows.Forms.SendKeys]::SendWait("^a")
Start-Sleep -Milliseconds 150
[System.Windows.Forms.SendKeys]::SendWait("^c")
Start-Sleep -Milliseconds 300
Write-Host ("after ctrl+c get=[" + (Get-Clipboard) + "]")
# 换行粘贴
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}PASTE:")
Start-Sleep -Milliseconds 150
[System.Windows.Forms.SendKeys]::SendWait("^v")
Start-Sleep -Milliseconds 300
Write-Host "check notepad window content next"

Write-Host "=== 5. 卡键复查 ==="
Write-Host ("Ctrl down? " + (([Kb]::GetKeyState(0x11) -band 0x8000) -ne 0))
Write-Host ("Shift down? " + (([Kb]::GetKeyState(0x10) -band 0x8000) -ne 0))
Write-Host ("Alt down? " + (([Kb]::GetKeyState(0x12) -band 0x8000) -ne 0))
