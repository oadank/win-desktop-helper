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
    description: '按窗口标题关键词查询窗口 {hwnd,title,process,rect}，操作前定位用。查不到返回 ok:false',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { title: { type: 'string', description: '窗口标题关键词' } },
      required: ['title']
    }
  },
  {
    name: 'active_window',
    description: '获取当前前台活动窗口 {title,process,rect}。打字/按键前必须先确认目标在前台',
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
    description: '窗口管理: action=activate(置前)/maximize/minimize/restore/close/move(需x,y)/wait(等窗口出现,timeout毫秒)/list(按pid列窗口)。title=窗口标题关键词定位',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        action: { type: 'string', description: 'activate|maximize|minimize|restore|close|move|wait|list' },
        title: { type: 'string', description: '窗口标题关键词' },
        x: { type: 'number' }, y: { type: 'number' },
        timeout: { type: 'number', description: 'wait 的超时毫秒(默认10000)' },
        pid: { type: 'number', description: 'list 时按进程过滤' }
      },
      required: ['action', 'title']
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
    description: '点击（带坐标先移动再点）。button=left|right|middle，double=1 双击',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        x: { type: 'number' }, y: { type: 'number' },
        button: { type: 'string', description: 'left|right|middle' },
        double: { type: 'number', description: '0|1' }
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
    description: '滚轮：正数=向上滚，负数=向下滚（典型 ±120/格）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { delta: { type: 'number' } },
      required: ['delta']
    }
  },
  // ---- 键盘 ----
  {
    name: 'keyboard_type',
    description: '向当前聚焦输入框打字。中文/emoji 直接支持（Unicode 事件，不依赖输入法）。≤2000 字符。打字前先 active_window 确认前台',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { text: { type: 'string' } },
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
    description: '写文本到系统剪贴板。配合 keyboard_press ctrl+v 粘贴到任意输入框（比逐字打字快且稳）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { text: { type: 'string' } },
      required: ['text']
    }
  },
  {
    name: 'clipboard_history',
    description: '读取剪贴板历史（常驻监听，最多50条，最新在前）。给 AI 读取用户刚复制的内容。limit=返回条数(可选)',
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
    description: '语义点击控件（invoke/toggle 模式优先，失败回退坐标点击）。title=窗口标题, i=ui_tree 给出的元素下标',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' }, hwnd: { type: 'number' },
        i: { type: 'number', description: 'ui_tree 元素下标' }
      },
      required: ['i']
    }
  },
  {
    name: 'ui_read',
    description: '读单个控件详情（名称/值/类型/矩形）。title=窗口标题, i=元素下标',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' }, hwnd: { type: 'number' },
        i: { type: 'number' }
      },
      required: ['i']
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
    description: '语义写值到输入控件（ValuePattern 直写，不模拟键盘，稳且快）。title=窗口标题, i=元素下标, value=要写入的文本',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string' }, hwnd: { type: 'number' },
        i: { type: 'number' }, value: { type: 'string' }
      },
      required: ['i', 'value']
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
  // ---- 应用 ----
  {
    name: 'app_run',
    description: '运行程序/打开（exe/快捷方式/URL）。GUI 会在用户桌面可见',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { path: { type: 'string' }, args: { type: 'string' } },
      required: ['path']
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
    case 'window_info': return { path: '/window', qs: ['title=' + enc(a.title)] };
    case 'active_window': return { path: '/active', qs: [] };
    case 'monitors': return { path: '/monitors', qs: [] };
    case 'win_manage': {
      const act = a.action || 'activate';
      let qs = [];
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.x !== undefined) qs.push('x=' + a.x);
      if (a.y !== undefined) qs.push('y=' + a.y);
      if (a.timeout !== undefined) qs.push('timeout=' + a.timeout);
      if (a.pid !== undefined) qs.push('pid=' + a.pid);
      return { path: '/win/' + act, qs };
    }
    case 'mouse_move': return { path: '/mouse/move', qs: ['x=' + a.x, 'y=' + a.y] };
    case 'mouse_click': {
      let qs = [];
      if (a.x !== undefined && a.y !== undefined) qs.push('x=' + a.x, 'y=' + a.y);
      if (a.button) qs.push('button=' + a.button);
      if (a.double) qs.push('double=' + a.double);
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
    case 'mouse_scroll': return { path: '/mouse/scroll', qs: ['delta=' + a.delta] };
    case 'keyboard_type': return { path: '/keyboard/type', qs: ['text=' + enc(String(a.text))] };
    case 'keyboard_press': return { path: '/keyboard/press', qs: ['keys=' + enc(a.keys)] };
    case 'keyboard_hold': return { path: '/keyboard/hold', qs: ['keys=' + enc(a.keys), 'ms=' + (a.ms || 500)] };
    case 'clipboard_set': return { path: '/clipboard/set', qs: ['text=' + enc(String(a.text))] };
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
      qs.push('i=' + a.i);
      return { path: '/ui/click', qs };
    }
    case 'ui_read': {
      let qs = [];
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      qs.push('i=' + a.i);
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
      if (a.title !== undefined) qs.push('title=' + enc(a.title));
      if (a.hwnd !== undefined) qs.push('hwnd=' + a.hwnd);
      qs.push('i=' + a.i, 'value=' + enc(String(a.value)));
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
    case 'app_run': {
      let qs = ['path=' + enc(a.path)];
      if (a.args) qs.push('args=' + enc(a.args));
      return { path: '/app/run', qs };
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
    default: return null;
  }
}

async function callTool(name, args) {
  // SKILL 工具：返回 SKILL.md 全文（同目录，缺文件时回退内嵌简版）
  if (name === 'get_skill') {
    guideRead = true;
    let text = '';
    try { text = require('fs').readFileSync(require('path').join(__dirname, 'SKILL.md'), 'utf8'); }
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
    return { isError: true, content: [{ type: 'text', text: '⚠️ 本服务强制要求：首次操作前必须先调用 get_skill 获取 SKILL 操作手册与安全纪律（点前定位 / 语义优先 / 输入前确认前台 / 操作后验证 / 敏感操作确认）。请先调用 get_skill，再重试本工具。踩坑后请用 update_skill 把经验写回共享 SKILL.md。' }] };
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
