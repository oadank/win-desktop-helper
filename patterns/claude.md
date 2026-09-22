<!-- claude 专项经验册(由 update_skill 自动建)。写经验请标日期, 拿不准的写"未验证"。 -->
# claude 专项经验


## 闸门实测补充：agents-to-feishu 侧的 claude 是「每轮」被拦（MCP 进程每轮重建）

_记录日期: 2026-09-19 · 应用: claude_

**现象（2026-09-19 实测，claude / agents-to-feishu 桥接侧）**：同一会话内，上一轮已 `get_skill`，下一轮再调 `ocr_image` 仍被闸门拦下（返回「本服务强制要求：动手之前必须先调用 get_skill…」），**只读工具同样被拦**。

**为什么与主手册「每会话一次」不冲突**：`mcp-bridge.js` 的 `guideRead` 是模块级变量 = 每个 bridge 进程一次。DSH 的 MCP stdio 进程跨轮常驻 → 表现为「每会话一次」；而 **agents-to-feishu 桥接侧每轮会重建 MCP 进程** → 表现为「每轮一次」。

**实操姿势（claude 侧）**：每收到一条新消息，首次要调 win-desktop-helper 工具前，先 `get_skill`（现在核心版仅 ~5K 字，成本可接受）。省这一步 = 白等一轮。

**验证**：`get_skill` → `ocr_image` 一次通过，返回 `{"ok":true,"chars":14,"text":"CTI-PROBE-2026"}`。
