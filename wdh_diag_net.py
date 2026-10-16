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

print("=== XDN -> LeCoo:18800 ===")
print(run('powershell -NoProfile -Command "Test-NetConnection 100.108.133.82 -Port 18800 | Select-Object ComputerName,TcpTestSucceeded,PingSucceeded | Format-List"'))
print("=== XDN firewall profile ===")
print(run("netsh advfirewall show currentprofile"))
print("=== 18800 related rules ===")
print(run('netsh advfirewall firewall show rule name=all dir=in | findstr /I "WinDesktopHelper 18800"'))
print("=== tailscale status (peers) ===")
print(run("tailscale status"))
