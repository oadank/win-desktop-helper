# -*- coding: utf-8 -*-
"""远程检查/放行 helper 18800 入站 + 连通性自检。"""
import os, subprocess, sys, time
import paramiko

def load_cred():
    raw = subprocess.check_output(["nssm", "get", "comfyui", "AppEnvironmentExtra"])
    text = raw.decode("utf-16le", "ignore")
    env = {}
    for line in text.replace("\x00", "").splitlines():
        line = line.strip().rstrip("\r")
        if "=" in line and line.startswith("XDN_"):
            k, v = line.split("=", 1)
            env[k] = v
    return env

def run(ssh, cmd):
    i, o, e = ssh.exec_command(cmd, timeout=40)
    out = o.read().decode("utf-8", "replace").strip()
    err = e.read().decode("utf-8", "replace").strip()
    return out + ("\n[err] " + err if err else "")

def main():
    host = sys.argv[1]
    env = load_cred()
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(host, 22, username=env["XDN_USER"], password=env["XDN_PASS"], timeout=20,
                allow_agent=False, look_for_keys=False)
    print("=== firewall rules 18800 ===")
    print(run(ssh, 'netsh advfirewall firewall show rule name=all dir=in | findstr /I "18800 WinDesktop Helper shot-service"'))
    print("=== add allow 18800 if missing ===")
    print(run(ssh, 'netsh advfirewall firewall add rule name="WinDesktopHelper-18800" dir=in action=allow protocol=TCP localport=18800'))
    print("=== listen ===")
    print(run(ssh, 'netstat -ano | findstr :18800'))
    print("=== local self-get ===")
    print(run(ssh, 'powershell -NoProfile -Command "(Invoke-WebRequest -Uri http://127.0.0.1:18800/health -UseBasicParsing -TimeoutSec 5).Content"'))
    print("=== tailscale ip self-get ===")
    print(run(ssh, 'powershell -NoProfile -Command "(Invoke-WebRequest -Uri http://%s:18800/health -UseBasicParsing -TimeoutSec 5).Content"' % host))

if __name__ == "__main__":
    main()
