<!-- 由 SKILL.md 瘦身拆分(2026-09-19): 内容原文逐字搬移未改写。主手册见 ../SKILL.md, 瘦身前全文见 ../SKILL-ARCHIVE-20260919.md -->

## Electron 托盘唤回与弹窗点击实测修正（2026-09-09）

本次实测 WorkBuddy 每日签到弹窗（Buddy加油站），修正技能里两条旧写法：

1. `tray_click` 对 Electron 托盘程序：用单 Enter，别用 double=1
   - `tray_click(name="WorkBuddy", double=1)` 在服务端实现为 Win+B → ←/→ 扫描 → Enter×2。
   - 对 Electron/大 DOM 类托盘图标，Enter×2 等同于双击，会把刚显示的窗口再次隐藏。
   - 正确做法：`tray_click(name="WorkBuddy", double=0)`，Win+B → ←/→ 扫描 → Enter 单点。

2. Electron 弹窗按钮：UIA 拿不到，必须截图 + 图像处理算按钮中心
   - `ui_find(hwnd=..., name="立即领取")` 报 UIA timeout 8000ms（Electron 限制）。
   - 禁用 UIA 后，唯一可行的是坐标点击，但肉眼估算会偏。
   - 稳定做法：
     a. `win_manage(action=snap, pos=left, hwnd=...)` 把窗口贴到固定位置。
     b. `screen_capture(x=0, y=1100, w=500, h=280)` 截取弹窗区域。
     c. Python + Pillow 二值化：灰度 → threshold>210 → 找最大连通域 → 过滤宽高比 1.2~8、面积>300 → 得按钮 bbox → 中心即落点。
   - 本次实测 WorkBuddy 5.5.3，主屏 2560×1440、贴左后半屏后，Buddy加油站 弹窗"立即领取"按钮中心在屏幕 **(254, 1254)**。

验证：OCR 文字从"立即领取"变为"今日已领"即成功。
