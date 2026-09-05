# ZCODE 接手：win-desktop-helper 项目上下文 + 待解决问题

> **状态刷新 2026-09-05 14:40（agents-to-feishu 会话的 ZCode，接了串台消息）**：
> - **文字旋转跟随已修**（shot-capture.cs）：根因 = `TextBoxBounds()` 忽略 textAngle（轴对齐框），线框和四角按钮全锚在未旋转的框上。新增 `RotatedTextCorners()`（文字框四角按 textAngle 旋转到客户区，测量口径与 TextBoxBounds 一致 ±4/+10）；OnPaint 编辑线框改为 `DrawPolygon(旋转后四角)`；`RelocateTextUI()` 容器=旋转四角包围盒、四按钮贴旋转后角点（TL/TR/BL/BR 一一对应 旋转/关闭/移动/缩放）、确认/取消在旋转包围盒下方。
> - **已提交文字悬停编辑已加**：`HitTextAnnot()`（点逆旋转到文字局部坐标判定，最上层命中）+ `UpdateTextHover()`（OnMouseMove 里调，仅 tool=="text" 时检测——**其他工具不调用 = 文字当背景**）+ OnPaint 悬停蓝框（随文字旋转）+ `EditTextInput()`（点击载入原文/角度/字号/颜色/字体，`textEditIdx` 提交时原位替换 annots[idx] 不新增一笔；清空提交=保留原文）。`CloseTextInput` 复位 hoverTextIdx/textEditIdx。
> - **已编译并上线**：csc 零错误（新 exe 196608B），服务已重启。**待实测**：①文字工具→输入→旋转拖动：线框+四角按钮应跟文字同转；②提交后文字工具悬停文字出蓝框、点击改字原位替换；③箭头/序号工具下点文字区域应直接画箭头/落序号（文字不被命中）。
> - 已知遗留：重编辑载入会把 curColor/curFontPt/curFontFamily 覆盖为原标注值（提交后属性栏显示值不同步回——如需同步再补一行）。

> **状态刷新 2026-09-05 01:05（ZCode 会话）**：
> - **P0-1 已修并实测通过**：根因不是交接文档猜的那些——是 `TransparencyKey=BackColor` 把遮罩抠成完全不可见+鼠标穿透（复现证据：遮罩窗口存在但屏幕上看不见、拖拽落不到它）。已照 PixPin/ShareX 重写：预合成暗化背景（BackgroundImage）+ 脏区重绘 + 松手不关遮罩 + 图标工具条（矩形/椭圆/箭头/画笔/文字/序号标注+撤销+OCR+翻译+保存/复制/另存）。用户亲手实测：标注全工具✓ OCR✓ 保存✓。
> - **P0-2 已修**：新实例自动顶替旧实例（日志 `take-over`）；build 指纹（exe mtime+大小）进启动日志/托盘菜单/启动气泡/`/health`，肉眼可验。
> - **P0-3 已修**（代码层）：设置窗显示配置路径+打开配置按钮+保存失败弹窗+掩码/显示切换+测试按钮；Cfg 解析器健壮化。**设置窗 UI 尚未最终走查**。
> - **P1-4/5/6 已做**（测试按钮/掩码/去全屏气泡）。
> - **OCR 中文乱码已修**：WebClient 未设 Encoding，Ollama 响应无 charset → Latin-1 解码中文全毁。两处 `wc.Encoding=UTF8`。
> - **重大发现：ZCode 环境能跑 csc**（WorkBuddy 才拦）。Git Bash 下用 `-` 参数风格。编译+启动+桌面控制全闭环 agent 可自助。
> - 剩余：设置窗走查、OCR prompt 质量、长截图/贴图（M3-M5）、回归（剪贴板/音量/MCP）。

## 0. 一句话任务
接管修复一个 Windows 托盘常驻工具（C# 单 exe）的**区域截图卡死**和**设置窗 bug**。源码在本地，编译由用户本机跑（AI 沙箱拦截 csc）。用户实测目前几乎全挂。

---

## 1. 项目身份
- 仓库：github.com/oadank/win-desktop-helper（公开）
- 本地目录：`C:\D\opt\win-desktop-helper`
- 技术栈：C# .NET Framework 4.x，`csc.exe` 多文件编译成**单 exe**（`shot-service.exe`）
- 运行形态：托盘常驻，Session 1（避 Session 0 隔离），HTTP `127.0.0.1:18800`，供 AI 通过 `mcp-bridge.js`（MCP stdio）调用
- 项目目标：吸收 **PixPin** 的截图能力（截图 / OCR / 翻译 / 后续贴图标注录屏）
- 当前版本：**0.0.17（内部修订中，未发版）**
- 主类名：`ShotService`（**不是 Program**），各 .cs 都是 `partial class ShotService`

---

## 2. 源码文件
| 文件 | 职责 |
|---|---|
| `shot-service.cs` | 主类，`Main`/托盘菜单/HTTP 服务/热键/剪贴板历史/任务栏音量钩子/自更新/`APP_VERSION`/单实例互斥 |
| `shot-capture.cs` | 区域截图：`CaptureOverlay`（全屏框选遮罩）+ `ToolBarForm`（动作工具栏）+ `ResultForm`（结果窗） |
| `shot-ocr.cs` | `IOcrProvider` + `QwenVlOcrProvider`（调本机 Ollama `:11434` 模型 `qwen3-vl:4b-instruct`）+ `Cfg()` 配置读取 |
| `shot-translate.cs` | `ITranslateProvider` + `LocalLlmTranslateProvider` + `BaiduTranslateProvider`（appid+key 做 md5 签名，**实测可用**） |
| `shot-config.cs` | 设置窗 `ShowSettingsForm` + `LoadCfgDict`/`SaveCfgDict`（写 `shot-service.json`） |
| `shot-service.json` | 运行配置（含百度真实 key，**已 git 排除**） |
| `setup.iss` | InnoSetup 打包脚本（版本号与 exe 同步） |
| `mcp-bridge.js` | MCP stdio 桥，给 Claude/DSH 暴露 :18800 工具 |
| `SKILL.md` | agent 操作手册（编译命令、铁律） |
| `docs/screenshot-suite-design.md` | PixPin 移植蓝图（M1~M5 里程碑） |

---

## 3. 编译 / 运行 / 打包 / 发版流程
**编译（只能用户本机跑，AI 沙箱按 LOLBin 硬拒 csc）：**
```
cd C:\D\opt\win-desktop-helper
Stop-Process -Name shot-service -Force -ErrorAction SilentlyContinue
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /win32icon:icon.ico /win32manifest:app.manifest /out:shot-service.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll shot-service.cs shot-capture.cs shot-ocr.cs shot-translate.cs shot-config.cs
Start-Process .\shot-service.exe
```

**⚠️ 已知部署坑（关键）：** 程序用单实例互斥 `Global\WinDesktopHelper`，**不先 Stop-Process 就 Start-Process，新进程会静默退出**，用户跑的还是旧 exe → 表现为"改了代码没变化"。这已经反复坑了用户（见 P0-2）。

- **日志**：`shot-service.log`（exe 同目录，`Log()` 写入），排障第一手证据
- **打包**：`ISCC.exe setup.iss`（AI 可跑，不拦）
- **发版**：`gh release create vX.Y.Z <包路径>`（**用户没实测通过绝不能发**）
- **配置脱敏**：`shot-service.json` 含真实百度 key。打包前备份到 `C:\D\opt\backup\` → 换空模板 → 打包 → 还原本机真实 key。安装包用 `onlyifdoesntexist` 不覆盖已装配置

---

## 4. 功能现状（用户实测结论）
1. 全屏截图：**能正常截图**（但会弹托盘提醒，见 P1-6）
2. 区域截图：**卡死，完全不能用**（P0-1，核心问题）
3. OCR / 翻译：代码已实现，因区域截图卡死**未能实测**
4. 设置窗：**一堆 bug**（P0-3、P1-4、P1-5）
5. 剪贴板历史 / 任务栏音量滚轮 / MCP 工具 / 自更新：老功能，自 v0.0.14 起**未回归测试**（P1-7）

---

## 5. 待解决问题清单（按优先级）

### P0-1 区域截图卡死（必须修）
- **症状**：托盘 → 截图 → 区域截图 → 拖框 → 松手后，**全屏半透明遮罩不消失**，鼠标被罩住，托盘右键所有按钮点不了，软件假死。
- **用户原话**："依然卡死，依然没法用""每个按钮都点不了了""你改的区域截图，是没有一点点的变化"
- **已尝试的修复（2026-09-04 21:29 重写 `shot-capture.cs`）**：照 PixPin/ShareX 交互改成——松手**立即截屏 + 立即关遮罩**，然后弹**独立动作工具栏**（保存 / 复制 / 另存 / OCR / 翻译 / 取消），遮罩绝不跨过松手存活。
  - 21:45 编译报 `CS0122`（`SaveToShotDir` 在 `CaptureOverlay` 内私有，`ToolBarForm` 访问不到）→ 已修：把 `SaveToShotDir` 提升到 `ShotService` 类级。
  - 21:51 用户编译通过。
- **但用户 22:10 实测仍卡死、且"没有一点变化"** → 高度怀疑**跑的不是新 exe**（21:51 那次 `Start-Process` 前用户没跑 `Stop-Process`，互斥导致新进程没起来）。**这是首先要排除的，见 P0-2。**
- **若确认是新 exe 仍卡**，排查方向：
  1. `ShowCaptureOverlay()` 用 `hk.Invoke(...)` 把 `ShowDialog` 排到热键线程，托盘线程**同步等待**——遮罩不关则托盘线程永久阻塞
  2. `OnMouseUp` → `DialogResult = DialogResult.OK` + `Close()` 链路是否真的执行（看日志有无 `capture take err`）
  3. `Overlay` 的 `TopMost` / `Opacity=0.35` / `TransparencyKey` 组合的生命周期
- **参考做法**：PixPin / ShareX 都是「松手 → 截图 → 弹独立工具栏」，遮罩立即销毁，绝不长期存活。

### P0-2 部署自验证漏洞（用户为此发火）
- **现象**：每次改完代码，用户编译+启动，**无法判断跑的是不是新代码**，反复出现"跑了旧 exe → 修复看起来无效"的假象。
- **用户原话**："每次都怀疑跑的是不是旧代码，每次都不自己堵死这个漏洞"
- **要求**：设计一个机制**堵死**这个漏洞。例如：启动时日志打印 `APP_VERSION` + 源码构建指纹（各 .cs 的 mtime 或哈希）；托盘菜单显示实际加载版本；或 HTTP `/version` 端点。让用户一眼确认"跑的是不是刚编的"。

### P0-3 设置窗保存失效
- **症状**：
  1. 手动选「百度翻译」→ 保存 → 重开设置窗，仍显示「本地 LLM」
  2. 百度 APP ID / 密钥填了 → 重开是空（用户自己填了重开也空）
- **已知事实**：`shot-service.json` 里**已预填真实百度 key 且 `provider=baidu`**，但用户打开看到空 → 说明读取失败或进程跑的是旧代码。
- **排查点**：`SaveCfgDict` 写 `AppDomain.CurrentDomain.BaseDirectory\shot-service.json` 是否成功（日志搜 `config saved`）；`Cfg()` 是否读同一文件；进程版本（P0-2）；json 是否被打包脱敏模板覆盖。

### P1-4 设置窗缺「测试」按钮
- 用户要：填完百度 / 本地 LLM 配置后，能**一键测试连通性**（例如试着翻译一句话，返回成功/失败原因）。

### P1-5 密钥要掩码显示
- 已填的密钥应显示 `****`（`PasswordChar`），**不要明文显示**；空的时候就是空。用户原话："密钥默认要显示为**，表示已经填写了。别给我直接显示出来。"

### P1-6 全屏截图弹托盘提醒
- **用户疑问**："全屏截图后弹出提醒，为何要弹？AI 调用也弹提醒吗？之前没有提醒吧？"
- **待办**：查 `DoShot` 路径里的 `TrayIcon.ShowBalloonTip`；**AI 通过 HTTP/MCP 调用 `/shot` 时不应弹气泡**（或做成可配置开关）。

### P1-7 回归测试（别顾此失彼）
- 剪贴板历史（热键 `Ctrl+Alt+V`）、任务栏滚轮调音量、MCP 各工具、自更新机制，自 v0.0.14 起都没回归过，改动后要一起验。

---

## 6. 铁律（必须遵守，血泪教训）
1. **改完代码绝不主动发版 / 打包**——必须等用户本机实测通过、用户明确说"发"，才走脱敏打包 + `gh release`。历史教训：0.0.16 / 0.0.17 都在用户没测时就发了，用户发火。
2. **改动后必须让用户能自验证跑的是新代码**（P0-2），不要让用户猜。
3. **第三方 API 参数先实测确认够用再写界面**。百度翻译标准 API 只需 **appid + key 两个参数**（已实测：`appid+key` 直接调 `fanyi-api.baidu.com/api/trans/vip/translate` 返回正确译文）；曾因没测多加了无用的第三个字段。
4. **含真实凭据的 json 绝不进 git / 发布包**（`.gitignore` 已排除，打包前脱敏）。
5. **照抄 PixPin / ShareX 的交互**，不要自创交互。用户为此反复强调过很多次（这是用户最在意的点）。
6. **沟通风格**：极简直白中文，别自我辩护，认错 + 直接动手；改完给用户明确的**逐项测试步骤清单**，让用户验收。

---

## 7. 关键入口速查
- 区域截图：托盘菜单「截图 → 区域截图(框选)」→ `ShowCaptureOverlay()`（`shot-service.cs`）
- 设置窗：托盘「设置...」→ `ShowSettingsForm()`（`shot-config.cs`）
- 配置读写：`shot-ocr.cs` 的 `Cfg()` / `shot-config.cs` 的 `SaveCfgDict()`
- 单实例互斥：`shot-service.cs` 的 `MUTEX_NAME = Global\WinDesktopHelper`
- 日志：`shot-service.log`（exe 同目录）

---

## 8. 交接背景补充（为什么用户情绪激动）
这个项目连续多轮出现「改完 → 未实测就发版 → 用户一测就挂 → 再改」的循环，且每次都要怀疑"跑的是不是旧代码"。用户要求：**先把部署自验证漏洞堵死，再谈功能修复**。做的时候优先解决 P0-2（让版本可验证），再实测 P0-1（区域截图），最后收 P1 各项。
