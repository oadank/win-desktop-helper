# Win Desktop Helper

给**任意 AI Agent / 服务进程**（DSH、Claude、Codex、Gemini、……）提供 Windows 桌面能力的桥接服务：
**看**（截图/窗口/显示器）+ **动**（鼠标/键盘/运行程序）。

## 为什么需要它

Windows 从 Vista 起引入 **Session 0 隔离**：以服务方式运行的程序（如 nssm 托管的 agent）在 Session 0，
**没有用户桌面** —— GDI 截图报"句柄无效"、SendInput 无目标、截图全黑。本服务常驻在**用户登录会话（Session 1）**，
以 HTTP / MCP 暴露统一接口，Session 0 的任何进程按需调用即可完成"看屏幕 + 操作电脑"。

## 快速开始

1. 编译（需 .NET Framework 4.8，Windows 自带 csc）：

```bat
cd win-desktop-helper
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ ^
  /out:shot-service.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll shot-service.cs
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ ^
  /out:shot-watcher.exe /r:System.dll shot-watcher.cs
```

2. 启动（在登录会话）：

```bat
shot-service.exe          # 常驻 + 托盘图标（-notray 可不显示托盘）
shot-watcher.exe          # 可选：自愈守护（30s 探测，挂了自动拉起）
```

3. 验证：`curl http://127.0.0.1:18800/health` → `{"ok":true,"session":1,...}`

## 两种接入方式（AI 任选）

### 方式一：HTTP（零依赖，任何语言直接调）

| 能力 | 接口 |
|---|---|
| 存活检查 | `GET /health` |
| 截图 | `GET /shot?region=all` \| `?screen=0` \| `?x=&y=&w=&h=` \| `?window=关键词` |
| 窗口信息 | `GET /active`（当前前台）`GET /window?title=关键词`（按标题查 rect） |
| 显示器 | `GET /monitors` |
| 鼠标 | `GET /mouse/move?x=&y=` `GET /mouse/click?x=&y=&button=left\|right\|middle&double=0\|1` `GET /mouse/scroll?delta=±120`（正上负下） |
| 键盘 | `GET /keyboard/type?text=中文也行`（Unicode 直发，不依赖输入法）`GET /keyboard/press?keys=ctrl+shift+a` |
| 运行程序 | `GET /app/run?path=C:\\Windows\\explorer.exe&args=`（ShellExecute，GUI 在用户桌面可见） |

- 截图保存路径：`<用户图片目录>\Screenshots\shot_毫秒时间戳.png`（可用环境变量 `WDH_SHOT_DIR` 覆盖）
- 操作前建议 `GET /active` 确认前台目标，防止输入进错窗口

### 方式二：MCP（Model Context Protocol，Claude Desktop / Cursor 等开箱即用）

`mcp-bridge.js` 是零依赖的 stdio MCP 服务，内部转发到同机 HTTP。配置示例（Claude Desktop `claude_desktop_config.json`）：

```json
{
  "mcpServers": {
    "win-desktop-helper": {
      "command": "node",
      "args": ["C:/path/to/win-desktop-helper/mcp-bridge.js"]
    }
  }
}
```

暴露 10 个工具：`screen_capture` `window_info` `active_window` `monitors`
`mouse_move` `mouse_click` `mouse_scroll` `keyboard_type` `keyboard_press` `app_run`

## 文件清单

| 文件 | 说明 |
|---|---|
| `shot-service.cs` | 主服务源码（C#5，.NET Framework 4.8，winexe） |
| `shot-watcher.cs` | 可选自愈守护源码（30s 探测 18800，挂了拉起，15s 防抖） |
| `mcp-bridge.js` | MCP stdio 桥（Node.js，零依赖） |
| `shot-service.log` / `shot-watcher.log` | 运行日志 |

## 托盘图标

默认显示右下角托盘图标（代码绘制：深蓝底 + 白色镜头 + 青色核心，见 `BuildIcon()`，可自行改图）。
右键菜单：立即截图 / 打开截图目录 / 打开日志 / 隐藏托盘图标 / 退出服务。
- 新图标首次出现可能收在系统托盘**溢出区**（任务栏 `^`），拖出来即固定
- 隐藏后重启服务恢复显示；`-notray` 参数可完全禁用托盘

## 推荐部署（本机 agent 全部可用）

- 登录自启：`HKCU\...\CurrentVersion\Run` 注册 `shot-service` 与 `shot-watcher`（crashed 自愈）
- 手动拉起/重启：`schtasks /run /tn <your-task>`（用 /it 交互式任务，把进程放进登录会话）
- 注意：**不要在 Session 0 里直接启动本服务**（否则截图仍然黑屏，/health 的 session 字段会暴露）

## 关键限制与坑

- 必须目标用户已登录（Session 1 Active）；锁屏/注销期间不可用
- `/app/run` 需要管理员权限的程序会弹 UAC（secure desktop 无法自动点，需用户确认）
- Win11 商店版应用（如新记事本）是 WinUI 窗口，`/window` 按标题可能查不到（标题为英文 "Notepad"）；可用 `/active` 兜底
- 后台启动的窗口受"前台锁"影响可能不置前，用 `alt+tab` 或点击窗口区域激活
- 服务仅绑定 `127.0.0.1`，无鉴权，勿暴露公网

## License

MIT