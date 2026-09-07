# -*- coding: utf-8 -*-
"""问AI 提问框端到端(重写):
准备: 自己开记事本 -> 打字 -> 用 UIA 确认文本已落窗(不再拿旧脚本的"两个记事本"猜)
A) 划词 -> 小点 -> 问AI -> 输入问题回车 => 按问题回答
B) 划词 -> 问AI -> 直接回车          => 内置默认提示词
C) 划词 -> 问AI -> Esc               => 取消, 无卡片
D) 单击(不划选)落在词上               => 也弹小点
E) 小点已显示时单击别处空白            => 小点不消失(不被 dismiss)
"""
import sys, time, json, ctypes, urllib.request
import ctypes.wintypes as wt
sys.path.insert(0, '.')
from _mcp import MCP

user32 = ctypes.windll.user32
pid = json.loads(urllib.request.urlopen("http://127.0.0.1:18800/health").read())["pid"]
VK_RETURN, VK_ESCAPE = 0x0D, 0x1B
KEYUP = 0x0002
SVC = pid

TEXT = "RTX 3080Ti 是安培架构显卡"


def find(wlo, whi, hlo, hhi):
    out = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, l):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == SVC and user32.IsWindowVisible(h):
            r = wt.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            if r.left > 0 and wlo <= r.right - r.left <= whi and hlo <= r.bottom - r.top <= hhi:
                out.append((r.left, r.top, r.right - r.left, r.bottom - r.top))
        return True
    user32.EnumWindows(cb, 0)
    return out[0] if out else None


def dot():
    return find(20, 32, 20, 32)


def bar():
    return find(190, 215, 30, 50)


def askbox():
    return find(390, 415, 86, 108)


def card():
    return find(410, 435, 290, 315)


def press(vk):
    user32.keybd_event(vk, 0, 0, 0)
    user32.keybd_event(vk, 0, KEYUP, 0)


def wait_card(m, secs=30):
    c = None
    txt = ""
    for _ in range(secs):
        time.sleep(1.0)
        c = card()
        if c:
            break
    if not c:
        return None, ""
    for _ in range(12):
        time.sleep(1.0)
        sp = json.loads(m.call("screen_capture", x=c[0], y=c[1], w=c[2], h=c[3]))
        ocr = json.loads(m.call("ocr_image", path=sp["file"]))
        txt = (ocr.get("text", "") if isinstance(ocr, dict) else str(ocr)).replace("\n", " ")
        if "思考中" not in txt and len(txt) > 6:
            return c, txt
    return c, txt


def close_card(m):
    c = card()
    if c:
        m.call("mouse_click", x=c[0] + 420 - 21, y=c[1] + 19)
        time.sleep(0.6)


R = {}
with MCP() as m:
    # ---------- 准备: 记事本 + 确认文本真的落窗 ----------
    r = json.loads(m.call("app_run", path="notepad.exe", args="", wait=2500, process="Notepad"))
    assert r.get("ok"), "app_run failed: %s" % r
    time.sleep(0.8)
    # Win11 记事本会派生一堆"快捷键提示"小窗 + 标签页容器, 只有主窗有 Document+TextPattern。
    # 逐个试(大窗优先), 别赌 app_run 返回的那个 hwnd。
    tl = json.loads(m.call("list_apps"))
    cands = sorted([w for w in tl["apps"]
                    if "notepad" in (w.get("process") or "").lower()
                    and (w["rect"]["w"] * w["rect"]["h"]) > 100000],
                   key=lambda w: -w["rect"]["w"] * w["rect"]["h"])
    hwnd, doc = None, None
    for w in cands:
        m.call("win_manage", action="activate", hwnd=w["hwnd"])
        time.sleep(0.4)
        ed = json.loads(m.call("ui_tree", hwnd=w["hwnd"], max=120))
        ds = [e for e in (ed.get("elements") or [])
              if e["type"] == "Document" and "textpattern" in (e.get("patterns") or "")]
        if ds:
            hwnd, doc = w["hwnd"], max(ds, key=lambda e: e["rect"]["w"] * e["rect"]["h"])
            break
    assert doc, "no Notepad window exposes UIA Document+TextPattern (tried %s)" % [w["hwnd"] for w in cands]
    print("[准备] hwnd=%s doc=%s" % (hwnd, doc["rect"]))
    dr = doc["rect"]
    # 编辑区左上角起笔(点 Document 区内), 清空后输入
    m.call("mouse_click", x=dr["x"] + 60, y=dr["y"] + 22)
    time.sleep(0.3)
    m.call("keyboard_press", keys="ctrl+a"); time.sleep(0.2)
    m.call("keyboard_press", keys="delete"); time.sleep(0.3)
    m.call("keyboard_type", text=TEXT)
    time.sleep(0.7)
    # 校验: 用剪贴板读记事本全文(全选+复制是本地窗口内操作, 安全)
    m.call("keyboard_press", keys="ctrl+a"); time.sleep(0.3)
    m.call("keyboard_press", keys="ctrl+c"); time.sleep(0.4)
    got = json.loads(m.call("clipboard_get")).get("text", "")
    assert TEXT in got, "text not in Notepad (got=%r)" % got[:60]
    R["准备:文本落窗"] = True
    # 单击一处取消选区, 免得后续划词被残留选区干扰
    m.call("mouse_click", x=dr["x"] + 40, y=dr["y"] + 22)
    time.sleep(0.4)

    # 定位首行文字真实像素位置(别猜行高): 从 doc 顶部往下扫条带 OCR, 找到含 "3080Ti" 的那条
    sy = None
    for band in range(0, 150, 22):
        y0 = dr["y"] + band
        sp = json.loads(m.call("screen_capture", x=dr["x"], y=y0, w=min(500, dr["w"]), h=22))
        o = json.loads(m.call("ocr_image", path=sp["file"]))
        t = (o.get("text") or "").replace(" ", "")
        if "3080" in t:
            sy = y0 + 11
            print("[准备] 首行 OCR band=%d -> y=%d txt=%r" % (band, sy, t))
            break
    if sy is None:
        sy = dr["y"] + 18
        print("[准备] OCR 未命中, 用估计行 y=%d" % sy)
    sx = dr["x"] + 20   # 划选起点(RTX 起始处)

    def select_words():
        m.call("win_manage", action="activate", hwnd=hwnd)
        time.sleep(0.4)
        m.call("mouse_drag", x1=sx, y1=sy, x2=sx + 110, y2=sy)
        time.sleep(0.6)

    def open_bar():
        d = dot()
        assert d, "no dot after select"
        user32.SetCursorPos(d[0] + 13, d[1] + 13)
        b = None
        for _ in range(15):
            time.sleep(0.2)
            b = bar()
            if b:
                return d, b
        raise AssertionError("no bar after hover")

    # ---------- A) 输入问题 -> 回车 ----------
    select_words()
    d, b = open_bar()
    m.call("mouse_click", x=b[0] + 101, y=b[1] + 20)   # 问 AI (中按钮)
    a = None
    for _ in range(12):
        time.sleep(0.2)
        a = askbox()
        if a:
            break
    R["A提问框弹出"] = bool(a)
    assert a, "A no ask box"
    time.sleep(0.6)
    m.call("keyboard_type", text="它一共有几种显存容量? 只回答数字")
    time.sleep(0.5)
    press(VK_RETURN)
    c1, txt1 = wait_card(m)
    print("[A] 卡片:", txt1[:200])
    R["A提问框已关闭"] = askbox() is None
    R["A按问题回答"] = bool(c1) and any(k in txt1 for k in ("10", "12", "20", "GB", "种"))
    close_card(m)

    # ---------- B) 留空回车 -> 默认提示词 ----------
    select_words()
    d, b = open_bar()
    m.call("mouse_click", x=b[0] + 101, y=b[1] + 20)
    a = None
    for _ in range(12):
        time.sleep(0.2)
        a = askbox()
        if a:
            break
    R["B提问框再弹"] = bool(a)
    assert a, "B no ask box"
    time.sleep(0.5)
    press(VK_RETURN)
    c2, txt2 = wait_card(m)
    print("[B] 默认回答:", txt2[:200])
    R["B默认回答"] = bool(c2) and any(k in txt2 for k in ("3080", "安培", "显卡", "Ampere"))
    close_card(m)

    # ---------- C) Esc 取消 ----------
    select_words()
    d, b = open_bar()
    m.call("mouse_click", x=b[0] + 101, y=b[1] + 20)
    a = None
    for _ in range(12):
        time.sleep(0.2)
        a = askbox()
        if a:
            break
    assert a, "C no ask box"
    time.sleep(0.5)
    press(VK_ESCAPE)
    time.sleep(1.0)
    R["C取消无卡片"] = (askbox() is None) and (card() is None)
    time.sleep(1.0)

    # ---------- D) 单击落在词上 -> 也弹小点 ----------
    m.call("win_manage", action="activate", hwnd=hwnd)
    time.sleep(0.5)
    m.call("mouse_click", x=sx + 30, y=sy)      # 点在 "3080Ti" 里
    d = None
    for _ in range(12):
        time.sleep(0.2)
        d = dot()
        if d:
            break
    R["D单击弹球"] = bool(d)
    print("[D] 单击后小点:", d)
    if not d:
        lg = open("shot-service.log", encoding="utf-8", errors="replace").read()
        print("[D] 日志尾部:", lg.strip().splitlines()[-6:])

    # ---------- E) 小点显示期间单击别处(不 dismiss) ----------
    if d:
        user32.SetCursorPos(d[0] + 13, d[1] + 13)   # 让它展开成工具条
        bb = None
        for _ in range(15):
            time.sleep(0.2)
            bb = bar()
            if bb:
                break
        if bb:
            # 单击离工具条很远的空白处(记事本下方空区)
            m.call("mouse_click", x=dr["x"] + 700, y=dr["y"] + 500)
            time.sleep(0.4)
            R["E点击不消失"] = bar() is not None
            print("[E] 点击空白后工具条:", bar())
        else:
            R["E点击不消失"] = False

print("\n=== 验收 ===")
for k, v in R.items():
    print(("  OK  " if v else "  FAIL"), k)
sys.exit(0 if all(R.values()) else 1)
