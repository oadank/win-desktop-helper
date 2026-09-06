【第八轮·补充更正】workbuddy 自查纠错 + 又抓到 1 个真 bug（F1）

先更正我上一条报告的错误结论：我说"强 UI 三域（遮罩/设置/工具条）我打不动、SendInput 触发不了全局热键、只能你人工测"——**这句是我错了，撤回**。
真实原因：我的探针用"固定 sleep + 按窗口标题过滤"检测遮罩，而遮罩是**无标题全屏窗**、弹出时机在毫秒级，被我漏掉了。改成"轮询 shot-service 名下可见窗口尺寸"后，一次就抓到 overlay shown。
纠正后实测确认：/keyboard/press 注入 Ctrl+Shift+S **能稳定弹遮罩**（连测 3 次全中，日志 capture: overlay shown bounds={0,0,2560,1440}）。所以遮罩链路我能黑盒测了。下面就是测出来的。

== 复验通过（PASS，无 bug）==
M1 重入防护：遮罩开着再注入热键 → 日志 capture: busy, ignore，仅 1 个 overlay，不叠窗。✓
M2 Esc 取消（焦点在遮罩时）：capture: cancelled→closed，captureBusy 正确复位，可再次弹出。✓
M3 拖框选区：mouse 拖 → mouseup sel=550x240 → toolbar shown。✓
双击复制 use-after-dispose（侦察报告的头号候选）：**撤回不成立**。双击 → 日志 dblclick=copy→copied，clip-watcher 随即记录 clip image captured 300x200 md5=801CDBA1——位图完整可读、尺寸正确、无损坏。.NET Framework 的 Clipboard.SetImage 在 using 块内已同步序列化进剪贴板，dispose 掉本地 bmp 无副作用。✓

== 新发现 F1【P2 可用性 DoS：焦点被抢→全屏遮罩滞留+区域截图拒绝服务】==
复现（全程黑盒可复现，已实测）：
  1. 注入 Ctrl+Shift+S → 全屏遮罩弹出（TopMost，盖住整个 2560×1440）
  2. /win/activate 激活任意其它窗口（=模拟"任何弹窗抢焦点"：UAC/托盘气泡/另一个 TopMost 工具/ResultForm）→ 前台变 ZCode
  3. 注入 Esc → **遮罩纹丝不动**（Esc 发给抢焦点方，遮罩收不到），capture 日志停在 overlay shown，无 closed
  4. 再注入 Ctrl+Shift+S → capture: busy, ignore（captureBusy 仍 true）→ 区域截图功能对全体调用方**拒绝服务**，且屏幕被半透明全屏遮罩糊住
根因（源码 shot-capture.cs）：遮罩无 OnDeactivate 自动关闭逻辑（侦察 grep 全库无果，与实测吻合）；captureBusy 复位**只挂在 OnFormClosed**（:1845）。所以"失焦但没关"= 遮罩永远挂在那、busy 永远不复位。
恢复代价实测：只能靠**鼠标**（鼠标不依赖键盘焦点）——
  · 无选区时：右键点遮罩任意处 → 一次即 CancelAll 救回
  · 已拖出选区时：右键**选区内**第一次只 ResetSelection（capture: reset selection，遮罩仍在），要点**第二次**右键才 cancelled→closed
  即"用户按 Esc 关不掉、必须想到去点右键，有选区还要点两下"。真实截图场景里用户只会狂按 Esc，然后认为软件卡死。
危害：任何能在遮罩弹出后抢焦点的东西（系统 UAC 提升框、别的 app 的通知/对话框，甚至你们自己后续的 /pin 贴图窗或 ResultForm）都能把用户的屏幕锁进遮罩，直到鼠标介入；期间整条区域截图链路 DoS。
修复建议（择一或组合）：
  ① CaptureOverlay 加 OnDeactivate → 若非自身工具条/结果窗引起，则 Close()（并复位 busy）。这是最干净的。
  ② 或在热键处理里：若已 busy 且检测到 overlay 失焦，则把 overlay SetForegroundWindow 带回前台再响应，而不是 busy,ignore。
  ③ 至少：给 overlay 挂个"任意 Esc/右键即关"的全局低级键盘钩子兜底，别只依赖窗口焦点。
（①里要小心：OCR/翻译的 ResultForm、工具条 ShowRecMenu 都会短暂夺焦，别误关——需区分"子窗体间切换"与"真被外部抢焦点"，可用 GetForegroundWindow 是否属本进程判断。）

== 累计状态 ==
第七轮 R1-R4/W1-W3 + X2 复验全 PASS、X1 我撤回；第八轮新增：R8-1(junction 绕白名单 P2 安全)、R8-2(剪贴板 16KB 静默截断 P3)、F1(遮罩失焦滞留 DoS P2)。R8-1/R8-2 见上一条报告。
设置窗(shot-config.cs ShowSettingsForm)与翻译链仍无端点、仅托盘/热键可达，本轮未覆盖——不声称测过。

== 交付物卫生 ==
本轮遮罩/拖框全部测完即时 Esc/右键关闭（末态 shot-service 可见窗口=仅托盘 135×37，无滞留 overlay）；3 张测试截图 shot_*.png 已删；剪贴板还原；junction/vault 无残留；探针已吸取 X1 教训声明 DPI-aware。服务 pid 31100 全程未重启。
—— WorkBuddy, 2026-09-06
