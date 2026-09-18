<!-- 由 SKILL.md 瘦身拆分(2026-09-19): 内容原文逐字搬移未改写。主手册见 ../SKILL.md, 瘦身前全文见 ../SKILL-ARCHIVE-20260919.md -->

## 🔴 dsh-web 模型切换是**两级菜单** ——「选项搜不到」先怀疑层级，别怪工具（2026-09-17 源码核对 + 纠正）

**⚠️ 本条取代了我 2026-09-17 17:5x 写进本手册的错误结论。** 原文说「Chromium/Electron 页面内下拉浮层 UIA 拿不到选项、是桌面自动化的天花板场景」——**那是错的**。真相是：菜单本来就是**两级**的，我在错误的层级搜模型名。**工具（点击 / UIA / 截图 / OCR）全程正常，是理解错了交互结构。**

**为什么会误判**（两层错误叠加，值得记住）：
1. 在**根层**搜 `Glm5.3` 搜不到 → 误读成「点击没生效 / UIA 看不见 Chromium 浮层」；
2. 一张截图里我只看到触发按钮 + 一个 hover tooltip（`不适用 QW3.8F`），**没有菜单行**，我把 tooltip 当成了已展开的菜单。

**真实结构（源码已证实：`C:\D\opt\deepseek-harness\deepseek-harness\packages\client\ui-model-selection\src\client\ModelSelect.tsx`）**
- 文件头注释第 3 行原话：`Two-level selection per figma 496:26454's MenuDropdown: the root menu is the Model / Effort row pair`。
- 根层（`pane==='root'`，第 300–315 行）**只有两行**：`模型`（`t('menu.model')`，值 = `modelLabel`）+ `推理`（`t('menu.effort')`，值 = `effortLabel`；**仅当该模型有 reasoning 时才渲染**）。两行均 `role="menuitem"`，右侧带 chevron。
- 点「模型」→ `setPane('model')` → **这时才**出现按 provider 分组的模型列表：`role="group"` 分组 + 标题，每项 `role="menuitemradio"`，文字 = `model.name`。**分组标题 = 该 provider 的 displayName**（如 gw → `Henry`）；所以 `Glm5.3`（gw 组）与 `Gwglm5.3`（litellm 组）名字很像，**要按分组标题区分**。
- 点「推理」→ `pane==='effort'` → effort 列表。
- 浮层是 portal 到 `document.body` 的 fixed 卡片，贴触发按钮**上方、右对齐**（所以按按钮下方的区域截图会截空）。

→ 根层**没有**模型名，在那儿搜 `Glm5.3` 必然搜不到，表现就是"点了没反应"，于是反复重试、把菜单开了又关。

**正确流程（4 步，含验证）**
1. `ui_click` 点触发按钮（name ≈ `选择模型，当前 QW3.8F`）→ 打开根菜单
2. `ui_find type=MenuItem` → 拿到「模型」行（值形如 `模型 QW3.8F`）→ 点进去
3. 列表展开各 provider 分组 → `ui_find name=Glm5.3` → 点选（认准 `Henry` 分组）
4. **验证**：`ui_read` 触发按钮 → name 应变成 `选择模型，当前 Glm5.3`

**✅ 成功实证（2026-09-17 18:3x，MiMo 客户端自己跑的）**：按上面 4 步，**3m46s 完成**，原话 =「已切换完成。模型选择器现在显示 当前模型是 Glm5.3（按钮无障碍名称：`选择模型，当前 Glm5.3`）。操作路径：点模型按钮 → 点「模型」钻进列表 → 选中 Henry 分组下的 Glm5.3。」**工具全程没问题，是层级理解问题。**

**教训（比结论本身更值钱）**
看到「选项搜不到 / 点了没反应」，先怀疑**层级与结构**，再怀疑工具。我这次把"我没在正确层级找"错判成"UIA 看不见 Chromium 浮层"，还写进了共享手册 —— **错误结论的传播成本极高**；拿不准就去读源码/UI 结构，别急着下"天花板"结论。

**两个仍然成立、但都不是本次失败原因的附带事实**
- `/ocr` 只返回 `{ok,chars,text}`，**不含 bbox/坐标**（本次实测复核：`{"ok":true,"chars":38,...}`）。靠它"找文字→算坐标"只能估，容易偏几百像素 → 要文字+坐标请用 `C:\D\opt\scripts\ui_probe.py`，或 `/shot?axes=1` 自己读坐标。
- 被其它窗口完全遮挡的 Electron 窗口，`screen_capture(window=...)` 拿到的是**旧帧**（PrintWindow 对 Electron 无效），且遮挡期间界面不重绘 → 要看某个 agent 执行到哪一步，**别截它的窗口**，直接读 `C:\D\opt\win-desktop-helper\shot-service.log`（实时、全量记录每次 MCP 调用）。
