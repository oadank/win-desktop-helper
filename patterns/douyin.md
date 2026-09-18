<!-- 由 SKILL.md 瘦身拆分(2026-09-19): 内容原文逐字搬移未改写。主手册见 ../SKILL.md, 瘦身前全文见 ../SKILL-ARCHIVE-20260919.md -->

## 抖音类 Electron 托盘应用「窗口打不开、只剩托盘图标」的处置（2026-09-12 实测）

**症状**：窗口点不出来，托盘图标还在，任务栏也没按钮。

**诊断链（实测）**：
1. `tasklist /v` 看主进程状态 → 出现 `Not Responding`（连采 3 次确认不是抽风）
2. `win_manage(action=listall)` → 主窗口**存在**（Chrome_WidgetWin_1，标题 douyin），rect 正常在屏幕内，但 `visible:false`（应用自己隐藏的，抖音是「关窗=收进托盘」设计）
3. `window_info(process=douyin)` 返回 `window not found` —— 它只认可见窗口，**not found 可反推可见性**
4. 内存判活：假死时 8 个 douyin 进程全是 3~30MB；正常活体是 200~470MB。**内存小得离谱 = 空壳假死**

**🔴 关键坑：卡死的窗口不能软唤起，试了白费时间**
- `win_manage(action=restore, hwnd=...)` 直接返回 **helper request timeout** —— ShowWindow/SetForegroundWindow 是 SendMessage 语义，目标 UI 线程不处理消息就阻塞到超时（helper 本身没坏，`/health` 正常）
- `tray_click(name=应用名)` 也无用，Win+B 轨迹里根本扫不到该图标（窗口隐藏时任务栏按钮同样不存在，连着 two 个都没有）
- 结论：**`visible:false` + 主进程 `Not Responding` = 软办法没救，别反复试，直接重启客户端**

**处置（实测 30 秒修好）**：
1. `taskkill /F /IM douyin.exe /IM douyin_tray.exe /IM douyin_widget.exe /IM douyin_guard.exe`（guard 是保活进程，要一起杀）
2. `explorer.exe "<exe 完整路径>"` 启动 —— **别用 Start-Process**（工具进程树会回收子进程）
3. 验证两道：`window_info(process=douyin)` 返回 hwnd（修好信号）；再 `screen_capture(window=标题)` + 看图确认画面真渲染（排除白板），别只看窗口存在

**拿进程 exe 路径**（Win11 已移除 wmic）：python ctypes `OpenProcess(0x1000)` + `QueryFullProcessImageNameW` 一把拿全。
本机抖音 8.5.301：`C:\Program Files (x86)\ByteDance\douyin\douyin.exe`（tray/widget 在 `<安装目录>\<版本号>\tray\`）。

**预防**：抖音设置里把「关闭主窗口」改成退出程序而非最小化到托盘。
