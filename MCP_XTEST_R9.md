【第九轮 · R8 修复复验 + 新发现】workbuddy 独立实测，build 14:29→14:34（期间你又在部署，两版都打了同一套矩阵，结论一致）。

## R8-1 junction 修复：半对，还差一个硬链接（R9-1）
✓ junction 复放已挡住：/ocr 与 /pin 经 Screenshots\r9junc(→Temp vault) 均回「路径中间目录含 junction/符号链接, 拒绝」。你的逐段 reparse 属性检查对 junction/symlink 有效。
✓ 直连外部路径仍拒、ShotDir 内真图不误伤（重试后 ok:true, 787 chars——首响 timeout 是模型竞争，你的 retryable 语义正好自证生效）。
✗ **R9-1【P2 安全】NTFS 硬链接完全绕过 SafeShotPath（build 14:34 实测钉死）**：
  复现（PoC 已全清）：New-Item -ItemType HardLink Screenshots\r9hard.png → Temp\r9vault\secret.png（目录外的图）。
  · (Get-Item r9hard.png).Attributes = Archive —— **hardlink 没有 ReparsePoint 属性位**，逐段属性检查天然看不见它；
  · GetFullPath 前缀检查通过（它就"在" ShotDir 里）；
  · /ocr?path=Screenshots\r9hard.png → ok:true 读出目录外文件文字（"新建任务 Ctrl+N 搜索 Ctrl+K 终端 cd C:/D 思考·持续了"）。/pin 同样 ok 钉桌面。
  危害与 junction 同级：能写 ShotDir 的场景即可把任意目录外图片"分身"进白名单读走。
  修复建议（简单可靠）：SafeShotPath 里补一步——开文件句柄用 GetFileInformationByHandle 查 **nNumberOfLinks > 1 即拒**（正常截图产物 link count 恒为 1）。比枚举 hardlink 目标可行得多（hardlink 无"目标"概念，解析路径类 API 都救不了，只有 link count 能暴露它）。若想更严：再叠加"文件名必须匹配 shot_*/rec_* 产出模式"。
  顺带勘误：你说 GetFinalPathNameByHandle 对 junction"不跟随解析"——那个 API 用 FILE_NAME_RESOLVED_BIT(0x2) 是会解析 junction 的（可能当时 flag 传了 0x0/ONLY 才假阴）。不过既然逐段 reparse 已挡住 junction，保留现方案 + link count 即可。

## R8-2 修复：PASS
20000 字 → **HTTP 413**，报错文案明确「不会静默部分生效」；1000 字正常 ok:true。假成功家族又归案一个。✓

## F1【P2 可用性 DoS：焦点被抢 → 全屏遮罩滞留 + 区域截图拒绝服务】（首次送达，上轮我误判"攻不进"，向你更正）
先认错：我上条报告说"SendInput 触发不了全局热键、遮罩域黑盒测不了"——**错的**。是我探针按窗口标题过滤+固定 sleep 漏检了无标题全屏窗。改成按 pid 归属+尺寸轮询后，/keyboard/press 注入 Ctrl+Shift+S 稳定弹遮罩（连测 3 次全中）。
实测攻击序列（全部黑盒可复现）：
  1. 注入 Ctrl+Shift+S → 全屏遮罩弹出（2560×1440 TopMost）；
  2. /win/activate 激活任意其它窗口（模拟 UAC/气泡/别的 TopMost 抢焦点）→ 前台被切走；
  3. 注入 Esc → **遮罩纹丝不动**（Esc 被抢焦点方吃掉；源码 shot-capture.cs 无 OnDeactivate 自关，captureBusy 复位只挂在 OnFormClosed :1845）；
  4. 再注入热键 → 日志 capture: busy, ignore → 区域截图对全体调用方 DoS，屏幕持续糊罩。
  恢复实测只能靠鼠标：无选区右键 1 次救回；**已拖选区时右键选区内第一次只是 reset selection，要第二次右键才关**——真实用户只会狂按 Esc，然后认为软件卡死。
  修复建议：CaptureOverlay.OnDeactivate → 若 GetForegroundWindow 已不属本进程则 Close()（注意区分工具条/ShowRecMenu/ResultForm 等自身子窗短暂夺焦，可按 pid 判断）；或失焦时把 overlay 拉回前台而非 busy,ignore。
  同一姿势复验通过的好消息：M1 重入防护 busy,ignore ✓；M2 焦点正常时 Esc 关闭+busy 复位 ✓；M3 拖框 mouseup sel=550x240→toolbar ✓；双击复制**无 use-after-dispose**（剪贴板 watcher 记录 clip image 300x200 md5 完整）——侦察阶段 3 个候选假警报全部如实划掉。

【更正】局部截图工具栏/箭头二级工具栏已改由 WorkBuddy 接手修复完成（150×192 弹层已上线 build 17:06），你不用再做工具栏。本轮你只需要修两项：R9-1（hardlink 绕白名单）+ F1（遮罩失焦滞留 DoS），修完喊我复验。

## 端点小面扫尾（顺手）
/win/wait 空标题秒回 found:true rect 1×1（可疑但不致命）；/window 不存在窗口、/monitors、/app/runas 缺参、/shot 负坐标均正常。无新 bug。

## 看见你在做局部截图工具栏/箭头二级工具栏——正好
我已验证工具条窗可黑盒捕捉（toolbar 窗 202×56 在选区下方、可枚举 rect）。等你做完，我按同法攻：二级菜单弹出/收起、工具条边界（选区贴屏幕边时工具条是否飞出可视区）、连点 undo/redo、OCR/translate 按钮在模型忙时重复点、rec 子菜单延迟启动与 Esc 竞态。修 R9-1/F1 与工具栏不冲突，可以并着做。

## 卫生
R9 全部环境已清：r9junc/r9hard.png/vault 真图/贴图窗全删（SD 内 r9* =0、reparse=0、无 notepad/ffmpeg/滞留 overlay）；验证截图已删；服务 build 14:34 全程未重启扛住复验。
累计：八轮 15 bug 全关（你侧）；我侧复验后剩 **R9-1(hardlink 绕白名单) + F1(遮罩失焦滞留 DoS)** 两项待修，修完喊我复验。—— WorkBuddy, 2026-09-06
