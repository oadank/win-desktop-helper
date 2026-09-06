# win-desktop-helper v0.0.18(12:43) 第六轮交叉测试报告（录屏/窗口管理/贴图OCR）

测试者：WorkBuddy | 时间：2026-09-06 12:35~13:10 | 方式：纯 HTTP :18800 黑盒 + 源码对照，全程未触碰你的界面（除了误开你设置页那一次，已认过）

先确认：**E1 的修复我已验证** —— UiCall 已包住全部 UIA 入口、快路径换成精确过滤、12:43 build 起再没见 uia timeout 线程泄漏；服务现 pid 稳定、CPU 干净。

## R1（P0，DoS：一条 URL 打挂整个服务）
`/record/start?x=0&y=0&w=100000&h=100000&fps=10` → `ok:true`，随后 **shot-service 进程直接消失**（实测发生在 12:44，之后所有请求 10061 拒绝连接，只能重拉）。

根因（shot-automation.cs:746 一带）：
```csharp
static void RecordLoop() {
    int bw = recRect.Width, bh = recRect.Height;
    using (Bitmap frame = new Bitmap(bw, bh, Format32bppArgb))  // 100000x100000x4B = 40GB → OOM
```
- RecordStart 只挡了 `w<=0`（回退全屏），**没有上限校验**；
- Bitmap 分配在 `using`、循环 try **之外**、且在后台线程 → OutOfMemoryException 未捕获 → CLR fail-fast 带走整个进程；
- 8000x8000(256MB) 实测能过（12:46 RC 用例不崩），崩溃点在两者之间，本质"任意大于内存阈值的 w/h 都能杀进程"。

修法：RecordStart 入口加 `if (w*h > 4_000_000) w=h=0回退全屏`（或按 VirtualScreen 尺寸 clamp）；RecordLoop 的 Bitmap 分配包 try/catch 并置 recording=false。

## R2（P1，并发 start 六连全中 → ffmpeg 孤儿）
同一秒并发打 6 路 `/record/start`：**全部返回 ok:true**（`if (recording)` 检查在状态置位前，TOCTOU），且共用同一个秒级时间戳文件名 `rec_2026-09-06_12-46-42.mp4`。
后果实测：录屏状态复位后，**ffmpeg.exe(pid 52872) 成为孤儿继续跑**（recProc 变量被后到的 start 覆盖，先前几个 ffmpeg 进程再没人 Wait/Close）。我手工 TerminateProcess 清的。
修法：RecordStart 用 `lock (recLock)` 包住"检查+置位+启动"整段；文件名加毫秒/随机后缀；发现已 recording 时返回 409 语义。

## R3（P2，奇数宽高规整静默吞）
`/record/start?w=801&h=601` → `ok:true`，响应里看不出实际录的是 800x600（`w--` 无回显）。建议响应带 `"video_size":"800x600"`，agent 才能对齐。

## R4（P2，fps=9999 静默改默认）
`fps=9999` → 回显 `"fps":10` 看起来像"传啥用啥"，实际是规整到默认。行为正确但**语义混**（回显字段不区分"你给的"和"改过的"）。建议加 `"fpsNormalized":true`。另：start→秒停实测产出 8427B 有效 mp4，**不是 0 字节假成功**，这项通过。

## W1（P1，move 参数无校验，负坐标/零宽高全收）
- `/win/move?hwnd=..&x=-5000&y=-5000&w=800&h=600` → `ok:true moved:true`，窗口真飞出可视区（rect 回读 -5000）。窗口"丢了"，用户只能 alt+tab 捞。
- `/win/move?...&w=0&h=0` → `ok:true`，Windows 偷偷夹到 498x303，回显毫无提示。
修法：负坐标 clamp 到 VirtualScreen 边缘、w/h<50 直接 ok:false；成功响应回带最终 rect（现在只能靠 /window 二查）。

## W2（P2，min 态 move 假成功）
最小化窗口（rect=-32000 哨兵值）时 `/win/move?x=100&y=100...` → `ok:true moved:true`，但 rect 纹丝不动——restore 后位置还是旧的（实测 300,300）。SetWindowPos 对最小化窗口无效是 Win32 常识，但 helper 不查 IsIconic 就回 ok:true，属"参数吞了没生效"。建议：move 前先 restore，或如实报 `ok:false, need restore first`。

## W3（P2，对无效句柄 close 报 closed:true）
`/win/close?hwnd=99999999`（不存在的句柄）→ `{"ok":true,"closed":true}`；activate 同句柄倒是诚实的 `activated:false`。close 应以 PostMessage 返回值/句柄有效性为准。
**同时表扬**：未保存文档的 close 实测返回 `{"ok":true,"closed":false,"hint":"window still alive - 可能有未保存对话框..."}` ——12:43 build 新加的防御，行为完全正确。

## 贴图 OCR：HTTP/MCP 面不存在，无法黑盒测（记为覆盖面缺口）
源码确认 OCR/翻译/贴图(Pin) 只有三个入口：遮罩工具条按钮、F3 全局热键、托盘菜单——**没有任何 /ocr、/pin HTTP 端点或 MCP 工具**（33 个 MCP case 里也没有）。agent 集群用不了这些能力；且 PinForm 在 nssm/远程调用场景完全不可达。建议 v0.0.19 加 `/ocr?path=截图文件|&lang=` 与 `/pin?path=&x=&y=` 两个只读低风险端点，agent 侧立刻多出识别/贴图能力，我也能真正测这块。

## 测试卫生声明
- 全部攻击用自建靶子（临时记事本），测完已清：Notepad 进程 0、孤儿 ffmpeg 已杀、录像/截图产物在 Pictures\Screenshots 里（rec_*.mp4 若干，可删）。
- 上一轮"close 后窗口仍在"是我脚本中断留的第二枚记事本干扰，**本轮同 hwnd 复验不成立，撤回**。

——WorkBuddy，2026-09-06
