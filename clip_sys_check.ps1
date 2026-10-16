# 系统剪贴板体检 + 修复卡键
Write-Host "=== 1. Set/Get Clipboard ==="
try {
  $v = "CLIP-SYS-TEST-" + (Get-Date -Format HHmmss)
  Set-Clipboard -Value $v
  Start-Sleep -Milliseconds 300
  $g = Get-Clipboard
  Write-Host "set=$v get=$g  match=$($g -eq $v)"
} catch { Write-Host "FAIL $($_.Exception.Message)" }

Write-Host "=== 2. clipboard owner / open window ==="
$src = @'
using System;
using System.Runtime.InteropServices;
public static class ClipNative {
  [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
  [DllImport("user32.dll")] public static extern IntPtr GetOpenClipboardWindow();
  [DllImport("user32.dll")] public static extern int GetClipboardSequenceNumber();
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue
Write-Host ("owner=" + [ClipNative]::GetClipboardOwner() + " openWnd=" + [ClipNative]::GetOpenClipboardWindow() + " seq=" + [ClipNative]::GetClipboardSequenceNumber())

Write-Host "=== 3. release modifier keys (stuck Ctrl/Shift/Alt/Win) ==="
$ks = @'
using System;
using System.Runtime.InteropServices;
public static class Keys {
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  public const byte VK_CONTROL=0x11, VK_LCONTROL=0xA2, VK_RCONTROL=0xA3;
  public const byte VK_SHIFT=0x10, VK_LSHIFT=0xA0, VK_RSHIFT=0xA1;
  public const byte VK_MENU=0x12, VK_LMENU=0xA4, VK_RMENU=0xA5;
  public const byte VK_LWIN=0x5B, VK_RWIN=0x5C;
  public const uint KEYUP=0x2;
  public static void ReleaseAll() {
    keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_LCONTROL, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_RCONTROL, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_SHIFT, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_LSHIFT, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_RSHIFT, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_LMENU, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_RMENU, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_LWIN, 0, KEYUP, UIntPtr.Zero);
    keybd_event(VK_RWIN, 0, KEYUP, UIntPtr.Zero);
  }
}
'@
Add-Type -TypeDefinition $ks -ErrorAction SilentlyContinue
[Keys]::ReleaseAll()
Write-Host "released"

Write-Host "=== 4. Get-Clipboard again ==="
try { Write-Host ("now=" + (Get-Clipboard)) } catch { Write-Host "FAIL $($_.Exception.Message)" }

Write-Host "=== 5. rdpclip / clipboard-related processes ==="
Get-Process rdpclip,rdpclip.exe,TextInputHost,ctfmon -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path
