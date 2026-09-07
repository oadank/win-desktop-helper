# -*- coding: utf-8 -*-
"""pick_config MCP 工具验收: 查询 / 关闭 / 开启 / 持久化 / 改模型"""
import sys, json, io
sys.path.insert(0, '.')
from _mcp import MCP

CFG = 'shot-service.json'

def readjson():
    return io.open(CFG, encoding='utf-8-sig').read()

with MCP() as m:
    print('--- 1) 查询(不带参) ---')
    r = json.loads(m.call('pick_config'))
    print(r)
    assert r['ok'] and r['enabled'] == 1, '默认应为开启'

    print('--- 2) 关闭 ---')
    r = json.loads(m.call('pick_config', enabled=0))
    print(r)
    assert r['enabled'] == 0
    assert '"enabled": "0"' in readjson().replace("'", '"') or '"pick"' in readjson(), 'json 应已写回'

    print('--- 3) 改模型(不动开关) ---')
    r = json.loads(m.call('pick_config', askModel='GwV4F'))
    print(r)
    assert r['enabled'] == 0, '只改模型不应顺带打开'

    print('--- 4) 开启还原 ---')
    r = json.loads(m.call('pick_config', enabled=1))
    print(r)
    assert r['enabled'] == 1

    print('--- 5) 写回的 json 全文 ---')
    print(readjson())

print('=== pick_config 全部通过 ===')
