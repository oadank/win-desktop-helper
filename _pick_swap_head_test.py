# -*- coding: utf-8 -*-
# 验收: ①反转按钮在标题栏 ②点它标题变"(反转)"方向对 ③细滚动条/无粉边(截图肉眼+量化)
import sys, json, time, ctypes, ctypes.wintypes as wt
sys.path.insert(0, r'C:\D\opt\win-desktop-helper')
from _mcp import MCP
u = ctypes.windll.user32
SVC = json.loads(__import__('urllib.request', fromlist=['x']).urlopen("http://127.0.0.1:18800/health").read())["pid"]

def find(wlo, whi, hlo, hhi):
    out = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, l):
        p = wt.DWORD(); u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == SVC and u.IsWindowVisible(h):
            r = wt.RECT(); u.GetWindowRect(h, ctypes.byref(r))
            if r.left > 0 and wlo <= r.right - r.left <= whi and hlo <= r.bottom - r.top <= hhi:
                out.append((h, r.left, r.top, r.right - r.left, r.bottom - r.top))
        return True
    u.EnumWindows(cb, 0)
    return out[0] if out else None

with MCP() as m:
    # 找有 UIA Document 的记事本
    tl = json.loads(m.call("list_apps"))
    cands = sorted([w for w in tl["apps"] if "notepad" in (w.get("process") or "").lower()
                    and w["rect"]["w"] * w["rect"]["h"] > 100000],
                   key=lambda w: -w["rect"]["w"] * w["rect"]["h"])
    hwnd = dr = None
    for w in cands:
        m.call("win_manage", action="activate", hwnd=w["hwnd"]); time.sleep(0.4)
        ed = json.loads(m.call("ui_tree", hwnd=w["hwnd"], max=150))
        ds = [e for e in (ed.get("elements") or []) if e["type"] == "Document" and "textpattern" in (e.get("patterns") or "")]
        if ds:
            hwnd = w["hwnd"]; dr = max(ds, key=lambda e: e["rect"]["w"] * e["rect"]["h"])["rect"]; break
    assert hwnd, "no notepad with Document"
    m.call("mouse_click", x=dr["x"] + 60, y=dr["y"] + 22); time.sleep(0.3)
    m.call("keyboard_press", keys="ctrl+a"); time.sleep(0.2)
    m.call("keyboard_press", keys="delete"); time.sleep(0.3)
    m.call("keyboard_type", text="显存容量有两种版本"); time.sleep(0.6)
    m.call("mouse_click", x=dr["x"] + 30, y=dr["y"] + 20); time.sleep(0.3)
    sx, sy = dr["x"] + 20, dr["y"] + 16
    m.call("win_manage", action="activate", hwnd=hwnd); time.sleep(0.4)
    m.call("mouse_drag", x1=sx, y1=sy, x2=sx + 110, y2=sy); time.sleep(0.7)
    d = find(20, 32, 20, 32); assert d, "A no dot"
    u.SetCursorPos(d[1] + 13, d[2] + 13)
    b = None
    for _ in range(15):
        time.sleep(0.2); b = find(190, 215, 30, 50)
        if b: break
    assert b, "B no bar"
    m.call("mouse_click", x=b[1] + 35, y=b[2] + 20)   # 翻译
    c = None
    for _ in range(10):
        time.sleep(0.5); c = find(410, 435, 290, 315)
        if c: break
    assert c, "C no card"
    ch, cx, cy, cw, chh = c
    def ocr():
        sp = json.loads(m.call("screen_capture", x=cx, y=cy, w=cw, h=chh))
        o = json.loads(m.call("ocr_image", path=sp["file"]))
        return (o.get("text") or "").replace("\n", " ")
    t = ""
    for _ in range(15):
        time.sleep(1.0); t = ocr()
        if "中→英" in t: break
    print("[1] 翻译卡 OCR:", t[:180])
    # 标题栏区域单独截: 验证反转按钮在标题(右上, ×左边)
    sp = json.loads(m.call("screen_capture", x=cx, y=cy, w=cw, h=38))
    o = json.loads(m.call("ocr_image", path=sp["file"]))
    head = (o.get("text") or "").replace("\n", " ")
    print("[2] 标题栏 OCR:", head)
    swap_in_head = ("反转" in head)
    # 点标题栏反转按钮(本地 316..380, 8..30)
    m.call("mouse_click", x=cx + 348, y=cy + 19)
    t2 = ""
    for _ in range(18):
        time.sleep(1.0); t2 = ocr()
        if "反转" in t2: break
    print("[3] 反转后 OCR:", t2[:180])
    body2 = t2.split("原文")[0]
    ok2 = ("反转" in t2) and any('一' <= ch <= '鿿' for ch in body2)
    # 截整卡给老大看
    sp = json.loads(m.call("screen_capture", x=cx, y=cy, w=cw, h=chh))
    print("shot:", sp["file"])
    m.call("mouse_click", x=cx + cw - 21, y=cy + 19)  # 关闭
    print("\n反转按钮在标题栏:", swap_in_head, " 反转回中文:", ok2)
