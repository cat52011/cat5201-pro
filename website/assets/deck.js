// 簡報：方向鍵、空白鍵、滑鼠點擊翻頁；F 全螢幕；網址 #頁碼 可直接跳頁
(function () {
  var stage = document.getElementById('stage');
  var slides = [].slice.call(document.querySelectorAll('.slide'));
  var count = document.getElementById('count');
  var i = 0;

  function fit() {
    var s = Math.min(window.innerWidth / 1600, window.innerHeight / 900);
    stage.style.setProperty('--s', s);
  }
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  function go(n) {
    i = Math.max(0, Math.min(slides.length - 1, n));
    slides.forEach(function (el, k) { el.classList.toggle('is-on', k === i); });
    count.textContent = pad(i + 1) + ' / ' + pad(slides.length);
    try { history.replaceState(null, '', '#' + (i + 1)); } catch (e) {}
  }

  window.addEventListener('resize', fit);
  window.addEventListener('keydown', function (e) {
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown' || e.key === 'PageDown' || e.key === ' ' || e.key === 'Enter') { e.preventDefault(); go(i + 1); }
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp' || e.key === 'PageUp' || e.key === 'Backspace') { e.preventDefault(); go(i - 1); }
    else if (e.key === 'Home') go(0);
    else if (e.key === 'End') go(slides.length - 1);
    else if (e.key === 'f' || e.key === 'F') {
      if (document.fullscreenElement) document.exitFullscreen();
      else if (document.documentElement.requestFullscreen) document.documentElement.requestFullscreen();
    }
  });
  window.addEventListener('click', function (e) {
    go(e.clientX < window.innerWidth * 0.3 ? i - 1 : i + 1);
  });
  window.addEventListener('hashchange', function () {
    var n = parseInt(location.hash.slice(1), 10);
    if (n && n - 1 !== i) go(n - 1);
  });

  fit();
  var start = parseInt(location.hash.slice(1), 10);
  go(start ? start - 1 : 0);
})();
