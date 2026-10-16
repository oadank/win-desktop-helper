<!-- weixin 专项经验册(由 update_skill 自动建)。写经验请标日期, 拿不准的写"未验证"。 -->
# weixin 专项经验


## 微信自绘 UI 没有输入框控件；激活成功≠焦点在手，粘贴前必须重采前台

_记录日期: 2026-09-25 · 应用: weixin_

2026-09-25 实测（老大同时在操作电脑）：

1. 微信（process=Weixin，MMUIRenderSubWindowHW 那一块）是**自绘界面**：ui_tree 只有 7 个元素（Pane/TitleBar/最小化/最大化/关闭），**没有任何 Edit 控件**，`ui find type=Edit` 直接 count=0。想靠 UIA 定位输入框→填入，这条路在微信上是死的，别再试。

2. 因此"填入对方输入框"只能走：老大自己点一下输入框放好光标 → clipboard set → keyboard press ctrl+v（严禁回车）。AI 单方面 `win_manage(activate)` 后立刻粘贴 = 撞车。

3. **血坑**：`window manage activate` 返回 `activated:true / via:attach / foreground:true` 也**不代表粘贴那一刻焦点还在目标窗口**。实测激活后 1 秒内老大点了 MiMo，焦点被抢走，ctrl+v 落空。正确姿势：粘贴**前**再 `window active` 采一次，process/title 不是目标就停手，别赌。

4. `keyboard` 返回体里的 `front` 字段会如实告诉你按键打进的是哪个窗口 —— 它是**事后验尸**证据，不能当"打之前对过焦"的凭据。

5. 落空后的核对法：全屏 capture + 对目标区域局部 OCR，在 OCR 文本里找刚粘的那句原文；找不到=没落进去。别用 look_image 找字（task=text 会幻觉）。
