【第七轮 · R6 修复复验】workbuddy 独立实测，服务 pid 54072（build 09-06 13:16）。
结论：R1-R4 / W1-W3 六项修复全部真实生效，不是表面补丁。另发现 2 个新观察项（X1 坐标一致性、X2 OCR 冷启动），都不阻塞。

== 逐条复验（PASS）==
R1 P0 DoS（100000×100000 打挂服务）：
  /record/start?w=100000&h=100000 → {"ok":true,"video_size":"2560x1440","sizeClamped":true}，进程 pid 未变、全程存活。源码确认 new Bitmap 已进 try/catch（shot-automation.cs:772），OOM 不再 fail-fast。真根因修复。✓
R2 并发孤儿 ffmpeg：
  6 路并发 start → 1 个 ok + 5 个 {"error":"already recording"}，成功文件唯一（毫秒名），ffmpeg 0 残留。整段 lock(recLock)（:727）到位。✓
R3 奇数宽高静默-1：
  w=1023&h=767 → video_size:"1022x766"（回显偶数、不再是请求值与真实值不一致的哑谜）。✓
R4 fps 越界：
  fps=9999 → {"fps":10,"fpsNormalized":true}，回显明确。✓
W1 move 负坐标/零宽高：
  x=-5000&y=-5000 → {"clamped":true,"rect":{"x":0,"y":0,...}}，窗口真落到 0,0 不飞丢；w=0 → {"error":"w/h must be >= 50"} 明确拒绝。✓
W2 最小化 move 假成功：
  min 后 move → {"restoredFirst":true,"rect":{"x":120,"y":120,...}}，独立进程 GetWindowRect 复核窗口确实回到屏内。✓
W3 无效句柄 close 假成功：
  /win/close?hwnd=99999999 → {"error":"invalid handle"}（IsWindow 前置校验，:103）。真实记事本 close 后 IsWindow=false，无假阳性。✓
/ocr /pin 新端点：
  越界路径（C:\Windows\...\SAM）→ {"error":"path outside screenshots dir (安全限制)"} 双端点均拒。✓
  /pin 真实截图 → ok + rect + 拖动/缩放/双击 hint，贴图窗可 close。✓
  /ocr 热态 → 1.3s 正确识别中文（"新建任务 Ctrl+N / 搜索 Ctrl+K / AI电脑控制…"）。✓

== 新发现（P2/P3，不阻塞）==
X1（P2，坐标系不自洽）：
  /shot、/active 报物理像素（2560×1440），但 /win/move 回报的 rect 用逻辑像素（请求 w=800 → 回 rect.w=800，而独立 DPI-aware 进程真实读 533×400，150% 缩放下差 1.5×）。
  影响：agent 拿 move 回显的 rect 去喂 /shot 或 /click 会算错位置。
  根因猜测：shot-service.exe 未声明 Per-Monitor DPI awareness → 系统把它的窗口坐标 virtualize 成 96dpi 逻辑值，而截屏/GetWindowRect 经 VirtualScreen 又是物理值，两套单位混用。
  建议：要么进程加 DPI 清单（app.manifest dpiAware/PerMonitorV2）统一物理像素；要么明确在文档标注"win 坐标=逻辑像素，截图=物理像素"，避免调用方混用。

X2（P3，OCR 首调偶发超时）：
  服务冷启动后第一次 /ocr：即使传 wait=25000 也返回 {"error":"OCR timeout"}，但同参数热态 Ollama 裸打首帧 load 仅 0.18s、生成 8.1s，之后 1.3s。
  非代码 bug（provider/timeout 逻辑正确），是 Ollama 模型冷加载首包偏慢 + wait 语义对调用方不透明。
  建议（可选）：/ocr 超时响应带 retryable:true + 已等待毫秒，或启动时异步 ping 一次模型预热，减少 agent 侧"第一次必超时"的困惑。

== 交付物卫生 ==
本轮所有测试产物（rec_*.mp4 / shot_*.png / 贴图窗 / 记事本靶子 / 我上轮遗留的第 2 枚记事本）已全清；Screenshots 目录无残留；无孤儿 ffmpeg/notepad；剪贴板无哨兵。服务 pid 54072 全程未重启即扛住 R1/R2 攻击。
累计：六轮 13 bug 修复经第七轮复验全部真实闭环；新增 X1/X2 两项供参考。—— WorkBuddy, 2026-09-06
