# -*- coding: utf-8 -*-
"""强断言验证 askPrompt 真的传到模型: 要求回答首字符为★"""
import sys, time, json, ctypes, urllib.request
import ctypes.wintypes as wt
sys.path.insert(0, '.')
from _mcp import MCP

user32 = ctypes.windll.user32
pid = json.loads(urllib.request.urlopen("http://127.0.0.1:18800/health").read())["pid"]
MARK = "★"
PROMPT = "硬性格式要求: 你的回答第一个字符必须是 %s , 之后再用一句话解释。不要任何前缀。" % MARK

def find(wlo, whi, hlo, hhi):
    out = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, l):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h):
            r = wt.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            if r.left > 0 and wlo <= r.right - r.left <= whi and hlo <= r.bottom - r.top <= hhi:
                out.append((r.left, r.top, r.right - r.left, r.bottom - r.top))
        return True
    user32.EnumWindows(cb, 0)
    return out[0] if out else None

with MCP() as m:
    r = json.loads(m.call('pick_config', askPrompt=PROMPT))
    assert r['askPrompt'] == PROMPT

    tl = json.loads(m.call("win_manage", action="list"))
    npd = [w for w in tl["apps"] if w["process"] == "Notepad"][0]
    rect = npd["rect"]
    tx, ty = rect["x"] + 60, rect["y"] + 160
    m.call("win_manage", action="activate", hwnd=npd["hwnd"])
    time.sleep(0.5)
    m.call("mouse_click", x=tx, y=ty)
    time.sleep(0.3)
    m.call("keyboard_type", text="牛顿第三定律说的是作用力与反作用力")
    time.sleep(0.5)
    m.call("mouse_drag", x1=tx + 20, y1=ty, x2=tx + 300, y2=ty)
    time.sleep(0.5)
    d = find(20, 30, 20, 30)
    assert d, "no dot"
    user32.SetCursorPos(d[0] + 13, d[1] + 13)
    bar = None
    for _ in range(12):
        time.sleep(0.2)
        bar = find(190, 210, 30, 50)
        if bar:
            break
    assert bar, "no bar"
    m.call("mouse_click", x=bar[0] + 101, y=bar[1] + 20)
    txt = ""
    got = None
    for k in range(30):
        time.sleep(1.0)
        c = find(410, 430, 290, 310)
        if not c:
            continue
        got = c
        sp = json.loads(m.call("screen_capture", x=c[0], y=c[1], w=c[2], h=c[3]))
        ocr = json.loads(m.call("ocr_image", path=sp["file"]))
        txt = (ocr.get("text", "") if isinstance(ocr, dict) else str(ocr))
        if "原文" in txt and "思考中" not in txt:
            break
    print("[%ds] 卡片文本:\n%s" % (k + 1, txt[:300]))
    hit = MARK in txt
    # 复位
    m.call('pick_config', askPrompt='|')
    if got:
        m.call("mouse_click", x=got[0] + 420 - 21, y=got[1] + 19)
print("\n=== 附加提示词生效(回答含%s): %s ===" % (MARK, hit))
sys.exit(0 if hit else 1)
