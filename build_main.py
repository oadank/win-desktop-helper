# -*- coding: utf-8 -*-
# 验证主程序 shot-service.exe 能编入 shot-annotate-geom.cs (只编译到源码目录, 不部署不重启服务)
import subprocess, os, time

os.chdir(r"C:\D\opt\win-desktop-helper")
print("=== build main shot-service.exe ===")
r = subprocess.run(["cmd", "/c", "build-annotation.cmd"],
                   capture_output=True, text=True, errors='replace')
print("returncode:", r.returncode)
if r.stdout.strip():
    print("STDOUT:", r.stdout)
if r.stderr.strip():
    print("STDERR:", r.stderr)
if os.path.exists("shot-service.exe"):
    print("shot-service.exe mtime:", time.strftime("%Y-%m-%d %H:%M:%S",
          time.localtime(os.path.getmtime("shot-service.exe"))),
          "size:", os.path.getsize("shot-service.exe"))
