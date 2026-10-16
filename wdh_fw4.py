# -*- coding: utf-8 -*-
import subprocess
import paramiko

raw = subprocess.check_output(["nssm", "get", "comfyui", "AppEnvironmentExtra"]).decode("utf-16le", "ignore")
env = {}
for line in raw.replace("\x00", "").splitlines():
    line = line.strip().rstrip("\r")
    if line.startswith("XDN_") and "=" in line:
        k, v = line.split("=", 1)
        env[k] = v

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect("100.119.140.33", 22, username=env["XDN_USER"], password=env["XDN_PASS"], timeout=20, allow_agent=False, look_for_keys=False)

def run(cmd):
    i, o, e = ssh.exec_command(cmd, timeout=50)
    return (o.read().decode("utf-8", "replace") + "\n" + e.read().decode("utf-8", "replace")).strip()

ps = r'''
Get-NetFirewallRule | Where-Object { $_.DisplayName -match "18800|shot-service|WinDesktop|WDH" -or $_.Name -match "18800|shot-service|WDH" } |
  ForEach-Object {
    $af = $_ | Get-NetFirewallAddressFilter
    $pf = $_ | Get-NetFirewallPortFilter
    $app = $_ | Get-NetFirewallApplicationFilter
    [pscustomobject]@{
      Name=$_.DisplayName; Enabled=$_.Enabled; Action=$_.Action; Direction=$_.Direction
      Profile=$_.Profile; Program=$app.Program; Protocol=$pf.Protocol; LocalPort=$pf.LocalPort
      RemoteAddress=$af.RemoteAddress
    }
  } | Format-List
'''
print("=== WDH/18800/shot-service rules ===")
print(run('powershell -NoProfile -Command "%s"' % ps.replace('"', '\\"').replace('\n', ' ')))

print("=== force enable + allow exe any ===")
print(run(r'powershell -NoProfile -Command "Get-NetFirewallRule | Where-Object { $_.DisplayName -match \"shot-service|WDH|WinDesktopHelper\" } | Enable-NetFirewallRule; Get-NetFirewallApplicationFilter -Program \"C:\Users\oadan\AppData\Local\Programs\win-desktop-helper\shot-service.exe\" | Get-NetFirewallRule | Enable-NetFirewallRule"'))
print(run(r'powershell -NoProfile -Command "New-NetFirewallRule -DisplayName WDH-exe-any -Direction Inbound -Action Allow -Program \"C:\Users\oadan\AppData\Local\Programs\win-desktop-helper\shot-service.exe\" -Profile Any -ErrorAction SilentlyContinue | Out-Null; Get-NetFirewallRule -DisplayName WDH* | Select-Object DisplayName,Enabled,Action,Direction | Format-Table"'))
print("=== drop log tail ===")
print(run(r'powershell -NoProfile -Command "Get-Content $env:systemroot\system32\LogFiles\Firewall\pfirewall.log -Tail 30 -ErrorAction SilentlyContinue | Select-String \"18800|DROP|shot-service\""'))
print("=== self test ===")
print(run('powershell -NoProfile -Command "(Invoke-WebRequest -Uri http://100.119.140.33:18800/health -UseBasicParsing -TimeoutSec 5).StatusCode"'))
