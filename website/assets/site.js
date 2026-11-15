// 語言切換
(function () {
  var root = document.documentElement;
  function setLang(l) {
    root.setAttribute('data-lang', l);
    root.setAttribute('lang', l === 'en' ? 'en' : 'zh-Hant');
    try { localStorage.setItem('cat5201-lang', l); } catch (e) {}
  }
  try {
    var saved = localStorage.getItem('cat5201-lang');
    if (saved === 'en' || saved === 'zh') setLang(saved);
  } catch (e) {}
  var btn = document.getElementById('langBtn');
  if (btn) btn.addEventListener('click', function () {
    setLang(root.getAttribute('data-lang') === 'en' ? 'zh' : 'en');
  });
})();

// 紙帶：送出後，回答一行一行出現，決策紀錄一列一列印出來
(function () {
  var demo = document.getElementById('demo');
  if (!demo) return;
  var lines = [].slice.call(demo.querySelectorAll('.ln'));
  var rows = [].slice.call(demo.querySelectorAll('.row'));
  var replay = document.getElementById('replay');
  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var timers = [];

  function later(fn, ms) { timers.push(setTimeout(fn, ms)); }

  function play() {
    timers.forEach(clearTimeout); timers = [];
    lines.concat(rows).forEach(function (el) { el.classList.add('wait'); });
    demo.setAttribute('data-state', 'running');
    var t = 500;
    lines.forEach(function (el) { later(function () { el.classList.remove('wait'); }, t); t += 520; });
    later(function () { demo.setAttribute('data-state', 'flash'); }, t);       // 外框轉綠
    later(function () { demo.setAttribute('data-state', 'done'); }, t + 900);  // 淡回
    t += 150;
    rows.forEach(function (el) { later(function () { el.classList.remove('wait'); }, t); t += 360; });
  }

  if (replay) replay.addEventListener('click', play);

  if (!reduce && 'IntersectionObserver' in window) {
    var seen = false;
    new IntersectionObserver(function (entries, io) {
      entries.forEach(function (e) {
        if (e.isIntersecting && !seen) { seen = true; io.disconnect(); play(); }
      });
    }, { threshold: 0.45 }).observe(demo);
  }
})();
