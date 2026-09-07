# -*- coding: utf-8 -*-
"""开关即时生效实测: 关 -> 划词无点 -> 开 -> 划词有点"""
import sys, time, json, ctypes, urllib.request
import ctypes.wintypes as wt
sys.path.insert(0, '.')
from _mcp import MCP

user32 = ctypes.windll.user32
pid = json.loads(urllib.request.urlopen("http://127.0.0.1:18800/health").read())["pid"]

def dot():
    out = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, l):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h):
            r = wt.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            if r.left > 0 and r.top > 0 and 20 <= r.right - r.left <= 30 and 20 <= r.bottom - r.top <= 30:
                out.append((int(h), r.left, r.top))
        return True
    user32.EnumWindows(cb, 0)
    return out[0] if out else None

def drag_on_notepad(m):
    tl = json.loads(m.call("win_manage", action="list"))
    npd = [w for w in tl["apps"] if w["process"] == "Notepad"][0]
    r = npd["rect"]
    tx, ty = r["x"] + 60, r["y"] + 120
    m.call("win_manage", action="activate", hwnd=npd["hwnd"])
    time.sleep(0.5)
    m.call("mouse_click", x=tx, y=ty)
    time.sleep(0.3)
    m.call("keyboard_type", text="Toggle switch verification text")
    time.sleep(0.4)
    m.call("mouse_drag", x1=tx + 20, y1=ty, x2=tx + 220, y2=ty)
    time.sleep(0.7)
    return dot()

with MCP() as m:
    json.loads(m.call('pick_config', enabled=0))
    d1 = drag_on_notepad(m)
    print("关闭后划词 -> 小点:", d1, "(应为 None)")

    json.loads(m.call('pick_config', enabled=1))
    d2 = drag_on_notepad(m)
    print("开启后划词 -> 小点:", d2, "(应存在)")

    ok = (d1 is None) and (d2 is not None)
    print("\n=== 开关即时生效 ===", ok)
    if not ok:
        sys.exit(1)
