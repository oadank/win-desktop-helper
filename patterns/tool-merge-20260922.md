# 桌面助手工具合并对照（2026-09-22）

> 变更：`mcp-bridge.js` 的 42 个工具合并为 **12 个**（带 `action` 参数），工具定义体量 19,330 → 8,484 字符，发给模型的说明书省了 56%。**能力零损失**：原 42 个工具名与参数映射一字未改，整体留在 `buildUrlLegacy()` 里，新层只做 `action → 旧工具名` 分派；后端 C# 服务（shot-service.exe）一行没动。
> 回滚：还原 `mcp-bridge.js.bak-20260922-lean`，重启桌面助手 MCP 即可。

## 完整映射（旧 → 新）

| 新工具 + action | 吸收的旧工具 |
|---|---|
| `window` active / list / info / monitors / state / manage | active_window / list_apps / window_info / monitors / window_state / win_manage |
| `mouse` move / click / down / up / drag / pos / scroll | mouse_move / mouse_click / mouse_down / mouse_up / mouse_drag / mouse_pos / mouse_scroll |
| `keyboard` type / press / hold | keyboard_type / keyboard_press / keyboard_hold |
| `clipboard` get / set / history | clipboard_get / clipboard_set / clipboard_history |
| `capture` shot / longshot / pin / ocr | screen_capture / longshot / pin_image / ocr_image |
| `ui` tree / find / click / read / readall / set / select | ui_tree / ui_find / ui_click / ui_read / ui_readall / ui_set / ui_select |
| `record` start / stop / status | record_start / record_stop / record_status |
| `app` run / runas / restore / tray | app_run / app_runas / app_restore / tray_click |
| `desk_skill` get / update | get_skill / update_skill |
| `wait_for`（action 可省） | wait_for |
| `pick_config`（action 可省） | pick_config |
| `taskbar_volume`（action 可省） | taskbar_volume |

## 调用示例（旧 → 新）

| 旧写法 | 新写法 |
|---|---|
| `list_apps()` | `window(action="list")` |
| `window_state(hwnd=123)` | `window(action="state", hwnd=123)` |
| `win_manage(action="snap", pos="left")` | `window(action="manage", verb="snap", pos="left")` |
| `ui_find(title="记事本", name="保存")` | `ui(action="find", title="记事本", name="保存")` |
| `ui_click(ref="x", expect="已保存", verify=1)` | `ui(action="click", ref="x", expect="已保存", verify=1)` |
| `mouse_click(x=100, y=200)` | `mouse(action="click", x=100, y=200)` |
| `keyboard_type(text="你好")` | `keyboard(action="type", text="你好")` |
| `screen_capture(axes=1)` | `capture(action="shot", region="all", axes=1)` |
| `ocr_image(path="C:\\x.png")` | `capture(action="ocr", path="C:\\x.png")` |
| `get_skill(app="WorkBuddy")` | `desk_skill(action="get", app="WorkBuddy")` |

`win_manage` 的动作名从 `action=` 挪到 `verb=`（避免和新层的 `action` 撞名）；旧写法仍兼容（分派层会兜底读 `verb` 再读 `action`）。

## 连带改动

1. **闸门改判据**：豁免从"按工具名"改成"按 (工具, action) 是否只读"。原来 `window` 一个工具里既含"看"（list/info/state）又含"动"（manage），只看名字会把纯观察也拦下逼读手册。只读白名单：`window(state/list/info/active/monitors)`、`mouse(pos)`、`clipboard(get/history)`、`record(status)`、`desk_skill(get)`；`capture`/`ui`/`keyboard`/`app` 及一切写动作仍需先读手册。
2. **超时表**：`TOOL_TIMEOUT` 的 key 从 42 个旧名收敛成 12 个新名（取该工具各动作的最大预算，`capture` 沿用 longshot 的 200s）。
3. **未知 action 不静默**：`mouse(action="launch_rocket")` 这类返回 `unknown tool/action: mouse action=launch_rocket`，不会假装成功（实测已验证）。
4. **顺手修掉的老 bug**：后端 `/taskbar-volume` 返回的 `"pt":1418,1036` 是裸逗号（少引号也不是数组），整条响应非法 JSON，查音量状态一直报 `bad json from helper`。已在桥侧 `httpGet` 里定点修复为 `"pt":"1418,1036"`（未动 C# 服务）。实测 `taskbar_volume(action="get")` 现已 `ok:true`。

## 验收记录（2026-09-22）

- `tools/list` 返回 12 个，schema 体量 8,484 字符。
- 逐工具实调：`window(active/list/state/manage)`、`mouse(pos)`、`keyboard(press)`、`clipboard(get)`、`capture(shot)`、`ui(tree)`、`record(status)`、`app(restore)`、`desk_skill(get)`、`wait_for`、`pick_config(get)`、`taskbar_volume(get)` —— 分派全部正确；传不存在的窗口/进程时的报错来自服务端业务校验（属预期），无 `unknown tool` 误报。
- 反向用例：不存在的 action 被拦下并报错。
- `node --check` 语法通过；MCP 已重挂（新 bridge 子进程生效）。
