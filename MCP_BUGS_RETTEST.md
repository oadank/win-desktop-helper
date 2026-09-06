# win-desktop-helper MCP 复测报告（第二轮）

- 被测版本：`0.0.18` build `09-05 22:50`，session:1，pid 15264
- 复测时间：2026-09-06 07:38–07:50
- 复测方法：走**真 MCP 协议**（stdio → bridge → HTTP），源码与实测双验证，每个 bug 跑 3 轮确认复现率
- 沙盒：记事本 / mspaint / charmap，测完已全部清理

## ⚠️ 先说上轮测法本身的缺陷

上轮全程用 HTTP 直连（绕过 bridge）测，导致两类误判：
1. 直连绕过了 bridge，所以"服务端没实现 maximize"是**错判**——服务端好好的，是 bridge 动词名写错
2. zcode 加了 `get_skill` 强制门禁，未先调用则**所有工具一律拒绝**；上轮没走 MCP 所以没碰到，这轮一开始就被挡了 5 个调用

**结论：审计 MCP 必须走 MCP 协议，HTTP 直连只能用于分层定位。**

---

## ✅ 确认真 bug（6 个）

### 1. `win_manage action=maximize/minimize` → 404 unknown verb　【3/3 复现】
- 现象：`{"ok":false,"error":"unknown verb"}`
- 根因：**非服务端问题**。服务端 `/win/` 分支只认 `activate|max|min|restore|close|move|wait|list`（`shot-service.cs` ~1300 行）；bridge 却声明并转发 `maximize`/`minimize`
- 对照：直连 `/win/max` 完全正常（`{"ok":true,"maximized":true}`，rect 真变）
- 修法：bridge 内做动词映射

### 2. `win_manage action=move` 永远失败　【2/2 复现】
- 现象：只传 x,y → `need x,y,w,h`；**补上 w,h 仍报同样错**
- 根因：bridge `inputSchema` 只声明 `x`/`y` 且 `additionalProperties:false`，**w/h 被静默丢弃**，用户怎么传都过不去
- 对照：直连 `/win/move?x&y&w&h` 完全正常

### 3. 🆕 `/win/activate` 目标窗口在后台 → HTTP 500　【3/3 复现】
- 现象：HTTP 500，日志 `无法在 DLL"user32.dll"中找到名为"GetCurrentThreadId"的入口点`
- 根因：`shot-automation.cs:24` 把 `GetCurrentThreadId` 声明在 `user32.dll`，**该 API 实际在 kernel32.dll**
- 触发条件：`SetForegroundWindow` 首次失败才走到那行 → **恰恰是窗口在后台、最需要激活时必炸**
- 影响：SKILL 手册铁律第一条就是"点前先 activate"，这条挂了等于导航能力残废
- 修法：改 `kernel32.dll`（一行）

### 4. `/app/run` 卡死不返回　【3 种程序 × 全卡】
- 现象：HTTP 40~50 秒无响应。**程序其实每次都真启动了**（进程数 1-2）
- 测试：`notepad.exe`、`mspaint.exe`、`cmd.exe /c echo hi`（无窗口程序也卡）→ 全部超时
- 日志佐证：今天的 `/app/run` **完全没进 handler 记录**（说明卡在 `AppRun()` 返回前）
- 根因：`shot-service.cs:302` `Process.Start(psi)` 用 `UseShellExecute=true` 阻塞（典型 ShellExecute DDE 等待 30s+）
- 修法建议：`UseShellExecute=false`，或改异步立即返回 + 单独查询接口

### 5. `/win/close` 遇未保存对话框 → 返回 ok:true 但没关　【2/2，条件性】
- 现象：脏窗口 close 返回 `{"ok":true}`，窗口仍在（剩余 1 个）
- 根因：`WM_CLOSE` 被系统保存对话框拦截，但返回值仍报成功
- 修法建议：关闭后校验窗口是否真消失，返回值反映真实状态（如 `pending` / `blocked_by_dialog`）

### 6. 部分窗口 close 卡 35 秒不返回　【mspaint 2/2】
- 现象：窗口确实关了，但 HTTP 35 秒无响应
- 与 #4 同类：活干完了但响应写不回

---

## ❌ 撤回（上轮误报，2 条）

### 「close 返回 ok 但没关掉」→ 部分撤回
干净窗口 close **完全正常**（窗口=0、进程=0），Store 版记事本也真关了。
上轮之所以"关不掉"，是**我往记事本打过字**，未保存对话框挡住——属测试污染，非 bug。
（保留为 #5 的**条件性**版本：仅脏窗口下返回值不可信）

### 「未保存对话框 UIA 枚举不到按钮」→ 完全撤回
**能枚举**：`ui_find(name='保存')` 直接返回 `是否要将更改保存到 XXX.txt?` + `保存` Button，**带 rect 可点击**。上轮是我没找对方法。

---

## 🔧 已修（根因 100% 确定，改动极小）

| 文件 | 改动 |
|---|---|
| `shot-automation.cs:24` | `[DllImport("user32.dll")]` → `[DllImport("kernel32.dll")]`（修 #3） |
| `mcp-bridge.js` | `win_manage` 加动词映射 `{maximize:'max', minimize:'min'}`（修 #1，保持 agent 习惯写法可用） |
| `mcp-bridge.js` | schema 补 `w`/`h` 并透传（修 #2） |

**实测验证通过**：
- `maximize` → `{"ok":true,"maximized":true}`，rect 变 `2578×1398`
- `minimize` / `restore` → ok
- `move(x=150,y=120,w=900,h=650)` → `{"ok":true,"moved":true}`，rect **精确 `150,120,900,650`**
- `node --check mcp-bridge.js` 语法通过

> #3 的修复（C#）**需重新 csc 编译**才生效；#1/#2 是 JS，MCP 每次新起进程，**已立即生效**。

---

## ✅ 修复后独立复测（2026-09-06 08:0x，workbuddy 独立跑，不采信 zcode 数据）

服务端新版 build `09-06 07:58`（含 kernel32 修复），提交 `a9c7b75`。**6 个 bug 全部关闭。**

| # | 修复前 | 独立复测结果 | zcode 自报 |
|---|---|---|---|
| 4 | app_run 卡 40-50s | **mspaint 0.55s / 记事本 0.29s**，返回 `{"ok":true,"pid":..,"window":{hwnd,pid,title,process}}` | 0.82s |
| 6 | close 卡 35s | **0.23s**，`{"ok":true,"closed":true}`，关闭后窗口数 0 | 0.28s |
| 5 | 脏窗口返回 ok 但没关 | **2.03s**，`{"ok":true,"closed":false,"hint":"window still alive - 可能有未保存对话框, 用 ui_find name=保存 定位处理"}`，剩余窗口=1 | 同 |
| 3 | activate 后台窗口 500 | **3/3 + 1 次补充全成功**，0.0~0.05s，`via:"direct"`，不再 500 | 0.05s via=alt |
| 1/2 | bridge 动词映射 + move 丢 w/h | 实测 maximize→rect `2578×1398`、move→rect 精确 `150,120,900,650` | — |

### 诚实标注（未覆盖点）
**#3 的 fallback 分支（`via=alt/attach`）没能构造触发** —— 所有测试都是 `via:"direct"`（`SetForegroundWindow` 首次就成功）。
因此 `GetCurrentThreadId` 那行代码路径**未被直接执行验证**，只是 500 现象消失 + API 归属（kernel32）属常识性正确。
**后续若遇到 `via=alt` 场景，才算真正覆盖该分支。**

## 未测

- `app_runas`（弹 UAC 需人工确认）

## 🔴 测试事故与经验（重要）

**盲打污染**：复测中 `keyboard_type` 时前台是 **WorkBuddy**，16 字符 `DIRTY-VERIFY-999` 被打进了用户正在使用的输入框。

**根本原因**：`active_window` 工具的 HTTP 端点是 **`/active`**，不是字面上的 `/active_window`。前面几轮一直调 `/active_window`（返回 404），**前台确认环节形同虚设**——这才是盲打的真因，不是"忘了确认"。

**正确做法（已验证有效）**：
```python
def activate_until_front(title, want_proc, maxtry=6):
    for _ in range(maxtry):
        call("/win/activate?title=" + quote(title))
        sleep(1.2)
        if json.loads(call("/active")).get("process") == want_proc:
            return True
    return False   # 确认不了就不许打字
```
用这个模板：1 次确认成功，打字后 `front` 字段回显 `Notepad`，零污染。


---

## ✅ 修复落地（2026-09-06, zcode）

- **#3 activate 500**：kernel32.dll 修正随 4aa54e7+ 本次编译生效。复测：后台窗口 activate **0.05s** 成功（via=alt），3/3 无 500
- **#4 app_run 卡死**：根因即 STA — ShellExecute 在 MTA 线程等 DDE 超时。Process.Start 挪 STA 线程后实测 mspaint **0.82s** 返回（含真实窗口信息）
- **#6 close 卡 35s**：SendMessage(WM_CLOSE) 同步等待改为 PostMessage。实测 mspaint close **0.28s**，closed:true
- **#5 脏窗口返回值**：close 轮询 2s 校验窗口真消失，返回 closed:true/false + hint（未保存对话框时 ui_find name=保存 可定位处理按钮）。实测脏窗口返回 closed:false ✓
- **#1/#2**（bridge 动词映射 + move w/h）：workbuddy 已修，MCP 实测通过
- 全部 6 bug 关闭。剩余未测：app_runas（UAC 人工确认场景）
