#!/usr/bin/env node
// mcp-bridge.js — Win Desktop Helper 的 MCP (Model Context Protocol) stdio 服务
// 让任意 MCP 客户端（Claude Desktop / Cursor / 等）获得 看+动+运行 工具能力
// 传输: stdio JSON-RPC 2.0 (每行一条消息); 内部转发到 HTTP 127.0.0.1:18800
// 用法: node mcp-bridge.js   （在 MCP 客户端配置为 command: node, args: [本文件路径]）
// 零依赖：手写协议，无需 npm install
'use strict';

const HOST = '127.0.0.1';
const PORT = 18800;
const VERSION = '1.0.0';

let guideRead = false; // 强制闸门: 首次操作前必须先读 get_usage_guide

// 工具定义（名称/说明/参数 —— 与 HTTP API 一一对应）
const TOOLS = [
  {
    name: 'screen_capture',
    description: '截取用户桌面指定区域，返回保存的 PNG 文件路径。region=all 全屏(默认)；screen=0 指定显示器；x,y,w,h 任意矩形；window=窗口标题关键词',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: {
        region: { type: 'string', description: 'all | 忽略表示全屏' },
        screen: { type: 'number', description: '显示器下标' },
        x: { type: 'number' }, y: { type: 'number' }, w: { type: 'number' }, h: { type: 'number' },
        window: { type: 'string', description: '窗口标题关键词（截该窗口区域）' }
      }
    }
  },
  {
    name: 'window_info',
    description: '按窗口标题关键词查询窗口信息 {hwnd,title,process,rect}，操作前定位用。查不到返回 ok:false',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { title: { type: 'string', description: '窗口标题关键词' } },
      required: ['title']
    }
  },
  {
    name: 'active_window',
    description: '获取当前活动窗口信息 {title,process,rect}',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
  {
    name: 'monitors',
    description: '列出显示器元数据（分辨率/主屏/设备名）',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
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
    name: 'mouse_scroll',
    description: '滚轮：正数=向上滚，负数=向下滚（典型 ±120）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { delta: { type: 'number' } },
      required: ['delta']
    }
  },
  {
    name: 'keyboard_type',
    description: '向当前聚焦输入框打字。中文/emoji 直接支持（Unicode 事件，不依赖输入法）。≤2000 字符',
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
    name: 'app_run',
    description: '运行程序/打开（exe/快捷方式/URL）。GUI 会在用户桌面可见',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: { path: { type: 'string' }, args: { type: 'string' } },
      required: ['path']
    }
  },
  {
    name: 'get_usage_guide',
    description: '获取本服务的完整操作手册（操作铁律、避坑速查、标准流程模板）。【必须最先调用】本服务所有工具在调用前都强制要求先调用本工具读取操作手册，否则工具会返回错误。',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  },
  {
    name: 'update_usage_guide',
    description: '把新踩坑经验写回共享操作手册 OPERATING_GUIDE.md（全体 agent 共享，立即生效）。【约定：每次执行操作踩坑后必须调用本工具记录】，不要只记在自己的记忆里。参数 title=小节标题，entry=经验正文（markdown）',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        title: { type: 'string', description: '小节标题，如 "2026-08-23 窗口定位坑"' },
        entry: { type: 'string', description: '经验正文（markdown，含现象/原因/解法）' }
      },
      required: ['title', 'entry']
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
    req.setTimeout(15000, () => { req.destroy(new Error('helper request timeout')); });
  });
}

function buildUrl(name, args) {
  const a = args || {};
  switch (name) {
    case 'screen_capture': {
      let qs = [];
      if (a.screen !== undefined) qs.push('screen=' + a.screen);
      else if (a.x !== undefined && a.y !== undefined && a.w !== undefined && a.h !== undefined) qs.push('x=' + a.x, 'y=' + a.y, 'w=' + a.w, 'h=' + a.h);
      else if (a.window) qs.push('window=' + encodeURIComponent(a.window));
      else qs.push('region=all');
      return { path: '/shot', qs };
    }
    case 'window_info': return { path: '/window', qs: ['title=' + encodeURIComponent(a.title)] };
    case 'active_window': return { path: '/active', qs: [] };
    case 'monitors': return { path: '/monitors', qs: [] };
    case 'mouse_move': return { path: '/mouse/move', qs: ['x=' + a.x, 'y=' + a.y] };
    case 'mouse_click': {
      let qs = [];
      if (a.x !== undefined && a.y !== undefined) qs.push('x=' + a.x, 'y=' + a.y);
      if (a.button) qs.push('button=' + a.button);
      if (a.double) qs.push('double=' + a.double);
      return { path: '/mouse/click', qs };
    }
    case 'mouse_scroll': return { path: '/mouse/scroll', qs: ['delta=' + a.delta] };
    case 'keyboard_type': return { path: '/keyboard/type', qs: ['text=' + encodeURIComponent(String(a.text))] };
    case 'keyboard_press': return { path: '/keyboard/press', qs: ['keys=' + encodeURIComponent(a.keys)] };
    case 'app_run': {
      let qs = ['path=' + encodeURIComponent(a.path)];
      if (a.args) qs.push('args=' + encodeURIComponent(a.args));
      return { path: '/app/run', qs };
    }
    default: return null;
  }
}

async function callTool(name, args) {
  // 说明书工具：返回操作手册全文（同目录 OPERATING_GUIDE.md，缺文件时回退内嵌简版）
  if (name === 'get_usage_guide') {
    guideRead = true;
    let text = '';
    try { text = require('fs').readFileSync(require('path').join(__dirname, 'OPERATING_GUIDE.md'), 'utf8'); }
    catch (e) {
      text = '【Win Desktop Helper 操作手册·简版】\n' +
             '1. 点任何东西前先 window_info/active_window 定位并确认前台；\n' +
             '2. keyboard_type 发给当前前台窗口，输入前必须 active_window 确认目标；\n' +
             '3. 操作后立即文件系统验证（保存对话框默认位置≠你以为的位置）；\n' +
             '4. 启动程序/弹窗后等 1-3s 再截图确认，关键节点截图验证；\n' +
             '5. 删除/发送等敏感操作先经用户对话确认；\n' +
             '6. 点不动/找不到时先 active_window+全屏截图看真实状态，别盲试。\n' +
             '（完整版见仓库 OPERATING_GUIDE.md）';
    }
    return { content: [{ type: 'text', text }] };
  }
  // 写回工具：踩坑经验 append 进共享手册（全体 agent 可见）
  if (name === 'update_usage_guide') {
    const fs = require('fs'), path = require('path');
    const file = path.join(__dirname, 'OPERATING_GUIDE.md');
    try {
      const entry = '\n## ' + (args.title || '经验补充') + '\n\n' + (args.entry || '') + '\n';
      fs.appendFileSync(file, entry, 'utf8');
      return { content: [{ type: 'text', text: '已写入共享手册: ' + file + '（下次任何 agent 调用 get_usage_guide 即可读到新经验）' }] };
    } catch (e) { return { isError: true, content: [{ type: 'text', text: '写入失败: ' + e.message }] }; }
  }
  // 强制闸门：所有工具（含观察类）首次调用前必须先读手册
  if (!guideRead) {
    return { isError: true, content: [{ type: 'text', text: '⚠️ 本服务强制要求：首次操作前必须先调用 get_usage_guide 获取操作手册与安全纪律（点前定位 / 输入前确认前台 / 操作后验证 / 敏感操作确认）。请先调用 get_usage_guide，再重试本工具。踩坑后请用 update_usage_guide 把经验写回共享手册。' }] };
  }
  const u = buildUrl(name, args);
  if (!u) return { isError: true, content: [{ type: 'text', text: 'unknown tool: ' + name }] };
  const url = `http://${HOST}:${PORT}${u.path}?${u.qs.join('&')}`;
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