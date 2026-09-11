// content.js — Edge MV3 划词注入（方案A）
// 双击 / 拖选松手 → getSelection().toString() → background → POST /pick-inject
// 不做坐标换算、不截图、不 OCR；球位置由 shot-service 用 GetCursorPos 贴光标。
(function () {
  if (window.__wdhPickInjected) return;
  window.__wdhPickInjected = true;

  var lastText = '';
  var lastAt = 0;
  var DOWN_MS = 450;

  function selText() {
    try {
      var s = window.getSelection && window.getSelection();
      if (!s || s.isCollapsed) return '';
      return String(s.toString() || '').replace(/ /g, ' ').trim();
    } catch (e) {
      return '';
    }
  }

  function send(text, via) {
    if (!text || text.length < 1) return;
    if (text.length > 2000) text = text.slice(0, 2000);
    var now = Date.now();
    if (text === lastText && now - lastAt < 600) return;
    lastText = text;
    lastAt = now;
    try {
      chrome.runtime.sendMessage({ type: 'wdh-pick-inject', text: text, via: via || 'sel' });
    } catch (e) { /* 扩展被禁用时静默 */ }
  }

  // 拖选松手
  document.addEventListener(
    'mouseup',
    function (ev) {
      if (ev && ev.button !== 0) return;
      // 点在输入框/可编辑区且未形成选区时忽略
      var t = selText();
      if (t.length < 1) return;
      // 单击空白/点按钮：无选区就跳过（collapsed 已挡）
      send(t, 'mouseup');
    },
    true
  );

  // 双击选词
  document.addEventListener(
    'dblclick',
    function () {
      var t = selText();
      if (t.length < 1) return;
      send(t, 'dblclick');
    },
    true
  );
})();
