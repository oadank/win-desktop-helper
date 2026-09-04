# Win Desktop Helper 截图套件（吸收 PixPin）详细设计

> 状态：设计稿 v1，待老大审阅（未动代码）
> 目标：把 PixPin 的截图/OCR/翻译/保存/滚动/贴图/标注/录屏能力吸收进 win-desktop-helper 自实现，不再依赖 PixPin 进程

---

## 0. 现状发现（决定路线）

目录里翻到早期桥接残留，关键信息：

- `pixpin-shot.ps1` / `pixpin-shot.vbs` / `shot.js`：早期用 **PixPin 官方脚本 API** 做截图。
  - 机制：`PixPin.exe -f shot.js`，`shot.js` 调 `pixpin.directScreenShotSpRect(SpRectScreenUnderMouse, ShotAction.Copy)`（官方 API，见 pixpin.cn/docs/configuration/script）。
  - 用途：让 Session 0 的 agent 服务经计划任务 `dsh-pixpin-shot`（Session 1 / wscript 无窗口）截用户桌面，图存 `C:\Users\oadan\Pictures\Screenshots`。
- `shot-watcher.cs`：旧自愈守护（每 30s 探 18800 端口，不通拉起）。注释写"v0.0.6 起无 watcher"，属遗留文件。
- `unins000/001/002.exe`：历史安装器残留（旧版本卸载程序），可清理。

**结论：PixPin 可被命令行/脚本驱动，但你这次的诉求是"吸收/移植过来"= 功能自己做进 win-desktop-helper，不依赖 PixPin 常驻。** 下面按自实现路线设计。

---

## 1. 路线选择

| 路线 | 做法 | 工期 | 利弊 |
|---|---|---|---|
| **A 自实现（推荐）** | 截图/OCR/翻译/保存/对话框/滚动/贴图/标注/录屏 全在 win-desktop-helper 内实现 | ~28 人日 | ✅ 不依赖 PixPin、更新机制统一、现有 `/shot` 端点天然给 agent 用；❌ 工作量大 |
| B 桥接 PixPin | 热键/托盘触发时调 `PixPin.exe -f script.js`，读回 `Pictures\Screenshots` | 几天 | ✅ 极快；❌ 依赖 PixPin 常驻、两套更新、PixPin 自身更新坑、仍跑俩程序 |

**定调：走 A。** B 仅作"先用起来"备选——若你想要，我可先出 B 极简版解燃眉，再慢慢换 A。

---

## 2. 模块划分（partial class 多文件，单 winexe 产出）

现状 `shot-service.cs` 已 1600+ 行。全量加进来会冲 2500+，拆多文件避免爆炸。保留单 exe 产出（`csc` 多文件明文编译，你手动编译流程不变）。

| 文件 | 职责 | 里程碑 |
|---|---|---|
| `shot-service.cs` | `Main` / HTTP 路由 / 托盘 / 音量钩子 / 剪贴板历史（**现有，不动**） | — |
| `shot-capture.cs` | `CaptureOverlay`（半透明框选窗）、`CaptureHotkey`、区域截图入口 | M1 |
| `shot-ocr.cs` | `IOcrProvider` + `QwenVlOcrProvider`（Ollama qwen3-vl） | M2 |
| `shot-translate.cs` | `ITranslateProvider` + `BaiduTranslateProvider` + `LocalLlmProvider`（预留） | M2 |
| `shot-scroll.cs` | `Scroller`（长截图逐帧拼接） | M3 |
| `shot-annotate.cs` | `Annotator`（画笔/箭头/马赛克/文字/序号渲染） | M4 |
| `shot-pin.cs` | `PinWindow`（钉屏幕置顶/缩放/透明） | M4 |
| `shot-aux.cs` | `ColorPicker` / `Ruler` / `Magnifier`（辅助） | M4 |
| `shot-record.cs` | `Recorder`（录屏/GIF，FFmpeg 封装） | M5 |

所有新文件用 `partial class Program` 或独立 `static class`，共享 `Main` 所在程序集。

---

## 2.1 架构决策：单文件 / 多文件 / 多进程（回应老大疑问）

老大问："安装后不要求只用一个文件包含所有功能，可以做多个文件、多个进程，是不是会更好？"

**直接结论：对截图套件这类软件，业界标准答案是「单进程 + 内部模块化」，不是多进程。多进程不是"更先进"，而是"更复杂"。**

要分三层看（它们互不矛盾）：

| 层 | 含义 | 本项目做法 |
|---|---|---|
| **源码层** | 一个 .cs 拆成多个 .cs | ✅ 已定：8 个 partial class 文件（shot-capture/ocr/...），好维护、编译快 |
| **部署层（安装目录）** | InnoSetup 打包一整目录 | ✅ 本来就是多文件：主 exe + 图标 + 配置 + （录屏用的 ffmpeg.exe 可单独随包）+ OCR 模型文件 |
| **运行时进程** | 几个 exe 同时跑、互相 IPC | ⚠️ 推荐**单进程**；只有录屏（M5）可选拆独立进程 |

**业界事实（别人咋做的）：**

| 软件 | 进程模型 | 说明 |
|---|---|---|
| **ShareX**（开源版 PixPin，C#） | 单进程 | 内部模块化解耦，OCR/上传走后台 Task，不拆进程 |
| **Snipaste** | 单进程 | F1 截图 / F3 贴图都是同进程内不同窗口 |
| **PixPin / Greenshot / Flameshot** | 单进程 | 主流截图工具全是单进程 |
| **OBS / Captura（录屏）** | 录屏引擎独立 | 因为编码器重、要独占资源、易崩，才值得拆 |
| **Electron 系（如某些企业方案）** | 主进程+渲染进程 | Web 技术栈天然多进程，非 C# winexe |

**为什么不全拆进程：**
1. **收益有限**：多进程的真实收益只有两点——①崩溃隔离（某模块崩不影响主程序）②长任务不阻塞 UI。但截图 UI 崩的概率极低（就是画个窗），而"不阻塞 UI"用线程/Task 就能解决，不必上进程。
2. **成本高**：IPC（管道/命名管道/HTTP）、进程生命周期管理、更新时要逐个替换文件、调试复杂、还有 Session 0/1 隔离的经典坑（你之前就被这坑坑过）。
3. **更新机制刚稳**：v0.0.14 好不容易把"单 exe 替换"的死循环根除了，拆多进程会让更新逻辑（逐个杀、逐个换）重新变复杂，不划算。

**本项目推荐架构（最终定调）：**
- 源码：多 .cs 文件（partial class）—— ✅ 已设计
- 部署：单主 exe + 辅助文件随包（ffmpeg.exe 等按功能按需出现，但**不作为常驻独立进程**）
- 运行时：**单进程**跑全部功能（截图/OCR/翻译/贴图/标注/辅助），和 ShareX/PixPin 一致
- **唯一例外 = 录屏（M5）**：编码器重、可能崩、要独占 GPU/采集。两种落地：
  - 轻量：ffmpeg.exe 作为**随包文件**，主程序 `Process.Start` 调它录完回收（多文件但不多进程常驻）—— 推荐，最简单
  - 隔离：录屏拆**独立子进程**，崩了不影响主程序 —— 仅当实测录屏频繁崩再上

> 一句话：源码多文件（好维护）+ 部署单 exe（好更新）+ 运行时单进程（业界标准、最简单稳当）；"多进程"只在录屏这种重模块才考虑，且优先用"随包独立 exe + 按需 spawn"而不是常驻多进程。

---

## 3. 核心接口（provider 抽象，便于换后端）

### 3.1 OCR
```csharp
// shot-ocr.cs
interface IOcrProvider {
    Task<string> RecognizeAsync(Bitmap bmp);
}

// 默认实现：本地 qwen3-vl（你本机 Ollama :11434 常驻，零花费、最聪明）
class QwenVlOcrProvider : IOcrProvider {
    // POST http://127.0.0.1:11434/api/generate
    // body: { "model":"qwen3-vl:4b-instruct",
    //         "prompt":"OCR all text in image, output raw text only, keep layout",
    //         "images":[ "<base64>" ] }
    // 解析 stream JSON -> "response" 字段拼接
}

// 离线兜底：建议用 Tesseract（~20MB 模型随包），不碰 .NET Framework 调 WinRT 的坑
// （Windows.Media.Ocr 在 .NET FX 4.x 需特殊 winmd 引用，坑多，M2 先只上 qwen3-vl）
```

### 3.2 翻译
```csharp
// shot-translate.cs
interface ITranslateProvider {
    Task<string> TranslateAsync(string text, string from, string to);
}

// 默认：百度通用翻译 API（你现用免费）
class BaiduTranslateProvider : ITranslateProvider {
    // GET https://fanyi-api.baidu.com/api/trans/vip/translate
    // sign = md5(appid + q + salt + key)；需 appid + key（从配置读）
}

// 预留：本地 LLM（Ollama qwen / LiteLLM），零花费离线
class LocalLlmProvider : ITranslateProvider {
    // POST Ollama/LiteLLM chat，prompt="翻译以下为中文: ..."
}
```

---

## 4. 截图覆盖层（CaptureOverlay）设计 — M1 核心

- 全屏透明 `Form`：`FormBorderStyle.None`、`Bounds = VirtualScreen()`、`Opacity=0.4`、`TopMost=true`、`TransparencyKey` 设背景色。
- 交互：
  - `MouseDown` 记起点；`MouseMove` 画矩形 + 实时 `WxH` 尺寸标注；`MouseUp` 取矩形 → `DoShot(rect)`。
  - 键盘：`Esc` 取消 / `Enter` 确认 / `Ctrl+S` 弹保存对话框 / `C` 复制图到剪贴板 / `T` 翻译选中文本（M2）。
- 窗口自动吸附（M4 增强）：拖动靠近窗口边缘时磁吸 `GetWindowRect`。
- 放大镜/标尺（M4 辅助）：跟随光标局部放大。

**复用现有**：`DoShot(Rectangle)`（155 行）直接复用；新增重载 `Bitmap DoShotBmp(Rectangle)` 返回 Bitmap 供 OCR/标注用（不落盘）。

---

## 5. 数据流（截图 → OCR → 复制/翻译 → 保存）

```
热键 Win+Shift+S
  → CaptureOverlay 框选
  → DoShotBmp(rect) 得 Bitmap
  → 复制到剪贴板（图）：Clipboard.SetImage(bmp)   // 复用现有封装
  → 异步 OCR：QwenVlOcrProvider.RecognizeAsync(bmp)
        → 成功则回填剪贴板文本（可选，配置开关）
  → 翻译：BaiduTranslateProvider.TranslateAsync(text) → 弹小窗/复制
  → 保存：bmp.Save(path) 或 SaveFileDialog（STA 线程，进程已具消息循环）
```

HTTP 端点扩展（给 agent 用，复用现有 `/shot`）：`/shot?x&y&w&h&ocr=1&translate=1` → 返回 `{path, ocr, translate}`。

---

## 6. 热键/钩子复用（零新坑）

现有 `RegisterHotKey(hk.Handle, ...)` + `WM_HOTKEY` 在 `hkForm`（STA 消息循环，1135/1140/1160 行）已成熟。新增截图热键 `MOD_WIN|MOD_SHIFT, VK_S` 在同一窗注册，`WM_HOTKEY` 分支 `new CaptureOverlay().Show()`。音量钩子的自愈机制可照搬。

---

## 7. 配置机制（新增，当前无配置层）

当前 win-desktop-helper **无配置文件**（只有运行时 `clipboard-history.json`）。新增轻量 `shot-service.json`（同目录）：

```json
{
  "capture": { "hotkey": "Win+Shift+S", "saveDir": "C:\\Users\\oadan\\Pictures\\Screenshots", "autoCopy": true },
  "ocr":     { "provider": "qwen3vl", "endpoint": "http://127.0.0.1:11434/api/generate", "asyncClipboard": true },
  "translate": { "provider": "baidu", "baiduAppId": "***", "baiduKey": "***", "to": "zh" },
  "pin": { "enabled": true },
  "record": { "ffmpeg": "ffmpeg.exe" }
}
```

读取：启动时 `File.ReadAllText` + `Json.NET`（或手写轻量解析，避免引第三方）。**百度 key 走配置不硬编码**（你现用百度免费，appid/key 你提供）。

---

## 8. 编译 / 发布命令（更新 SKILL.md）

多文件 `csc` 明文编译（顺序传文件，产单 exe）：
```
csc /nologo /target:winexe /optimize+ /win32icon:icon.ico /win32manifest:app.manifest ^
    /out:shot-service.exe ^
    shot-service.cs shot-capture.cs shot-ocr.cs shot-translate.cs ^
    shot-scroll.cs shot-annotate.cs shot-pin.cs shot-aux.cs shot-record.cs ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll
```
- `shot-watcher.cs` 仍是独立小程序（单独编译，逻辑不变）。
- SKILL.md 编译段同步更新为多文件。
- 每里程碑发版（v0.0.15 / 16 / 17 / 18 / 19），沿用 v0.0.14 已稳的更新机制自动静默升。

---

## 9. 里程碑交付物

| 里程碑 | 内容 | 工期 | 产出版本 |
|---|---|---|---|
| **M1** | 框选覆盖层 + 保存 + 保存对话框 + 截图热键 | 5人日 | v0.0.15 |
| **M2** | OCR 复制 + 翻译（provider 抽象） | 4.5人日 | v0.0.16 |
| **M3** | 滚动长截图 | 4.5人日 | v0.0.17 |
| **M4** | 贴图 + 标注 + 辅助(取色/标尺/放大镜) | 10.5人日 | v0.0.18 |
| **M5** | 录屏/GIF（含 FFmpeg 集成） | 4人日 | v0.0.19 |

每里程碑独立发版、你真机验证完再进下一个。

---

## 10. 遗留文件清理建议（定稿后做，先问你）

路线 A 落地后，这些早期桥接残留可删（先确认不影响别的 agent 的截图调用）：
- `pixpin-shot.ps1` / `pixpin-shot.vbs` / `shot.js`（PixPin 桥接，路线 A 不再需要）
- `shot-watcher.cs`（v0.0.6 起弃用的守护）
- `unins000/001/002.exe` + `unins002.dat`（历史卸载程序残留）
- 计划任务 `dsh-pixpin-shot`（若存在，需你确认后清）

> 注意：若走路线 B 过渡，则保留 `pixpin-shot.*` / `shot.js`。

---

## 11. 风险 / 未决

1. **OCR 离线兜底**：M2 先只上 `qwen3-vl`（你 Ollama 常驻）。要真正离线兜底再说 Tesseract（带模型），不碰 .NET FX 调 WinRT 的坑。
2. **滚动截屏质量**：浏览器/Excel/文件管理器拼接好；自绘 UI 的 app 易错位，M3 要调。
3. **录屏体积**：唯一真增体积项（+FFmpeg ~30MB）。备选：用 Windows 桌面复制 API 自己编码避免带 ffmpeg（更费码），M5 时定。
4. **标注最费码**：M4 优先级最低，若工期紧可砍。
5. **百度 key 来源**：需你提供 appid/key（或确认从哪读），不硬编码进仓库。
6. **单文件→多文件**：`csc` 多文件编译需你本机手动跑（沙箱 `csc`/`msbuild` 被代理硬拒，铁律不变）。

---

## 12. 下一步

你审阅此稿。确认后我按 M1 开工：先写 `shot-capture.cs` + 热键 + 保存 + 对话框，编译发 v0.0.15 给你真机验。每里程碑走"写码→你本机编译打包发布→我远程/shlc 验证"闭环。
