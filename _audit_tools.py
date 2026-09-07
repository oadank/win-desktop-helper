# -*- coding: utf-8 -*-
"""桥接三处名册交叉校验:
  1. TOOLS 清单 (AI 能看到的工具)
  2. buildUrl 转发映射 (bridge case)
  3. 服务端 switch case + HTTP 路由 (shot-service.cs)
任一对不上 = 断链工具 (和 get_skill/tray_click 同款坑)
"""
import io
import re

BR = io.open('mcp-bridge.js', encoding='utf-8').read()
SV = io.open('shot-service.cs', encoding='utf-8').read()

# 1. TOOLS 数组范围
m = re.search(r'const TOOLS\s*=\s*\[(.*?)\n\];', BR, re.S)
tools_block = m.group(1) if m else ''
tools = re.findall(r"^\s*name:\s*'([a-z_0-9]+)'", tools_block, re.M)

# 2. buildUrl 的 case
m2 = re.search(r'function buildUrl\(name, a\)\s*\{(.*?)\n\}\n', BR, re.S)
bu_block = m2.group(1) if m2 else ''
build = re.findall(r"case\s+'([a-z_0-9]+)':", bu_block)

# 3. 服务端 switch case
m3 = re.search(r'switch \(name\)\s*\{(.*)\n        \}\n        catch', SV, re.S)
sv_block = m3.group(1) if m3 else ''
svcase = re.findall(r'case\s+"([a-z_0-9]+)":', sv_block)

# 4. 服务端 HTTP 路由
http = set(re.findall(r'path\s*==\s*"(/[a-z_0-9/-]+)"', SV)) | set(re.findall(r'path\.StartsWith\("(/[a-z_0-9/-]+)', SV))

print('TOOLS 清单 %d 个 | buildUrl 转发 %d 个 | 服务端 case %d 个' % (len(tools), len(build), len(svcase)))
print()

# 断链 1: AI 看得到但调用就 unknown tool
d1 = [t for t in tools if t not in build]
d2 = [t for t in build if t not in tools]          # 转发有了但 AI 看不到 = 等于不存在
d3 = [t for t in tools if t not in svcase]          # 服务端没实现 (可能走特殊路径如 get_skill/update_skill)
d4 = [c for c in svcase if c not in tools]          # 服务端实现了但 AI 看不到

print('[A] TOOLS 有 / buildUrl 没有 -> AI 调用直接 unknown tool:')
print('   ', d1 or '无')
print('[B] buildUrl 有 / TOOLS 没有 -> AI 根本看不到这个工具:')
print('   ', d2 or '无')
print('[C] TOOLS 有 / 服务端 case 没有 -> (get_skill/update_skill 等 bridge 本地实现属正常):')
print('   ', d3 or '无')
print('[D] 服务端实现了 / TOOLS 没有 -> AI 看不到:')
print('   ', d4 or '无')
print()

# HTTP 路由 vs buildUrl 路径
print('[E] buildUrl 指向的路径, 服务端 HTTP 没有的:')
missing = []
for t in build:
    seg = re.search(r"case\s+'%s':(.*?)(?=\n    case|\n  \})" % re.escape(t), bu_block, re.S)
    if not seg:
        continue
    paths = re.findall(r"path:\s*'([^']+)'", seg.group(1))
    for p in paths:
        base = p.split('${')[0].rstrip('/') or p
        if p.startswith('/') and '+' not in p and p not in http:
            missing.append((t, p))
print('   ', missing or '无')
