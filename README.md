# Win Desktop Helper

给**任意 AI Agent / 服务进程**（DSH、Claude、Codex、DeepTutor、飞书 bot、……）提供 Windows 桌面能力的桥接服务：
**看**（截图/窗口/UIA 元素树）+ **动**（鼠标/键盘/窗口管理/运行程序）+ **录**（屏幕录制）+ **控**（语义操作）。

同时自带一套**给人用的截图工具箱**（区域截图 / 长截图 / 标注 / OCR / 翻译 / 贴图 / 录屏录 GIF / 划词 / 剪贴板历史 / 任务栏音量），一个 exe 全搞定。

- 单 exe（.NET Framework 4.8，Windows 自带，**零外部依赖**）
- 常驻用户会话（Session 1）+ 托盘 + HTTP `127.0.0.1:18800` + MCP stdio
- **默认以管理员权限常驻**（全自动静默提权，装完即用；否则碰不到管理员运行的窗口）
- 自动检查更新、静默自升级

> 当前正式版：**v0.0.25**

## 为什么需要它

Windows 从 Vista 起引入 **Session 0 隔离**：以服务方式运行的程序（如 nssm 托管的 agent）在 Session 0，
**没有用户桌面**——GDI 截图报"句柄无效"、SendInput 无目标、截图全黑。本服务常驻在**用户登录会话（Session 1）**，
以 HTTP / MCP 暴露统一接口，Session 0 的任何进程按需调用即可完成"看屏幕 + 操作电脑 + 录屏"。

## 安装

1. 下载本仓库最新 Release 的 `win-desktop-helper-setup-<版本>.exe`，双击安装（可选桌面图标/开机自启）
2. 安装后自动启动，托盘出现深蓝图标
3. **自动以管理员运行（无需任何手工操作）**：首次启动会自行完成提权——优先注册计划任务 `WinDesktopHelper`（`ONLOGON` + 最高权限），之后每次登录静默以管理员常驻，不再弹 UAC；若计划任务被组策略禁用，会自动退化到注册表 `AppCompatFlags\Layers = RUNASADMIN` 兜底。
   > 为什么必须管理员：不少目标应用（IDE、企业软件）本身以管理员运行，Windows 的 UIPI 会**静默丢弃**普通权限进程合成的键鼠输入——工具会报成功但屏幕毫无反应。管理员常驻是唯一可靠解。
3. 已装用户：托盘 → 工具 → 检查更新（发现新版会自动静默升级，无需手动）

## 给人用的功能（托盘 / 热键）

### 截图与标注

| 功能 | 入口 | 说明 |
|---|---|---|
| 区域截图 | `Ctrl+Shift+S` 或托盘→截图 | 冻结屏遮罩框选 → 图标工具条 |
| 全屏截图 | `Ctrl+Alt+A` 或托盘→截图 | 截图直接进剪贴板 |
| **UI 元素自动识别** | 截图遮罩上直接悬停 | 自动吸附到窗口/面板/按钮/输入框级选框；三档可调（检测元素/仅窗口/不检测，设置→截图）；**滚轮切换父子层级**逐级放大缩小选框 |
| 长截图（滚动拼接） | 截图工具条「长截图」 | 纵向/横向 + 手动滚动拼接（选区上滚滚轮）或自动滚动，侧边实时预览，逐像素拼接无接缝 |
| 贴图 | `F3`（剪贴板图）/ 中键点选区 / 工具条📌 | 图钉到桌面，可拖动/滚轮缩放/双击关 |

**标注工具**（截图工具条）：矩形 / 椭圆 / 箭头 / 折线 / 曲线 / 画笔 / **荧光笔** / **马赛克** / 文字 / 序号，+ 撤销重做。

| 工具 | 明细 |
|---|---|
| 箭头 / 折线 / 曲线 | **折叠成一个按钮**（点开组菜单切换成员，谁最后用谁占槽，记忆保留）；箭头 4 种头型、双向/虚线/闭合填充可选；折线/曲线单击加点、双击或右键收尾 |
| 画笔 / 荧光笔 | 荧光笔**透明度 5 档**（20%~100%）、**按住 Shift 画直线**（自动 45° 吸附）、半透明叠加不互相覆盖 |
| 马赛克 | **马赛克/模糊双模式**、强度 5 档，直接把屏幕原图打码 |
| 文字 | 中文输入；**字号直接键入**（点开即输数字回车生效）或 16 档列表/滚轮选；字体 5 种；颜色 7 色 |
| 序号 | 格式 1.2.3 / I.II.III / a.b.c / A.B.C，起始数字可调 |
| 属性栏 | 选工具即弹：颜色/粗细/填充/虚线等全部跟随；**悬停滚轮直接调值**（粗细/字号/编号/透明度/强度，不用点开下拉）；所有标注参数持久化，下次截图还原 |

**收尾操作**：双击选区 = 复制并完成；右键选区内 = 重新框选、空白处 = 退出；保存支持 png/jpg/bmp/gif/tif/webp。

### 识别与翻译

| 功能 | 入口 | 说明 |
|---|---|---|
| OCR 文字识别 | 截图工具条 | 双引擎可选：本机 Ollama 视觉模型（默认，零花费零 key）或远程 OpenAI 兼容视觉接口（设置→OCR 切换）；结果自动进剪贴板 |
| 翻译 | 截图工具条 / 划词卡片 | **自动语言检测反向翻译**（中文→英/英文→中）；本地 LLM 或百度引擎 |
| 划词悬浮球 | 设置中开启 | 选中文字 → 光标旁小圆点，停留展开工具条，直接点立即翻译；不抢焦点不打断输入；支持「问 AI」自定义后端 |

### 录制与效率

| 功能 | 入口 | 说明 |
|---|---|---|
| 录屏 | 截图工具条🔴 | 选区/全屏 + 延迟 1/2/3/5/10s；**MP4 (h264) 或 GIF**（上限 120s）；录制中右下角 HUD 计时/停止 |
| 剪贴板历史 | `Ctrl+Alt+V` | 自动记录文本/图片持久化，筛选框搜索，Ctrl+P/右键收藏(不淘汰)，单击复制/双击粘贴；`clipboard.ignoreApps` 来源应用不进历史 |
| 任务栏音量 | 任务栏上滚轮/中键 | 滚轮调音量、中键静音（步进/反向可配） |
| 检查更新 | 托盘→工具 | 自动发现新版本并静默升级 |

## 设置中心（托盘 → 设置）

左侧分类：**翻译 / OCR / 截图 / 剪贴板 / 任务栏音量**。
- 翻译：引擎（本地 LLM 零花费 / 百度）、目标语言、内置英文示例一键测试（结果直接显示在界面）
- 截图：保存目录、三个热键（留空走默认降级链，改后重启生效）
- 剪贴板/音量：开关与参数，保存即生效
- 配置持久化：安装目录 `shot-service.json`（含密钥，安装包已排除、不覆盖用户已有配置）

## 给 AI Agent 的能力（HTTP + MCP，27 个工具）

先 `GET /health` 确认存活（`session:1`）。两种接入等价：

### 方式一：HTTP（零依赖，任何语言直接调）

| 分类 | 接口 |
|---|---|
| 看 | `GET /shot`（全屏/区域/窗口/显示器）· `/active` · `/window?title=` · `/monitors` · `/img/文件名`（图片托管） |
| 动 | `/mouse/move·click·scroll·down·up·drag·pos` · `/keyboard/type·press·hold` |
| 窗口管理 | `/win/activate·max·min·restore·close·move·wait·list`（置前三级策略防前台锁） |
| 语义操作 (UIA) | `/ui/tree`（元素树：类型/名称/位置/模式）· `/ui/click?i=` · `/ui/set?i=&value=` · `/ui/read?i=` · `/ui/readall`（批量读值） |
| 录屏 | `/record/start·stop·status`（MP4 h264） |
| 剪贴板 | `/clipboard/history` · `/clipboard/set` |
| 其它 | `/app/run` · `/app/runas`（管理员，UAC 用户确认） · `/taskbar-volume` · `/health` · `/guide`(SKILL 全文) · `/check-update` · `/update` |

### 方式二：MCP（Claude Desktop / Cursor / DSH 等开箱即用）

`mcp-bridge.js` 是零依赖的 stdio MCP 服务，内部转发到同机 HTTP。配置（Claude Desktop `claude_desktop_config.json`）：

```json
{ "mcpServers": { "win-desktop-helper": { "command": "node", "args": ["C:/path/to/mcp-bridge.js"] } } }
```

27 个工具与 HTTP 一一对应：`screen_capture` `window_info` `active_window` `monitors` `win_manage`
`mouse_move` `mouse_click` `mouse_scroll` `mouse_down` `mouse_up` `mouse_drag` `mouse_pos`
`keyboard_type` `keyboard_press` `keyboard_hold` `ui_tree` `ui_click` `ui_set` `ui_read` `ui_readall`
`record_start` `record_stop` `record_status` `clipboard_history` `clipboard_set`
`app_run` `app_runas` `taskbar_volume` `get_skill` `update_skill`。

**首次操作前必须调用 `get_skill`**（服务端强制，含操作铁律与避坑清单）；踩坑后 `update_skill` 写回共享手册。

## Agent 安全纪律（内置强制）

1. 点任何东西之前先定位（`window_info`/`ui_tree`），确认前台再操作
2. 输入前确认前台是目标窗口
3. 操作后文件系统/截图验证，不信 UI 口头承诺
4. 删除/发送等敏感操作先向用户确认
5. 管理员操作走 `app_runas`（UAC 由用户点确认，AI 无法静默提权）

## 编译（agent 可自助，需 .NET Framework 4.8）

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:winexe -optimize+ ^
  -win32icon:icon.ico -win32manifest:app.manifest -out:shot-service.exe ^
  -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
  -r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll ^
  -r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll ^
  -r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll ^
  shot-service.cs shot-capture.cs shot-ocr.cs shot-translate.cs shot-config.cs shot-automation.cs shot-pick.cs shot-pick-ui.cs shot-longshot.cs shot-annotate-geom.cs
```

## 目录结构

| 文件 | 职责 |
|---|---|
| `shot-service.cs` | 主类：托盘/HTTP/热键/剪贴板/音量/自更新/MCP |
| `shot-capture.cs` | 区域截图套件：遮罩/工具条/标注/贴图/结果面板/录屏 HUD |
| `shot-automation.cs` | 自动化：窗口管理/鼠标扩展/UIA/录屏/提权 |
| `shot-pick.cs` / `shot-pick-ui.cs` | 划词悬浮球：选区监听/取词链/低级鼠标钩子 + 三个免激活窗体 |
| `shot-longshot.cs` | 长截图：滚动逐帧拼接（手动/自动） |
| `shot-annotate-geom.cs` | 标注几何辅助 |
| `shot-ocr.cs` / `shot-translate.cs` | OCR / 翻译引擎 + 轻量 JSON 工具 |
| `shot-config.cs` | 设置中心（五分类） |
| `mcp-bridge.js` | MCP stdio 桥（零依赖 node 脚本） |
| `setup.iss` | Inno Setup 打包脚本 |

## 关键限制与坑

- 必须目标用户已登录（Session 1 Active）；锁屏/注销期间不可用
- 管理员权限的程序会弹 UAC（secure desktop 无法自动点，需用户确认）；要操作 elevated 窗口请用托盘"以管理员重启"
- Win11 商店版应用（如新记事本）标题是英文 "Notepad"；可用 `/active` 兜底
- 后台启动的窗口受"前台锁"影响可能不置前，`/win/activate` 已内置三级置前策略
- 服务仅绑定 `127.0.0.1`，无鉴权，勿暴露公网
- 录屏需要 PATH 里有 ffmpeg（winget install ffmpeg）

## License

MIT
