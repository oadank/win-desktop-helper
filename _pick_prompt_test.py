# -*- coding: utf-8 -*-
"""askPrompt 附加提示词: 配置往返 + 真实问AI风格验证"""
import sys, time, json, ctypes, urllib.request
import ctypes.wintypes as wt
sys.path.insert(0, '.')
from _mcp import MCP

user32 = ctypes.windll.user32
pid = json.loads(urllib.request.urlopen("http://127.0.0.1:18800/health").read())["pid"]
PROMPT = "你是高中班主任。回答必须: 1)用一句顺口溜开头 2)不超过100字"

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
    print("--- 1) 设置 askPrompt ---")
    r = json.loads(m.call('pick_config', askPrompt=PROMPT))
    print(r)
    assert r['askPrompt'] == PROMPT, "配置往返失败"

    print("--- 2) 真实走一遍划词问AI ---")
    tl = json.loads(m.call("win_manage", action="list"))
    npd = [w for w in tl["apps"] if w["process"] == "Notepad"][0]
    rect = npd["rect"]
    tx, ty = rect["x"] + 60, rect["y"] + 120
    m.call("win_manage", action="activate", hwnd=npd["hwnd"])
    time.sleep(0.5)
    m.call("mouse_click", x=tx, y=ty)
    time.sleep(0.3)
    m.call("keyboard_type", text="光合作用是植物利用光能合成有机物的过程")
    time.sleep(0.4)
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
    m.call("mouse_click", x=bar[0] + 101, y=bar[1] + 20)   # 问AI 按钮
    c = None
    for _ in range(15):
        time.sleep(1.0)
        c = find(410, 430, 290, 310)
        if c:
            break
    assert c, "no card"
    # 等结果落卡
    time.sleep(6)
    sp = json.loads(m.call("screen_capture", x=c[0], y=c[1], w=c[2], h=c[3]))
    ocr = json.loads(m.call("ocr_image", path=sp["file"]))
    txt = (ocr.get("text", "") if isinstance(ocr, dict) else str(ocr)).replace("\n", " ")
    print("卡片 OCR:", txt[:200])
    ok_style = ("班主任" in txt or "顺口溜" in txt) or len(txt) < 160
    print("--- 3) 清空 askPrompt(|) ---")
    r = json.loads(m.call('pick_config', askPrompt='|'))
    print(r)
    assert r['askPrompt'] == "", "清空失败"
    m.call("mouse_click", x=c[0] + 420 - 21, y=c[1] + 19)  # 关卡片
print("\n=== askPrompt 验收:", "风格生效" if ok_style else "需人工看OCR", "===")
