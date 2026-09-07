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
    description: '截取用户桌面指定区域，返回保存的 PNG 文件路径(可用 Read 工具读图)。region=all 全屏(默认)；screen=N 指定显示器；x,y,w,h 任意矩形(物理像素)；window=窗口标题关键词(截该窗口)',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        region: { type: 'string', description: 'all | 忽略表示全屏' },
        screen: { type: 'number', description: '显示器下标' },
        x: { type: 'number' }, y: { type: 'number' }, w: { type: 'number' }, h: { type: 'number' },
        window: { type: 'string', description: '窗口标题关键词' }
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
        pos: { type: 'string', description: 'action=snap 时必填: left 左半屏 / right 右半屏 / top 上半 / bottom 下半 / topleft|topright|bottomleft|bottomright 四分之一屏 / max 最大化 / min 最小化 / restore 还原' },
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
    description: '点击（带坐标先移动再点）。button=left|right|middle，double=1 双击，triple=1 三击（选整行/段），mods=shift/ctrl/alt/win 按住修饰键点击（如 shift+click 选范围、ctrl+click 多选）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        x: { type: 'number' }, y: { type: 'number' },
        button: { type: 'string', description: 'left|right|middle' },
        double: { type: 'number', description: '0|1' },
        triple: { type: 'number', description: '0|1 三连击(选整行/段); 坐标务必取行内 rect.x+20 以上、行垂直中线 —— 打左边缘 2px 会被 RichEdit 边距命中区变成全选(实测坑)' },
        mods: { type: 'string', description: 'shift|ctrl|alt|win，可组合如 ctrl+shift' }
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
    description: '【必须先调用】获取本服务 SKILL 操作手册（铁律/避坑/黄金路径）。本服务强制闸门: 首次调用任何工具前必须先读本 SKILL, 否则一律报错。默认返回核心版(约 3K 字, 够用); 需要历史考古/完整坑表用 detail=\"full\"; 只想查某个主题用 topic=\"关键词\"(按标题匹配抽段, 例 topic=\"分屏\" / \"冻结\" / \"D6\")。踩坑必须用 update_skill 写回共享手册, 不要只写进自己的记忆。',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        detail: { type: 'string', description: 'full = 返回核心 + 完整历史档案(SKILL-HISTORY.md, 含已修 bug 考古/完整坑表); 不给 = 只返回核心' },
        topic: { type: 'string', description: '按标题关键字抽取相关段落(如 分屏/冻结/录屏/D6/任务栏), 找不到就回退返回核心版' }
      }
    }
  },
  {
    name: 'update_skill',
    description: '【踩坑必写】把新踩的坑写回共享 SKILL.md（全体 agent 共享, 下次 get_skill 立即生效）。title=小节标题, entry=markdown 正文',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' },
        entry: { type: 'string' }
      },
      required: ['title', 'entry']
    }
  },
  // ---- 托盘/隐藏窗口 (托盘应用窗口失踪时用) ----
  {
    name: 'tray_click',
    description: '点击系统托盘/任务栏图标。用途: 托盘应用(如 ZCode/微信类 Electron 应用)进程活着但窗口失踪时, 双击托盘图标唤回主窗。name=图标名(含糊匹配, 主区找不到会自动展开溢出区找), button=left(默认)/right, double=1 双击(多数托盘应用双击=打开主窗口, 单击可能只弹预览)。点击后必须重新采样验证: window_info(process=应用进程名) 或 win_manage(action=listall)。',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        name: { type: 'string', description: '图标名, 如 ZCode' },
        button: { type: 'string', description: 'left(默认)/right' },
        double: { type: 'number', description: '1=双击(推荐, 打开主窗)' }
      },
      required: ['name']
    }
  }
];

function send(msg) { process.stdout.write(JSON.stringify(msg) + '\n'); }

function httpGet(url) {
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
    req.setTimeout(30000, () => { req.destroy(new Error('helper request timeout')); });
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
      // 网格布局参数 (cols/col/colspan + rows/row/rowspan): 覆盖 Win11 Snap Layouts 全部布局 + 任意比例
      ['cols', 'col', 'colspan', 'rows', 'row', 'rowspan'].forEach((k) => {
        if (a[k] !== undefined) qs.push(k + '=' + a[k]);
      });
      return { path: '/win/' + act, qs };
    }
    case 'tray_click': {
      const qs = ['name=' + enc(a.name)];
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

async function callTool(name, args) {
  // SKILL 工具：返回 SKILL.md 全文（同目录，缺文件时回退内嵌简版）
  if (name === 'get_skill') {
    guideRead = true;
    const _fs2 = require('fs'), _path2 = require('path');
    let _hist = '';
    try { _hist = _fs2.readFileSync(_path2.join(__dirname, 'SKILL-HISTORY.md'), 'utf8'); } catch (e) { }
    let core = '';
    try { core = _fs2.readFileSync(_path2.join(__dirname, 'SKILL.md'), 'utf8'); } catch (e) { }
    const full = core + '\n\n================ 以下为完整历史档案 ================\n\n' + _hist;
    // 按主题抽取: 从两个文件里按标题关键字匹配段落
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
      const hits = pick(core).concat(pick(_hist)).filter(s => s.trim());
      if (hits.length) guideRead = true;
      const body = hits.length ? hits.join('\n\n---\n\n') : core;
      const tip = hits.length
        ? '(get_skill topic=\"' + kw + '\" 命中 ' + hits.length + ' 段)'
        : '(topic=\"' + kw + '\" 无匹配, 已返回核心版; 换个关键词或 detail=\"full\" 取全部)';
      return { content: [{ type: 'text', text: tip + '\n\n' + body }] };
    }
    if (args && args.detail === 'full') {
      guideRead = true;
      return { content: [{ type: 'text', text: full }] };
    }
    let text = '';
    try { text = _fs2.readFileSync(_path2.join(__dirname, 'SKILL.md'), 'utf8'); }
    catch (e) {
      text = '【Win Desktop Helper SKILL·简版】\n' +
             '1. 点任何东西前先 window_info/active_window 定位并确认前台；\n' +
             '2. 语义优先: ui_tree → ui_click/ui_set/ui_read, 坐标点击是兜底；\n' +
             '3. keyboard_type 发给当前前台窗口，输入前必须 active_window 确认目标；\n' +
             '4. 大段文本用 clipboard_set + keyboard_press ctrl+v (比逐字打字快且稳)；\n' +
             '5. 操作后立即 screen_capture/文件系统验证；\n' +
             '6. 删除/发送等敏感操作先经用户对话确认；\n' +
             '7. 点不动/找不到时先 active_window+全屏截图看真实状态，别盲试。\n' +
             '（完整版见仓库 SKILL.md）';
    }
    return { content: [{ type: 'text', text: text + '\n\n—— 请遵守以上 SKILL 纪律。执行中若踩坑，务必用 update_skill 写回共享 SKILL.md（全体 agent 共享），不要只写进自己的记忆。' }] };
  }
  // 写回工具：踩坑经验 append 进共享 SKILL.md（全体 agent 可见）
  if (name === 'update_skill') {
    const fs = require('fs'), path = require('path');
    const file = path.join(__dirname, 'SKILL.md');
    try {
      const entry = '\n## ' + (args.title || '经验补充') + '\n\n' + (args.entry || '') + '\n';
      fs.appendFileSync(file, entry, 'utf8');
      return { content: [{ type: 'text', text: '已写入共享 SKILL.md: ' + file + '（下次任何 agent 调用 get_skill 即可读到新经验）' }] };
    } catch (e) { return { isError: true, content: [{ type: 'text', text: '写入失败: ' + e.message }] }; }
  }
  // 强制闸门：所有工具（含观察类）首次调用前必须先读 SKILL
  if (!guideRead) {
    return { isError: true, content: [{ type: 'text', text: '⚠️ 本服务强制要求：首次操作前必须先调用 get_skill（该工具已在工具清单中, 直接调用即可, 无参数）获取 SKILL 操作手册与安全纪律（点前定位 / 语义优先 / 输入前确认前台 / 操作后验证 / 敏感操作确认）。请先调用 get_skill，再重试本工具。踩坑后请用 update_skill 把经验写回共享 SKILL.md。' }] };
  }
  const u = buildUrl(name, args);
  if (!u) return { isError: true, content: [{ type: 'text', text: 'unknown tool: ' + name }] };
  const url = `http://${HOST}:${PORT}${u.path}${u.qs.length ? '?' + u.qs.join('&') : ''}`;
  try {
    const r = await httpGet(url);
    return { content: [{ type: 'text', text: JSON.stringify(r) }], isError: !r.ok };
  } catch (e) {
    return { isError: true, content: [{ type: 'text', text: 'helper unreachable (' + e.message + ') — 请确认 win-desktop-helper 已运行 (127.0.0.1:18800)' }] };
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
