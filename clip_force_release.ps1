$src = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ClipForce {
  [DllImport("user32.dll")] public static extern IntPtr GetOpenClipboardWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
  [DllImport("user32.dll")] public static extern bool CloseClipboard();
  [DllImport("user32.dll")] public static extern bool EmptyClipboard();
}
'@
Add-Type -TypeDefinition $src -ErrorAction Stop

$h1 = [ClipForce]::GetOpenClipboardWindow()
$h2 = [ClipForce]::GetClipboardOwner()
Write-Host ("openWnd=" + $h1 + " owner=" + $h2)

function ShowWin($h) {
  if ($h -eq [IntPtr]::Zero) { return }
  $p = 0
  [void][ClipForce]::GetWindowThreadProcessId($h, [ref]$p)
  $t = New-Object System.Text.StringBuilder 256
  $c = New-Object System.Text.StringBuilder 256
  [void][ClipForce]::GetWindowTextW($h, $t, 256)
  [void][ClipForce]::GetClassNameW($h, $c, 256)
  $name = "?"
  try { $name = (Get-Process -Id $p -ErrorAction Stop).ProcessName } catch { }
  Write-Host ("  hwnd=" + $h + " pid=" + $p + " proc=" + $name + " title=" + $t.ToString() + " class=" + $c.ToString())
}
Write-Host "--- open ---"
ShowWin $h1
Write-Host "--- owner ---"
ShowWin $h2

Write-Host "--- force open/close ---"
$ok = [ClipForce]::OpenClipboard([IntPtr]::Zero)
Write-Host ("OpenClipboard=" + $ok)
if ($ok) {
  [void][ClipForce]::EmptyClipboard()
  [void][ClipForce]::CloseClipboard()
  Write-Host "closed"
}

Start-Sleep -Milliseconds 300
Write-Host ("after openWnd=" + [ClipForce]::GetOpenClipboardWindow())
try {
  Set-Clipboard -Value "CLIP-FIXED-TEST"
  Start-Sleep -Milliseconds 250
  Write-Host ("Get=" + (Get-Clipboard))
} catch {
  Write-Host ("still FAIL " + $_.Exception.Message)
}
