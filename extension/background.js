// background.js — content → 本地 shot-service
// 方案A：只 POST 文本；服务端 GetCursorPos 贴球（零 DPI 换算）
async function inject(text, via) {
  if (!text || !String(text).trim()) return;
  try {
    const base = (typeof WDH_PICK_URL !== 'undefined' && WDH_PICK_URL)
      ? WDH_PICK_URL
      : 'http://127.0.0.1:18800/pick-inject';
    await fetch(base, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: String(text), via: via || 'ext' })
    });
  } catch (e) {
    // helper 未启动时静默；不阻塞页面
  }
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg && msg.type === 'wdh-pick-inject') {
    inject(msg.text, msg.via);
    sendResponse({ ok: true });
    return true;
  }
  return false;
});
