# -*- coding: utf-8 -*-
"""拖动跟手度实测: 手动 down->20步move->up 慢速拖卡片, 后台 5ms 采样卡片位置。
旧版(200ms心跳)整段拖拽只能跟 ~3 档; 新版(15ms+实时GetCursorPos)应 >=12 档且跟手误差小。"""
import sys, time, json, ctypes, threading, urllib.request
import ctypes.wintypes as wt
sys.path.insert(0, '.')
from _mcp import MCP

user32 = ctypes.windll.user32
pid = json.loads(urllib.request.urlopen("http://127.0.0.1:18800/health").read())["pid"]
stop_flag = [False]
samples = []   # (t, left, top)

def sampler():
    while not stop_flag[0]:
        out = []
        @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
        def cb(h, l):
            p = wt.DWORD()
            user32.GetWindowThreadProcessId(h, ctypes.byref(p))
            if p.value == pid and user32.IsWindowVisible(h):
                r = wt.RECT()
                user32.GetWindowRect(h, ctypes.byref(r))
                if 410 <= r.right - r.left <= 430 and 290 <= r.bottom - r.top <= 310:
                    out.append((r.left, r.top))
            return True
        user32.EnumWindows(cb, 0)
        if out:
            samples.append((time.perf_counter(), out[0][0], out[0][1]))
        time.sleep(0.005)

def card():
    out = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, l):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h):
            r = wt.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            if 410 <= r.right - r.left <= 430 and 290 <= r.bottom - r.top <= 310 and r.left > 0:
                out.append((int(h), r.left, r.top))
        return True
    user32.EnumWindows(cb, 0)
    return out[0] if out else None

MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP = 0x0002, 0x0004

with MCP() as m:
    # 1) 记事本划词 -> 展开工具条 -> 点"翻译"出卡片
    tl = json.loads(m.call("win_manage", action="list"))
    npd = [w for w in tl["apps"] if w["process"] == "Notepad"][0]
    r = npd["rect"]
    tx, ty = r["x"] + 60, r["y"] + 120
    m.call("win_manage", action="activate", hwnd=npd["hwnd"])
    time.sleep(0.5)
    m.call("mouse_click", x=tx, y=ty)
    time.sleep(0.3)
    m.call("keyboard_type", text="Drag smoothness performance test")
    time.sleep(0.4)
    m.call("mouse_drag", x1=tx + 20, y1=ty, x2=tx + 260, y2=ty)
    time.sleep(0.5)
    # 点小点展开(直接 hover 停留 300ms)
    dots = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, l):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h):
            rr = wt.RECT()
            user32.GetWindowRect(h, ctypes.byref(rr))
            if 20 <= rr.right - rr.left <= 30 and 20 <= rr.bottom - rr.top <= 30:
                dots.append((rr.left, rr.top))
        return True
    user32.EnumWindows(cb, 0)
    assert dots, "no dot"
    user32.SetCursorPos(dots[0][0] + 13, dots[0][1] + 13)
    bar = None
    for _ in range(15):
        time.sleep(0.15)
        # 用同款写法找工具条
        found = []
        @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
        def cb3(h, l):
            p = wt.DWORD()
            user32.GetWindowThreadProcessId(h, ctypes.byref(p))
            if p.value == pid and user32.IsWindowVisible(h):
                rr = wt.RECT()
                user32.GetWindowRect(h, ctypes.byref(rr))
                if 190 <= rr.right - rr.left <= 210 and 30 <= rr.bottom - rr.top <= 50 and rr.left > 0:
                    found.append((rr.left, rr.top))
            return True
        user32.EnumWindows(cb3, 0)
        if found:
            bar = found[0]
            break
    assert bar, "no bar (hover expand failed)"
    m.call("mouse_click", x=bar[0] + 35, y=bar[1] + 20)   # 翻译按钮(第1个)
    c0 = None
    for _ in range(15):
        time.sleep(0.5)
        c0 = card()
        if c0:
            break
    assert c0, "no card"
    print("卡片出现 @", c0[1], c0[2])

    # 2) 手动慢速拖动: 按住标题栏, 20 步 × (+8,+4), 步间隔 20ms (总位移 160,80, 全程约400ms)
    ax, ay = c0[1] + 60, c0[2] + 18
    user32.SetCursorPos(ax, ay)
    time.sleep(0.15)
    th = threading.Thread(target=sampler)
    th.start()
    time.sleep(0.1)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    for i in range(20):
        user32.SetCursorPos(ax + (i + 1) * 8, ay + (i + 1) * 4)
        time.sleep(0.02)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.35)
    stop_flag[0] = True
    th.join()

c1 = card()
# 3) 统计: 拖动窗口内卡片 left 的"档位变化数"与最大滞后
drag_lefts = []
for t, l, tp in samples:
    drag_lefts.append(l)
uniq = 0
prev = None
steps = []
for l in drag_lefts:
    if prev is None or l != prev:
        if prev is not None:
            steps.append(l)
        prev = l
print("采样帧数:", len(samples), "| 卡片位置变化次数:", len(steps))
final = c1 if c1 else (0, prev, 0)
dx = final[1] - c0[1] if c1 else 0
dy = final[2] - c0[2] if c1 else 0
print("总位移: (%d,%d) 期望≈(160,80)" % (dx, dy))
# 跟手度: 变化次数/20步
ratio = len(steps) / 20.0
print("=== 拖动档位跟随率: %.0f%% (%d/20) ===" % (ratio * 100, len(steps)))
assert c1 and 120 <= dx <= 200 and 40 <= dy <= 100, "落位不对"
assert len(steps) >= 12, "拖动不够跟手(应>=12档)"
print("PASS: 跟手度达标")
