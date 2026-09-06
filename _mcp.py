#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""win-desktop-helper MCP stdio 调用器 (session 1 真实操作)
用法:
  from _mcp import MCP
  with MCP() as m:
      m.call('screen_capture', region='all')
      m.call('ui_tree', hwnd=123)
命令行: python _mcp.py tool arg=val arg2=val2
"""
import subprocess, json, sys, os

NODE = 'C:/Users/oadan/.workbuddy/binaries/node/versions/22.22.2-2/node.exe'
CWD = os.path.dirname(os.path.abspath(__file__))


class MCP(object):
    def __init__(self):
        self.p = subprocess.Popen(
            [NODE, 'mcp-bridge.js'], cwd=CWD,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding='utf-8')
        self._id = 0
        self.send('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                                 'clientInfo': {'name': 'helper', 'version': '1.0'}}, mid=1)
        self.recv()
        self.p.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n')
        self.p.stdin.flush()
        self.call('get_skill')  # 握手, 某些 bridge 要求

    def send(self, method, params, mid=None):
        self._id += 1
        obj = {'jsonrpc': '2.0', 'id': mid or self._id, 'method': method, 'params': params}
        self.p.stdin.write(json.dumps(obj) + '\n')
        self.p.stdin.flush()

    def recv(self):
        line = self.p.stdout.readline()
        return json.loads(line) if line.strip() else None

    def call(self, tool, **kw):
        self._id += 1
        self.send('tools/call', {'name': tool, 'arguments': kw}, mid=self._id)
        r = self.recv()
        if not r:
            return {'error': 'no response'}
        if 'error' in r:
            return {'error': r['error']}
        res = r.get('result', {})
        cont = res.get('content') or []
        if cont and isinstance(cont[0], dict) and 'text' in cont[0]:
            return cont[0]['text']
        return res

    def close(self):
        try:
            self.p.terminate()
        except Exception:
            pass

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()


def jcall(tool, **kw):
    """一次性调用"""
    with MCP() as m:
        return m.call(tool, **kw)


if __name__ == '__main__':
    tool = sys.argv[1]
    kw = {}
    for a in sys.argv[2:]:
        if '=' in a:
            k, v = a.split('=', 1)
            try:
                v = json.loads(v)
            except Exception:
                pass
            kw[k] = v
    out = jcall(tool, **kw)
    print(out if isinstance(out, str) else json.dumps(out, ensure_ascii=False))
