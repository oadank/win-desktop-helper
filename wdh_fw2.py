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
    i, o, e = ssh.exec_command(cmd, timeout=40)
    return (o.read().decode("utf-8", "replace") + "\n" + e.read().decode("utf-8", "replace")).strip()

print("=== enable local firewall rules ===")
print(run("netsh advfirewall set currentprofile settings localfirewallrules enable"))
print(run("netsh advfirewall set publicprofile settings localfirewallrules enable"))
print(run("netsh advfirewall set privateprofile settings localfirewallrules enable"))
print(run("netsh advfirewall firewall add rule name=\"WinDesktopHelper-18800\" dir=in action=allow protocol=TCP localport=18800"))
print(run("netsh advfirewall firewall add rule name=\"WinDesktopHelper-18800-exe\" dir=in action=allow program=\"C:\\Users\\oadan\\AppData\\Local\\Programs\\win-desktop-helper\\shot-service.exe\" enable=yes"))
print("=== profile ===")
print(run("netsh advfirewall show currentprofile | findstr /I \"State LocalFirewallRules\""))
print("=== netstat 18800 ===")
print(run("netstat -ano | findstr :18800"))
print("=== from XDN to LeCoo:18800 ===")
print(run('powershell -NoProfile -Command "Test-NetConnection 100.108.133.82 -Port 18800 | Select-Object TcpTestSucceeded | Format-List"'))
print("=== from XDN to self Tailscale IP ===")
print(run('powershell -NoProfile -Command "(Invoke-WebRequest -Uri http://100.119.140.33:18800/health -UseBasicParsing -TimeoutSec 5).StatusCode"'))
