<!-- 由 SKILL.md 瘦身拆分(2026-09-19): 内容原文逐字搬移未改写。主手册见 ../SKILL.md, 瘦身前全文见 ../SKILL-ARCHIVE-20260919.md -->

## Win+Z 已知系统问题（老大 2026-09-11 实测定性：系统 bug，非工具问题）

| 布局 | 多窗 fill 实测 | 结论 |
|---|---|---|
| L4 半 / L5 左大右窄 | ✅ 稳 | 可用 |
| **L6 三均分** | ✅ 双 fill 稳定落位 | **多窗首选** |
| L7 左半+右上下 | 第2格 ZCode api ok 但不落位 | ⚠️ 不可靠 |
| **L8 四均分** | **选中 ZCode 后 assist 自动退出**（老大手测确认） | ❌ 系统级 bug，四窗组做不成 |
| L9 中间大 | ZCode fill api ok 不落位，其余 app 正常 | ⚠️ 不可靠 |

规律：失败全部发生在 **ZCode** 缩略图上（Enter 后窗不动/assist 退出）；Edge/设置/MiMo 正常。
怀疑与 ZCode 最小高 800 + Electron 拒绝缩放有关（全高格 L6 能成，短格 698/683 必挂，L9 全高也偶挂）。

对策：多窗吸附组用 **L6 三均分**；必须四分/含 ZCode 的其它布局时，ZCode 用 grid MoveWindow 摆（无组联动），其余窗走 assist。

## Win+Z 布局键位（老大实测定论 + 微软官方口径）

**官方**：飞出条布局集合动态（随屏幕/窗口变）；数字=飞出条第几格（行优先）；选完布局 Snap Assist 自动触发点选填位 → 自动成 Snap group；拖分隔条相邻窗联动缩放。

**老大实测关键定论（2026-09-11）**：
- **格 1–3 = 动态预设组**：把当前 app 自动摆位占格（一次动多个窗）——**自动化禁用**
- **格 4–9 = 纯区域分割**（不占位）——**自动化只用这些**：
  | # | 形状 | zone（行优先） |
  |---|---|---|
  | 4 | 左右两半 | 1左 2右 |
  | 5 | 左大右窄 | 1左大 2右窄 |
  | **6** | **三列均分** | **1左 2中 3右** |
  | 7 | **左一半 + 右半上下分** | 1=左 2=右上 3=右下 |
  | 8 | 2×2 四格 | 1左上 2右上 3左下 4右下 |
  | 9 | 三列中间大 | 1左窄 2中大 3右窄 |

落错格 = 集合变了 → 先 /shot 截飞出条核对，再改 layout=/zone=。



划词悬浮球(shot-pick*.cs)与常驻浮窗调试五大坑 (2026-09-07 全部实测):
1. WinForms CreateParams 里手加 WS_EX_LAYERED(0x80000) = 整窗隐形(窗口存在/IsWindowVisible=1 但屏幕全空)。圆点/圆角一律用 TransparencyKey 画。
2. 免激活窗(ShowWithoutActivation+WS_EX_NOACTIVATE|TOOLWINDOW)收不到 MouseEnter/Click —— hover计时/点击/拖动必须由低级鼠标钩子 DOWN/UP + 200ms 心跳 GetCursorPos 轮询驱动。WM_MOUSEMOVE 不进本机低级钩子(探针实锤 moves=0)。
3. 构造函数 TopMost=true 在免激活窗上会被丢弃 —— Show() 后必须 SetWindowPos(HWND_TOPMOST,...)，心跳每4轮补置顶。
4. 剪贴板取词法发全局 Ctrl+C 有投错窗口风险(实测把词粘进抖音评论区) —— 安全阀: 前台窗变化或指针离开按下点24px即拒走; 等待循环内前台变化立刻 abort。
5. 给本服务加 MCP 工具三处都要动: shot-service.cs 的 McpCall switch case + McpToolsJson 条目 + **mcp-bridge.js 注册表数组与 buildUrl 路由**(bridge 有独立白名单, 漏改直接 unknown tool)。
配置入口: 设置页「划词」节 / HTTP GET /pick-config?enabled=0|1 / MCP pick_config。开关立即生效+持久化到 shot-service.json pick 节。
