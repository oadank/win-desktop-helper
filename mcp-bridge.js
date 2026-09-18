#!/usr/bin/env node
// mcp-bridge.js — Win Desktop Helper 的 MCP (Model Context Protocol) stdio 服务
// 让任意 MCP 客户端（Claude Desktop / Cursor / DSH 等）获得 看+动+读 的完整电脑操作能力
// 传输: stdio JSON-RPC 2.0 (每行一条消息); 内部转发到 HTTP 127.0.0.1:18800
// 用法: node mcp-bridge.js   （在 MCP 客户端配置为 command: node, args: [本文件路径]）
// 零依赖：手写协议，无需 npm install
// 工具集与 HTTP 后端全量对齐 (27 个): 截图/窗口管理/鼠标7/键盘3/剪贴板2/UIA语义5/录屏3/应用2/任务栏音量
'use strict';

const HOST = '127.0.0.1';
const PORT = 18800;
const VERSION = '2.0.0';

let guideRead = false; // 强制闸门: 首次操作前必须先读 get_skill

// 工具定义（名称/说明/参数 —— 与 HTTP API 一一对应）
const TOOLS = [
  // ---- 观察 ----
  {
    name: 'screen_capture',
    description: '截取用户桌面指定区域，返回保存的 PNG 文件路径(可用 Read 工具读图)。region=all 全屏(默认)；screen=N 指定显示器；x,y,w,h 任意矩形(物理像素)；window=窗口标题关键词(截该窗口)；axes=1 叠加屏幕绝对坐标网格(每50px细线/每100px标数字)，AI 定位专用 —— 看图直接读出目标元素的屏幕坐标喂给 mouse_click，避免盲估坐标。不传则无坐标轴(人工截图/日常用)',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        region: { type: 'string', description: 'all | 忽略表示全屏' },
        screen: { type: 'number', description: '显示器下标' },
        x: { type: 'number' }, y: { type: 'number' }, w: { type: 'number' }, h: { type: 'number' },
        window: { type: 'string', description: '窗口标题关键词' },
        axes: { type: 'number', description: '1=叠加屏幕绝对坐标网格(50px 细线/100px 标数字)，AI 定位专用；不传或 0 = 无坐标轴' }
      }
    }
  },
  {
    name: 'window_info',
    description: '按窗口标题/进程名查询窗口 {hwnd,title,process,rect}，操作前定位用。匹配优先级: 标题全等>标题前缀>标题包含>仅进程名。⚠ 模糊匹配会误伤(实测 title=微信 命中了浏览器标签页标题里含"微信"的窗口), 建议同时给 process 或先用 list_apps 拿 hwnd。查不到返回 ok:false',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string', description: '窗口标题关键词' },
        process: { type: 'string', description: '进程名过滤(如 Weixin/msedge, 忽略大小写可带 .exe), 强烈建议给, 避免标题模糊匹配误伤' }
      }
    }
  },
  {
    name: 'active_window',
    description: '获取当前前台活动窗口 {title,process,rect}。打字/按键前必须先确认目标在前台',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
  {
    name: 'list_apps',
    description: '列出当前所有可见应用窗口 {hwnd,pid,process,title,front,rect}（Z 序，front=true 是前台）——找操作目标第一步',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
  {
    name: 'monitors',
    description: '列出显示器元数据（分辨率/主屏/设备名）',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
  // ---- 窗口管理 ----
  {
    name: 'win_manage',
    description: '窗口管理: action=activate(置前)/snap(半屏分屏贴靠, 配 pos+monitor)/maximize/minimize/restore/close/move(需x,y,w,h)/wait(等窗口出现,timeout毫秒)/list(列窗口)/listall(含隐藏窗口)。★布局窗口一律用 snap: pos=left|right|top|bottom|topleft|topright|bottomleft|bottomright|max|min|restore, monitor=1..n|next|prev(不给=当前屏)。这是 Win+方向键那套分屏能力的工具版, 且能指定第几块屏。★任意比例用网格参数(不给 pos): cols 横向切几列 + col 第几列 + colspan 跨几列 / rows 纵向切几行 + row 第几行 + rowspan 跨几行。例: 横三等分中间 cols=3 col=2; 竖屏上中下 rows=3 row=1; 2/3 左 cols=3 col=1 colspan=2; 四等分左上 cols=2 col=1 rows=2 row=1',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        action: { type: 'string', description: 'activate|maximize|minimize|restore|close|move|wait|list|listall (listall=枚举全部顶层窗口含隐藏/最小化/托盘化的, 找失踪窗口用; maximize/minimize 会自动映射为服务端的 max/min)' },
        verb: { type: 'string', description: '同 action 的别名, 两种写法都支持' },
        hwnd: { type: 'number', description: '窗口句柄(推荐! 比 title 可靠: 标题会变/会误匹配)。list_apps 采样得到, 优先于 title' },
        title: { type: 'string', description: '窗口标题关键词(没给 hwnd 时才用, 模糊匹配可能误伤)' },
        x: { type: 'number' }, y: { type: 'number' },
        w: { type: 'number', description: 'move 时的宽度(服务端必填)' },
        h: { type: 'number', description: 'move 时的高度(服务端必填)' },
        pos: { type: 'string', description: 'action=snap: left|right|top|bottom|topleft|…=MoveWindow 画矩形; sysleft|sysright|…=真系统 Win+方向+Esc; zthirdleft|zthirdmid|zthirdright|zkbd=真系统 Win+Z Snap Layouts+Esc(三均分首选); max|min|restore' },
        layout: { type: 'number', description: 'Win+Z 布局数字键。本机三均分=6; 9=中间大两边小。缺省按 pos' },
        zone: { type: 'number', description: 'Win+Z 区域 1-9(三均分左中右=1/2/3)' },
        esc: { type: 'number', description: '1=贴完后 Esc 提交(无 fill 时默认 1); 有 fill 时自动 0' },
        fill: { type: 'string', description: 'Snap Assist 点选填位(吸附组正道!): 逗号分隔窗口标题/进程名, 按序占剩余区。例: fill=ZCode,DSH 本地构建' },
        monitor: { type: 'string', description: 'action=snap 时可选: 第几块屏(1..n) 或 next 移到下一屏 / prev 上一屏; 不给 = 窗口当前所在屏。多屏布局用这个' },
        cols: { type: 'number', description: 'action=snap 网格布局: 横向切几列(1-12)。横三等分 cols=3; 竖屏三等分用 rows' },
        col: { type: 'number', description: 'action=snap 网格布局: 占第几列(1-based, 必须 <= cols)' },
        colspan: { type: 'number', description: 'action=snap 网格布局: 横向跨几列(默认1)。2/3 左 = cols=3 col=1 colspan=2' },
        rows: { type: 'number', description: 'action=snap 网格布局: 纵向切几行(1-12)。竖屏上中下三等分 rows=3' },
        row: { type: 'number', description: 'action=snap 网格布局: 占第几行(1-based, 必须 <= rows)' },
        rowspan: { type: 'number', description: 'action=snap 网格布局: 纵向跨几行(默认1)' },
        timeout: { type: 'number', description: 'wait 的超时毫秒(默认10000)' },
        pid: { type: 'number', description: 'list 时按进程过滤' }
      },
      required: ['action']
    }
  },
  // ---- 鼠标 (物理像素坐标) ----
  {
    name: 'mouse_move',
    description: '移动鼠标到物理像素坐标',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { x: { type: 'number' }, y: { type: 'number' } },
      required: ['x', 'y']
    }
  },
  {
    name: 'mouse_click',
    description: '点击（带坐标先移动再点）。button=left|right|middle，double=1 双击，triple=1 三击（选整行/段），mods=shift/ctrl/alt/win 按住修饰键点击。返回 at=落点顶层窗口(process/title/front)；front=1 时落点非前台直接拒点',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        x: { type: 'number' }, y: { type: 'number' },
        button: { type: 'string', description: 'left|right|middle' },
        double: { type: 'number', description: '0|1' },
        triple: { type: 'number', description: '0|1 三连击(选整行/段); 坐标务必取行内 rect.x+20 以上、行垂直中线 —— 打左边缘 2px 会被 RichEdit 边距命中区变成全选(实测坑)' },
        mods: { type: 'string', description: 'shift|ctrl|alt|win，可组合如 ctrl+shift' },
        front: { type: 'number', description: '1=严格模式，落点窗口不是前台直接拒点' }
      }
    }
  },
  {
    name: 'mouse_down',
    description: '按下鼠标键(不松开)。button=left|right|middle。与 mouse_up 配对使用(自定义拖拽)',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { button: { type: 'string', description: 'left|right|middle (默认left)' } }
    }
  },
  {
    name: 'mouse_up',
    description: '松开鼠标键。与 mouse_down 配对',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { button: { type: 'string', description: 'left|right|middle (默认left)' } }
    }
  },
  {
    name: 'mouse_drag',
    description: '拖拽：从(x1,y1)按住左键拖到(x2,y2)再松开。button=left|right。适合选区/滑块/移动文件',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        x1: { type: 'number' }, y1: { type: 'number' }, x2: { type: 'number' }, y2: { type: 'number' },
        button: { type: 'string', description: 'left|right (默认left)' }
      },
      required: ['x1', 'y1', 'x2', 'y2']
    }
  },
  {
    name: 'mouse_pos',
    description: '查询当前鼠标坐标 {x,y}',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
  {
    name: 'mouse_scroll',
    description: '滚轮：正数=向上滚，负数=向下滚（典型 ±120/格）。可选 x,y：先移动到目标坐标再滚（作用于光标处）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        delta: { type: 'number' },
        x: { type: 'number' }, y: { type: 'number' }
      },
      required: ['delta']
    }
  },
  // ---- 键盘 ----
  {
    name: 'keyboard_type',
    description: '向当前聚焦输入框打字。中文/emoji 直接支持（Unicode 事件，不依赖输入法）。≤2000 字符。打字前先 active_window 确认前台。换行默认发 Shift+Enter（软换行：记事本照常换行、聊天框不会误发送）；nl=enter 显式裸回车；整段精确多行推荐 clipboard_set+ctrl+v（粘贴前先去掉 \\r）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { text: { type: 'string' }, nl: { type: 'string', description: 'enter=裸回车(默认 shift+enter 软换行)' } },
      required: ['text']
    }
  },
  {
    name: 'keyboard_press',
    description: '按组合键，如 ctrl+shift+a / enter / alt+f4 / win / ctrl+s（修饰符 ctrl/shift/alt/win + 主键）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { keys: { type: 'string', description: '组合键描述' } },
      required: ['keys']
    }
  },
  {
    name: 'keyboard_hold',
    description: '按住组合键持续 ms 毫秒（如按住 space 快进视频、按住 shift 多选）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        keys: { type: 'string', description: '组合键描述' },
        ms: { type: 'number', description: '持续毫秒' }
      },
      required: ['keys', 'ms']
    }
  },
  // ---- 剪贴板 ----
  {
    name: 'clipboard_set',
    description: '写文本到系统剪贴板。配合 keyboard_press ctrl+v 粘贴到任意输入框（比逐字打字快且稳）。默认 \r 归一为 \n（聊天框粘贴遇回车符会触发发送）；keep_cr=1 保留原样',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { text: { type: 'string' } },
      required: ['text']
    }
  },
  {
    name: 'clipboard_get',
    description: '直读当前剪贴板(多格式): type=text 返回文本; type=image 返回 PNG 文件路径+md5(用 Read 看图/OCR/传多模态——用户截屏后 agent 即可读图; 同内容图片 md5 相同不重复落盘); type=files 返回复制的文件路径列表。读选中文字 = 先 keyboard_press ctrl+c 再调本工具',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
  {
    name: 'ocr_image',
    description: '对截图/图片文件跑 OCR（本地 qwen3-vl，无云端外泄）。path=PNG 路径（须位于截图目录，安全限制），返回 chars+text；wait=超时毫秒(默认60000, 大图冷启动建议120000)',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { path: { type: 'string' }, wait: { type: 'number' } },
      required: ['path']
    }
  },
  {
    name: 'pin_image',
    description: '把图片文件钉到桌面（贴图窗，与截图工具条贴图同一实现）：左键拖动/滚轮缩放/双击关闭/右键菜单。path=PNG（截图目录内），x/y=屏幕坐标(缺省居中)。给用户看对比图/参考图用这个',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { path: { type: 'string' }, x: { type: 'number' }, y: { type: 'number' } },
      required: ['path']
    }
  },
  {
    name: 'clipboard_history',
    description: '读取剪贴板历史（常驻监听，最多50条，最新在前，含 "[图片] 路径" 条目）。给 AI 读取用户刚复制的内容。limit=返回条数(可选)',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { limit: { type: 'number', description: '返回条数' } }
    }
  },
  // ---- UIA 语义操作 (核心: 不靠坐标盲点, 直接读写控件) ----
  {
    name: 'ui_tree',
    description: '【语义操作第一步】UIA 枚举窗口全部控件 {index,name,type}。拿到 index 后可 ui_click/ui_read/ui_set。title=窗口标题关键词, max=上限(默认400)',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string', description: '窗口标题关键词' },
        hwnd: { type: 'number', description: '或直接给窗口句柄' },
        max: { type: 'number', description: '最大枚举数' }
      }
    }
  },
  {
    name: 'ui_click',
    description: '【点击首选】语义点击控件。定位优先级: ref > name > i。ref=ui_find/ui_tree 返回的元素稳定引用(最稳, 不漂移, 也不需要 hwnd); name=控件名(一条命令直达, 服务端内部定位+校验); i=ui_tree 下标是下策: 索引跨调用必漂移(实测点偏到别的控件还返回 ok), 必须用 i 时请同时传 name 做校验。坐标点击会自动做落点归属校验: 若该坐标实际命中的元素属于别的进程(目标被别的窗口盖住), 直接报错拦下不点。(如 "保存"/"确定", 一条命令直达, 精确优先模糊兜底, 可加 type=Button 过滤)。invoke/toggle/expand/select 模式优先, 失败回退坐标点击。⚠ 部分应用(微信等)不响应 UIA Invoke: 返回 via=invoke 但界面无变化 —— 改用 ui_find 拿 rect 后 mouse_click 中心, 或本工具传 mode=coord',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' }, hwnd: { type: 'number' },
        ref: { type: 'string', description: '元素稳定引用 (ui_find/ui_tree 返回的 ref 字段, 同 RuntimeId)。最稳: 不随树变化漂移, 不需要 hwnd/title, 元素失效会明确报错要求重新采样。优先用它' },
        i: { type: 'number', description: 'ui_tree 元素下标' },
        name: { type: 'string', description: '按控件名定位' },
        nohit: { type: 'string', description: '填 1 = 跳过落点归属校验 (仅当确定目标就在最顶层时用)' },
        type: { type: 'string', description: '配合 name 过滤类型, 如 Button/MenuItem' },
        mode: { type: 'string', description: 'coord=跳过 UIA Invoke, 直接真实鼠标点控件中心(应用不响应 Invoke 时用)' },
        verify: { type: 'string', description: '填 1 = 点击前后自动截取控件区域像素做对比, 返回 verify.changed 告诉你界面到底变没变。UIA Invoke 常假成功(返回 ok 但界面毫无变化), 强烈建议每次点击都带 verify=1' },
        expect: { type: 'string', description: '点击后要校验的预期内容: 填一段点完应该出现的文字(如目标会话标题/页面标题), 工具会重新扫一遍元素树并返回 expect.found。verify 只能说"界面变了", expect 才能证明"变成了对的那个" —— 切页/切会话/进列表项这类操作必填' },
        force: { type: 'string', description: '填 1 = 跳过可见性校验强行点击(仅当确定元素可见而工具误判时用)' }
      }
    }
  },
  {
    name: 'ui_find',
    description: '按名称/类型查控件(只查不点): 返回全部匹配 {ref,i,name,type,rect,enabled,pid}。name=(模糊) 与 type=(精确类名如 Button/MenuItem) 至少给一个。返回里的 ref 是元素稳定引用, 后面 ui_click/ui_set/ui_read 直接传 ref= 复用, 不会漂移; i 仅本次响应内有效, 跨调用必须重查。先 find 拿 ref 再 click/set',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' }, hwnd: { type: 'number' },
        name: { type: 'string' }, type: { type: 'string' }
      }
    }
  },
  {
    name: 'ui_select',
    description: '设置编辑控件选区 (EM_SETSEL, Win32 Edit/RichEdit 系): 定位控件(i 或 name), start/end 必须都传且非负整数。越界自动 clamp(返回 clamped:true), start>end 交换(swapped:true), 相等=光标定位(collapsed:true)。配合 ctrl+c 读选中文本',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' }, hwnd: { type: 'number' },
        i: { type: 'number' }, name: { type: 'string' },
        start: { type: 'number', description: '起始字符(默认0)' },
        end: { type: 'number', description: '结束字符(默认0=不选)' }
      }
    }
  },
  {
    name: 'ui_read',
    description: '读单个控件详情（名称/值/类型/矩形）。定位: ref=元素稳定引用(推荐) / i=ui_tree 下标 / name=控件名',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' }, hwnd: { type: 'number' },
        i: { type: 'number' }, name: { type: 'string' },
        ref: { type: 'string', description: '元素稳定引用 (ui_find/ui_tree 返回的 ref), 优先于 i/name, 不漂移' }
      }
    }
  },
  {
    name: 'ui_readall',
    description: '读窗口全部控件的名称+值（带值的输入框/勾选状态，比 ui_tree 信息全）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { title: { type: 'string' }, hwnd: { type: 'number' } }
    }
  },
  {
    name: 'ui_set',
    description: '语义写值到输入控件（ValuePattern 直写，不模拟键盘，稳且快）。定位: ref=元素稳定引用(推荐) / i=ui_tree 下标 / name=控件名',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' }, hwnd: { type: 'number' },
        i: { type: 'number' }, name: { type: 'string' },
        ref: { type: 'string', description: '元素稳定引用 (ui_find/ui_tree 返回的 ref), 优先于 i/name, 不漂移' },
        value: { type: 'string' }
      },
      required: ['value']
    }
  },
  // ---- 就绪等待 / 零副作用断言 (抄 web-access B1/B4) ----
  {
    name: 'wait_for',
    description: '【等"内容"出现/消失的首选】在服务端轮询目标窗口, 直到出现指定文字(或 disappear=1 时直到它消失)才返回, 只回结论+耗时+采样次数, 不把每棵 UIA 树灌回上下文。与 win_manage(action=wait) 的区别: 那个只等"窗口存在", 这个等"窗口里的内容到位"。⚠ 关键用法: 点击/导航后不要 sleep 固定秒数再截图判断 —— 慢渲染(Electron 切页、聊天进会话)会误判失败并诱发重复点击; 用本工具等到看见为止。text=目标文字(子串匹配, 别写整句长文本), hwnd=从 list_apps 取(推荐, 桌面共享句柄会变), timeout=毫秒(默认8000, 上限25000), poll=毫秒(默认400), disappear=1 改为等消失。返回 satisfied/waitedMs/samples; 没等到会附三种可能原因(操作没生效/文字写错或窗口不对/该应用 UIA 读不到内容需改走截图+OCR), 按它判断而不是原样重试',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        text: { type: 'string', description: '要等的文字(子串匹配)' },
        hwnd: { type: 'number', description: '限定窗口句柄(推荐, list_apps 取)' },
        title: { type: 'string', description: '按标题关键词定位窗口(可能误匹配, 优先用 hwnd)' },
        timeout: { type: 'number', description: '最长等待毫秒, 默认 8000, 上限 25000' },
        poll: { type: 'number', description: '轮询间隔毫秒, 默认 400' },
        disappear: { type: 'number', description: '1=改成等这段文字消失(如加载中/转圈提示消失)' }
      },
      required: ['text']
    }
  },
  {
    name: 'window_state',
    description: '【零副作用状态断言】一次调用拿准窗口真实状态: {visible,minimized,maximized,foreground,responsive,rect,pid,process,title,style(含 layered/transparent/noactivate/toolwindow)}。不激活、不改焦点、不落盘、完全不碰 UIA —— 因此 Electron 冻结窗也能秒回。替代"截图裁一个像素来验证窗口状态"的土办法(那是拿重活当断言, 还会产生文件)。判据说明: responsive=false 表示窗口线程已不理会消息(真卡死); 但 Electron 渲染层黑屏时 responsive 仍可能为 true, 那种情况按手册"截图字节数"判据(黑屏约 22KB vs 正常 300~400KB)。句柄失效会返回 stale_handle=true 并要你重新 list_apps 采样, 不会让你拿旧句柄白重试',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        hwnd: { type: 'number', description: '窗口句柄(推荐)' },
        title: { type: 'string', description: '标题关键词(可能误伤, 建议同时给 process 或先用 list_apps)' }
      }
    }
  },
  // ---- 录屏 ----
  {
    name: 'record_start',
    description: '开始录屏(ffmpeg 管道→MP4)。不带参数=全屏；x,y,w,h=区域；fps=帧率(默认20)。返回后用 record_status 查时长',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        x: { type: 'number' }, y: { type: 'number' }, w: { type: 'number' }, h: { type: 'number' },
        fps: { type: 'number' }
      }
    }
  },
  {
    name: 'record_stop',
    description: '停止录屏，返回 MP4 文件路径',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
  {
    name: 'record_status',
    description: '查询录屏状态 {recording,seconds,file}',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
  {
    name: 'longshot',
    description: '长截图(滚动拼接, AI 直达无 UI): 对屏幕区域自动滚动并拼接成整图, 存文件返回 path。x,y,w,h=屏幕物理坐标必填; dir=down/up/left/right(默认down, 即滚动方向); max_screens=上限(默认60); timeout_ms=超时(默认120000)。自动滚到内容尽头停止。调用前建议先把目标窗口置前并算好客户区屏幕坐标',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        x: { type: 'number' }, y: { type: 'number' }, w: { type: 'number' }, h: { type: 'number' },
        dir: { type: 'string', enum: ['down', 'up', 'left', 'right'] },
        max_screens: { type: 'number' }, timeout_ms: { type: 'number' }
      },
      required: ['x', 'y', 'w', 'h']
    }
  },
  // ---- 应用 ----
  {
    name: 'app_run',
    description: '运行程序/打开（exe/快捷方式/URL）。GUI 会在用户桌面可见。⚠ 多进程应用(微信/Electron)启动后会换进程换窗, 返回的 hwnd 可能是过渡态: 建议 wait=3000 + process=进程名, 服务端等窗口 rect 稳定后再返回并带 stable 标记',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        path: { type: 'string' }, args: { type: 'string' },
        wait: { type: 'number', description: '找到窗口后额外等待稳定的毫秒数(建议 3000), 0=不等待' },
        process: { type: 'string', description: '只认该进程名的窗口, 如 Weixin' }
      },
      required: ['path']
    }
  },
  {
    name: 'app_restore',
    description: '深度恢复应用窗口(一条命令搞定): 关窗 -> 托盘双击重开 -> 等窗口稳定 -> 贴回原位置。专治两类顽疾: ①窗口看得见但点不动(Electron 假激活/冻结, win_manage activate 唤回的窗口经常是冻的) ②应用完全没有窗口(缩在托盘或任务栏隐藏区, list_apps 看不到)。返回 hwnd/rect/stable/steps',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        process: { type: 'string', description: '进程名, 如 ZCode (推荐, 比标题可靠)' },
        title: { type: 'string', description: '窗口标题, 进程名找不到时用' },
        hwnd: { type: 'number', description: '已知句柄' },
        snap: { type: 'string', description: '恢复后贴靠位置: left|right|top|bottom|topleft|topright|bottomleft|bottomright|max' },
        wait: { type: 'number', description: '等窗口稳定毫秒数, 默认 8000' }
      },
      required: []
    }
  },
  {
    name: 'app_runas',
    description: '以管理员权限运行程序（触发 UAC 提权，用户需确认）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { path: { type: 'string' }, args: { type: 'string' } },
      required: ['path']
    }
  },
  // ---- 常驻功能 ----
  {
    name: 'taskbar_volume',
    description: '任务栏滚轮调音量状态（常驻功能）。enabled=0/1 开关，step=每次滚轮音量变化百分比(1-20,默认2)，reverse=1 反向。带参修改，不带参返回当前状态',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        enabled: { type: 'number', description: '0|1 开关' },
        step: { type: 'number', description: '音量步进百分比' },
        reverse: { type: 'number', description: '0|1 反向' }
      }
    }
  },
  {
    name: 'pick_config',
    description: '划词悬浮球配置（常驻功能，取代豆包划词）。enabled=0/1 开关划词(立即生效+持久化)；askEndpoint/askKey/askModel 改「问AI」后端(默认本机 litellm :4000 / GwV4F)；askPrompt 问AI附加提示词(定制回答风格/角色, 传 "|" 清空回默认)。带参修改，不带参返回当前状态。翻译引擎沿用「翻译」设置。',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        enabled: { type: 'number', description: '0|1 开关划词悬浮球' },
        askEndpoint: { type: 'string', description: '问AI 的 /chat/completions 地址' },
        askKey: { type: 'string', description: '问AI 的 API Key' },
        askModel: { type: 'string', description: '问AI 模型名' },
        askPrompt: { type: 'string', description: '问AI 附加提示词(拼在问题前, 定制风格/角色); 传 "|" 清空' }
      }
    }
  },
  // ---- SKILL 手册 (强制闸门的唯一入口, 必须暴露给客户端, 否则死锁) ----
  {
    name: 'get_skill',
    description: '【必须先调用】取本服务操作手册。强制闸门: 首次调用任何工具前必须先读一次, **读一次管一整轮会话**(不是每轮重读, 别浪费 token)。2026-09-19 起手册已瘦身: 主文件只剩策略与铁律(约 4.8K 字), 具体经验按应用拆成小册子。用法: 不带参数=主手册(开工必读); app="WorkBuddy"/"douyin"/"ocr"/"win-z"=直达该应用小册子(推荐, 比翻主手册准); topic="关键词"=在主手册+历史+全部分片里搜段落(**无命中就什么都不返回, 不会灌整本**); detail="full"=主手册+完整历史考古(28K 字, 只在真要查旧 bug 时用)。踩坑必须 update_skill 写回, 不要只记自己记忆。',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        detail: { type: 'string', description: 'full = 主手册 + 完整历史档案(贵, 慎用); 不给 = 只回主手册' },
        topic: { type: 'string', description: '按标题关键字抽段(如 分屏/冻结/黑屏/熔断); 无命中返回提示而非整本' },
        app: { type: 'string', description: '直达某应用小册子, 如 WorkBuddy / douyin / mimo / ocr / win-z / edge-cdp / electron-tray / build' }
      }
    }
  },
  {
    name: 'update_skill',
    description: '【踩坑必写】把新经验写回共享经验库(全体 agent 下次读即生效)。🔴 必须带 app=：给了就写到 patterns/<app>.md(该应用专项), 不给才写主手册(主手册只放通用纪律, 塞胖了所有人每会话多烧 token)。title=小节标题, entry=markdown 正文, supersedes=本条推翻了哪条旧经验(标题原文, 会打出"维护时删旧条"警告), as_of=日期(默认今天)。拿不准的结论请写"未验证", 别当定论写。',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' },
        entry: { type: 'string' },
        app: { type: 'string', description: '归属应用(如 WorkBuddy/notepad/douyin), 会自动落到对应小册子' },
        supersedes: { type: 'string', description: '本条推翻了哪条旧经验的标题 —— 防止手册里堆互相矛盾的段落' },
        as_of: { type: 'string', description: '结论日期 YYYY-MM-DD, 默认今天' }
      },
      required: ['title', 'entry']
    }
  },
  // ---- 托盘/隐藏窗口 (托盘应用窗口失踪时用) ----
  {
    name: 'tray_click',
    description: '托盘唤回(Win+B 键盘流)。name=图标名；Enter 单击；勿 double=1（会把刚显示的窗再藏回去）；找不到如实 ok:false 带焦点轨迹。点击后重新 window_info(process=...) 或 win_manage listall 验证',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        name: { type: 'string', description: '图标名, 如 WorkBuddy' },
        relaunch: { type: 'number', description: '1=直接再启动应用 exe; 0=托盘键盘流(默认)' },
        button: { type: 'string', description: 'left(默认)/right' },
        double: { type: 'number', description: '0=单击(推荐); 1=勿用(Enter×2 会把刚显示的窗再藏回去)' }
      },
      required: ['name']
    }
  }
];

function send(msg) { process.stdout.write(JSON.stringify(msg) + '\n'); }

// 超时链路单一真源 (P0-1): 服务端每个端点自己的耗时上限必须 <= 本表 <= DSH toolCallTimeoutMs。
// 原来 httpGet 硬编码 30s, 而 longshot 默认 120s / ocr_image 默认 60s —— 长活儿在服务端
// 明明干得完, 桥这边先掐线, Agent 拿到的是 timeout 而不是结果。现按工具声明分别给预算。
// 单位 ms。key = 工具名, 缺省 DEFAULT_TIMEOUT_MS。
const DEFAULT_TIMEOUT_MS = 30000;
const TOOL_TIMEOUT = {
  longshot: 200000,          // 上游 DSH 现为 180s(见 ~/.dsh/mcp-servers.json), 桥略大让超时由上游报出; 服务端硬上限 5min, 超 180s 需分段
  ocr_image: 130000,         // 服务端 wait 默认 60000, 大图可传 120000
  wait_for: 65000,           // 上游放开到 180s 后, 语义等待可用满服务端 60s 上限
  window_state: 8000,        // 纯 Win32 不该慢, 慢了就是出问题了
  ui_tree: 20000, ui_find: 20000, ui_readall: 20000, ui_read: 20000,
  ui_click: 30000,           // 内含 verify(500ms)+expect 轮询(默认 2.5s, 可传 expect_timeout)
  ui_set: 20000, ui_select: 20000,
  record_stop: 25000,        // Stop 走 Join(4s)+关 stdin+WaitForExit(15s)
  app_run: 40000, app_restore: 40000,   // 服务端 wait 可到 8s+ 且要等窗口稳定
  win_manage: 25000,         // action=wait 的 timeout 参数上限 20s
  tray_click: 25000,         // Win+B 键盘流双向扫 80 步
  screen_capture: 20000, pin_image: 15000,
  keyboard_type: 20000,      // ≤2000 字符逐字发送
};
function httpGet(url, timeoutMs) {
  const tmo = timeoutMs || DEFAULT_TIMEOUT_MS;
  return new Promise((resolve, reject) => {
    const http = require('http');
    const req = http.get(url, (res) => {
      let data = '';
      res.on('data', (c) => { data += c; });
      res.on('end', () => {
        try { resolve(JSON.parse(data)); }
        catch (e) { reject(new Error('bad json from helper: ' + data.slice(0, 200))); }
      });
    });
    req.on('error', reject);
    req.setTimeout(tmo, () => { req.destroy(new Error('helper request timeout after ' + tmo + 'ms')); });
  });
}

function buildUrl(name, a) {
  a = a || {};
  const enc = encodeURIComponent;
  switch (name) {
    case 'screen_capture': {
      let qs = [];
      if (a.screen !== undefined) qs.push('screen=' + a.screen);
      else if (a.x !== undefined && a.y !== undefined && a.w !== undefined && a.h !== undefined) qs.push('x=' + a.x, 'y=' + a.y, 'w=' + a.w, 'h=' + a.h);
      else if (a.window) qs.push('window=' + enc(a.window));
      else qs.push('region=all');
      if (a.axes !== undefined && String(a.axes) === '1') qs.push('axes=1');
      return { path: '/shot', qs };
    }
    case 'window_info': {
      const qs = [];
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.process) qs.push('process=' + enc(a.process));
      return { path: '/window', qs };
    }
    case 'active_window': return { path: '/active', qs: [] };
    case 'list_apps': return { path: '/apps', qs: [] };
    case 'monitors': return { path: '/monitors', qs: [] };
    case 'win_manage': {
      // 服务端 /win/ 分支只认 max|min (见 shot-service.cs), 这里做同义映射,
      // 让 agent 写 maximize/minimize 也能正常用, 否则会 404 unknown verb
      const VERB = { maximize: 'max', minimize: 'min' };
      const act = VERB[a.verb || a.action] || a.verb || a.action || 'activate';
      let qs = [];
      if (a.hwnd !== undefined) qs.push('hwnd=' + enc(a.hwnd));
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.x !== undefined) qs.push('x=' + a.x);
      if (a.y !== undefined) qs.push('y=' + a.y);
      if (a.w !== undefined) qs.push('w=' + a.w); // 服务端 move 必填 x,y,w,h, 不传会 400
      if (a.h !== undefined) qs.push('h=' + a.h);
      if (a.timeout !== undefined) qs.push('timeout=' + a.timeout);
      if (a.pid !== undefined) qs.push('pid=' + a.pid);
      if (a.pos) qs.push('pos=' + enc(a.pos));
      if (a.monitor) qs.push('monitor=' + enc(a.monitor));
      // 网格布局参数 (cols/col/colspan + rows/row/rowspan): MoveWindow 画矩形
      ['cols', 'col', 'colspan', 'rows', 'row', 'rowspan'].forEach((k) => {
        if (a[k] !== undefined) qs.push(k + '=' + a[k]);
      });
      // Win+Z 系统 Snap Layouts: pos=zthirdleft|zthirdmid|zthirdright|zkbd 或显式 layout/zone
      if (a.layout !== undefined) qs.push('layout=' + a.layout);
      if (a.zone !== undefined) qs.push('zone=' + a.zone);
      if (a.esc !== undefined) qs.push('esc=' + (a.esc ? '1' : '0'));
      // Snap Assist 点选填位(吸附组正确姿势): fill=标题或进程名, 逗号分隔按序填剩余区
      if (a.fill) qs.push('fill=' + enc(a.fill));
      return { path: '/win/' + act, qs };
    }
    case 'tray_click': {
      const qs = ['name=' + enc(a.name)];
      if (a.relaunch) qs.push('relaunch=1');
      if (a.button) qs.push('button=' + a.button);
      if (a.double) qs.push('double=' + a.double);
      return { path: '/tray/click', qs };
    }
    case 'mouse_move': return { path: '/mouse/move', qs: ['x=' + a.x, 'y=' + a.y] };
    case 'mouse_click': {
      let qs = [];
      if (a.x !== undefined && a.y !== undefined) qs.push('x=' + a.x, 'y=' + a.y);
      if (a.button) qs.push('button=' + a.button);
      if (a.double) qs.push('double=' + a.double);
      if (a.triple) qs.push('triple=' + a.triple);
      if (a.mods) qs.push('mods=' + enc(a.mods));
      if (a.front) qs.push('front=' + a.front);
      return { path: '/mouse/click', qs };
    }
    case 'mouse_down': return { path: '/mouse/down', qs: a.button ? ['button=' + a.button] : [] };
    case 'mouse_up': return { path: '/mouse/up', qs: a.button ? ['button=' + a.button] : [] };
    case 'mouse_drag': {
      let qs = ['x1=' + a.x1, 'y1=' + a.y1, 'x2=' + a.x2, 'y2=' + a.y2];
      if (a.button) qs.push('button=' + a.button);
      return { path: '/mouse/drag', qs };
    }
    case 'mouse_pos': return { path: '/mouse/pos', qs: [] };
    case 'mouse_scroll': {
      let qs = ['delta=' + a.delta];
      if (a.x !== undefined && a.y !== undefined) qs.push('x=' + a.x, 'y=' + a.y);
      return { path: '/mouse/scroll', qs };
    }
    case 'keyboard_type': {
      let qs = ['text=' + enc(String(a.text))];
      if (a.nl) qs.push('nl=' + enc(a.nl));
      return { path: '/keyboard/type', qs };
    }
    case 'keyboard_press': return { path: '/keyboard/press', qs: ['keys=' + enc(a.keys)] };
    case 'keyboard_hold': return { path: '/keyboard/hold', qs: ['keys=' + enc(a.keys), 'ms=' + (a.ms || 500)] };
    case 'clipboard_set': {
      let qs = ['text=' + enc(String(a.text))];
      if (a.keep_cr) qs.push('keep_cr=1');
      return { path: '/clipboard/set', qs };
    }
    case 'ocr_image': {
      let qs = ['path=' + enc(a.path)];
      if (a.wait !== undefined) qs.push('wait=' + a.wait);
      return { path: '/ocr', qs };
    }
    case 'pin_image': {
      let qs = ['path=' + enc(a.path)];
      if (a.x !== undefined) qs.push('x=' + a.x);
      if (a.y !== undefined) qs.push('y=' + a.y);
      return { path: '/pin', qs };
    }
    case 'clipboard_get': return { path: '/clipboard/get', qs: [] };
    case 'clipboard_history': return { path: '/clipboard/history', qs: a.limit !== undefined ? ['limit=' + a.limit] : [] };
    case 'ui_tree': {
      let qs = [];
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      if (a.max !== undefined) qs.push('max=' + a.max);
      return { path: '/ui/tree', qs };
    }
    case 'ui_click': {
      let qs = [];
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      if (a.ref) qs.push('ref=' + enc(a.ref));
      if (a.i !== undefined) qs.push('i=' + a.i);
      if (a.name) qs.push('name=' + enc(a.name));
      if (a.type) qs.push('type=' + enc(a.type));
      if (a.mode) qs.push('mode=' + enc(a.mode));
      if (a.nohit !== undefined) qs.push('nohit=' + enc(a.nohit));
      if (a.verify !== undefined) qs.push('verify=' + enc(a.verify));
      if (a.expect) qs.push('expect=' + enc(a.expect));
      if (a.force !== undefined) qs.push('force=' + enc(a.force));
      return { path: '/ui/click', qs };
    }
    case 'ui_find': {
      let qs = [];
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      if (a.name !== undefined) qs.push('name=' + enc(a.name));
      if (a.type !== undefined) qs.push('type=' + enc(a.type));
      return { path: '/ui/find', qs };
    }
    case 'ui_select': {
      let qs = [];
      if (a.ref) qs.push('ref=' + enc(a.ref));
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      if (a.i !== undefined) qs.push('i=' + a.i);
      if (a.name !== undefined) qs.push('name=' + enc(a.name));
      qs.push('start=' + (a.start || 0), 'end=' + (a.end || 0));
      return { path: '/ui/select', qs };
    }
    case 'ui_read': {
      let qs = [];
      if (a.ref) qs.push('ref=' + enc(a.ref));
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      if (a.i !== undefined) qs.push('i=' + a.i);
      if (a.name) qs.push('name=' + enc(a.name));
      return { path: '/ui/read', qs };
    }
    case 'ui_readall': {
      let qs = [];
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      return { path: '/ui/readall', qs };
    }
    case 'ui_set': {
      let qs = [];
      if (a.ref) qs.push('ref=' + enc(a.ref));
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      if (a.i !== undefined) qs.push('i=' + a.i);
      if (a.name) qs.push('name=' + enc(a.name));
      qs.push('value=' + enc(String(a.value)));
      return { path: '/ui/set', qs };
    }
    case 'wait_for': {
      let qs = [];
      qs.push('text=' + enc(String(a.text)));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      if (a.title) qs.push('title=' + enc(a.title));
      if (a.timeout) qs.push('timeout=' + a.timeout);
      if (a.poll) qs.push('poll=' + a.poll);
      if (a.disappear) qs.push('disappear=' + a.disappear);
      return { path: '/wait_for', qs };
    }
    case 'window_state': {
      let qs = [];
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      else if (a.title) qs.push('title=' + enc(a.title));
      return { path: '/win/state', qs };
    }
    case 'record_start': {
      let qs = [];
      if (a.x !== undefined) qs = qs.concat(['x=' + a.x, 'y=' + a.y, 'w=' + a.w, 'h=' + a.h]);
      if (a.fps !== undefined) qs.push('fps=' + a.fps);
      return { path: '/record/start', qs };
    }
    case 'record_stop': return { path: '/record/stop', qs: [] };
    case 'record_status': return { path: '/record/status', qs: [] };
    case 'longshot': {
      const qs = ['x=' + a.x, 'y=' + a.y, 'w=' + a.w, 'h=' + a.h];
      if (a.dir) qs.push('dir=' + enc(a.dir));
      if (a.max_screens !== undefined) qs.push('max_screens=' + a.max_screens);
      if (a.timeout_ms !== undefined) qs.push('timeout_ms=' + a.timeout_ms);
      return { path: '/longshot', qs };
    }
    case 'app_run': {
      let qs = ['path=' + enc(a.path)];
      if (a.args) qs.push('args=' + enc(a.args));
      if (a.wait !== undefined) qs.push('wait=' + a.wait);
      if (a.process) qs.push('process=' + enc(a.process));
      return { path: '/app/run', qs };
    }
    case 'app_restore': {
      const qs = [];
      if (a.process) qs.push('process=' + enc(a.process));
      if (a.title) qs.push('title=' + enc(a.title));
      if (a.hwnd) qs.push('hwnd=' + a.hwnd);
      if (a.snap) qs.push('snap=' + enc(a.snap));
      if (a.wait) qs.push('wait=' + a.wait);
      return { path: '/app/restore', qs };
    }
    case 'app_runas': {
      let qs = ['path=' + enc(a.path)];
      if (a.args) qs.push('args=' + enc(a.args));
      return { path: '/app/runas', qs };
    }
    case 'taskbar_volume': {
      let qs = [];
      if (a.enabled !== undefined) qs.push('enabled=' + a.enabled);
      if (a.step !== undefined) qs.push('step=' + a.step);
      if (a.reverse !== undefined) qs.push('reverse=' + a.reverse);
      return { path: '/taskbar-volume', qs };
    }
    case 'pick_config': {
      let qs = [];
      if (a.enabled !== undefined) qs.push('enabled=' + a.enabled);
      if (a.askEndpoint) qs.push('askEndpoint=' + enc(a.askEndpoint));
      if (a.askKey) qs.push('askKey=' + enc(a.askKey));
      if (a.askModel) qs.push('askModel=' + enc(a.askModel));
      if (a.askPrompt !== undefined) qs.push('askPrompt=' + enc(a.askPrompt));
      return { path: '/pick-config', qs };
    }
    default: return null;
  }
}

// ===== 参数校验：TOOLS[].inputSchema 是唯一真源，校验/提示/超时全从它派生 (抄 web-access A3) =====
// 治的病：以前参数名写错不报错 —— keyboard_press 把 keys 写成 key，bridge 照样拼出
// keys=undefined 发给服务端，Agent 收到"成功"，实际一下也没按。静默假成功是这里最贵的债。
const TOOL_INDEX = {};
for (const t of TOOLS) TOOL_INDEX[t.name] = t;

function editDistance(a, b) {
  if (a === b) return 0;
  let prev = Array.from({ length: b.length + 1 }, (_, i) => i);
  for (let i = 1; i <= a.length; i++) {
    const cur = [i];
    for (let j = 1; j <= b.length; j++) {
      cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
    }
    prev = cur;
  }
  return prev[b.length];
}

// did_you_mean：编辑距离足够近才建议，否则宁可不提（瞎建议比不建议更坑）
function suggestKey(k, keys) {
  const lim = Math.max(2, Math.floor(k.length / 3));
  let best = null, bestD = Infinity;
  for (const c of keys) {
    const d = editDistance(k.toLowerCase(), c.toLowerCase());
    if (d < bestD) { bestD = d; best = c; }
  }
  return best && bestD <= lim ? best : null;
}

// 从 schema 现造一条正确调用示例 —— 报错时把饭喂到嘴边，不让 Agent 自己猜
function makeExample(name) {
  const tool = TOOL_INDEX[name];
  const props = (tool && tool.inputSchema && tool.inputSchema.properties) || {};
  const req = (tool && tool.inputSchema && tool.inputSchema.required) || Object.keys(props).slice(0, 2);
  const sample = {};
  for (const k of req) {
    const p = props[k] || {};
    if (Array.isArray(p.enum)) sample[k] = p.enum[0];
    else if (p.type === 'number') sample[k] = k === 'delta' ? -3 : 0;
    else if (p.type === 'string') sample[k] = '…';
    else sample[k] = true;
  }
  return name + '(' + Object.keys(sample).map(k => k + '=' + JSON.stringify(sample[k])).join(', ') + ')';
}

function typeOk(declared, v) {
  if (v === null || v === undefined) return true;
  switch (declared) {
    case 'number': return typeof v === 'number' ? !Number.isNaN(v) : (typeof v === 'string' && v.trim() !== '' && !Number.isNaN(Number(v)));
    case 'string': return typeof v === 'string' || typeof v === 'number';
    case 'boolean': return typeof v === 'boolean';
    default: return true;
  }
}

// 返回 null = 通过；返回字符串 = 要回给 Agent 的完整报错文本（含正确写法）
function validateArgs(name, args) {
  const tool = TOOL_INDEX[name];
  if (!tool) return 'unknown tool: ' + name;
  const schema = tool.inputSchema || {};
  const props = schema.properties || {};
  const required = schema.required || [];
  const a = (args && typeof args === 'object') ? args : {};
  const keys = Object.keys(props);
  const problems = [];

  for (const k of Object.keys(a)) {
    if (k in props) continue;
    const s = suggestKey(k, keys);
    problems.push('未知参数 "' + k + '"' + (s ? '，你是不是想写 "' + s + '"？' : '，本工具不认识它'));
  }
  for (const k of required) {
    if (a[k] === undefined || a[k] === null || a[k] === '') problems.push('缺少必填参数 "' + k + '"');
  }
  for (const k of Object.keys(a)) {
    const p = props[k];
    if (!p || !(k in props)) continue;
    if (p.type && !typeOk(p.type, a[k])) problems.push('参数 "' + k + '" 类型应为 ' + p.type + '，实际收到 ' + JSON.stringify(a[k]));
    if (Array.isArray(p.enum) && a[k] !== undefined && p.enum.indexOf(a[k]) < 0) {
      const s = suggestKey(String(a[k]), p.enum);
      problems.push('参数 "' + k + '" 只能是 ' + p.enum.join('|') + '，收到 ' + JSON.stringify(a[k]) + (s ? '（想写 "' + s + '"？）' : ''));
    }
  }
  if (!problems.length) return null;
  return '参数不合法，本次调用已被拦下，什么都没执行:\n  - ' + problems.join('\n  - ') +
    '\n正确写法: ' + makeExample(name) +
    '\n本工具全部可用参数: ' + (keys.join(', ') || '（无参数）') +
    '\n别用同样的调用重试 —— 照上面改参数名再发一次。';
}

// 桥侧等待预算 = 声明预算；Agent 自己传了更长的超时就把预算抬到它要的值（服务端会自己裁）
function toolTimeout(name, args) {
  let budget = TOOL_TIMEOUT[name] || DEFAULT_TIMEOUT_MS;
  for (const k of ['timeout_ms', 'timeout', 'wait']) {
    const v = Number(args && args[k]);
    if (v > 0) budget = Math.max(budget, v + 8000);
  }
  return Math.min(budget, 200000);
}

async function callTool(name, args) {
  // SKILL 工具：返回 SKILL.md 全文（同目录，缺文件时回退内嵌简版）
  if (name === 'get_skill') {
    guideRead = true;
    const _fs2 = require('fs'), _path2 = require('path');
    const rd = (p) => { try { return _fs2.readFileSync(_path2.join(__dirname, p), 'utf8'); } catch (e) { return ''; } };
    let _hist = rd('SKILL-HISTORY.md');
    let core = rd('SKILL.md');
    if (!core) core = '【SKILL.md 缺失】兜底简版纪律: 点前先定位并确认前台 / 语义优先坐标兜底 / 中文用剪贴板粘贴 / 每次操作后验证 / 敏感操作先问用户。';
    // 2026-09-19 瘦身: 主手册只留策略与铁律, 细节按应用拆在下列目录的小册子里(经验真源), 按需取而不是每会话灌一整本
    const SHARD_DIRS = ['patterns', 'dev', 'scripts-doc', 'docs-moved'];
    const shards = [];
    for (const d of SHARD_DIRS) {
      try { for (const f of _fs2.readdirSync(_path2.join(__dirname, d))) if (/\.md$/i.test(f)) shards.push(d + '/' + f); } catch (e) { }
    }
    const norm = (s) => String(s).toLowerCase().replace(/[^a-z0-9\u4e00-\u9fff]/g, '');
    const full = core + '\n\n================ 以下为完整历史档案 ================\n\n' + _hist;
    // 按应用直达小册子 (抄 web-access 的 site-patterns/{domain}.md: 经验按对象分片)
    if (args && args.app) {
      const want = norm(args.app);
      const hits = shards.filter((p) => { const b = norm(_path2.basename(p, '.md')); return b && (b.includes(want) || want.includes(b)); });
      if (!hits.length)
        return { content: [{ type: 'text', text: '没有 app="' + args.app + '" 对应的小册子。\n现有分片: ' + (shards.join('  ') || '(空)') + '\n\n—— 主手册(先按它的索引表判断该翻哪本) ——\n\n' + core }] };
      guideRead = true;
      return { content: [{ type: 'text', text: '(app="' + args.app + '" 命中 ' + hits.length + ' 本: ' + hits.join(', ') + ')\n\n' + hits.map((p) => '======== ' + p + ' ========\n' + rd(p)).join('\n\n') }] };
    }
    // 按主题抽取: 主手册 + 历史档案 + **全部分片** 一起搜
    if (args && args.topic) {
      const kw = String(args.topic);
      const pick = (md) => {
        const lines = md.split('\n');
        const out = [];
        let buf = [], taking = false;
        for (const ln of lines) {
          const isHead = /^#{1,6}\s/.test(ln);
          if (isHead) {
            if (taking) out.push(buf.join('\n'));
            taking = ln.indexOf(kw) >= 0;
            buf = taking ? [ln] : [];
          } else if (taking) buf.push(ln);
        }
        if (taking) out.push(buf.join('\n'));
        return out;
      };
      let hits = pick(core).concat(pick(_hist));
      for (const p of shards) hits = hits.concat(pick(rd(p)));
      hits = hits.filter((s) => s.trim());
      // 标题没命中就再搜正文(取命中行 ±2 行) —— 否则像"熔断"这种只出现在正文的关键字永远抽不到, 抽段能力名不副实
      if (!hits.length) {
        const pickBody = (md, label) => {
          const ls = md.split('\n'), o = [];
          for (let i = 0; i < ls.length; i++) {
            if (/^#{1,6}\s/.test(ls[i])) continue;
            if (ls[i].indexOf(kw) < 0) continue;
            const a = Math.max(0, i - 2), b = Math.min(ls.length - 1, i + 3);
            o.push('（' + label + ' · 正文命中 @第' + (i + 1) + '行）\n' + ls.slice(a, b + 1).join('\n'));
            i = b;
          }
          return o;
        };
        hits = pickBody(core, 'SKILL.md').concat(pickBody(_hist, 'SKILL-HISTORY.md'));
        for (const p of shards) hits = hits.concat(pickBody(rd(p), p));
        hits = hits.filter((s) => s.trim()).slice(0, 12);   // 正文命中可能很多, 封顶 12 段防倒灌
      }
      if (hits.length) {
        guideRead = true;
        return { content: [{ type: 'text', text: '(get_skill topic="' + kw + '" 命中 ' + hits.length + ' 段)\n\n' + hits.join('\n\n---\n\n') }] };
      }
      // 关键改动: 无命中**不再回退整份主手册**(旧版实测一次灌回 42,912 字, 抽不到反而更贵)
      return { content: [{ type: 'text', text: 'topic="' + kw + '" 在主手册、历史档案和全部分片里都没命中 —— 所以什么都不返回, 不再拿整本手册充数。\n可以试: ① get_skill(app=应用名) 直达小册子 ② 按主手册索引表 read 对应 .md ③ 确实要考古才用 detail="full"\n\n现有分片: ' + (shards.join('  ') || '(空)') }], isError: true, severity: 'self_heal' };
    }
    if (args && args.detail === 'full') {
      guideRead = true;
      return { content: [{ type: 'text', text: full }] };
    }
    guideRead = true;
    return { content: [{ type: 'text', text: core + '\n\n—— 请遵守以上纪律。踩到坑用 update_skill 写回, **务必带 app= 落到对应小册子**, 别把主手册重新写胖。' }] };
  }
  // 写回工具：默认落到**对应应用的小册子**，不再无差别往主手册尾部堆
  // 病根记录: 旧实现只有 appendFileSync 一条路, 谁踩坑都往 SKILL.md 尾部追加, 写错了只能再加一节说"上节作废"
  //          —— 四万字手册和 15 处「纠正/推翻」标记就是这么长出来的。
  if (name === 'update_skill') {
    const fs = require('fs'), path = require('path');
    const mainFile = path.join(__dirname, 'SKILL.md');
    const rawApp = args.app ? String(args.app).trim() : '';
    const slug = rawApp.toLowerCase().replace(/[^a-z0-9]/g, '');
    let file = mainFile, note = '';
    try {
      if (slug) {
        fs.mkdirSync(path.join(__dirname, 'patterns'), { recursive: true });
        file = path.join(__dirname, 'patterns', slug + '.md');
        if (!fs.existsSync(file))
          fs.writeFileSync(file, '<!-- ' + rawApp + ' 专项经验册(由 update_skill 自动建)。写经验请标日期, 拿不准的写"未验证"。 -->\n# ' + rawApp + ' 专项经验\n\n', 'utf8');
        note = '已按 app="' + rawApp + '" 归入小册子';
      } else {
        note = '⚠️ 未指定 app=，本次写进了主手册。主手册只该放**通用纪律**，专项坑请下次带 app=应用名 写到 patterns/ 下，否则它又会涨回四万字';
      }
      const _d = new Date();
      // 用本地日期: toISOString 取 UTC 会把"今天"写成昨天, 而手册日期正是判断结论新旧的唯一依据
      const stamp = args.as_of ? String(args.as_of) : (_d.getFullYear() + '-' + String(_d.getMonth() + 1).padStart(2, '0') + '-' + String(_d.getDate()).padStart(2, '0'));
      const sup = String(args.supersedes || '').trim();
      const supOk = sup.length > 3 && !/^(无|没有|新坑|无此条|none|null|n\/a|\(.*\))$/i.test(sup);
      const supLine = supOk
        ? '> ⚠️ 本条推翻「' + sup + '」（' + stamp + '）—— 维护时**删掉旧条**，别留两段互相矛盾的话让后来人自己猜。\n\n'
        : (args.supersedes ? '<!-- supersedes 内容像占位文字("' + sup + '"), 未生成推翻标记; 要真推翻请传旧条标题原文 -->\n\n' : '');
      const entry = '\n## ' + (args.title || '经验补充') + '\n\n' + supLine +
        '_记录日期: ' + stamp + (rawApp ? ' · 应用: ' + rawApp : ' · 通用') + '_\n\n' + (args.entry || '') + '\n';
      fs.appendFileSync(file, entry, 'utf8');
      let warn = '';
      try {
        const sz = fs.readFileSync(mainFile, 'utf8').length;
        if (sz > 9000) warn = '\n【体积守卫】主手册已有 ' + sz + ' 字(瘦身目标 ≤9000)。请把细节移去 patterns/ 小册子, 只在这份里留一行指路 —— 四万字就是这么攒起来的。';
      } catch (e) { }
      return { content: [{ type: 'text', text: '已写入: ' + file + '\n' + note + '。下次任何 agent 用 get_skill(app=…) 即可读到。' + warn }] };
    } catch (e) { return { isError: true, severity: 'self_heal', content: [{ type: 'text', text: '写入失败: ' + e.message }] }; }
  }
  // 参数校验排在闸门之前：写错参数这种事当场就该说清，不该先逼 Agent 白读四万字手册再告诉它。
  const bad = validateArgs(name, args);
  if (bad) {
    return { isError: true, severity: 'self_heal', content: [{ type: 'text', text: guideRead ? bad : bad + '\n（另：本服务要求首次操作前先调用 get_skill 读手册，改完参数顺手把它调了）' }] };
  }
  // 强制闸门：**动手类**工具首次调用前必须先读 SKILL。
  // 2026-09-19 分级(老大拍板 A 方案): 纯观察工具不再拦 —— 看一眼屏幕不破坏任何东西, 拦它只是逼 agent 先吐 4.8K 字手册。
  // 仍然拦的: 一切点击/输入/窗口变更; UIA 枚举(ui_tree/ui_find 会物化整棵树, 在 Electron 上真能把服务拖挂, 必须先懂纪律);
  //          截图与录屏(会落盘产生文件); ui_* 读控件(依赖前置定位纪律, 不放行)。
  const GATE_EXEMPT = ['active_window', 'list_apps', 'window_info', 'monitors', 'mouse_pos', 'clipboard_get', 'clipboard_history', 'record_status'];
  if (!guideRead && !GATE_EXEMPT.includes(name)) {
    return { isError: true, severity: 'self_heal', content: [{ type: 'text', text: '⚠️ 本服务强制要求：动手之前必须先调用 get_skill（无参数, 现在只要 4.8K 字）获取操作手册与安全纪律（点前定位 / 语义优先 / 输入前确认前台 / 操作后验证 / 敏感操作确认）。请先调用 get_skill，再重试本工具。踩坑后用 update_skill 写回，记得带 app=。' }] };
  }
  const u = buildUrl(name, args);
  if (!u) return { isError: true, content: [{ type: 'text', text: 'unknown tool: ' + name + '（不在本服务工具清单里，先 tools/list 核对名字）' }] };
  const url = `http://${HOST}:${PORT}${u.path}${u.qs.length ? '?' + u.qs.join('&') : ''}`;
  try {
    const r = await httpGet(url, toolTimeout(name, args));
    return { content: [{ type: 'text', text: JSON.stringify(r) }], isError: !r.ok };
  } catch (e) {
    // 失败分三态 (抄 web-access 退出码协议): self_heal=Agent 能自己救 / need_user=得叫人 / dead_end=到此为止
    const msg = String(e.message || e);
    if (/timeout/i.test(msg)) {
      return { isError: true, severity: 'self_heal', content: [{ type: 'text', text:
        '桥侧等待超时 (' + msg + ') —— 注意这不代表服务端没干完，可能只是它比桥等得久。\n' +
        '本次请求: ' + url.replace(/([?&](text|value|entry|expect|name|title)=)[^&]*/g, '$1…') + '\n' +
        'self_heal 处理顺序: (1) 用 /health 或该资源的 status 工具确认活儿是否已经干完（长截图/录屏产物已落盘就别重跑）; ' +
        '(2) 确实没干完就把本工具的超时参数调小、目标范围缩小后重试一次; ' +
        '(3) 同一调用连续两次超时就不再重试，向用户报告并附本条错误。' }] };
    }
    if (/ECONNREFUSED|helper unreachable|socket hang up|connect/i.test(msg)) {
      return { isError: true, severity: 'need_user', content: [{ type: 'text', text:
        '连不上桌面助手 (127.0.0.1:' + PORT + '): ' + msg + '\n' +
        'need_user —— 这不是参数问题，重试同一个调用没用。请告诉用户：桌面助手程序（托盘上的 Win Desktop Helper）没在运行或正在重启，' +
        '需要用户在托盘菜单里恢复；恢复后 GET http://127.0.0.1:' + PORT + '/health 能回 json 即可继续。' }] };
    }
    return { isError: true, severity: 'dead_end', content: [{ type: 'text', text: 'helper request failed: ' + msg }] };
  }
}

// MCP stdio 主循环
const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin, terminal: false });
rl.on('line', async (line) => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  const id = msg.id;
  const method = msg.method;

  if (method === 'initialize') {
    send({ jsonrpc: '2.0', id, result: {
      protocolVersion: '2025-06-18',
      capabilities: { tools: { listChanged: false } },
      serverInfo: { name: 'win-desktop-helper', version: VERSION }
    } });
  } else if (method === 'notifications/initialized' || method === 'notifications/cancelled') {
    // 无操作
  } else if (method === 'ping') {
    send({ jsonrpc: '2.0', id, result: {} });
  } else if (method === 'tools/list') {
    send({ jsonrpc: '2.0', id, result: { tools: TOOLS } });
  } else if (method === 'tools/call') {
    const p = msg.params || {};
    const r = await callTool(p.name, p.arguments);
    send({ jsonrpc: '2.0', id, result: r });
  } else if (method === 'tools/notifications/list_changed') {
    // 无操作
  } else {
    send({ jsonrpc: '2.0', id, error: { code: -32601, message: 'method not found: ' + method } });
  }
});
rl.on('close', () => { setTimeout(() => process.exit(0), 3000); }); // 留时间给异步 tools/call 完成
