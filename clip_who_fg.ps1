Add-Type -Name N -Namespace U -MemberDefinition '[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);[DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int m);[DllImport("user32.dll")] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int m);[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();'

function ShowWin($h) {
  $p = 0
  [void][U.N]::GetWindowThreadProcessId([IntPtr]$h, [ref]$p)
  $t = New-Object System.Text.StringBuilder 256
  $c = New-Object System.Text.StringBuilder 256
  [void][U.N]::GetWindowTextW([IntPtr]$h, $t, 256)
  [void][U.N]::GetClassNameW([IntPtr]$h, $c, 256)
  $name = '?'
  $path = ''
  try {
    $proc = Get-Process -Id $p -ErrorAction Stop
    $name = $proc.ProcessName
    $path = $proc.Path
  } catch {}
  Write-Host ("hwnd=$h pid=$p proc=$name title=[" + $t.ToString() + "] class=" + $c.ToString() + " path=$path")
}

Write-Host '--- windows ---'
ShowWin 1377564
ShowWin 655480
Write-Host '--- foreground ---'
$fg = [U.N]::GetForegroundWindow()
ShowWin $fg

Write-Host '--- GameViewer / OpenWith / helper ---'
Get-Process GameViewer,Notepad,shot-service,OpenWith -ErrorAction SilentlyContinue |
  Select-Object Id, ProcessName, MainWindowTitle, MainWindowHandle
