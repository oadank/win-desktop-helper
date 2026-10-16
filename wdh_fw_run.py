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
sftp = paramiko.SFTPClient.from_transport(ssh.get_transport())
local = r"C:\D\opt\win-desktop-helper\wdh_fw.ps1"
remote = r"C:\Users\oadan\AppData\Local\Temp\wdh_fw.ps1"
sftp.put(local, remote)
i, o, e = ssh.exec_command(
    'powershell -NoProfile -ExecutionPolicy Bypass -File "%s"' % remote, timeout=60)
print(o.read().decode("utf-8", "replace"))
print(e.read().decode("utf-8", "replace"))
