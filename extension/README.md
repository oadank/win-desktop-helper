# Win Desktop Helper · Edge 划词扩展

方案 A（2026-09-10 定稿）：扩展只传选中文字，球由本地 `shot-service` 贴在 **GetCursorPos**。

## 安装

1. 确保 `shot-service.exe` 在跑：`http://127.0.0.1:18800/health` → `ok:true`
2. Edge → `edge://extensions` → 开发人员模式 → **加载解压缩的扩展** → 选本目录 `extension/`
3. 打开任意网页 → **双击选词** 或 **拖选松手** → 光标旁出现悬浮小点

## 原理

```
content.js (mouseup/dblclick → getSelection)
    → background.js (fetch)
    → POST http://127.0.0.1:18800/pick-inject  {"text":"..."}
    → PickInject → GetCursorPos → ShowPickDot
```

- 不发 Ctrl+C、不碰剪贴板
- 不做 CSS 像素 / 物理像素换算（球不锚在选区框上，贴松手处光标）
- 原生钩子在浏览器里已让位（单击/划选不再 UIA），避免双球

## 文件

| 文件 | 作用 |
|---|---|
| `manifest.json` | MV3 + `host_permissions` 本地 18800 |
| `config.js` | 端点（可改） |
| `content.js` | 页面选区监听 |
| `background.js` | 转发 POST |

## 已知边界

- helper 没启动 → 扩展静默失败（不弹错误）
- 输入框内复制粘贴类操作不触发（无选区）
- 合成 Ctrl+C 在 Chromium 无效是系统边界，本扩展不走复制链路
