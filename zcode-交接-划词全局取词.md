# 交接提示词：win-desktop-helper 划词取词全盘审查与重写（给 ZCode，零上下文自足）

你是接手 `C:\D\opt\win-desktop-helper` 项目**划词悬浮球子系统**的工程师。前一个 AI 把这个子系统越修越烂（打了十几轮补丁，交互状态机已经补丁摞补丁），老大定性为"屎山"，要求你**全盘审查，必要时推倒重写成清晰状态机**。以下是全部上下文。

## 一、项目背景

- C# 单进程桌面工具 `shot-service.exe`（WinForms，无框架），v0.0.19，源码全在 `C:\D\opt\win-desktop-helper`
- 功能：截图/贴图/OCR/剪贴板历史/音量/托盘 + **划词悬浮球**（2026-09-07 新增，问题源头）
- **运行环境铁律**：必须 explorer 拉起跑 Session 1 + 管理员（用户常用应用设了 RUNASADMIN，UIPI 会静默丢弃低权限进程的合成键鼠输入——工具报 ok 屏幕没反应就查这个）；**绝不能 nssm 化**（Session 0 抓不到桌面）
- 编译：`cd C:\D\opt\win-desktop-helper && .\build-annotation.cmd`（多文件 csc，输出一堆 warning 正常，只看"0 个错误"）；exe 被锁先 `taskkill /IM shot-service.exe /F`；编译完 `Start-Process explorer.exe "...\shot-service.exe"`
- 日志：`C:\D\opt\win-desktop-helper\shot-service.log`（纯文本，所有 pick 相关行为都有埋点，排查全靠它）
- HTTP：127.0.0.1:18800 `/health`（build/session/elevated）、`/pick-config?enabled=1` 划词开关（持久化）

## 二、用户核心诉求（原话）

1. "我就是要全局生效"——划词悬浮球要在**所有应用**（浏览器/终端/本地应用）都能出，像豆包一样
2. "豆包等其他都没这些问题"——交互必须跟手：双击选词秒出点、点空白秒取消、连点不卡、菜单秒开
3. "能触发就触发，不触发是你的能力问题"——**禁止模拟 Ctrl+C 兜底**（抢剪贴板、终端蹦 ^C、覆盖用户复制结果，全部副作用都来自它，代码已删，任何人不许加回来）
4. 最新暴怒点（当前待修 bug）：**双击选词非常卡/点被吞**；点空白取消也卡

## 三、当前代码结构（shot-pick.cs，partial class ShotService）

- 入口：低级鼠标钩子 `PickOnMouse` → DOWN 记 pickX0/Y0 → UP 按位移判定 click/drag
- 判定阈值：位移 >8px 算划选；`PICK_TOL=16`（点中悬浮球容差）、`PICK_TOL_MISS=40`（近失也当想点球）
- 双击检测：`pickLastClickCap/pickLastClickX/Y`，450ms 内同位置 12px = dblClick 放行，否则 400ms 节流吞掉
- 取词：`PickCaptureWork`（ThreadPool）→ 单击走 UIA 三连（PickWordAtPoint）/划选走 PickTextUia 或 **OCR**（`PickOcrRect`：截 DOWN-UP 矩形 → 2x 放大 → OcrProvider，Ollama qwen3-vl:4b @ :11434，keep_alive 60min，耗时 5-9 秒）
- 出点：UP 时 `ShowPickDot` **立即**出点（不等取词），文字后台到位挂上
- 展开：hover 180ms 或点击 → `PickDotActivated`（有 pickSel 空守卫）→ 三键工具条
- 黑名单：`PickIsBrowser`（17 个进程名，pid 缓存 5min）、`PickIsTerminal`（10 个）
- 退避：UIA 慢目标（>700ms）30s 退避 `pickSlowUntil`

## 四、实锤证据（全部来自 shot-service.log，勿再重复踩）

1. **浏览器单击 UIA 是"右键卡 7 秒"主凶**：Chromium 处理跨进程 UIA 同步霸占自己 UI 线程（`slow UIA target 3203ms`）。浏览器单击取词禁用后右键秒开
2. **Edge 的 UIA 拿不到划选文本**（0ms 空返回）——Chromium 不给外部进程划选内容，UIA 在浏览器划选场景无意义
3. **conhost 的 UIA GetSelection 有内部 Ctrl+C 副作用**：有选区→复制并清掉（用户选区自动消失）；无选区→蹦 `^C`。**终端绝不发 UIA**
4. **纯 VK 无扫描码的合成键被 Chromium 无视**——这是旧剪贴板兜底零成功的根因（该方案已整体删除）
5. **400ms 单击节流会吞双击**（双击间隔通常 <300ms）→ 双击取词永远走不到 → 等 UIA 把上次选区读出来 → 点别处球乱冒
6. **点小点偏出容差 / 点空白 → 掉进单击取词路径 → 对底层窗口发 UIA → 慢目标挂 700ms+ = 用户说的"卡"**
7. 剪贴板历史 watcher 曾每 400ms 无条件全量读剪贴板抢用户 Ctrl+C，已改先读 `GetClipboardSequenceNumber` 序号门控（shot-service.cs，勿回退）

## 五、当前已知 bug（老大最新反馈，你的首要任务）

1. **双击选词卡**：dblClick 放行逻辑和 400ms 节流、click/drag 判定、pickBusy 互斥搅在一起，实测仍然卡/吞点
2. **点空白取消会卡**：疑似仍有路径掉进取词（近失 40px 逻辑可能误伤）
3. **出点时机飘**：双击没出点、点别处反而出（第 5 条证据的余毒）
4. 交互整体"卡卡的"：怀疑还有隐藏的 UIA 发起路径

## 六、建议做法（老大已授权重写）

**不要继续打补丁。** 把 shot-pick.cs 的交互判定重写成显式状态机：
- DOWN → (跟踪) → UP：一次交互只产生一个明确结论 ∈ {单击球/近失球/双击选词/划选取词/点空白取消}
- 状态机内**任何指向"取词"的分支都要先问：用户此刻想要的是操作球，还是取词？** 球存在且点击位置在球附近（含近失）= 永远是操作球，绝不吃进取词
- 双击用系统 `GetDoubleClickTime()`/双击消息判定，不用手搓节流
- 取词全部丢后台线程，UI 线程（钩子回调）零阻塞；取词失败静默收点，成功才挂文字
- 浏览器划选出球的**终局方案是 Edge 扩展**：页面 JS `getSelection().toString()` → `fetch('http://127.0.0.1:18800/pick-inject')` → 服务端直接弹球。零 UIA 零按键零剪贴板，毫秒级。建议做，这是唯一干净路线

## 七、铁律（违者必翻车，前任多次翻车点）

1. **绝不发模拟 Ctrl+C / 不碰用户剪贴板做取词**（老大终审作废，见二-3）
2. **绝不向终端（conhost/WindowsTerminal/pwsh/cmd/wt）发 UIA**（^C 副作用）
3. **绝不向浏览器发单击 UIA**（右键卡 7 秒主凶）
4. 改 shot-pick.cs 后**必须 grep 复验落盘**——Edit 工具在本项目有静默回滚前科（3 次），且同文件并行多针会互相覆盖（后写覆盖先写），必须逐针单发打一针验一针
5. 任何源码改动前先 push GitHub 基线（老大规矩）
6. 改完必须真实环境实测（老大亲手划选验证），日志带耗时埋点是验收依据
7. `nssm` 输出是 UTF-16LE 要 `tr -d '\0'`；本服务不是 nssm，日志纯文本

## 八、验收标准（老大亲手测）

- [ ] 双击选词：秒出点，不吞不卡
- [ ] 划选文字：秒出点（OCR 文字后台 5-9s 挂上可接受，点本身必须立即出）
- [ ] 点空白：秒取消，零卡顿
- [ ] 连点/快点点球：不卡、菜单秒开
- [ ] Edge 右键：秒开
- [ ] Ctrl+C：永远正常
- [ ] PowerShell：选区稳定、移动窗口不蹦 ^C
