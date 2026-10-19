<!-- win-desktop-helper 专项经验册(由 update_skill 自动建)。写经验请标日期, 拿不准的写"未验证"。 -->
# win-desktop-helper 专项经验


## 远程部署 wdh 的三个坑 + v0.0.24 发版口径（版本号 5 处 / Release 命名硬约束）

_记录日期: 2026-09-23 · 应用: win-desktop-helper_

## 🔴 远程部署 wdh + v0.0.24 发版口径（2026-09-23 实测）

### 坑 1：`wdh_deploy.py` 旧版第 5 步会**重建废弃任务**（已修）
旧写法 `schtasks /Create /TN dsh-shot-helper /TR <exe> /SC ONCE /ST 00:00 /IT /RL HIGHEST /RU oadan /F` 后 `/Run`，两个硬伤：
1. `/SC ONCE /ST 00:00` 是一次性且**时刻已过**的触发器 —— 除"建完立刻 /Run"那一次，**重启后永不启动**，当持久自启用是错的；
2. `dsh-shot-helper` 自 2026-09-18 起**已废弃并从本机删除**，与程序自注册的 `WinDesktopHelper` 职责重复（单实例互斥会让进程反复消失）。

**已修**（`~/.workbuddy/skills/xdn-remote-ops/wdh_deploy.py`，备份 `.bak-20260923-014116`）：第 5 步改成「删除废弃任务 + `schtasks /Run /TN WinDesktopHelper`」。持久自启靠程序自注册的 `WinDesktopHelper`（`/sc ONLOGON /rl HIGHEST`），**部署只需 Run 它**，不要再 Create 任何任务。

### 坑 2：`schtasks /Run` 后**别用一瞬间的 tasklist 下结论**
XDN 上 `/Run` 返回 SUCCESS 后立刻查：任务 Status=Ready、`tasklist` 空 → 误判失败；**等 6 秒再看就有了**（PID 31276）。
另外任务详情里 `Last Result: 1` 是**上一次运行**的残留结果码，不是本次结论。
**判据以 `/health` 为准**（`version` / `session=1` / `elevated=true` 三个一起看）。

### 坑 3：脚本会把**完整配置（含 API key）打印出来**
`wdh_deploy.py` 第 4 步原本 `print("--- 改前/改后配置 ---")` 直接打印整个 `shot-service.json` ⇒ **agnes key / 百度 key / `sk-200418` 全落进日志和对话记录**。
⚠️ 部署前先想清楚 stdout 会进哪里。已加 `redact()` 脱敏（apiKey/apiKeys/baiduKey/askKey 一律打码）。

### v0.0.24 发版口径（下次照做）
| 环节 | 要点 |
|---|---|
| 版本号 | **5 处同步**：`AssemblyInfo.cs`（AssemblyVersion + AssemblyFileVersion）、`setup.iss`（AppVersion + OutputBaseFilename）、`shot-service.cs`（`APP_VERSION`） |
| 编译 | 参数以 `build-annotation.cmd` 为准（11 个源文件，**不含 shot-watcher.cs**，多塞报 CS0017）；DSH 的 pwsh 里可直接调 csc（exit=0，只有无关 warning）。**编到 TEMP 先验语法**，不碰正在跑的 exe |
| 换 exe | 先备份运行目录那份 → `/app/exit?confirm=1` 让旧实例自退（兜底 `Stop-Process`）→ 拷**仓库根 + AppData 两份**并核对 md5 → `schtasks /run /tn WinDesktopHelper` |
| 打包 | `"C:\Users\oadan\AppData\Local\Programs\Inno Setup 6\ISCC.exe" setup.iss` → `release\win-desktop-helper-setup-<ver>.exe` |
| 发布 | **GitHub Release 的 tag 必须是 `v<ver>`、附件名必须是 `win-desktop-helper-setup-<ver>.exe`** —— 程序自动更新是按 `releases/download/<tag>/<附件名>` 硬拼的（`shot-service.cs` L3210）。发完**必验 `releases/latest` 指向新 tag**，否则别的机器查不到更新 |
| push | `git -c http.proxy=http://127.0.0.1:7897 -c https.proxy=http://127.0.0.1:7897 push`（Clash 7897，不走代理 TLS 会失败） |

### 2026-09-23 落地状态
- **LeCoo（本机）+ XDN 均已 0.0.24**，`/health` 实测 `version=0.0.24` / `session=1` / `elevated=true`；XDN 旧任务 `dsh-shot-helper` 已删
- **shlc 未升级**（那台本来 OCR 就坏：没装 Ollama，用的是百度/远程 API）


## HTTP API 鉴权：直调 18800 必须带 token（health/img/pick 除外）

_记录日期: 2026-10-19 · 应用: win-desktop-helper_

2026-10-19 起服务端强制鉴权（上线前全检立项）：回环来源的非豁免端点必须带 X-WDH-Token 头或 ?token= 参数，否则 403。token 文件 %LOCALAPPDATA%\win-desktop-helper\http-token（首次访问服务后生成，32hex）。豁免端点：/health、/img/*、/pick-*（旧脚本里裸 curl http://127.0.0.1:18800/mouse/* /keyboard/* /capture 等会 403，改成 ?token=$(cat %LOCALAPPDATA%/win-desktop-helper/http-token) 或加 header 即可）。Tailscale 来源（100.64.0.0/10）放行不用 token——防火墙 WDH-18800 规则同日已收窄到该网段，物理局域网/公网进不来。mcp-bridge.js 已自动带 token，走 bridge 的 agent 无感。
