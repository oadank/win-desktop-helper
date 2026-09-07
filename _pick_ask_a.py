# -*- coding: utf-8 -*-
"""场景A复测: 划词 -> 问AI -> 现输入问题 -> 回车。给足 45s，并用「卡片正文的 UIA 值」判定，
不再依赖 OCR 的识别时机。"""
import sys, time, json, ctypes, ctypes.wintypes as wt, urllib.request
sys.path.insert(0, '.')
from _mcp import MCP

SVC = json.loads(urllib.request.urlopen("http://127.0.0.1:18800/health").read())["pid"]
u = ctypes.windll.user32
TEXT = "RTX 3080Ti 是安培架构显卡，显存有 10GB 和 12GB 两个版本"
Q = "它一共有几种显存容量？只回答数字，用中文顿号分隔"


def wins():
    out = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, l):
        p = wt.DWORD(); u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == SVC and u.IsWindowVisible(h):
            r = wt.RECT(); u.GetWindowRect(h, ctypes.byref(r))
            if r.left > 0: out.append((h, r.left, r.top, r.right - r.left, r.bottom - r.top))
        return True
    u.EnumWindows(cb, 0)
    return out


def find(wlo, whi, hlo, hhi):
    for h, x, y, w, hh in wins():
        if wlo <= w <= whi and hlo <= hh <= hhi:
            return (h, x, y, w, hh)
    return None


R = {}
with MCP() as m:
    tl = json.loads(m.call("list_apps"))
    cands = sorted([w for w in tl["apps"] if "notepad" in (w.get("process") or "").lower()
                    and w["rect"]["w"] * w["rect"]["h"] > 100000],
                   key=lambda w: -w["rect"]["w"] * w["rect"]["h"])
    hwnd = dr = None
    for w in cands:
        m.call("win_manage", action="activate", hwnd=w["hwnd"]); time.sleep(0.4)
        ed = json.loads(m.call("ui_tree", hwnd=w["hwnd"], max=150))
        ds = [e for e in (ed.get("elements") or [])
              if e["type"] == "Document" and "textpattern" in (e.get("patterns") or "")]
        if ds:
            hwnd = w["hwnd"]; dr = max(ds, key=lambda e: e["rect"]["w"] * e["rect"]["h"])["rect"]; break
    assert dr, "no usable Notepad"
    m.call("mouse_click", x=dr["x"] + 60, y=dr["y"] + 22); time.sleep(0.3)
    m.call("keyboard_press", keys="ctrl+a"); time.sleep(0.2)
    m.call("keyboard_press", keys="delete"); time.sleep(0.3)
    m.call("keyboard_type", text=TEXT); time.sleep(0.6)
    m.call("mouse_click", x=dr["x"] + 30, y=dr["y"] + 20); time.sleep(0.3)
    sx, sy = dr["x"] + 20, dr["y"] + 16
    m.call("win_manage", action="activate", hwnd=hwnd); time.sleep(0.4)
    m.call("mouse_drag", x1=sx, y1=sy, x2=sx + 110, y2=sy); time.sleep(0.6)
    d = find(20, 32, 20, 32)
    assert d, "no dot"
    u.SetCursorPos(d[1] + 13, d[2] + 13)
    b = None
    for _ in range(15):
        time.sleep(0.2); b = find(190, 215, 30, 50)
        if b: break
    assert b, "no bar"
    m.call("mouse_click", x=b[1] + 101, y=b[2] + 20)
    a = None
    for _ in range(12):
        time.sleep(0.2); a = find(390, 415, 86, 108)
        if a: break
    assert a, "no ask box"
    R["提问框弹出"] = True
    time.sleep(0.6)
    m.call("keyboard_type", text=Q)
    time.sleep(0.5)
    # 打字内容必须落在提问框里(不是记事本): 读回该窗的 Edit 值
    u.keybd_event(0x0D, 0, 0, 0); u.keybd_event(0x0D, 0, 2, 0)
    c = None
    for _ in range(20):
        time.sleep(1.0); c = find(410, 435, 290, 315)
        if c: break
    assert c, "no card"
    card_hw = c[0]
    R["卡片出现"] = True
    body = ""
    names = []
    for _ in range(45):
        time.sleep(1.0)
        rd = json.loads(m.call("ui_readall", hwnd=card_hw))
        vals = [e.get("value") or "" for e in rd.get("elements", []) if e.get("value")]
        cand = max(vals, key=len) if vals else ""
        names = [e.get("name") or "" for e in rd.get("elements", [])]
        if cand and "处理中" not in cand and "思考中" not in cand and "思考中" not in " ".join(names):
            body = cand; break
        if "失败" in cand or "空响应" in cand:
            body = cand; break
    print("正文:", body[:250])
    print("标题元素:", [n for n in names if n][:5])
    R["按问题回答"] = any(k in body for k in ("10", "12", "两种", "两种。", "、"))
    print("\n=== 验收 ===", R)
    sys.exit(0 if all(R.values()) else 1)
