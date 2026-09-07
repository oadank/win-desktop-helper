# Win Desktop Helper — GUI 操作手册（核心版）

> ⚠️ 任何拿到本能力的 AI：① 首次操作前必须 `get_skill` 读本文件（服务端强制，未读拒绝工具）；
> ② **每次踩坑必须用 `update_skill` 写回本文件**，禁止只记在自己记忆里；
> ③ 完整历史/已修 bug 考古不在这里，`get_skill(detail="full")` 或 `get_skill(topic="分屏")` 按需取。

## 黄金路径（任何"点击某个东西"的任务，照这个走）

```
1. 找窗口  list_apps() 按 process 名拿 hwnd
2. 置前    win_manage(action=activate, hwnd=...)   → foreground=true 才算真置前
3. 找控件  ui_find(hwnd=..., name="按钮的可见文字") → 拿到 ref
           （找不到就 ui_tree(hwnd=...) 看它到底叫什么）
4. 点      ui_click(ref=...)                       ← 首选，永不漂移
           点完必须 verify=1，changed=false 说明没生效，别连点
5. 验证    ui_tree / ui_read / window_info 确认界面真变了，没变就重新采样
```

## 铁律（违反必踩坑）

1. **定位优先级 ref > name > i。截图只用来判断界面状态，绝不用它算落点**
   - `ref` = `ui_find`/`ui_tree` 返回的稳定引用（UIA RuntimeId），窗口存活期内不变，**不会漂移**，不需要 hwnd/title。失效会明确报错，不会静默点错
   - `i` 是树下标，**跨调用必漂移**（实测同一输入框 637→644→659→664，点偏了还返回 ok）。非用不可时必须同时传 `name` 校验
   - 截图估坐标实测偏 150px、视觉小模型估偏 96px，**两者都点不中。别让模型做算术**
2. **状态每次重新采样**：句柄会变（微信实测 920778→1968942）、下标会漂、窗口会被盖住关闭。禁止复用上一步结果
3. **每次操作后必须验证**。**切页/切会话/进列表项这类操作必须带 `expect="点完应该出现的文字"`** —— `verify` 只回答"界面变了"，`expect` 才证明"变成了对的那个"（实测：点会话项报 ok 且 changed=true，界面根本没进那个会话）。元素 `offscreen:true` 表示它滚出视口/被折叠，**点了不会生效**，先滚动或展开让它可见
4. **中文一律剪贴板粘贴**：`clipboard_set(text)` → 点输入框聚焦 → `keyboard_press(keys="ctrl+v")`。组合键参数名是 `keys`（不是 key/modifiers）
5. **工具不报假成功**：`ui_click` 的 via=invoke 只代表调到了不代表生效 → 配 `verify=1`；`ui_set` 写入后自动读回校验，不一致直接报错并指路剪贴板方案；坐标点击前自动做落点归属校验，位置命中的不是目标进程会拦下并报错。**看到报错就按报错里的提示改，别重试同一个动作**
6. **"看得见但点不动" / "压根没窗口" → 直接 `app_restore(process=应用名, snap=right)`**：一条命令自动 close 关窗 → 托盘双击重开 → 等窗口稳定 → 贴回指定位置（返回 `stable` / `steps`）。**`win_manage activate` 唤回的窗口经常是冻结的，别拿它当恢复手段**；失败看返回里的 steps（托盘名不对用 `tray_list`，应用真退了用 `app_run`）
7. **敏感操作先问**（删除/发送消息/改系统设置），付款不做

## 常用工具速查

| 用途 | 工具 |
|---|---|
| 语义定位/点击/读写 | `ui_tree` `ui_find` `ui_click(ref\|name, expect=, verify=1)` `ui_read` `ui_set` `ui_select` |
| 证明"点对了" | `ui_click(..., expect="预期出现的文字")` → 返回 `expect.found` |
| 窗口信息/管理 | `window_info`(支持 process 精确匹配) `active_window` `list_apps` `win_manage` `monitors` |
| 半屏/四分之一布局 | `win_manage(action=snap, hwnd=, pos=left\|right\|top\|bottom\|topleft\|topright\|bottomleft\|bottomright\|max\|min\|restore, monitor=2\|next\|prev)` |
| 找失踪窗口 | `win_manage(action=listall, pid=)` 含隐藏/最小化/托盘化的窗口 |
| **深度恢复(冻结/没窗口)** | `app_restore(process=, snap=left\|right\|max, wait=8000)` 一条命令：关窗→托盘双击重开→等稳定→贴回，首选它 |
| 托盘唤回 | `tray_click(name=, double=1)`（单击=toggle 最小化，双击=恢复/打开） |
| 鼠标/键盘 | `mouse_click` `mouse_move` `mouse_drag` `keyboard_type` `keyboard_press` |
| 看 | `screen_capture` `ocr_image` |
| 启动程序 | `app_run(path=, wait=1, process=)` 返回稳定后的窗口 rect |
| 经验写回 | `get_skill` `update_skill` |

## 踩坑速查（最痛的几条，完整版 detail="full"）

| 现象 | 解法 |
|---|---|
| 按截图/视觉模型给的坐标点击全落空 | 改用 `ui_find` 拿 ref，工具算落点 |
| `ui_click` 返回 ok 但界面没变 | 加 `verify=1`；invoke 不生效的应用用 `mode=coord` 或 `mouse_click` |
| 点了报成功但进的不是目标页/会话 | `verify` 只看"变没变"，必须再加 `expect="目标标题"` 证明"变成了对的" |
| 元素在列表里但点了没反应 | 看返回/树里的 `offscreen`：为 true = 滚出视口，先滚动展开再点 |
| `window_info` 查到不相干的窗口 | title 模糊匹配会误伤浏览器标签页，传 `process` 按进程过滤 |
| 粘贴没生效 | 参数名是 `keys`：`keyboard_press(keys="ctrl+v")` |
| 窗口能看见但点不动 / 完全没有窗口 | `app_restore(process=应用名)` 一条命令恢复，别用 activate |
| `app_run` 返回的 hwnd 找不到窗口 | 多进程应用会换窗，用 `list_apps` 按 process 重新取 |
| 布局想贴半屏 | 别用 move 手算坐标，用 snap；返回 target≠rect 说明应用有最小尺寸约束，按 rect 补差 |
| 要三等分/任意比例(系统 Snap Layouts 全支持) | snap 不带 pos，改带网格参数：`cols` 切几列 + `col` 第几列 + `colspan` 跨几列；`rows/row/rowspan` 同理管纵向。横三等分中间 `cols=3 col=2`；竖屏上中下 `rows=3 row=1`；2/3 左 `cols=3 col=1 colspan=2`；四等分左上 `cols=2 col=1 rows=2 row=1`；50/25/25 的右上 `cols=4 col=3 rows=2 row=1`。参数非法会直接报错并说清原因 |
