# -*- coding: utf-8 -*-
import sys, json
sys.path.insert(0, '.')
from _mcp import MCP

entry = """划词悬浮球(shot-pick*.cs)与常驻浮窗调试五大坑 (2026-09-07 全部实测):
1. WinForms CreateParams 里手加 WS_EX_LAYERED(0x80000) = 整窗隐形(窗口存在/IsWindowVisible=1 但屏幕全空)。圆点/圆角一律用 TransparencyKey 画。
2. 免激活窗(ShowWithoutActivation+WS_EX_NOACTIVATE|TOOLWINDOW)收不到 MouseEnter/Click —— hover计时/点击/拖动必须由低级鼠标钩子 DOWN/UP + 200ms 心跳 GetCursorPos 轮询驱动。WM_MOUSEMOVE 不进本机低级钩子(探针实锤 moves=0)。
3. 构造函数 TopMost=true 在免激活窗上会被丢弃 —— Show() 后必须 SetWindowPos(HWND_TOPMOST,...)，心跳每4轮补置顶。
4. 剪贴板取词法发全局 Ctrl+C 有投错窗口风险(实测把词粘进抖音评论区) —— 安全阀: 前台窗变化或指针离开按下点24px即拒走; 等待循环内前台变化立刻 abort。
5. 给本服务加 MCP 工具三处都要动: shot-service.cs 的 McpCall switch case + McpToolsJson 条目 + **mcp-bridge.js 注册表数组与 buildUrl 路由**(bridge 有独立白名单, 漏改直接 unknown tool)。
配置入口: 设置页「划词」节 / HTTP GET /pick-config?enabled=0|1 / MCP pick_config。开关立即生效+持久化到 shot-service.json pick 节。"""

with MCP() as m:
    r = m.call('update_skill', title='划词悬浮球五大坑与配置入口', entry=entry)
    print(r)
