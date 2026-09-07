# -*- coding: utf-8 -*-
"""pick_config: 1) shot-service.cs 加 HTTP /pick-config  2) mcp-bridge.js 注册工具+路由"""
import io

def rep(path, old, new, n=1):
    s = io.open(path, encoding='utf-8').read()
    c = s.count(old)
    assert c == n, 'anchor %r found %d times in %s (expect %d)' % (old[:50], c, path, n)
    io.open(path, 'w', encoding='utf-8', newline='\n').write(s.replace(old, new))
    print('patched', path, '<-', old[:40].replace('\n', ' '))


# ---- 1. HTTP 端点: 紧跟 /taskbar-volume 之后 ----
rep('shot-service.cs',
    '''                else if (path == "/clipboard/history")
''',
    '''                else if (path == "/pick-config")
                {
                    // 划词悬浮球配置: GET 查状态; ?enabled=0|1 开关; ?askModel/?askEndpoint/?askKey 改「问AI」后端
                    // 写回 shot-service.json (设置页同一份配置, 保存即两边可见)
                    int pen = -1;
                    if (q.ContainsKey("enabled")) { int v; if (int.TryParse(q["enabled"], out v)) pen = v == 1 ? 1 : 0; }
                    string sEp = q.ContainsKey("askEndpoint") ? q["askEndpoint"] : "";
                    string sKey = q.ContainsKey("askKey") ? q["askKey"] : "";
                    string sModel = q.ContainsKey("askModel") ? q["askModel"] : "";
                    body = PickConfig(pen, sEp, sKey, sModel, 1);
                }
                else if (path == "/clipboard/history")
''')


# ---- 2a. bridge 工具注册 ----
rep('mcp-bridge.js',
    """      properties: {
        enabled: { type: 'number', description: '0|1 开关' },
        step: { type: 'number', description: '音量步进百分比' },
        reverse: { type: 'number', description: '0|1 反向' }
      }
    }
  },
""",
    """      properties: {
        enabled: { type: 'number', description: '0|1 开关' },
        step: { type: 'number', description: '音量步进百分比' },
        reverse: { type: 'number', description: '0|1 反向' }
      }
    }
  },
  {
    name: 'pick_config',
    description: '划词悬浮球配置（常驻功能，取代豆包划词）。enabled=0/1 开关划词(立即生效+持久化)；askEndpoint/askKey/askModel 改「问AI」后端(默认本机 litellm :4000 / GwV4F)。带参修改，不带参返回当前状态。翻译引擎沿用「翻译」设置。',
    inputSchema: {
      type: 'object', additionalProperties: false,
      properties: {
        enabled: { type: 'number', description: '0|1 开关划词悬浮球' },
        askEndpoint: { type: 'string', description: '问AI 的 /chat/completions 地址' },
        askKey: { type: 'string', description: '问AI 的 API Key' },
        askModel: { type: 'string', description: '问AI 模型名' }
      }
    }
  },
""")


# ---- 2b. bridge 路由 ----
rep('mcp-bridge.js',
    """    case 'taskbar_volume': {
      let qs = [];
      if (a.enabled !== undefined) qs.push('enabled=' + a.enabled);
      if (a.step !== undefined) qs.push('step=' + a.step);
      if (a.reverse !== undefined) qs.push('reverse=' + a.reverse);
      return { path: '/taskbar-volume', qs };
    }
""",
    """    case 'taskbar_volume': {
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
      return { path: '/pick-config', qs };
    }
""")

print('ALL DONE')
