# -*- coding: utf-8 -*-
"""在 XDN 上写入剪贴板 → 应自动推回 LeCoo。"""
import subprocess, time
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
    i, o, e = ssh.exec_command(cmd, timeout=30)
    return (o.read().decode("utf-8", "replace") + e.read().decode("utf-8", "replace")).strip()

print(run('powershell -NoProfile -Command "(Invoke-WebRequest -Uri \'http://127.0.0.1:18800/clipboard/set?text=SYNC-FROM-XDN-1456\' -UseBasicParsing -TimeoutSec 5).Content"'))
time.sleep(2)
print(run(r'powershell -NoProfile -Command "Select-String -Path C:\Users\oadan\AppData\Local\Programs\win-desktop-helper\shot-service.log -Pattern \'clip push\' | Select-Object -Last 3"'))
