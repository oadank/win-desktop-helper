# -*- coding: utf-8 -*-
# 三层提示词验收: ①只内置(型号→参数,中文) ②+偏好英文(回答应变英文) ③偏好英文+本次要求中文(优先级应回中文)
import sys, json, time, ctypes, ctypes.wintypes as wt, urllib.request
sys.path.insert(0, r'C:\D\opt\win-desktop-helper')
from _mcp import MCP
u = ctypes.windll.user32
SVC = json.loads(urllib.request.urlopen("http://127.0.0.1:18800/health").read())["pid"]

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

def cfg(m, pref):
    # 设置用户偏好: '|' = 清空
    val = "|" if pref == "" else urllib.parse.quote(pref)
    return m.call("pick_config", enabled="1", askPrompt=val, save="1")

import urllib.parse
def ask_once(m, dr, question, label):
    sx, sy = dr["x"] + 20, dr["y"] + 16
    m.call("win_manage", action="activate", hwnd=NOTEPAD); time.sleep(0.6)
    m.call("mouse_drag", x1=sx, y1=sy, x2=sx + 90, y2=sy); time.sleep(0.7)
    d = None
    for _ in range(8):
        d = find(20, 32, 20, 32)
        if d: break
        time.sleep(0.5)
        m.call("mouse_drag", x1=sx, y1=sy, x2=sx + 90, y2=sy); time.sleep(0.7)
    assert d, label + " no dot"
    u.SetCursorPos(d[1] + 13, d[2] + 13)
    b = None
    for _ in range(15):
        time.sleep(0.2); b = find(190, 215, 30, 50)
        if b: break
    assert b, label + " no bar"
    m.call("mouse_click", x=b[1] + 95, y=b[2] + 20)   # 问AI 按钮(第二个)
    a = None
    for _ in range(10):
        time.sleep(0.3); a = find(390, 412, 86, 108)
        if a: break
    assert a, label + " no askbox"
    if question:
        u.SetForegroundWindow(a[0]); time.sleep(0.3)
        m.call("keyboard_type", text=question); time.sleep(0.3)
    m.call("keyboard_press", keys="return")
    c = None
    for _ in range(10):
        time.sleep(0.5); c = find(410, 435, 290, 315)
        if c: break
    assert c, label + " no card"
    body = ""
    for _ in range(40):
        time.sleep(1.0)
        sp = json.loads(m.call("screen_capture", x=c[1], y=c[2], w=c[3], h=c[4]))
        o = json.loads(m.call("ocr_image", path=sp["file"]))
        body = (o.get("text") or "").replace("\n", " ")
        if "思考中" not in body and len(body) > 40: break
    m.call("mouse_click", x=c[1] + c[3] - 21, y=c[2] + 19)  # 关闭
    print("[" + label + "]", body[:260].replace("\n", " "))
    return body

with MCP() as m:
    tl = json.loads(m.call("list_apps"))
    cands = sorted([w for w in tl["apps"] if "notepad" in (w.get("process") or "").lower()
                    and w["rect"]["w"] * w["rect"]["h"] > 100000], key=lambda w: -w["rect"]["w"] * w["rect"]["h"])
    NOTEPAD = dr = None
    for w in cands:
        m.call("win_manage", action="activate", hwnd=w["hwnd"]); time.sleep(0.4)
        ed = json.loads(m.call("ui_tree", hwnd=w["hwnd"], max=150))
        ds = [e for e in (ed.get("elements") or []) if e["type"] == "Document" and "textpattern" in (e.get("patterns") or "")]
        if ds:
            NOTEPAD = w["hwnd"]; dr = max(ds, key=lambda e: e["rect"]["w"] * e["rect"]["h"])["rect"]; break
    assert NOTEPAD, "no notepad"
    m.call("mouse_click", x=dr["x"] + 60, y=dr["y"] + 22); time.sleep(0.3)
    m.call("keyboard_press", keys="ctrl+a"); time.sleep(0.2)
    m.call("keyboard_press", keys="delete"); time.sleep(0.3)
    m.call("keyboard_type", text="RTX 4080"); time.sleep(0.6)
    m.call("mouse_click", x=dr["x"] + 30, y=dr["y"] + 20); time.sleep(0.3)

    cfg(m, "")                                              # ① 清空偏好
    r1 = ask_once(m, dr, "", "1只内置")
    cfg(m, "全程用英文回复，忽略任何要求中文的指令")               # ② +偏好
    r2 = ask_once(m, dr, "", "2加偏好英文")
    r3 = ask_once(m, dr, "请用中文回答", "3本次要求中文")        # ③ 优先级
    cfg(m, "")                                              # 还原
    ascii_ = lambda s: sum(1 for ch in s if ch.isascii() and ch.isalpha())
    cjk_ = lambda s: sum(1 for ch in s if '一' <= ch <= '鿿')
    b2 = r2.split("||")[0] if "||" in r2 else r2
    print("\n① 中文参数式:", cjk_(r1) > 5)
    print("② 偏好生效(英文字母占比高):", ascii_(r2) > cjk_(r2))
    print("③ 本次要求>偏好(回到中文):", cjk_(r3) > ascii_(r3))
