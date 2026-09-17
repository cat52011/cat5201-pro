using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cat5201
{
    /// <summary>
    /// 手機鏡像（§17 階段一）：在 WPF 進程內跑一個輕量唯讀 web server（Kestrel）。
    /// 手機同區網用瀏覽器連 http://&lt;電腦IP&gt;:5201 即可看到畫布節點的即時狀態。
    /// 完全唯讀：只 GET 快照 JSON，不接受任何寫入/指令（階段二才做輕操控）。
    /// 執行核心（AgentRuntime/NodeControl）完全不動，本類只消費 MainWindow 推來的快照字串。
    /// </summary>
    public sealed class MobileMirrorServer
    {
        public const int DefaultPort = 5201;

        private WebApplication? _app;
        // 最新快照 JSON。volatile 參照指派即執行緒安全（UI 執行緒寫、HTTP 執行緒讀）。
        private volatile string _snapshotJson = "{\"version\":0,\"nodes\":[]}";
        // 存取權杖：每次啟動隨機產生，URL 帶 ?t=token，避免同區網其他人隨手連上看到工作區。
        private string _token = "";

        public int Port { get; private set; }
        public bool IsRunning => _app != null;

        /// <summary>
        /// 手機端輕操控指令的處理器（§17 階段二），由 MainWindow 設定。
        /// 委派實作務必把工作丟回 WPF UI 執行緒（碰 NodeControl 的技術地雷）。
        /// server 只認這個委派，不認 MainWindow——保持解耦。
        /// </summary>
        public Func<MirrorCommand, Task<MirrorCommandResult>>? CommandHandler { get; set; }

        /// <summary>
        /// §17：取單一節點完整輸出文字（快照只帶 240 字預覽，點卡片看全文時呼叫）。
        /// 由 MainWindow 設定，實作須丟回 UI 執行緒。回 null＝找不到節點。
        /// </summary>
        public Func<string, Task<string?>>? NodeTextProvider { get; set; }

        /// <summary>由 UI 執行緒在每次推送時呼叫，覆寫最新快照。</summary>
        public void Publish(string json)
        {
            if (!string.IsNullOrEmpty(json))
                _snapshotJson = json;
        }

        /// <summary>啟動 server。失敗（埠被占用/防火牆）會丟例外，呼叫端顯示友善訊息。</summary>
        public async Task StartAsync(int port = DefaultPort)
        {
            if (_app != null) return;

            // 128-bit 密碼學隨機權杖（P1-7 便宜半份）：舊的 12 hex＝48-bit 對區網暴力猜測太短。
            // QR 掃碼連線,URL 長一點對使用者無感。
            _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();                 // WinExe 無主控台，關掉預設 log provider
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}"); // 綁所有介面，手機才連得到

            var app = builder.Build();

            bool Authorized(HttpContext ctx) => ctx.Request.Query["t"] == _token;

            app.MapGet("/", (HttpContext ctx) => Authorized(ctx)
                ? Results.Content(IndexHtml, "text/html; charset=utf-8")
                : Results.Content(DeniedHtml, "text/html; charset=utf-8", statusCode: 401));
            app.MapGet("/api/snapshot", (HttpContext ctx) => Authorized(ctx)
                ? Results.Content(_snapshotJson, "application/json; charset=utf-8")
                : Results.Unauthorized());
            // 健康檢查 / 連線測試用（不需權杖）
            app.MapGet("/api/ping", () => Results.Content("ok", "text/plain; charset=utf-8"));

            // §17：單一節點完整輸出（手機點卡片看全文）。
            app.MapGet("/api/node/{id}/text", async (HttpContext ctx, string id) =>
            {
                if (!Authorized(ctx))
                    return Results.Unauthorized();
                var provider = NodeTextProvider;
                if (provider == null)
                    return Results.Content("", "text/plain; charset=utf-8");
                string? text = await provider(id);
                return text == null
                    ? Results.NotFound()
                    : Results.Content(text, "text/plain; charset=utf-8");
            });

            // 手機端輕操控（§17 階段二）：停止 / 重跑節點。權杖保護；實際動作由 CommandHandler 丟回 UI 執行緒執行。
            app.MapPost("/api/command", async (HttpContext ctx) =>
            {
                if (!Authorized(ctx))
                    return Results.Unauthorized();
                var handler = CommandHandler;
                if (handler == null)
                    return Results.Json(new MirrorCommandResult { Ok = false, Message = "尚未就緒" });
                MirrorCommand? cmd;
                try { cmd = await ctx.Request.ReadFromJsonAsync<MirrorCommand>(); }
                catch { return Results.Json(new MirrorCommandResult { Ok = false, Message = "指令格式錯誤" }); }
                if (cmd == null)
                    return Results.Json(new MirrorCommandResult { Ok = false, Message = "空指令" });
                var result = await handler(cmd);
                return Results.Json(result);
            });

            await app.StartAsync().ConfigureAwait(false);     // 綁定後即返回，不阻塞
            _app = app;
            Port = port;
        }

        public async Task StopAsync()
        {
            var app = _app;
            _app = null;
            if (app == null) return;
            try
            {
                await app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch { /* 關閉時的例外忽略 */ }
            try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        /// <summary>取手機可連的 LAN 網址（取第一個私有 IPv4）；取不到回 localhost。</summary>
        public string GetLanUrl()
        {
            string ip = GetLanIp() ?? "127.0.0.1";
            int port = Port == 0 ? DefaultPort : Port;
            return $"http://{ip}:{port}/?t={_token}";
        }

        public static string? GetLanIp()
        {
            try
            {
                // 優先：已連線、非虛擬的網卡上的私有 IPv4
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var bytes = ua.Address.GetAddressBytes();
                        bool isPrivate =
                            bytes[0] == 10 ||
                            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                            (bytes[0] == 192 && bytes[1] == 168);
                        if (isPrivate) return ua.Address.ToString();
                    }
                }

                // 退路：透過 UDP socket 問本機對外的來源 IP（不會真的送封包）
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint ep)
                    return ep.Address.ToString();
            }
            catch { }
            return null;
        }

        // ===== 手機端唯讀網頁（單檔內嵌；每 1.5 秒輪詢 /api/snapshot 重繪）=====
        private const string IndexHtml = """
<!DOCTYPE html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>cat5201 手機鏡像</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  body { margin:0; font-family:-apple-system,"Segoe UI","PingFang TC","Microsoft JhengHei",sans-serif;
         background:#0e0f13; color:#e8e8ea; padding:14px 12px 140px; }
  .cmdbar { position:fixed; left:0; right:0; bottom:0; display:flex; flex-direction:column; gap:7px;
            padding:8px 12px calc(10px + env(safe-area-inset-bottom)); background:#14151b;
            border-top:1px solid #23242c; }
  .cmdrow { display:flex; gap:8px; }
  .cmdopts { display:flex; gap:8px; }
  .cmdopts select { flex:1; font-size:12px; padding:7px 8px; border-radius:9px; min-width:0;
                    border:1px solid #2c2d36; background:#1d1e25; color:#9a9aa2; outline:none;
                    -webkit-appearance:none; }
  .cmdbar input { flex:1; font-size:14px; padding:11px 14px; border-radius:11px;
                  border:1px solid #2c2d36; background:#1d1e25; color:#e8e8ea; outline:none; }
  .cmdbar button { font-size:14px; font-weight:700; padding:0 18px; border-radius:11px;
                   border:1px solid #28306e; background:#28306e; color:#dfe5ff;
                   cursor:pointer; -webkit-appearance:none; }
  .cmdbar button:active { opacity:.6; }
  header { display:flex; align-items:center; justify-content:space-between; margin-bottom:12px; }
  h1 { font-size:17px; font-weight:700; margin:0; letter-spacing:.3px; }
  .meta { font-size:11px; color:#8a8a90; }
  .pill { display:inline-flex; align-items:center; gap:6px; font-size:11px; padding:4px 10px;
          border-radius:999px; background:#1a1b21; color:#9a9aa2; }
  .pill.live { color:#34d399; }
  .dot { width:7px; height:7px; border-radius:50%; background:#5b6cff; }
  .pill.live .dot { background:#34d399; animation:pulse 1.2s infinite; }
  @keyframes pulse { 0%{opacity:1} 50%{opacity:.3} 100%{opacity:1} }
  .card { background:#16171d; border:1px solid #23242c; border-radius:14px; padding:13px 14px;
          margin-bottom:10px; }
  .card.running { border-color:#3b54ff; box-shadow:0 0 0 1px #3b54ff55; }
  .card.success { border-color:#1f7a4d55; }
  .card.failed  { border-color:#7a1f2f88; }
  .card.waiting { border-color:#e0922b66; }
  .row1 { display:flex; align-items:flex-start; justify-content:space-between; gap:10px; }
  .title { font-size:14px; font-weight:600; line-height:1.35; word-break:break-word; }
  .badge { flex:none; font-size:11px; font-weight:600; padding:3px 9px; border-radius:8px; }
  .b-idle{background:#2a2b33;color:#9a9aa2} .b-running{background:#28306e;color:#aeb8ff}
  .b-success{background:#16432f;color:#5fe0a0} .b-failed{background:#491a24;color:#ff9aab}
  .b-waiting{background:#40320f;color:#f0c070}
  .sub { font-size:11.5px; color:#8a8a90; margin-top:7px; display:flex; flex-wrap:wrap; gap:6px 12px; }
  .sub b { color:#c8c8d0; font-weight:600; }
  .hint { font-size:12px; color:#aeb8ff; margin-top:8px; }
  .preview { font-size:12.5px; color:#bcbcc4; line-height:1.5; margin-top:9px;
             border-top:1px solid #23242c; padding-top:9px; white-space:pre-wrap; word-break:break-word; }
  .files { margin-top:8px; display:flex; flex-wrap:wrap; gap:6px; }
  .file { font-size:11px; background:#1d1e25; color:#cfcfe0; border:1px solid #2c2d36;
          padding:3px 8px; border-radius:7px; }
  .empty { text-align:center; color:#6a6a72; font-size:13px; margin-top:60px; }
  .confirm-banner { background:#182a1e; border:1px solid #2f7a4d; border-radius:14px; padding:14px 15px; margin-bottom:12px;
                    box-shadow:0 0 0 1px #2f7a4d33; }
  .cb-title { font-size:15px; font-weight:700; color:#7fe0a8; }
  .cb-what { font-size:13px; color:#c8c8d0; margin:7px 0 12px; line-height:1.5; word-break:break-word; }
  .cb-count { font-size:12px; color:#ffd479; margin:-6px 0 12px; }
  .cb-actions { display:flex; gap:8px; }
  .btn.ok { background:#16432f; border-color:#2f7a4d; color:#7fe0a8; }
  .actions { margin-top:11px; display:flex; gap:8px; border-top:1px solid #23242c; padding-top:11px; }
  .btn { flex:1; font-size:13px; font-weight:600; padding:9px 0; border-radius:9px; border:1px solid #2c2d36;
         background:#1d1e25; color:#cfcfe0; cursor:pointer; -webkit-appearance:none; }
  .btn:active { opacity:.6; }
  .btn.stop { background:#3a1720; border-color:#7a1f2f; color:#ff9aab; }
  .btn.rerun { background:#141f3a; border-color:#28306e; color:#aeb8ff; }
  .modal { position:fixed; inset:0; background:#0e0f13ee; z-index:50; display:none;
           flex-direction:column; padding:14px 12px calc(14px + env(safe-area-inset-bottom)); }
  .modal.show { display:flex; }
  .modal-head { display:flex; align-items:center; justify-content:space-between; margin-bottom:10px; }
  .modal-title { font-size:14px; font-weight:700; color:#e8e8ea; }
  .modal-close { font-size:13px; font-weight:600; padding:8px 16px; border-radius:9px;
                 border:1px solid #2c2d36; background:#1d1e25; color:#cfcfe0; cursor:pointer; -webkit-appearance:none; }
  .modal-body { flex:1; overflow-y:auto; background:#16171d; border:1px solid #23242c; border-radius:14px;
                padding:14px; font-size:13.5px; line-height:1.65; color:#d8d8de;
                white-space:pre-wrap; word-break:break-word; -webkit-overflow-scrolling:touch; }
  .toast { position:fixed; left:50%; bottom:28px; transform:translateX(-50%);
           background:#26272f; color:#f0f0f4; font-size:13px; padding:11px 18px; border-radius:11px;
           box-shadow:0 6px 24px #0009; opacity:0; transition:opacity .25s; pointer-events:none; max-width:80%; }
  .toast.show { opacity:1; }
  footer { text-align:center; color:#56565e; font-size:10.5px; margin-top:18px; }
</style>
</head>
<body>
  <header>
    <div>
      <h1>cat5201 工作台</h1>
      <div class="meta" id="meta">連線中…</div>
    </div>
    <div class="pill" id="livePill"><span class="dot"></span><span id="liveText">唯讀鏡像</span></div>
  </header>
  <div id="list"><div class="empty">讀取中…</div></div>

  <!-- §17：點卡片預覽 → 看完整輸出 -->
  <div class="modal" id="fullModal">
    <div class="modal-head">
      <div class="modal-title" id="fullTitle">完整輸出</div>
      <button class="modal-close" onclick="closeFull()">✕ 關閉</button>
    </div>
    <div class="modal-body" id="fullBody">載入中…</div>
  </div>

  <div class="toast" id="toast"></div>
  <footer>手機鏡像 · 每 1.5 秒更新 · 可下指令／停止／重跑</footer>

  <!-- §17 階段三：手機直接下指令 → 桌面畫布建節點並執行；可選連接節點與模型 -->
  <div class="cmdbar">
    <div class="cmdopts">
      <select id="parentSel" title="連接到哪個節點">
        <option value="">🔗 自動（接最後執行節點）</option>
      </select>
      <select id="modelSel" title="使用哪個模型">
        <option value="">🤖 預設模型</option>
      </select>
    </div>
    <div class="cmdrow">
      <input id="newCmd" type="text" placeholder="下指令給工作台（會花 token）…"
             enterkeyhint="send" onkeydown="if(event.key==='Enter')sendNew()">
      <button onclick="sendNew()">送出</button>
    </div>
  </div>

<script>
  const TOKEN = new URLSearchParams(location.search).get('t') || '';
  const STATUS_CLASS = { idle:'b-idle', running:'b-running', success:'b-success', failed:'b-failed', waiting:'b-waiting' };
  function esc(s){ return (s||'').replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c])); }

  function render(snap){
    syncSelects(snap);
    const list = document.getElementById('list');
    const nodes = snap.nodes || [];
    document.getElementById('meta').textContent =
      `${snap.nodeCount||0} 個節點 · 更新於 ${snap.generatedAtLocal||''}`;
    const pill = document.getElementById('livePill');
    const liveText = document.getElementById('liveText');
    if (snap.anyRunning){ pill.classList.add('live'); liveText.textContent='執行中'; }
    else { pill.classList.remove('live'); liveText.textContent='閒置'; }

    const pendingCount = (snap.pending && snap.pending.remainingSeconds >= 0)
      ? `<div class="cb-count">⏳ 約 ${snap.pending.remainingSeconds} 秒後未回應將自動執行</div>` : '';
    const pendingHtml = snap.pending ? `
      <div class="confirm-banner">
        <div class="cb-title">📎 要產生檔案／媒體嗎？</div>
        <div class="cb-what">偵測到即將產生：${esc(snap.pending.what)}</div>
        ${pendingCount}
        <div class="cb-actions">
          <button class="btn ok" onclick="cmd('','confirm')">是，執行</button>
          <button class="btn" onclick="cmd('','reject')">否，只要文字</button>
        </div>
      </div>` : '';

    if (!nodes.length){ list.innerHTML = pendingHtml + '<div class="empty">畫布上還沒有節點</div>'; return; }

    list.innerHTML = pendingHtml + nodes.map(n => {
      const st = n.status || 'idle';
      const files = (n.files||[]).map(f =>
        `<span class="file" onclick="toast('檔案在電腦端，請到桌面開啟')" style="cursor:pointer">📄 ${esc(f)}</span>`).join('');
      const tokens = n.hasRealTokens
        ? `<span><b>${(n.inputTokens||0)+(n.outputTokens||0)}</b> tokens</span>`
        : ((n.inputTokens||n.outputTokens) ? `<span>~${(n.inputTokens||0)+(n.outputTokens||0)} tokens(估算)</span>` : '');
      const cost = n.mediaCostUsd ? `<span>媒體 <b>US$${Number(n.mediaCostUsd).toFixed(2)}</b></span>` : '';
      const model = n.model ? `<span>${esc(n.model)}</span>` : '';
      const hint = (st==='running' && n.loadingHint) ? `<div class="hint">⏳ ${esc(n.loadingHint)}</div>` : '';
      const prev = n.outputPreview
        ? `<div class="preview" style="cursor:pointer" onclick="viewFull('${n.id}','${esc((n.title||'').slice(0,24)).replace(/'/g,'')}')">${esc(n.outputPreview)}<div style="color:#6f7dff;font-size:11px;margin-top:6px">點一下看完整輸出 ▸</div></div>`
        : '';
      const acts = [];
      if (n.canStop)  acts.push(`<button class="btn stop"  onclick="cmd('${n.id}','stop')">■ 停止</button>`);
      if (n.canRerun) acts.push(`<button class="btn rerun" onclick="cmd('${n.id}','rerun')">↻ 重跑</button>`);
      const actions = acts.length ? `<div class="actions">${acts.join('')}</div>` : '';
      return `<div class="card ${st}">
        <div class="row1">
          <div class="title">${esc(n.title) || '(未命名節點)'}</div>
          <div class="badge ${STATUS_CLASS[st]||'b-idle'}">${esc(n.statusLabel||st)}</div>
        </div>
        <div class="sub">${model}${tokens}${cost}</div>
        ${hint}${prev}
        ${files ? `<div class="files">${files}</div>` : ''}
        ${actions}
      </div>`;
    }).join('');
  }

  let toastTimer;
  function toast(msg){
    const t = document.getElementById('toast');
    t.textContent = msg; t.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.classList.remove('show'), 2200);
  }
  async function cmd(id, action, text){
    if (action === 'rerun' && !confirm('重跑會重新呼叫 AI（會花 token／費用），確定嗎？')) return;
    try {
      const r = await fetch('/api/command?t=' + encodeURIComponent(TOKEN), {
        method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({ nodeId:id, action, text: text || '' })
      });
      const res = await r.json();
      toast(res.message || (res.ok ? '完成' : '失敗'));
      tick();
      return res;
    } catch(e){ toast('連線失敗'); }
  }

  // 選項同步：內容變了才重建（避免打斷正在操作的使用者）；保留目前選擇。
  let lastParentKey = '', lastModelKey = '';
  function syncSelects(snap){
    const pSel = document.getElementById('parentSel');
    const mSel = document.getElementById('modelSel');

    const nodes = snap.nodes || [];
    const pKey = nodes.map(n => n.id + '|' + n.title).join(';');
    if (pKey !== lastParentKey && document.activeElement !== pSel){
      lastParentKey = pKey;
      const keep = pSel.value;
      pSel.length = 1; // 保留「自動」
      for (const n of nodes){
        const o = document.createElement('option');
        o.value = n.id;
        o.textContent = '🔗 ' + ((n.title || '(未命名)').slice(0, 18));
        pSel.appendChild(o);
      }
      if ([...pSel.options].some(o => o.value === keep)) pSel.value = keep;
    }

    const models = snap.models || [];
    const mKey = models.map(m => m.id).join(';');
    if (mKey !== lastModelKey && document.activeElement !== mSel){
      lastModelKey = mKey;
      const keep = mSel.value;
      mSel.length = 1; // 保留「預設模型」
      for (const m of models){
        const o = document.createElement('option');
        o.value = m.id;
        o.textContent = '🤖 ' + m.name;
        mSel.appendChild(o);
      }
      if ([...mSel.options].some(o => o.value === keep)) mSel.value = keep;
    }
  }

  async function sendNew(){
    const box = document.getElementById('newCmd');
    const text = (box.value || '').trim();
    if (!text) return;
    try {
      const r = await fetch('/api/command?t=' + encodeURIComponent(TOKEN), {
        method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({
          nodeId:'', action:'newnode', text,
          parentNodeId: document.getElementById('parentSel').value,
          modelId: document.getElementById('modelSel').value,
        })
      });
      const res = await r.json();
      toast(res.message || (res.ok ? '完成' : '失敗'));
      if (res && res.ok) box.value = '';
      tick();
    } catch(e){ toast('連線失敗'); }
  }

  // §17：看完整輸出
  async function viewFull(id, title){
    const modal = document.getElementById('fullModal');
    document.getElementById('fullTitle').textContent = title || '完整輸出';
    document.getElementById('fullBody').textContent = '載入中…';
    modal.classList.add('show');
    try {
      const r = await fetch('/api/node/' + encodeURIComponent(id) + '/text?t=' + encodeURIComponent(TOKEN), { cache:'no-store' });
      document.getElementById('fullBody').textContent = r.ok ? (await r.text() || '（沒有輸出）') : '（讀取失敗）';
    } catch(e){
      document.getElementById('fullBody').textContent = '（連線失敗）';
    }
  }
  function closeFull(){ document.getElementById('fullModal').classList.remove('show'); }

  let failCount = 0;
  async function tick(){
    try {
      const r = await fetch('/api/snapshot?t=' + encodeURIComponent(TOKEN), { cache:'no-store' });
      const snap = await r.json();
      failCount = 0;
      render(snap);
    } catch(e){
      failCount++;
      if (failCount > 3)
        document.getElementById('meta').textContent = '連線中斷，重試中…';
    }
  }
  tick();
  setInterval(tick, 1500);
</script>
</body>
</html>
""";

        // 未帶正確權杖時回這頁（避免同區網其他人裸連就看到工作區）。
        private const string DeniedHtml = """
<!DOCTYPE html>
<html lang="zh-Hant"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>需要授權</title>
<style>body{margin:0;font-family:-apple-system,"Segoe UI","Microsoft JhengHei",sans-serif;
background:#0e0f13;color:#e8e8ea;display:flex;align-items:center;justify-content:center;height:100vh;text-align:center}
div{max-width:280px;padding:20px}h1{font-size:18px}p{color:#8a8a90;font-size:13px;line-height:1.6}</style>
</head><body><div><h1>🔒 需要授權</h1>
<p>請用電腦「設定 → 手機鏡像」裡的 QR 碼或完整網址（含權杖）連線，不要只打 IP。</p></div></body></html>
""";
    }
}
