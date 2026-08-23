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