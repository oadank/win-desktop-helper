# Win Desktop Helper — 操作手册（Agent 通用版）

> 本手册给任何获得 **Win Desktop Helper** 能力的 AI（HTTP 或 MCP 接入均可）提供操作纪律与避坑知识。
> DSH 系 agent：此为 skill（`~/.dsh/skills/win-desktop-helper/`）；MCP 系 agent：把本文粘贴进系统提示词即可。

## 能力边界

- **看**：截图（全屏/显示器/区域/窗口）、活动窗口、按标题查窗口、显示器元数据
- **动**：鼠标（移动/点击/滚轮）、键盘（中文输入/组合键）、运行程序
- **不做**：文件删除/消息发送默认由 agent 自己的工具完成（敏感操作需在对话里向用户确认后方可执行）

## 入口与健康检查

- HTTP: `127.0.0.1:18800`，先 `GET /health` 确认 `session:1` 且存活
- MCP 10 工具名：screen_capture / window_info / active_window / monitors / mouse_move / mouse_click / mouse_scroll / keyboard_type / keyboard_press / app_run

## 铁律（违反必踩坑）

1. **点任何东西之前，先定位**：点窗口用 `window_info(标题)` 拿 rect 再点区域中心；点之前 `active_window` 确认它在前台（后台/被遮挡窗口点了等于点在遮蔽层上）
2. **输入之前，先确认目标**：`keyboard_type` 发给**当前前台窗口**。输入前必须 `active_window` 确认前台是目标（历史教训：误发 34 字符到浏览器）
3. **操作后立即验证**：保存文件 → 用文件系统检查（`Test-Path`/`Get-Content`），不要只信 UI；UI 会骗人（保存对话框的默认位置常常不是你以为的地方）
4. **等待 + 截图确认**：启动程序/弹窗后 sleep 1-3s 再继续；关键节点截图看效果再走下一步
5. **敏感操作先问**：删除文件、发送消息等在对话里向用户确认；付款不做；改系统设置/运行程序的敏感场景同样先确认
6. **遇到"点不动/找不到"**：先 `active_window` + 全屏截图看真实状态，别盲试

## 常见坑速查

| 现象 | 原因 | 解法 |
|---|---|---|
| 截图全黑 / 只有十几 KB | 在 Session 0 直接截图 | 确认服务 `session:1`；任何截图操作都经本服务 |
| `window_info` 找不到 | 目标窗口在别的桌面/被遮挡/Win11 商店应用（如新记事本标题是英文 "Notepad"） | 用 `active_window` 兜底；或先 `app_run`/点击把它带起来 |
| 程序开了但没在前台 | 前台锁：后台启动的窗口不自动置前 | `alt+tab` 切到最近窗口，或点击它可见区域 |
| 点窗口没反应 | 点到了遮蔽它的其他窗口 | 先 `alt+tab` 置前再点；用窗口 rect 中心附近点击 |
| 点击/输入打到别处 | 光标下/前台不是目标 | 立即 `active_window` 检查，停止继续操作并汇报 |
| 保存文件找不到 | 应用默认位置不是预期目录 | 保存后文件系统验证；用 `app_run` 打开目标目录再操作 |

## 标准流程模板

```
打开应用: app_run(path) → sleep 2-4s → active_window 确认 → 截图确认界面
点击:     window_info(标题) → 计算中心 → mouse_click(x,y) → 截图确认效果
输入:     active_window 确认目标 → mouse_click 聚焦 → keyboard_type → 截图/验证
滚动:     mouse_move 到滚动区 → mouse_scroll(delta)（正值向上、负值向下）→ 截图确认
保存:     完成操作 → 文件系统验证文件存在且内容正确 → 汇报路径
```

## 运行程序提示

- `app_run` 用 ShellExecute：可传 exe 路径、快捷方式、URL（会打开默认浏览器）
- 需要管理员权限的程序会弹 UAC（secure desktop）——需要用户手动点"是"，无法自动确认