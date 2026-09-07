# -*- coding: utf-8 -*-
"""划词悬浮球 v2 验收: 小点 / hover 展开 / 问AI / 卡片两键 / 拖动 / 关闭"""
import sys, time, json, ctypes, urllib.request, os
import ctypes.wintypes as wt
sys.path.insert(0, '.')
from _mcp import MCP
from PIL import Image
from collections import Counter

user32 = ctypes.windll.user32
pid = json.loads(urllib.request.urlopen("http://127.0.0.1:18800/health").read())["pid"]


def wins():
    out = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, l):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h):
            r = wt.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            if r.left > 0 and r.top > 0:
                out.append((int(h), r.left, r.top, r.right - r.left, r.bottom - r.top))
        return True

    user32.EnumWindows(cb, 0)
    return out


def pick(wlo, whi, hlo, hhi):
    for w in wins():
        if wlo <= w[3] <= whi and hlo <= w[4] <= hhi:
            return w
    return None


def logtail(a):
    with open("shot-service.log", encoding='utf-8', errors='replace') as f:
        f.seek(a)
        return f.read().replace("\r", "").strip().replace("\n", " | ")


def sz():
    return os.path.getsize("shot-service.log")


R = {}
# 防干扰: 记下 WorkBuddy 自己的顶层窗, pick 时排除(它浮在上面会抢 UIA 选区/挡住鼠标)
with MCP() as m0:
    tl0 = json.loads(m0.call("win_manage", action="list"))
    wb_hwnds = set()
    for w in tl0["apps"]:
        if "WorkBuddy" in (w.get("process") or "") or "workbuddy" in (w.get("title") or "").lower():
            wb_hwnds.add(int(w["hwnd"]))
print("排除 WorkBuddy 窗:", wb_hwnds)
_orig_pick = pick
def pick(wlo, whi, hlo, hhi):
    for w in wins():
        if w[0] in wb_hwnds:
            continue
        if wlo <= w[3] <= whi and hlo <= w[4] <= hhi:
            return w
    return None
with MCP() as m:
    tl = json.loads(m.call("win_manage", action="list"))
    npd = [w for w in tl["apps"] if w["process"] == "Notepad"][0]
    r = npd["rect"]
    tx, ty = r["x"] + 60, r["y"] + 120
    m.call("win_manage", action="activate", hwnd=npd["hwnd"])
    time.sleep(0.5)
    m.call("mouse_click", x=tx, y=ty)
    time.sleep(0.3)
    m.call("keyboard_type", text="Neural networks learn representations from data")
    time.sleep(0.4)

    off = sz()
    m.call("mouse_drag", x1=tx + 20, y1=ty, x2=tx + 320, y2=ty)
    time.sleep(0.4)
    d = pick(20, 30, 20, 30)
    R["1小点"] = bool(d)
    print("[1] 小点:", d)

    # hover 停留 (SetCursorPos 即可, hover 现在走 GetCursorPos)
    user32.SetCursorPos(d[1] + 13, d[2] + 13)
    b = None
    for k in range(10):
        time.sleep(0.3)
        b = pick(190, 210, 30, 50)
        if b:
            break
    R["2hover展开"] = bool(b)
    print("[2] hover 300ms 工具条:", b)
    if b:
        sp = json.loads(m.call("screen_capture", x=b[1], y=b[2], w=b[3], h=b[4]))
        im = Image.open(sp["file"]).convert('RGB')
        px = im.load()
        cnt = Counter(px[x, y] for y in range(0, im.size[1], 2) for x in range(0, im.size[0], 2))
        print("    工具条主色:", cnt.most_common(4))

        # 点"问 AI"(第2个按钮: 本地 x 72..133 -> 中心 +101)
        off = sz()
        m.call("mouse_click", x=b[1] + 101, y=b[2] + 20)
        got = None
        for k in range(14):
            time.sleep(1.0)
            got = pick(410, 430, 290, 310)
            if got:
                break
        R["3问AI卡片"] = bool(got)
        print("[3] 卡片(第%d秒):" % (k + 1), got)
        print("    日志:", logtail(off)[-300:])
        if got:
            c0 = got
            for k in range(10):
                time.sleep(1.0)
                cur = pick(410, 430, 290, 310)
                if cur is None:
                    break
                c0 = cur
            sp = json.loads(m.call("screen_capture", x=c0[1], y=c0[2], w=c0[3], h=c0[4]))
            ocr = json.loads(m.call("ocr_image", path=sp["file"]))
            txt = ocr.get("text", "") if isinstance(ocr, dict) else str(ocr)
            print("[4] 卡片OCR:", txt[:220].replace("\n", " / "))
            R["4渲染"] = ("AI 回答" in txt) or ("复制结果" in txt)

            # 拖动: 按住标题栏向右下拖 120,60
            before = pick(410, 430, 290, 310)
            ax, ay = before[1] + 60, before[2] + 18
            off = sz()
            m.call("mouse_drag", x1=ax, y1=ay, x2=ax + 120, y2=ay + 60)
            time.sleep(0.7)
            after = pick(410, 430, 290, 310)
            print("[5] 拖动 %s -> %s" % (before[1:3], after[1:3] if after else None))
            print("    日志:", logtail(off)[-200:])
            R["5拖动"] = bool(after) and (after[1] != before[1] or after[2] != before[2])

            # 关闭按钮(右上)
            if after:
                off = sz()
                m.call("mouse_click", x=after[1] + 420 - 21, y=after[2] + 19)
                time.sleep(0.6)
                R["6关闭"] = pick(410, 430, 290, 310) is None
                print("[6] 关闭后卡片:", pick(410, 430, 290, 310), "|", logtail(off)[-150:])

print("\n=== 验收 === %s" % R)
