<!-- XiaomiMiMo 专项经验册(由 update_skill 自动建)。写经验请标日期, 拿不准的写"未验证"。 -->
# XiaomiMiMo 专项经验


## 从服务会话(Session 0)用 Start-Process 拉 GUI 必假死：launch-failed 18，别误判成"应用被搞坏"

_记录日期: 2026-09-23 · 应用: XiaomiMiMo_

【2026-09-23 实测】AI 助手自己的 pwsh 是 nssm 服务(dsh-web)的子进程，处在 **Session 0**。从那里 `Start-Process 'Xiaomi MiMo.exe'` 起 GUI：进程数直接归 0，日志刷 `render-process-gone launch-failed 18` + `hidden session preparation failed ERR_FAILED(-2)`，**看起来像应用被搞坏了，其实根本没启动成功**。同一时刻可用物理内存 6.39GB/20.8GB —— 与内存无关。
✅ 正解：用本服务 `app(action="run", path=..., process=...)` 启动，它跑在用户桌面会话，返回 `session:1` + 真 hwnd，一次成功（5 进程、主进程 1145MB、日志 `config loaded successfully`）。**凡是启动/唤回 GUI 应用，一律走 app 工具，禁止 pwsh Start-Process。**
【连带两条】① Electron 关不掉：`taskkill /PID x`（不带 /F）报 "can only be terminated forcefully"，只能 /F；/F 强杀后没有残留单例锁文件（查过 AppData\Roaming\Xiaomi MiMo 下无 lockfile/Singleton*），所以起不来不是锁的问题，就是会话问题。② 该客户端 `engine-config\skills` 目录会**清掉非自己安装的外来子目录**（junction 和实体副本都被清过，junction 那次连真源内容一起删空）；要喂技能给它，放设置页列出的**兼容路径** `~/.agents/skills`（Agents 开放标准，DSH 同源，实测重启后不被清）。
