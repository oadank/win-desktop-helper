Add-Type -AssemblyName System.Windows.Forms
Add-Type -Name W -Namespace U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();'

$np = $null
foreach ($proc in @(Get-Process Notepad -ErrorAction SilentlyContinue)) {
  if ($proc.MainWindowHandle -ne 0) { $np = $proc; break }
  else { try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {} }
}
if ($np -eq $null) {
  Start-Process 'C:\Windows\System32\notepad.exe' | Out-Null
  Start-Sleep -Seconds 2
  foreach ($proc in @(Get-Process Notepad -ErrorAction SilentlyContinue)) {
    if ($proc.MainWindowHandle -ne 0) { $np = $proc; break }
  }
}
if ($np -eq $null) { Write-Host 'NO NOTEPAD WINDOW'; exit 1 }

Write-Host ('target hwnd=' + $np.MainWindowHandle + ' title=' + $np.MainWindowTitle)
[void][U.W]::SetForegroundWindow($np.MainWindowHandle)
Start-Sleep -Milliseconds 500
Write-Host ('foreground=' + [U.W]::GetForegroundWindow())

[Windows.Forms.SendKeys]::SendWait('REALKEY-COPY-TEST')
Start-Sleep -Milliseconds 300
[Windows.Forms.SendKeys]::SendWait('^a')
Start-Sleep -Milliseconds 200
[Windows.Forms.SendKeys]::SendWait('^c')
Start-Sleep -Milliseconds 500

$clip = Get-Clipboard
Write-Host ('clipboard=[' + $clip + ']')
if ($clip -like '*REALKEY-COPY-TEST*') { Write-Host 'CONCLUSION: 本机 Ctrl+C 正常' }
else { Write-Host 'CONCLUSION: 本机 Ctrl+C 失败' }
