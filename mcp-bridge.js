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
  {
    "name": "cdp",
    "description": "Chromium/Electron 桌面应用「界面元素直读」(走它自带的远程调试端口 CDP)——不截图不 OCR，直接拿到按钮的名字/class/矩形，毫秒级。action=scan 扫哪些应用开了通道；targets 列某端口的页面；elements 出一张元素表(穿透影子根 shadowRoot —— WorkBuddy 的「Buddy加油站」菜单就在影子根里，普通查询只读到空字符串，这才是过去只能靠 OCR 的真原因)；eval 跑自定义只读 JS；click 页面内合成点击(需 allowClick=1)。本机已知端口: 9223=WorkBuddy, 9222=MiMo 桌面端。🔴 分工铁律: 读一切用本工具；**开外部浮层(账号菜单之类)必须用 mouse 工具真点物理坐标**，合成事件打不开它。坐标换算: 屏幕 = 窗口原点 + 页面坐标 × dpr(返回值带 dpr，配合 window 工具取窗口原点)。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": { "type": "string", "description": "scan|targets|elements|eval|click" },
        "port": { "type": "number", "description": "调试端口(9223=WorkBuddy)；scan 可省略" },
        "target": { "type": "number", "description": "第几个 page(默认 0；应用开多窗时先 targets 看清)" },
        "match": { "type": "string", "description": "elements 用：只回名字含这段文字的元素(子串，不区分大小写)" },
        "selector": { "type": "string", "description": "elements 用：CSS 选择器收窄(如 .fuel-btn)" },
        "expr": { "type": "string", "description": "eval 用：JS 表达式，回结构化值(默认只读，含写特征需 allowWrite=1)" },
        "x": { "type": "number", "description": "click 用：页面坐标(非屏幕坐标)" },
        "y": { "type": "number", "description": "click 用：页面坐标" },
        "limit": { "type": "number", "description": "elements 最多回几条(默认 60，防炸上下文)" },
        "timeout": { "type": "number", "description": "毫秒，默认 8000，上限 25000" },
        "allowWrite": { "type": "number", "description": "eval 跑含写特征的 JS 时必须 1" },
        "allowClick": { "type": "number", "description": "click 时必须 1" }
      },
      "required": ["action"]
    }
  },
  {
    "name": "window",
    "description": "窗口观察与管理总入口。action=active 取当前前台窗口{title,process,rect}；list 列全部可见窗口(hwnd/pid/process/title/front/rect, Z序)；info 按 title/process 查窗口；monitors 列显示器；state 读窗口真实状态(visible/minimized/maximized/foreground/responsive/rect, 零副作用, Electron 冻结窗也能回)；manage 做窗口操作(置前/最大化/最小化/还原/关闭/移动/半屏贴靠/等窗口出现)。定位永远先 list/info/state 再动手, 模糊匹配会误伤(建议带 process)。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": {
          "type": "string",
          "description": "active|list|info|monitors|state|manage"
        },
        "title": {
          "type": "string",
          "description": "窗口标题关键词(info/state/manage/wait 用)"
        },
        "process": {
          "type": "string",
          "description": "进程名过滤(如 Weixin/msedge, 忽略大小写可带 .exe)"
        },
        "hwnd": {
          "type": "number",
          "description": "窗口句柄(最可靠, 优先于 title)"
        },
        "pid": {
          "type": "number"
        },
        "verb": {
          "type": "string",
          "description": "manage 的动作: activate|maximize|minimize|restore|close|move|wait|list|listall"
        },
        "x": {
          "type": "number"
        },
        "y": {
          "type": "number"
        },
        "w": {
          "type": "number"
        },
        "h": {
          "type": "number"
        },
        "pos": {
          "type": "string",
          "description": "manage 贴靠: left|right|top|bottom|topleft|topright|bottomleft|bottomright|max|min|restore|sysleft|sysright|zthirdleft|zthirdmid|zthirdright"
        },
        "monitor": {
          "type": "string",
          "description": "第几块屏 1..n 或 next/prev"
        },
        "cols": {
          "type": "number"
        },
        "col": {
          "type": "number"
        },
        "colspan": {
          "type": "number"
        },
        "rows": {
          "type": "number"
        },
        "row": {
          "type": "number"
        },
        "rowspan": {
          "type": "number"
        },
        "layout": {
          "type": "number",
          "description": "Win+Z 布局数字键(本机三均分=6)"
        },
        "zone": {
          "type": "number",
          "description": "Win+Z 区域 1-9"
        },
        "esc": {
          "type": "number"
        },
        "fill": {
          "type": "string",
          "description": "Snap Assist 点选填位: 逗号分隔窗口标题/进程名"
        },
        "timeout": {
          "type": "number",
          "description": "manage action=wait 的超时毫秒"
        }
      },
      "required": [
        "action"
      ]
    }
  },
  {
    "name": "mouse",
    "description": "鼠标动作。action=move 移到(x,y)；click 点击(可选 double/triple/mods/front)；down|up 按下松开(配对做自定义拖拽)；drag 从(x1,y1)拖到(x2,y2)；pos 查当前坐标(只读)；scroll 滚轮(delta 正数向上, 可带 x/y 先移过去)。点击前先确认目标窗口在前台。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": {
          "type": "string",
          "description": "move|click|down|up|drag|pos|scroll"
        },
        "x": {
          "type": "number"
        },
        "y": {
          "type": "number"
        },
        "x1": {
          "type": "number"
        },
        "y1": {
          "type": "number"
        },
        "x2": {
          "type": "number"
        },
        "y2": {
          "type": "number"
        },
        "button": {
          "type": "string",
          "description": "left|right|middle"
        },
        "double": {
          "type": "number",
          "description": "1=双击"
        },
        "triple": {
          "type": "number",
          "description": "1=三连击(选整行)"
        },
        "mods": {
          "type": "string",
          "description": "按住修饰键点击: shift|ctrl|alt|win, 可组合"
        },
        "front": {
          "type": "number",
          "description": "1=落点非前台直接拒点"
        },
        "delta": {
          "type": "number",
          "description": "滚轮: 正数向上, 典型 ±120/格"
        }
      },
      "required": [
        "action"
      ]
    }
  },
  {
    "name": "keyboard",
    "description": "键盘输入。action=type 打一段文字(中文/emoji 免输入法, ≤2000 字, nl=enter 发裸回车, 默认 Shift+Enter 软换行)；press 按组合键(如 ctrl+shift+a / enter / alt+f4)；hold 按住组合键 ms 毫秒。打字前先用 window(action=state/active) 确认目标在前台。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": {
          "type": "string",
          "description": "type|press|hold"
        },
        "text": {
          "type": "string",
          "description": "type 的正文"
        },
        "nl": {
          "type": "string",
          "description": "enter=裸回车(默认 shift+enter 软换行)"
        },
        "keys": {
          "type": "string",
          "description": "press/hold 的组合键"
        },
        "ms": {
          "type": "number",
          "description": "hold 持续毫秒"
        }
      },
      "required": [
        "action"
      ]
    }
  },
  {
    "name": "clipboard",
    "description": "剪贴板。action=get 读当前文本(读选中文字=先 keyboard press ctrl+c 再 get)；set 写文本(keep_cr=1 保留回车, 默认把 \\r 归一为 \\n 免得聊天框误发送)；history 读历史(常驻监听, 最新在前, 可 limit)。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": {
          "type": "string",
          "description": "get|set|history"
        },
        "text": {
          "type": "string"
        },
        "keep_cr": {
          "type": "number"
        },
        "limit": {
          "type": "number"
        }
      },
      "required": [
        "action"
      ]
    }
  },
  {
    "name": "capture",
    "description": "截图与图片处理, 产物落盘并返回 PNG 路径。action=shot 截屏(region=all 全屏 / screen=N / x,y,w,h 矩形 / window=标题; axes=1 叠加屏幕坐标网格, AI 定位专用)；longshot 自动滚动拼接长图(需 x,y,w,h, 可 dir/max_screens/timeout_ms)；pin 把图钉到桌面；ocr 对图片跑本地 OCR(path, wait 超时毫秒)。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": {
          "type": "string",
          "description": "shot|longshot|pin|ocr"
        },
        "region": {
          "type": "string",
          "description": "all=全屏"
        },
        "screen": {
          "type": "number"
        },
        "x": {
          "type": "number"
        },
        "y": {
          "type": "number"
        },
        "w": {
          "type": "number"
        },
        "h": {
          "type": "number"
        },
        "window": {
          "type": "string",
          "description": "截指定窗口"
        },
        "axes": {
          "type": "number",
          "description": "1=叠坐标网格(50px 细线/100px 标数)"
        },
        "dir": {
          "type": "string",
          "description": "longshot 滚动方向 down|up|left|right"
        },
        "max_screens": {
          "type": "number"
        },
        "timeout_ms": {
          "type": "number"
        },
        "path": {
          "type": "string",
          "description": "pin/ocr 的图片路径"
        },
        "wait": {
          "type": "number",
          "description": "ocr 超时毫秒"
        }
      },
      "required": [
        "action"
      ]
    }
  },
  {
    "name": "ui",
    "description": "UIA 语义操作(优先于坐标点击)。action=tree 枚举控件；find 按 name/type 查控件(拿 ref, 跨调用不漂移)；click 点控件(优先 ref, 其次 name, i 索引会漂移且需 name 校验; verify=1 比像素, expect=点后应出现的文字)；read 读单控件详情；readall 读全部控件名称+值；set 写值到输入控件；select 设编辑控件选区(start/end)。Electron 应用可能不响应 UIA Invoke, 失败就改坐标点击。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": {
          "type": "string",
          "description": "tree|find|click|read|readall|set|select"
        },
        "title": {
          "type": "string"
        },
        "hwnd": {
          "type": "number"
        },
        "ref": {
          "type": "string",
          "description": "ui_find/ui_tree 返回的元素稳定引用(最稳)"
        },
        "i": {
          "type": "number",
          "description": "ui_tree 下标(下策, 跨调用必漂移)"
        },
        "name": {
          "type": "string"
        },
        "type": {
          "type": "string"
        },
        "mode": {
          "type": "string",
          "description": "coord=跳过 UIA 直接点控件中心"
        },
        "nohit": {
          "type": "string"
        },
        "verify": {
          "type": "string"
        },
        "expect": {
          "type": "string"
        },
        "force": {
          "type": "string"
        },
        "start": {
          "type": "number"
        },
        "end": {
          "type": "number"
        },
        "value": {
          "type": "string"
        }
      },
      "required": [
        "action"
      ]
    }
  },
  {
    "name": "record",
    "description": "录屏(ffmpeg 管道出 MP4)。action=start 开始(不带坐标=全屏, 可 x,y,w,h + fps 默认20)；stop 停止并返回 MP4 路径；status 查状态(是否在录/已录秒数/文件)。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": {
          "type": "string",
          "description": "start|stop|status"
        },
        "x": {
          "type": "number"
        },
        "y": {
          "type": "number"
        },
        "w": {
          "type": "number"
        },
        "h": {
          "type": "number"
        },
        "fps": {
          "type": "number"
        }
      },
      "required": [
        "action"
      ]
    }
  },
  {
    "name": "app",
    "description": "应用与托盘。action=run 启动程序或打开URL(path, 可 args/wait/process)；runas 管理员启动(触发 UAC 需用户确认)；restore 深度恢复应用窗口(关窗→托盘双击重开→贴回原位, 治 Electron 假激活无响应/无窗口); tray 托盘图标点击唤回(name, 可 relaunch=1 直接重启 exe)。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": {
          "type": "string",
          "description": "run|runas|restore|tray"
        },
        "path": {
          "type": "string"
        },
        "args": {
          "type": "string"
        },
        "wait": {
          "type": "number"
        },
        "process": {
          "type": "string"
        },
        "title": {
          "type": "string"
        },
        "hwnd": {
          "type": "number"
        },
        "snap": {
          "type": "string"
        },
        "name": {
          "type": "string",
          "description": "tray 的图标名"
        },
        "relaunch": {
          "type": "number"
        },
        "button": {
          "type": "string"
        },
        "double": {
          "type": "number"
        }
      },
      "required": [
        "action"
      ]
    }
  },
  {
    "name": "desk_skill",
    "description": "本服务操作手册(共享经验库)。action=get 取手册(不带参数=主手册; app=应用名直达该应用小册子; topic=关键词抽段; detail=full 考古); action=update 把新踩的坑写回(title/entry 必填, app=落对应小册子, supersedes=推翻哪条旧经验)。动手类操作前必须先 get 读纪律。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "action": {
          "type": "string",
          "description": "get|update"
        },
        "app": {
          "type": "string",
          "description": "应用名, 如 WorkBuddy/douyin/notepad"
        },
        "topic": {
          "type": "string",
          "description": "get 的抽段关键词"
        },
        "detail": {
          "type": "string",
          "description": "full=主手册+完整历史"
        },
        "title": {
          "type": "string"
        },
        "entry": {
          "type": "string"
        },
        "supersedes": {
          "type": "string"
        },
        "as_of": {
          "type": "string"
        }
      },
      "required": [
        "action"
      ]
    }
  },
  {
    "name": "wait_for",
    "description": "等窗口里出现指定文字(或 disappear=1 等它消失)才返回, 替代固定 sleep 后截图。text 子串匹配(别写整句长文本)，hwnd 优先(桌面共享句柄会变)，timeout 默认 8000 上限 25000，poll 默认 400。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "text": {
          "type": "string"
        },
        "hwnd": {
          "type": "number"
        },
        "title": {
          "type": "string"
        },
        "timeout": {
          "type": "number"
        },
        "poll": {
          "type": "number"
        },
        "disappear": {
          "type": "number"
        },
        "action": {
          "type": "string",
          "description": "可省略(本工具只有一个动作)"
        }
      },
      "required": []
    }
  },
  {
    "name": "pick_config",
    "description": "划词悬浮球配置(常驻功能, 取代豆包划词)。不带参数=查当前状态；enabled=0/1 开关(立即生效并持久化)；askEndpoint/askKey/askModel 改问AI后端；askPrompt 定制回答风格(传 | 清空回默认)。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "enabled": {
          "type": "number"
        },
        "askEndpoint": {
          "type": "string"
        },
        "askKey": {
          "type": "string"
        },
        "askModel": {
          "type": "string"
        },
        "askPrompt": {
          "type": "string"
        },
        "action": {
          "type": "string",
          "description": "可省略(本工具只有一个动作)"
        }
      },
      "required": []
    }
  },
  {
    "name": "taskbar_volume",
    "description": "任务栏滚轮调音量(常驻功能)。不带参数=查状态；enabled=0/1 开关，step 每次滚轮音量变化百分比(1-20, 默认2)，reverse=1 反向。",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "enabled": {
          "type": "number"
        },
        "step": {
          "type": "number"
        },
        "reverse": {
          "type": "number"
        },
        "action": {
          "type": "string",
          "description": "可省略(本工具只有一个动作)"
        }
      },
      "required": []
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
  capture: 200000, window: 30000, ui: 30000, mouse: 30000, keyboard: 25000,
  clipboard: 20000, record: 30000, app: 45000, desk_skill: 20000,
  wait_for: 65000, pick_config: 15000, taskbar_volume: 15000,
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
        catch (e) {
          // [2026-09-22 工具合并时发现的老 bug] 后端 /taskbar-volume 把坐标输出成 "pt":1418,1036（裸逗号，
          // 少引号也不是数组）→ 整条响应非法 JSON，查音量状态一直报 bad json from helper。
          // 桥侧定点修复，不动 C# 服务（改 .cs 要重编译 exe，代价大收益小）：把两个裸数字包成字符串。
          try { resolve(JSON.parse(data.replace(/"pt":\s*(\d+)\s*,\s*(\d+)/g, '"pt":"$1,$2"'))); return } catch { /* 修不好才报原错 */ }
          reject(new Error('bad json from helper: ' + data.slice(0, 200)));
        }
      });
    });
    req.on('error', reject);
    req.setTimeout(tmo, () => { req.destroy(new Error('helper request timeout after ' + tmo + 'ms')); });
  });
}

// ===== 2026-09-22 工具合并层(42 -> 12): action -> 旧工具名, 旧映射整体保留在 buildUrlLegacy =====
const DEFAULT_ACTION = { wait_for: 'wait', pick_config: 'get', taskbar_volume: 'get', cdp: 'targets' };
const MERGE = {
  window: { active: 'active_window', list: 'list_apps', info: 'window_info', monitors: 'monitors', state: 'window_state', manage: 'win_manage' },
  mouse: { move: 'mouse_move', click: 'mouse_click', down: 'mouse_down', up: 'mouse_up', drag: 'mouse_drag', pos: 'mouse_pos', scroll: 'mouse_scroll' },
  keyboard: { type: 'keyboard_type', press: 'keyboard_press', hold: 'keyboard_hold' },
  clipboard: { get: 'clipboard_get', set: 'clipboard_set', history: 'clipboard_history' },
  capture: { shot: 'screen_capture', longshot: 'longshot', pin: 'pin_image', ocr: 'ocr_image' },
  ui: { tree: 'ui_tree', find: 'ui_find', click: 'ui_click', read: 'ui_read', readall: 'ui_readall', set: 'ui_set', select: 'ui_select' },
  record: { start: 'record_start', stop: 'record_stop', status: 'record_status' },
  app: { run: 'app_run', runas: 'app_runas', restore: 'app_restore', tray: 'tray_click' },
  desk_skill: { get: 'get_skill', update: 'update_skill' },
  wait_for: { wait: 'wait_for' },
  pick_config: { get: 'pick_config', set: 'pick_config', config: 'pick_config' },
  taskbar_volume: { get: 'taskbar_volume', set: 'taskbar_volume' },
};
// 返回 null = 工具名或 action 不认识; callTool 据此报错, 不会静默假成功。
function buildUrl(name, a) {
  a = a || {};
  const map = MERGE[name];
  if (!map) return buildUrlLegacy(name, a);   // 兼容外部脚本直接使用旧工具名
  const act = String(a.action || DEFAULT_ACTION[name] || '');
  const legacy = map[act];
  if (!legacy) return null;
  return buildUrlLegacy(legacy, a);
}

function buildUrlLegacy(name, a) {
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
  if (name === 'desk_skill' && String((args && args.action) || 'get') === 'get') {
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
  if (name === 'desk_skill' && String((args && args.action) || '') === 'update') {
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
    return { isError: true, severity: 'self_heal', content: [{ type: 'text', text: guideRead ? bad : bad + '\n（另：本服务要求首次操作前先调用 desk_skill(action="get") 读手册，改完参数顺手把它调了）' }] };
  }
  // 强制闸门：**动手类**工具首次调用前必须先读 SKILL。
  // 2026-09-19 分级(老大拍板 A 方案): 纯观察工具不再拦 —— 看一眼屏幕不破坏任何东西, 拦它只是逼 agent 先吐 4.8K 字手册。
  // 仍然拦的: 一切点击/输入/窗口变更; UIA 枚举(ui_tree/ui_find 会物化整棵树, 在 Electron 上真能把服务拖挂, 必须先懂纪律);
  //          截图与录屏(会落盘产生文件); ui_* 读控件(依赖前置定位纪律, 不放行)。
  // 2026-09-22 工具合并后: 豁免不再看工具名, 改看 (工具, action) 是否只读 ——
  // window 一个工具既含"看"又含"动", 只看名字会把 list/info 也拦下。
  const isReadOnlyCall = (n, a) => {
    const act = String((a && a.action) || DEFAULT_ACTION[n] || '');
    if (n === 'window') return ['active', 'list', 'info', 'monitors', 'state'].includes(act);
    if (n === 'mouse') return act === 'pos';
    if (n === 'clipboard') return act === 'get' || act === 'history';
    if (n === 'record') return act === 'status';
    if (n === 'desk_skill') return act === 'get';
    if (n === 'cdp') return ['scan', 'targets', 'elements', 'eval'].includes(act);   // click 不放行：真改界面
    if (n === 'pick_config' || n === 'taskbar_volume') return true;
    return false;   // capture/ui/keyboard/app 一律先读手册
  };
  if (!guideRead && !isReadOnlyCall(name, args)) {
    return { isError: true, severity: 'self_heal', content: [{ type: 'text', text: '⚠️ 本服务强制要求：动手之前必须先调用 desk_skill(action="get")（无参数, 现在只要 4.8K 字）获取操作手册与安全纪律（点前定位 / 语义优先 / 输入前确认前台 / 操作后验证 / 敏感操作确认）。请先调用 desk_skill(action="get")，再重试本工具。踩坑后用 update_skill 写回，记得带 app=。' }] };
  }
// ===== cdp: Chromium/Electron 界面元素直读（不截图不 OCR，走应用自带的远程调试端口） =====
  if (name === 'cdp') {
    const a = args || {};
    const act = String(a.action || 'targets');
    const tmo = Math.min(25000, Math.max(1000, Number(a.timeout) || 8000));
    const wrap = (o) => ({ content: [{ type: 'text', text: typeof o === 'string' ? o : JSON.stringify(o) }] });
    const bad = (m, sev) => ({ isError: true, severity: sev || 'self_heal', content: [{ type: 'text', text: m }] });
    const cdpCmd = (wsUrl, method, params) => new Promise((res, rej) => {
      let ws; try { ws = new WebSocket(wsUrl); } catch (e) { return rej(e); }
      let done = false;
      const fin = (fn, arg) => { if (done) return; done = true; clearTimeout(t); try { ws.close(); } catch (e) {} fn(arg); };
      const t = setTimeout(() => fin(rej, new Error('CDP 无回包(' + tmo + 'ms) —— 若你刚发过 Runtime.enable，事件流会淹掉回包；换 target 再试')), tmo);
      ws.onopen = () => { try { ws.send(JSON.stringify({ id: 1, method, params: params || {} })); } catch (e) { fin(rej, e); } };
      ws.onmessage = (ev) => { let m; try { m = JSON.parse(String(ev.data)); } catch (e) { return; }
        if (m.id !== 1) return;
        if (m.error) return fin(rej, new Error(method + ': ' + (m.error.message || JSON.stringify(m.error))));
        fin(res, m.result); };
      ws.onerror = (e) => fin(rej, new Error('WebSocket 错误: ' + ((e && e.message) || '连不上该端口')));
    });
    const evalIn = async (p, idx, expr) => {
      const list = await httpGet('http://127.0.0.1:' + p + '/json', tmo);
      const pages = (Array.isArray(list) ? list : []).filter((x) => x && x.webSocketDebuggerUrl && x.type === 'page');
      if (!pages.length) throw new Error('端口 ' + p + ' 上没有可调试页面（应用没开远程调试端口，或只有扩展后台页）');
      const one = pages[Math.max(0, Math.min(pages.length - 1, Number(idx) || 0))];
      const r = await cdpCmd(one.webSocketDebuggerUrl, 'Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
      if (r && r.exceptionDetails) throw new Error('页面 JS 抛错: ' + ((r.exceptionDetails.exception && r.exceptionDetails.exception.message) || r.exceptionDetails.text));
      return { value: r && r.result ? r.result.value : null, pages: pages.length, page: (one.title || one.url || '').slice(0, 60) };
    };
    try {
      if (act === 'scan') {
        const cand = [9222, 9223, 9224, 9225, 9229, 9333, 21222, 5500];
        const found = [];
        const one = async (p) => { try { const v = await httpGet('http://127.0.0.1:' + p + '/json/version', 1500);
          const l = await httpGet('http://127.0.0.1:' + p + '/json', 1500);
          found.push({ port: p, browser: v.Browser || v.product || '', pages: (Array.isArray(l) ? l : []).filter((x) => x && x.type === 'page').length }); } catch (e) {} };
        await Promise.all(cand.map(one));
        return wrap({ ok: true, listening: found, hint: found.length ? '对其中某个端口用 action=elements 出元素表' : '没扫到开调试端口的应用；已知: WorkBuddy=9223, MiMo 桌面端=9222' });
      }
      if (!Number(a.port)) return bad('cdp action=' + act + ' 需要 port（如 9223=WorkBuddy）。不知道哪个端口就先 action=scan 扫一遍。');
      if (act === 'targets') {
        const l = await httpGet('http://127.0.0.1:' + a.port + '/json', tmo);
        return wrap({ ok: true, port: Number(a.port), pages: (Array.isArray(l) ? l : []).map((x, i) => ({ i, type: x.type, title: (x.title || '').slice(0, 60), url: (x.url || '').slice(0, 80) })) });
      }
      if (act === 'elements' || act === 'eval') {
        const preset = act === 'elements';
        const limit = Math.min(300, Math.max(5, Number(a.limit) || 60));
        let expr;
        if (preset) {
          const mt = JSON.stringify(String(a.match || '')); const sl = JSON.stringify(String(a.selector || ''));
          expr = "(() => { const dpr = devicePixelRatio || 1, mt = " + mt + ".toLowerCase(), sel = " + sl + "; const out = []; const seen = new Set();" +
            " const walk = (root, d) => { if (!root || !root.querySelectorAll || d > 9) return; for (const e of root.querySelectorAll(sel || '*')) {" +
            " const r = e.getBoundingClientRect(); if (r.width > 3 && r.height > 3) {" +
            " const own = [...e.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent.trim()).join(' ');" +
            " const nm = (own || e.getAttribute('aria-label') || e.getAttribute('title') || '').trim().replace(/\\s+/g,' ');" +
            " const cls = String((e.className && e.className.baseVal !== undefined) ? e.className.baseVal : (e.className || ''));" +
            " const label = nm || (/^(path|use|svg|img)$/i.test(e.tagName) ? cls.slice(0,24) : '');" +
            " const k = label + '|' + cls + '|' + Math.round(r.x) + ',' + Math.round(r.y);" +
            " if (label && label.length <= 40 && !seen.has(k)) { seen.add(k);" +
            " if (!mt || label.toLowerCase().indexOf(mt) >= 0 || cls.toLowerCase().indexOf(mt) >= 0) {" +
            " out.push({ name: label, tag: e.tagName + (e.getAttribute('role') ? '[role=' + e.getAttribute('role') + ']' : ''), cls: cls.slice(0,36)," +
            " x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height)," +
            " px: Math.round((r.x + r.width/2) * dpr), py: Math.round((r.y + r.height/2) * dpr) }); if (out.length > " + limit + ") return; } } }" +
            " if (e.shadowRoot) walk(e.shadowRoot, d + 1); } }; walk(document, 0);" +
            " return JSON.stringify({ dpr: dpr, viewport: innerWidth + 'x' + innerHeight, count: out.length, items: out.slice(0, " + limit + ") }); })()"
        } else {
          expr = String(a.expr || '');
          if (!expr.trim()) return bad('action=eval 需要 expr（一段 JS 表达式，要 return 值就写成 (() => {...})() 形式）。');
          const WRITE = /(dispatchEvent|\.click\s*\(|innerHTML|outerHTML|insertAdjacent|removeChild|appendChild|localStorage|sessionStorage|document\.cookie|location\s*=|location\.(assign|replace|reload)|fetch\s*\(|XMLHttpRequest|new WebSocket|Runtime\.enable|Page\.navigate|Input\.dispatch|Network\.|Target\.|document\.write|\.submit\s*\(|\.focus\s*\(|execCommand|navigator\.(clipboard|permissions|geolocation)|window\.open|eval\s*\(|new Function)/i;
          if (WRITE.test(expr) && Number(a.allowWrite) !== 1) {
            return bad('🔴 这段 JS 含**改界面/发请求**的特征，默认拦下（cdp 能执行任意 JS = 能改这个应用的一切）。' +
              '确认要跑就带 allowWrite=1 重试；只是想看界面就改用 action=elements，或把 expr 换成只读查询（读 rect / innerText / class）。', 'need_user');
          }
        }
        const r = await evalIn(Number(a.port), a.target, expr);
        let v = r.value;
        if (typeof v === 'string') { try { v = JSON.parse(v); } catch (e) {} }
        const note = preset ? 'px/py = 窗口内物理坐标（页面坐标 × dpr）；屏幕绝对坐标 = 窗口原点 + px/py，用 window 工具取原点。' : '';
        if (preset && v && Array.isArray(v.items)) {
          const dropped = Math.max(0, (v.count || 0) - v.items.length);
          return wrap({ ok: true, port: Number(a.port), page: r.page, dpr: v.dpr, viewport: v.viewport, matched: v.items.length, truncated: dropped, items: v.items, note: note + (dropped ? '（还有 ' + dropped + ' 条被 limit 截掉，加 match/selector 收窄）' : '') });
        }
        return wrap({ ok: true, port: Number(a.port), page: r.page, value: v, note });
      }
      if (act === 'click') {
        if (Number(a.allowClick) !== 1) return bad('action=click 会真改这个应用的界面，必须显式带 allowClick=1（并且这是敏感操作，先跟用户确认落点）。');
        const x = Number(a.x), y = Number(a.y);
        if (!isFinite(x) || !isFinite(y)) return bad('click 需要页面坐标 x、y（来自 elements 的 x + w/2、y + h/2，**不是** px/py 那套物理坐标）。');
        const list = await httpGet('http://127.0.0.1:' + a.port + '/json', tmo);
        const pages = (Array.isArray(list) ? list : []).filter((p) => p && p.webSocketDebuggerUrl && p.type === 'page');
        if (!pages.length) return bad('端口 ' + a.port + ' 上没有可调试页面');
        const one = pages[Math.max(0, Math.min(pages.length - 1, Number(a.target) || 0))];
        for (const ev of [{ type: 'mouseMoved', x, y, button: 'none' }, { type: 'mousePressed', x, y, button: 'left', clickCount: 1, buttons: 1 }, { type: 'mouseReleased', x, y, button: 'left', clickCount: 1, buttons: 0 }]) {
          await cdpCmd(one.webSocketDebuggerUrl, 'Input.dispatchMouseEvent', ev);
        }
        return wrap({ ok: true, port: Number(a.port), page: (one.title || one.url || '').slice(0, 60), clicked: { x, y }, warn: '合成点击只对**页面内部**元素有效；外部浮层（如 WorkBuddy 账号菜单）不吃这套 —— 那要用 mouse 工具点物理坐标。点完请再 elements 回读验证，别当已生效。' });
      }
      return bad('cdp action 只认 scan|targets|elements|eval|click，收到 ' + act);
    } catch (e) {
      const m = String((e && e.message) || e);
      if (/ECONNREFUSED|connect|socket|ENOENT|404/i.test(m)) return bad('连不上端口 ' + (a.port || '?') + ': ' + m + ' —— 该应用没开调试端口（或端口不对）。先 action=scan。', 'dead_end');
      return bad('cdp ' + act + ' 失败: ' + m);
    }
  }
  const u = buildUrl(name, args);
  if (!u) return { isError: true, content: [{ type: 'text', text: 'unknown tool/action: ' + name + (args && args.action ? ' action=' + args.action : '') + '（先 tools/list 核对工具名与 action 取值）' }] };
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
