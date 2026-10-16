# -*- coding: utf-8 -*-
"""从 nssm comfyui 读 XDN_USER/XDN_PASS，探测/部署远程 helper。用法:
  python wdh_fleet.py probe   [host...]
  python wdh_fleet.py deploy  [host...]
默认 host: XDN, shlc, gs
"""
import os, subprocess, sys, time

DEFAULT_HOSTS = ["100.119.140.33", "100.93.226.56", "100.75.250.30"]
NAMES = {"100.119.140.33": "XDN", "100.93.226.56": "shlc", "100.75.250.30": "gs"}
SKILL = r"C:\D\opt\agents-to-feishu\skills\xdn-remote-ops"
PY = r"C:\Users\oadan\AppData\Local\Programs\Python\Python312\python.exe"


def load_cred():
    raw = subprocess.check_output(["nssm", "get", "comfyui", "AppEnvironmentExtra"])
    text = raw.decode("utf-16le", "ignore") if raw[:2] == b"\xff\xfe" or b"\x00" in raw[:4] else raw.decode("utf-8", "ignore")
    # nssm 输出可能是 UTF-16LE 无 BOM
    if "\x00" in text[:20] or (len(raw) > 2 and raw[1] == 0):
        text = raw.decode("utf-16le", "ignore")
    env = {}
    for line in text.replace("\x00", "").splitlines():
        line = line.strip().rstrip("\r")
        if "=" in line and line.startswith("XDN_"):
            k, v = line.split("=", 1)
            env[k] = v
    return env


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "probe"
    hosts = sys.argv[2:] if len(sys.argv) > 2 else DEFAULT_HOSTS
    env = load_cred()
    user, pw = env.get("XDN_USER", ""), env.get("XDN_PASS", "")
    if not user or not pw:
        print("!! XDN_USER/XDN_PASS not found in nssm comfyui env")
        print("keys:", sorted(env.keys()))
        sys.exit(1)
    os.environ["XDN_USER"] = user
    os.environ["XDN_PASS"] = pw
    print("cred user=%s pass-len=%d" % (user, len(pw)))
    for host in hosts:
        tag = NAMES.get(host, host)
        print("\n======== %s (%s) mode=%s ========" % (tag, host, mode))
        script = "wdh_probe.py" if mode == "probe" else "wdh_deploy.py"
        r = subprocess.call([PY, os.path.join(SKILL, script), host])
        print("[exit %d] %s %s" % (r, tag, mode))


if __name__ == "__main__":
    main()
