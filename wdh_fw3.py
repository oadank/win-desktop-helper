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

print("=== existing shot-service.exe rules detail ===")
print(run('netsh advfirewall firewall show rule name=all | findstr /N /I "shot-service"'))
print("=== AllowLocalPolicyMerge registry ===")
print(run(r'reg query "HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall" /s'))
print("=== set AllowLocalPolicyMerge=1 for Domain/Private/Public ===")
for prof in ("DomainProfile", "PrivateProfile", "PublicProfile", "StandardProfile"):
    print(run(r'reg add "HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall\%s" /v AllowLocalPolicyMerge /t REG_DWORD /d 1 /f' % prof))
print("=== after: LocalFirewallRules ===")
print(run("netsh advfirewall show currentprofile | findstr /I \"LocalFirewallRules State\""))
print("=== re-add port rule + exe rule ===")
print(run('netsh advfirewall firewall add rule name="WinDesktopHelper-18800" dir=in action=allow protocol=TCP localport=18800 enable=yes'))
print(run('powershell -NoProfile -Command "New-NetFirewallRule -DisplayName WDH-18800 -Direction Inbound -Action Allow -Protocol TCP -LocalPort 18800 -Profile Any -ErrorAction SilentlyContinue | Select-Object DisplayName,Enabled,Action"'))
print("=== test listen from XDN self ===")
print(run('powershell -NoProfile -Command "(Invoke-WebRequest -Uri http://100.119.140.33:18800/health -UseBasicParsing -TimeoutSec 5).StatusCode"'))
print("=== test to LeCoo ===")
print(run('powershell -NoProfile -Command "(Test-NetConnection 100.108.133.82 -Port 18800).TcpTestSucceeded"'))
