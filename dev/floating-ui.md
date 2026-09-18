<!-- 由 SKILL.md 瘦身拆分(2026-09-19): 内容原文逐字搬移未改写。主手册见 ../SKILL.md, 瘦身前全文见 ../SKILL-ARCHIVE-20260919.md -->

## 免激活浮窗拖动不跟手的根因与修法

免激活浮窗"拖动不跟手"根因与修法 (2026-09-07 划词卡片实测, 跟随率 20/20=100%):
不是电脑卡 —— 是跟随机制慢半拍。低级钩子在本机收不到 WM_MOUSEMOVE(moves=0), 拖动期间缓存的坐标是按下瞬间的陈旧值, 且只在 200ms 心跳应用一次 = 一步一卡。
修法三件套:
1. 拖动中直接 GetCursorPos 实时取坐标, 不依赖钩子 MOVE;
2. Timer 心跳拖动期加密到 15ms(平时 200ms), 起拖瞬间立刻切 Interval 不等下一轮;
3. 移动用单次 SetWindowPos(位置+HWND_TOPMOST 一步完成), 坐标没变整个跳过。
验收脚本 _pick_drag_perf.py: 手动 mouse_event DOWN + 20 步 SetCursorPos + UP, 后台 5ms 采样卡片 rect, 数位置变化档位(>=12 为跟手)。别用 mouse_drag 工具测跟手度——它本身一步到位看不出卡顿。

## 125% DPI 下悬浮窗字体必须用像素单位(AutoScaleMode.None 陷阱)

布局用 `AutoScaleMode.None` 按**物理像素**写死的自绘窗，字体绝不能用默认 Point 单位。
`new Font(name, 9.5f, style)` 的单位是 Point(逻辑单位)，系统 125% 缩放(GetDpiForSystem=120 / LogPixels=119)下会被放大 1.25 倍 → 字涨到 125% 而行高/容器坐标不动 → 文字互相挤压重叠，肉眼=「字太大 + 像乱码」(2026-09-07 老大截图实锤)。
修法: 统一 `new Font(name, px, style, GraphicsUnit.Pixel)`，参数值 = 期望物理像素字号(原磅值 × 96/72)。本仓库见 `PickStyle.F` + `FS_TITLE/FS_BODY/...` 常量。
教训: **别信视觉模型 OCR 判断"渲染正常"** —— 它会把重叠字脑补成正常句子照样读出正确文本；要么人眼看，要么量化: 截图后按行统计亮像素，文字行之间必须有暗行分隔、单行墨迹高度 ≈ 字号(14px 字体 → 11~13px)。验收脚本 `_font_check2.py`。

## 悬浮球交互: 按下不许 dismiss + 单击也弹球(只读 UIA 不发 Ctrl+C)

两条 2026-09-07 老大明确要的交互，改在 shot-pick.cs `PickOnMouse`:
1. **DOWN 不再一律 PickDismiss**。老逻辑"点到元素外就整组关掉"=用户随手一点浮窗就没了。改成按下不动手，收掉交给 UP 判定: 划词会重画(ShowPickDot 内部先 dismiss)、单击只尝试取词，两者都没命中就让元素按 4s/60s 超时自然收起。
2. **单击(未划选)也弹球**：`PickHandle(x,y,click=true)`，只走无副作用取词 —— UIA `TextPattern.RangeFromPoint` + `ExpandToEnclosingUnit(Word)` 取光标那个词，取不到就什么也不动。**绝不发全局 Ctrl+C**(单击不产生选区，这时复制 = 把用户刚点中的输入框内容/别处内容当"词"抓走)。
附带两个必修:
- 26px 的小点手抖就 miss，`PickNear(rect,x,y,8)` 容差内当作点中(否则"点它反而没了")。指针已落在元素上时绝不能再取词，否则小点会跳走。
- 闩锁(pickDownOnAskLive/pickDownOnCardLive)必须在 UP 入口一次性取走清零(`bool askJustClosed=...; ...=false`)。留到下一轮才清 → "点掉提问框后紧接着那次划词"被当成重复取词吞掉(要划第二次才出球)。
验收: `_pick_ask_test2.py` 五场景 A输入问题回车/B留空走默认/C Esc/D单击弹球/E点击不消失，全 OK。Win11 记事本 UIA 只有一个 Document+TextPattern，app_run 返回的 hwnd 常不是它，测试要按「大窗逐个试 ui_tree 找 Document」选目标。
