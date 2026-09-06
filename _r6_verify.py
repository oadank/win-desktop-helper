# -*- coding: utf-8 -*-
# 第六轮修复全量复测
import json, subprocess, time, urllib.request, os, glob

BASE = 'http://127.0.0.1:18800'
def get(url):
    try:
        return json.loads(urllib.request.urlopen(url, timeout=40).read().decode('utf-8'))
    except Exception as e:
        return {'ok': False, 'error': str(e)}

def alive():
    return subprocess.run(['tasklist', '/FI', 'IMAGENAME eq shot-service.exe'], capture_output=True).stdout.decode('gbk', 'ignore').count('shot-service') > 0

R = []

# R2: 并发 6 路 start -> 恰 1 ok 5 already; stop 后无 ffmpeg 孤儿
concl = []
def start():
    concl.append(get(BASE + '/record/start?x=100&y=100&w=400&h=300'))
ts = [__import__('threading').Thread(target=start) for _ in range(6)]
[t.start() for t in ts]; [t.join() for t in ts]
okn = sum(1 for c in concl if c.get('ok'))
files = set(c.get('file') for c in concl if c.get('ok'))
get(BASE + '/record/stop'); time.sleep(1.5)
orphans = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq ffmpeg.exe'], capture_output=True).stdout.decode('gbk', 'ignore').count('ffmpeg')
R.append(('R2 并发6路=1成功+无孤儿', okn == 1 and len(files) == 1 and orphans == 0, 'ok=%d files=%d ffmpeg_left=%d' % (okn, len(files), orphans)))

# R3: 奇数规整回显 video_size
d = get(BASE + '/record/start?x=0&y=0&w=801&h=601')
R.append(('R3 video_size 回显', d.get('video_size') == '800x600', str(d.get('video_size'))))
get(BASE + '/record/stop'); time.sleep(0.8)

# R4: fps 规整标记
d = get(BASE + '/record/start?x=0&y=0&w=400&h=300&fps=9999')
R.append(('R4 fpsNormalized 标记', d.get('fps') == 10 and d.get('fpsNormalized') is True, 'fps=%s norm=%s' % (d.get('fps'), d.get('fpsNormalized'))))
get(BASE + '/record/stop'); time.sleep(0.8)

# W1: 负坐标 clamp + 零宽高拒绝 + 回带 rect (记事本必须走 /app/run, Start-Process 会被 Store 启动器秒退)
ar = get(BASE + '/app/run?path=notepad.exe')
hw = (ar.get('window') or {}).get('hwnd')
R.append(('notepad hwnd 就绪', hw is not None, str(hw)))
d = get(BASE + '/win/move?hwnd=%s&x=-5000&y=-5000&w=800&h=600' % hw)
R.append(('W1a 负坐标 clamp+回rect', d.get('ok') and d.get('clamped') and d['rect']['x'] >= 0 and d['rect']['y'] >= 0, json.dumps(d.get('rect'), ensure_ascii=False)))
d = get(BASE + '/win/move?hwnd=%s&x=100&y=100&w=0&h=0' % hw)
R.append(('W1b w/h<50 拒绝', d.get('ok') is False, str(d.get('error'))[:50]))

# W2: 最小化先 restore
get(BASE + '/win/min?hwnd=%s' % hw); time.sleep(0.5)
d = get(BASE + '/win/move?hwnd=%s&x=200&y=200&w=700&h=500' % hw)
R.append(('W2 最小化 move restoredFirst', d.get('restoredFirst') is True and d['rect']['x'] == 200 and d['rect']['w'] == 700, json.dumps(d.get('rect'), ensure_ascii=False)))

# W3: 无效句柄 close
d = get(BASE + '/win/close?hwnd=99999999')
R.append(('W3 无效句柄 close 拒绝', d.get('ok') is False, str(d.get('error'))[:40]))
d = get(BASE + '/win/close?hwnd=%s' % hw)

# /ocr: 用之前 MD5 入库的图 (含"测试abc123"等字的截图太大慢; 用记事本截图)
time.sleep(0.5)
subprocess.run(['powershell', '-Command', 'Start-Process notepad'], capture_output=True); time.sleep(1.5)
get(BASE + '/win/activate?title=Notepad'); time.sleep(0.4)
get(BASE + '/ui/set?title=Notepad&name=%E6%96%87%E6%9C%AC%E7%BC%96%E8%BE%91%E5%99%A8&value=OCR%E7%AB%AF%E7%82%B9%E9%AA%8C%E8%AF%81')
time.sleep(0.4)
d = get(BASE + '/shot?window=Notepad')
png = d.get('file') or ''
import urllib.parse
d = get(BASE + '/ocr?path=' + urllib.parse.quote(png)) if png else {'ok': False, 'error': 'no shot file'}
R.append(('/ocr 端点识别', d.get('ok') is True and 'OCR' in (d.get('text') or '').upper(), 'chars=%s text=%r' % (d.get('chars'), (d.get('text') or '')[:30])))
# /ocr 安全: 越界路径拒绝
d = get(BASE + '/ocr?path=' + urllib.parse.quote(r'C:\Windows\win.ini'))
R.append(('/ocr 越界路径拒绝', d.get('ok') is False, str(d.get('error'))[:40]))

# /pin: 贴图 + 越界拒绝
d = get(BASE + '/pin?path=' + urllib.parse.quote(png) + '&x=300&y=300')
R.append(('/pin 贴图 ok', d.get('ok') is True and d['rect']['x'] == 300, json.dumps(d.get('rect'), ensure_ascii=False)))
d = get(BASE + '/pin?path=' + urllib.parse.quote(r'C:\Windows\win.ini'))
R.append(('/pin 越界拒绝', d.get('ok') is False, str(d.get('error'))[:40]))
time.sleep(1.0)
# 关掉贴图窗验证存在过 (双击关闭太重, 直接杀 notepad 连带)
print('==== 第六轮复测 ====')
allok = True
for name, ok, detail in R:
    print(('PASS' if ok else 'FAIL'), '|', name, '|', detail)
    if not ok: allok = False
print('====', 'ALL PASS' if allok else 'HAS FAIL', '====', 'service alive:', alive())
