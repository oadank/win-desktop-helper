Write-Host '--- disable Block rules for shot-service.exe ---'
Get-NetFirewallRule | Where-Object {
  $_.Action -eq 'Block' -and $_.DisplayName -match 'shot-service'
} | ForEach-Object {
  Write-Host "disable $($_.DisplayName) $($_.Direction) $($_.Profile)"
  $_ | Disable-NetFirewallRule
}

Write-Host '--- remaining shot-service rules ---'
Get-NetFirewallRule | Where-Object { $_.DisplayName -match 'shot-service' } |
  Select-Object DisplayName, Enabled, Action, Direction | Format-Table

Write-Host '--- ensure allow 18800 ---'
New-NetFirewallRule -DisplayName 'WDH-18800' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 18800 -Profile Any -ErrorAction SilentlyContinue | Out-Null
New-NetFirewallRule -DisplayName 'WDH-exe-any' -Direction Inbound -Action Allow -Program 'C:\Users\oadan\AppData\Local\Programs\win-desktop-helper\shot-service.exe' -Profile Any -ErrorAction SilentlyContinue | Out-Null
Get-NetFirewallRule -DisplayName 'WDH*' | Select-Object DisplayName, Enabled, Action | Format-Table

Write-Host '--- self test ---'
(Invoke-WebRequest -Uri 'http://100.119.140.33:18800/health' -UseBasicParsing -TimeoutSec 5).StatusCode
