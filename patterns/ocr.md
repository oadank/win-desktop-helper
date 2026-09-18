<!-- 由 SKILL.md 瘦身拆分(2026-09-19): 内容原文逐字搬移未改写。主手册见 ../SKILL.md, 瘦身前全文见 ../SKILL-ARCHIVE-20260919.md -->

### OCR 引擎选择（重要）
- **本地 ollama `qwen3-vl:4b-instruct`：实测 5.8s 中位、5/5 成功** ← 用这个
- **agnes 云端(api.agnes-ai.cn)：实测 0/5，每次卡满 60s 超时** ← 当前不可用，别用
- 两者对同一张徽标图的识别质量**完全一致**（12~16 行含段号全对）。

## 🔴纠正+增强: agnes 远程 OCR 已可用 —— 3 key 优先级 + 送图压缩（2026-09-17 实装实测）

**原结论作废**：本文件上方写「agnes 云端 0/5、每次卡满 60s 超时 ← 当前不可用，别用」是**误判**。
真相：当时用的是**国际站 apihub 的 key + PNG 无损大图**两个坑叠加；换成国内站 .cn + JPEG 压缩后完全可用。

### 实测数据（同一张图，同日）
| 组合 | 耗时 | 结果 |
|---|---|---|
| 小图 240x90（1 行字）@ .cn | **0.8s** | ✅ |
| 小图 240x90 @ apihub key2 | 6.1s | ✅ |
| 小图 240x90 @ apihub key1 | 20.9s | ✅ |
| 中图 1200x700（40 行字）@ .cn | 26~33s | ✅ |
| 全屏 2560x1440（70~100 行）@ .cn | 26~52s | ✅ |
| 本地 ollama qwen3-vl:4b 跑中图 | 14~22s | ✅ 但撞 num_predict=300 被**截断**（只出 15 行） |
| key1 打 .cn（跨站） | 0.1s | ❌ **HTTP 401 无效令牌** |

**三条结论**：
1. 🔴 **key 与站点绑死**：key1/key2 只认 apihub（国际站），key3 只认 api.agnes-ai.cn（国内站）。**跨站必 401，不能乱换**。
2. **.cn 比 apihub 快得多**（0.8s vs 6~21s）→ 配置里 **.cn 的 key 排第 1 位**。
3. **慢的根源是「图里文字多」**（VLM 自回归逐字生成），不是引擎/网络。小图秒回，全屏几十行就要几十秒 —— **要快就只截需要的区域**，别全屏 OCR。

### 配置：`ocr.apiKeys`（新键，2026-09-17 加）
```json
"ocr": {
  "provider": "openai", "model": "agnes-3.0-flash",
  "apiKey": "sk-...(兼容旧单key)",
  "apiKeys": "key3@https://api.agnes-ai.cn/v1/chat/completions|key2@https://apihub.agnes-ai.com/v1/chat/completions|key1@https://apihub.agnes-ai.com/v1/chat/completions"
}
```
- 格式：`key@endpoint` 用 `|` 分隔；endpoint 省略则用 `ocr.endpoint`
- **顺序 = 优先级**：第 1 个先用，失败才换下一个（不是轮流 —— 轮流会把请求打到慢 26 倍的 apihub）
- 3 个 key 的真值在 N5105 `100.110.110.12:/opt/text-api-images/.env`（`AGNES_KEY_1/2/3`）

### 送图压缩（原实现的最大坑）
原 `BitmapToBase64` 用 **PNG 无损** 送图：全屏 PNG 567KB → base64 757KB，**极易破 1MB 被拒**。
新增 `BitmapToBase64Jpeg(bmp, maxSide, maxBytes)`：等比缩到最长边 1400 + JPEG q88（体积超标自动降到 q55），目标 <500KB。
`OpenAiVisionOcrProvider` 已改用它；本地 qwen3vl provider 仍走 PNG（本地不限体积）。

### 🔴 超时坑：`HttpWebRequest.Timeout` 对**异步**请求无效
MS 文档明确：Timeout 不影响 `BeginGetResponse`/`BeginXxx` 系异步调用，而 `WebClient.UploadStringTaskAsync` 内部就是异步。
→ 直接设 `WebRequest.Timeout` **完全没用**（实测照样跑满 52s/60s）。
**正确写法**：自己包一层
```csharp
Task<string> dl = wc.UploadStringTaskAsync(url, json);
Task done = await Task.WhenAny(dl, Task.Delay(PerKeyTimeoutMs));
if (done != (Task)dl) { try { wc.CancelAsync(); } catch {} throw new WebException("timeout", WebExceptionStatus.Timeout); }
string resp = await dl;
```
本实装 `PerKeyTimeoutMs = 45000`（留足量，.cn 大图最坏见过 33s），超时后按 failover 换下一个 key。

### 改动落点（供后续维护）
- `shot-ocr.cs`：`OcrKeyEndpoint` / `TimeoutWebClient` / `OpenAiVisionOcrProvider` / `OcrProvider()` 工厂 / `BitmapToBase64Jpeg`
- `shot-config.cs`：`LoadCfgDict()`+`SaveCfgDict()` 已加 `ocr.apiKeys`（否则在设置 UI 点保存会把该字段冲掉）
- 备份：`shot-ocr.cs.bak-20260917-keyrot` / `shot-config.cs.bak-20260917-keyrot` / `shot-service.json.bak-20260917-keyrot`
- 编译后 exe 376832 → 380416 字节；启动日志会打印 `ocr: openai -> 3 key(s) rotation`

---

## 逐字取文字优先 ocr_image，look_image 的 task=text 会幻觉（2026-09-18 探针实测）

用探针图 `C:\D\opt\agents-to-feishu\team-artifacts\probe-ocr.png`（图内仅一行字 `CTI-PROBE-2026`）实测四个看图入口：

| 入口 | 结果 | 判定 |
|---|---|---|
| `mcp__cti-builtin__look_image(task="text")` | `Oproductivity` | ❌ **幻觉，与图完全无关，且返回 ok 不报错** |
| `mcp__visionqa__look(task="text")` | 总结里给出 `CTI-PROBE-2026` | 对，但属"总结"口径，非逐字提取 |
| `ocr_image` 直接传原路径 | `path outside screenshots dir (安全限制)` | 预期拦截（见「Buddy 签到」节坑①），不是故障 |
| copy 到截图目录后 `ocr_image` | `{"ok":true,"chars":14,"text":"CTI-PROBE-2026"}` | ✅ 权威逐字结果 |

**结论（写死）**：任务性质是「把图里的字认出来」→ **只用 `ocr_image`**（先 copy 到 `C:\Users\oadan\Pictures\Screenshots\`，用完删副本）；`look_image` 的 text 模式**不可信**，它会编一个词回来且不报错。`look_image` 只用于 describe / reverse（看图描述、反推提示词）。

**自检法**：拿内容已知的探针图多引擎交叉对答案，不一致时信 ocr_image。


## 修正+增强: look_image(task=text) 先裁紧+6x 放大就变可信（2026-09-19 DSH 实测）

上一节「逐字取文字优先 ocr_image，look_image 的 task=text 会幻觉」的结论**仍然成立**（原图直喂确实乱编），但根因不是"text 模式不可信"，而是**本地 qwen3-vl:4b 吃不下 37px 高的小字**。

实测（探针图 `probe-ocr.png` 512×256，单行字 bbox x64..447 / y109..145，字高仅 ~37px）：
- **原图直喂** `look_image(task=text)` ×4 → 4 个互不相干的答案：`OOPSY DOOZY PRINTS` / `CAPTCHA - Just like people` / `OPEN PROPERTIES` / `CATHARINE'S PLACE`（describe 模式还编个 `ORBITAL RAYS EP`）→ 与既有结论一致：幻觉且不报错。
- **像素预处理后再喂** → **连续 3 次全部 `CTI-PROBE-2026`（与 ocr_image 真值一致）**，两个入口（`mcp__cti-builtin__look_image`、`mcp__vision__look_image`）答案相同。
  做法（PIL，10 行以内）：灰度 → 按 `a<128` 求暗像素 bbox → `crop` 留出 ~8px 边距 → `resize(w*6, h*6, LANCZOS)` → 存图再 OCR。整图 4x LANCZOS 也够（裁紧更好，去掉大片空白干扰）。

**更新后的姿势（取代"只用 ocr_image"这条硬规矩）**：
1. 首选仍是 `ocr_image`（先 copy 到 `C:\Users\oadan\Pictures\Screenshots\`，用完删副本）—— 一次到位、给 `chars` 可自校验。
2. `ocr_image` 不可用/没截图目录权限时：`look_image(task=text)` **必须配"裁紧+≥4x LANCZOS 放大"**，且**至少跑 2 次投票**，答案不一致就判定失败，禁止把单次结果当事实写进结论或回报老大。
3. 判据：VLM 幻觉的共性是**给出一个像样但完全不相干的英文词组**，且常带「中部/顶部」前缀；对不上字数（本例应为 14 字符）就是幻觉。

**附带坑（DSH 侧）**：`mcp__visionqa__look` 在 DSH 里**三次全 `Request timed out`**（服务 `vision-qa`:8091 与 `visionqa`:8092 都 Running）。`visionqa_mcp.py` 对后端用 `urlopen(timeout=900)`，而质量管线是「强提示词+审查+自审」多轮 VLM → 单张耗时超过**调用方** MCP 超时，客户端先取消，服务端晚回写就炸 `AssertionError: Request already responded to`（`C:\D\opt\vision-qa\visionqa.err.log` 里那串 anyio TaskGroup ExceptionGroup 就是它，**不代表服务挂了**）。处置：探针走 `look_image`；真要用 visionqa 就把调用方 `toolCallTimeoutMs` 提到 ≥120s，或给 `task=text` 关掉审查/自审两遍。
