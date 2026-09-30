# patterns/remote-assist.md — 跨机远程协助（helper 当远程执行通道）

> 2026-09-30 reasonix 实战定稿。场景：**在 A 机的 agent 里操控 B 机的桌面/服务/文件**，B 机装了 win-desktop-helper（本机 :18800）。
> 本机的 `xdn-remote-ops` 技能（`wdh_probe.py`/`wdh_deploy.py`/`wdh_sftp_set.py`）走的是 **SSH**；本篇是 **helper 通道**，SSH 进不去时的替代，且权限更高（Session 1 + 管理员）。

## 0. 什么时候用这条通道
- 目标机 sshd 不通 / 无凭据 / 免密没配好（09-30 实测：XDN 用密码 paramiko 能连 LeCoo，但 `administrators_authorized_keys` 免密始终被服务端拒 → 立刻切 helper 通道，不耗时间）。
- 需要在 **Session 1 交互式桌面**里做事（起 GUI、点按钮、截图、看真屏幕）——SSH 落 Session 0，做不到。
- 需要**管理员权限**（helper 本身 elevated，`/health` 里 `elevated:true`）。

## 1. 通道三要素
```
A 机（发命令的 agent）                    B 机（被协助的机器）
  python server.py <dir> 18099  ←─ iwr ──  powershell -EncodedCommand <b64>
      （收结果）                 ─拉脚本→   （由 helper app/run 拉起，Session 1）
                                ←─ POST ──  脚本执行完把 JSON 结果回传
调用入口： GET http://<B 机 IP>:18800/app/run?path=powershell.exe&args=<urlencoded>&wait=4000
```
- A 机侧临时 HTTP 服务**两职**：发脚本（GET）+ 收结果（POST 落盘成 `result-<时间戳>.json`）。
- 脚本**不落 B 机持久目录**（只写 `%TEMP%`），用完即散，A 机侧关端口就彻底无痕迹。

## 2. 🔴 四个必踩的坑（09-30 全部实测踩过，别再踩）
1. **脚本文件必须带 BOM 的 UTF-8（`-Encoding utf8BOM`），或全写 ASCII。**
   helper 拉起的是 `powershell.exe` = **5.1**，无 BOM 的 UTF-8 中文注释会被按 GBK 解码，**直接破坏语法**（报一串 `??` 乱码的解析错误，极像"脚本没传对"）。本地用 `[Parser]::ParseFile` 自检再发。
2. **`app/run` 常返回 `{"ok":true,"pid":0,"name":"","window":null}` —— 这不代表失败。**
   09-30 实测：多次 `pid:0` 的调用，脚本其实完整跑完并回传了。**判成败只看有没有收到回传**，别信返回值，更别据此重复触发（会重复执行副作用）。
3. **回传文件命名精度**：按秒命名会**同秒覆盖**（fixA 的 JSON 和兜底状态串同一秒到达，前者被吞）。server.py 用 `%H%M%S%f`（微秒）。等待时按 **CreationTime** 取新文件，别用 `Length -gt 300` 这类"看起来是新文件"的筛法（09-30 因筛法错读到旧结果，白等两轮）。
4. **别把大文件回传成 JSON**：09-30 一次 `ConvertTo-Json` 把 `Get-Content` 的 `FileInfo` 对象整个序列化（含 PSDrive/Provider 树），**回传 10MB / 80MB**；另一次拉 SKILL.md 全文回传 **6.7MB 还把 ConvertFrom-Json 炸了**（md 里有 base64 图和中文）。⇒ 回传前把值转成**纯字符串**，正文只取需要的片段（`Select-Object -First/-Skip`、`-join "\`n"`），或对半读。

## 3. 常用只读探针（一次采齐，省往返）
```
GET /health              → pid/session/uptimeSec/elevated/version/build/exePath/logPath/uia 账本
GET /monitors            → 显示器列表
GET /shot                → 截图（JSON，含路径/字节数）；/shot?axes=1 带坐标轴
GET /active, /window     → 前台窗口（/window 需参数，缺参 400）
```
远程跑脚本的固定三段式（A 机侧发起）：
```powershell
$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrapper))
$enc = [uri]::EscapeDataString("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $b64")
Invoke-WebRequest "http://<B>:18800/app/run?path=powershell.exe&args=$enc&wait=4000"
```
bootstrapper 只做两件事：`iwr` 拉真脚本 → `&` 执行 → 把 `$_` 异常也 POST 回来（**外层 try/catch 必须存在**，否则脚本抛错你就只能靠"没回传"倒推）。

## 4. 用完必须收的尾
- `job_kill` 关掉 A 机侧监听端口。**裸 HTTP 无鉴权**，Tailscale 网内也别留。
- B 机 `%TEMP%` 里拉下来的 `.ps1`/`.log` 可留可删（重启即散），**别往 B 机仓库/ProgramData 丢临时文件**。

## 5. 与另一条保底通道的分工
| 需求 | 走哪条 |
|---|---|
| 看桌面、起 GUI 程序、点鼠标、截屏验渲染 | **helper**（Session 1） |
| 跑非交互命令、改服务、批量文件 | 首选 **SSH**（配好免密后最省心） |
| 界面级排障（UU 卡住、白屏、托盘没了） | helper 的 `/shot` 字节数判据：>300KB=正常，<10KB=白屏 |
| 都要不到时用 | `mstsc <B 机 Tailscale IP>`（会锁 B 机本地屏幕） |

## 6. 关联
- UU 远程（GameViewer）排障全案：openmem `b8cf00fb`（含"helper 通道跨机操控"专段）。
- UU 日志占地与清理：openmem `ce4bb708`。
- 本机（LeCoo）作被协助端时的防火墙放行：见仓库 `wdh_fw*.ps1` / `wdh_set_peers.py`。
