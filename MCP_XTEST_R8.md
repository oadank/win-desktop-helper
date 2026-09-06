【第八轮 · 强UI域复验+新发现】workbuddy 独立实测，服务 pid 31100（build 09-06 13:41，含 X2 修复）。

先表两态：
- 你 X1 裁定成立，我撤回。加 SetProcessDpiAwarenessContext(-4) 后探针实测 move→GetWindowRect 读 (100,100,900,700)，与你服务 /win/move 回显逐字一致；上轮 533×400 确是我探针 unaware 被 ÷1.5 virtualize 的错觉。"跨进程验坐标探针须先 aware"这条纪律我认。
- 你 X2（OCR 冷启动）修复复验 PASS：OcrWarmup() 启动后台 ping（shot-service.cs:1800）、超时带 retryable:true+waitedMs（:211）；实测首调成功出字。✓

== 强UI域可达性说明（非缺陷）==
遮罩截图全链(shot-capture.cs)、设置窗(shot-config.cs)、OCR/翻译工具条 全是 hkForm STA 线程上的 WinForms，无 HTTP/MCP 端点能直达（/shot 走 CopyFromScreen 不经 overlay；无 /settings /config /overlay）。唯一黑盒触发是 /keyboard/press 注入全局热键——但实测注入 Ctrl+Shift+S，服务收到并 SendInput（日志 [ctrl] press ctrl+shift+s），遮罩却不弹、无 WM_HOTKEY/capture 日志。判定：SendInput 注入难以稳定触发 RegisterHotKey（Windows 通用行为，非你的代码问题）。故这三域我无法自动化黑盒攻，留给你的白盒测试+人工。基线核验：注入后 shot-service 名下可见窗口始终为空、前台未变、captureBusy 未卡死（能反复注入无残留），无 DoS。

== 新发现（A 级端点，已实测钉死）==

R8-1【P2 安全：/ocr /pin 白名单可被 NTFS junction/reparse point 绕过】
  源码 shot-service.cs:200-203：OcrFile 白名单 = GetFullPath(path) 后 StartsWith(GetFullPath(ShotDir))。
  GetFullPath 只做字符串规整，**不解析 reparse point**；File.Exists 跟随 junction。于是"前缀在 ShotDir 内、实体在 ShotDir 外"的路径 → 过白名单 → 加载外部图片。
  实测 PoC（全程已清）：
   ① 在截图目录内建 junction：Screenshots\xtlink → C:\Windows
      /ocr?path=Screenshots\xtlink\System32\drivers\etc\hosts
      → {"error":"ArgumentException 参数无效"}（=已过白名单+文件存在，只因非图像在 Bitmap 阶段才挂；证明白名单对越界放行）
   ② Screenshots\xtvault → Temp\x8vault，vault 内放真实截图 secret.png
      直连 /ocr?path=Temp\x8vault\secret.png → {"path outside screenshots dir (安全限制)"}  ← 正确拒绝
      经 junction /ocr?path=Screenshots\xtvault\secret.png → {"ok":true,"text":"新建任务 Ctrl+N 搜索 Ctrl+K AI电脑控制..."}  ← 绕过成功，读出目录外图片文字
      经 junction /pin?path=...secret.png → {"ok":true,...已钉到桌面}  ← 贴图同样被绕
  危害：任何能诱导/预置 junction（或把敏感图软链进 Screenshots）的场景，"安全限制"形同虚设——越界读任意图片并 OCR 外传/贴图外显。/pin 与 /ocr 同白名单，一并受影响。
  修复建议：解析真实路径再判前缀——打开句柄后 GetFinalPathNameByHandle(..., FILE_NAME_RESOLVED|NORMALIZED) 得最终物理路径比对 ShotDir；或 GetFileAttributes 命中 FILE_ATTRIBUTE_REPARSE_POINT / 路径任一段是 reparse 即拒；最严：要求 ShotDir 内且自身 FileId 归属（GetFinalPathNameByHandle）一致。

R8-2【P3：/clipboard/set 超 16KB 静默截断仍报 ok（假成功）】
  根因：Handle 用裸 TcpListener，buf=new byte[16384]，只读首个 16KB 即止（shot-service.cs:1129-1138），参数仅解析 URL query。
  实测：/clipboard/set?text=20000×'b' → {"ok":true,"chars":16360}，/clipboard/get 确认落盘 16360。调用方以为整段写入，实被截断且无告警。
  （16000 字正常：ok:16000）
  同类你已修过的"假成功"家族（W1/R3）。建议：解到 query 未闭合/超长时明确报 {"ok":false,"error":"payload too large"}，或支持 POST body 读 Content-Length 直到读完。

== 复验通过（PASS，无 bug）==
- /ocr 越界目录/盘根/缺参：均干净拒绝（file not found / need path），无异常泄漏。
- /diag/threads：uiaLeaked:0 fused:false，UIA 泄漏熔断路正常。
- OCR provider 中文/JSON：多轮识别正确出字（含 \u 转义）。

== 交付物卫生 ==
本轮所有残留已清：Screenshots\xtlink、xtvault 两个 junction 已删（用 .Delete() 只删链接不动目标）；Temp\x8vault 真图+目录已删；测试贴图窗已 close；无遗留遮罩（shot-service 可见窗口=0）；剪贴板哨兵覆盖清除；验证截图已删。服务 pid 31100 全程未重启扛住全部用例。
—— WorkBuddy, 2026-09-06
