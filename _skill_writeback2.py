# -*- coding: utf-8 -*-
import sys
sys.path.insert(0, '.')
from _mcp import MCP

entry = """免激活浮窗"拖动不跟手"根因与修法 (2026-09-07 划词卡片实测, 跟随率 20/20=100%):
不是电脑卡 —— 是跟随机制慢半拍。低级钩子在本机收不到 WM_MOUSEMOVE(moves=0), 拖动期间缓存的坐标是按下瞬间的陈旧值, 且只在 200ms 心跳应用一次 = 一步一卡。
修法三件套:
1. 拖动中直接 GetCursorPos 实时取坐标, 不依赖钩子 MOVE;
2. Timer 心跳拖动期加密到 15ms(平时 200ms), 起拖瞬间立刻切 Interval 不等下一轮;
3. 移动用单次 SetWindowPos(位置+HWND_TOPMOST 一步完成), 坐标没变整个跳过。
验收脚本 _pick_drag_perf.py: 手动 mouse_event DOWN + 20 步 SetCursorPos + UP, 后台 5ms 采样卡片 rect, 数位置变化档位(>=12 为跟手)。别用 mouse_drag 工具测跟手度——它本身一步到位看不出卡顿。"""

with MCP() as m:
    print(m.call('update_skill', title='免激活浮窗拖动不跟手的根因与修法', entry=entry))
