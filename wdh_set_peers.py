# -*- coding: utf-8 -*-
"""远程写 helper 的 clipboard.peers / clipboard.syncPush（保留其余配置）。"""
import json, os, subprocess, sys, time
import paramiko

SKILL = r"C:\D\opt\agents-to-feishu\skills\xdn-remote-ops"
APPDIR = r"C:\Users\oadan\AppData\Local\Programs\win-desktop-helper"
REMOTE_CFG = APPDIR + r"\shot-service.json"


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


def main():
    host, peers, push = sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else "1"
    env = load_cred()
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(host, 22, username=env["XDN_USER"], password=env["XDN_PASS"], timeout=20,
                allow_agent=False, look_for_keys=False)
    sftp = paramiko.SFTPClient.from_transport(ssh.get_transport())
    raw = sftp.open(REMOTE_CFG, "rb").read().decode("utf-8-sig")
    cfg = json.loads(raw)
    cfg.setdefault("clipboard", {})
    cfg["clipboard"]["peers"] = peers
    cfg["clipboard"]["syncPush"] = push
    ts = time.strftime("%Y%m%d-%H%M%S")
    sftp.open(REMOTE_CFG + ".bak-" + ts, "wb").write(raw.encode("utf-8"))
    sftp.open(REMOTE_CFG, "wb").write(json.dumps(cfg, ensure_ascii=False, indent=2).encode("utf-8"))
    print("set %s peers=%s syncPush=%s" % (host, peers, push))
    print(json.dumps(cfg.get("clipboard"), ensure_ascii=False))
    # Cfg() 每次读文件, 不用重启; 但 syncPush/peers 是启动时进内存的 → 重启
    ssh.exec_command('taskkill /F /IM shot-service.exe', timeout=15)
    time.sleep(1.2)
    ssh.exec_command('schtasks /Run /TN WinDesktopHelper', timeout=15)
    time.sleep(2.5)
    i, o, e = ssh.exec_command('powershell -NoProfile -Command "Select-String -Path \'%s\' -Pattern \'peers=|listening on\' | Select-Object -Last 3"' % (APPDIR + r"\shot-service.log"), timeout=20)
    print(o.read().decode("utf-8", "replace"))


if __name__ == "__main__":
    main()
