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

        private volatile bool _readOnly = true;

        /// <summary>唯讀模式（預設開）：手機只能查看，所有操作指令都會被拒絕。可在執行中切換，立即生效。</summary>
        public bool ReadOnly
        {
            get => _readOnly;
            set => _readOnly = value;
        }

        public const string ReadOnlyMessage = "唯讀模式：手機只能查看。要在手機上操作，請到電腦的「個人化」→「手機鏡像」關閉唯讀。";

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
                // 唯讀：手機只能看。停止、重跑、下指令、回答確認框一律拒絕——
                // 連線是區網明碼 HTTP，拿到網址的人就能操作，而重跑和下指令會花錢（2026-11-03）。
                if (ReadOnly)
                    return Results.Json(new MirrorCommandResult { Ok = false, Message = ReadOnlyMessage }, statusCode: 403);
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
<meta name="theme-color" content="#EEE8DC">
<title>工作台 · 手機鏡像</title>
<style>
  /* 侘寂・陶器：灰米和紙底＋極淡紙紋、低彩度的陶土／苔綠／鐵鏽、
     卡片四角圓角刻意不一致（像手捏的陶器）、柔和陰影、不用框線。 */
  :root {
    color-scheme: light;
    --bg:#EEE8DC; --card:#F8F4ED; --ink:#2B2723; --deep:#3A342E; --on-deep:#EEE8DC;
    --muted:#6E665B; --faint:#978E81; --hair:rgba(43,39,35,.12);
    --clay:#9A6F50; --moss:#5F6A4B; --rust:#93432F; --ochre:#8C6A2E;
    --r-card:7px 3px 8px 4px; --r-btn:4px 2px 5px 3px;
    --shadow:0 1px 0 rgba(43,39,35,.05), 0 12px 26px -16px rgba(60,48,36,.4);
    --serif:"Noto Serif TC","Songti TC",Georgia,PMingLiU,serif;
  }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  html { -webkit-text-size-adjust:100%; }
  body { margin:0; font-family:-apple-system,"Segoe UI","PingFang TC","Microsoft JhengHei",sans-serif;
         color:var(--ink); padding:28px 18px 160px; line-height:1.5; font-variant-numeric:tabular-nums;
         background-color:var(--bg);
         background-image:url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='160' height='160'><filter id='n'><feTurbulence type='fractalNoise' baseFrequency='.9' numOctaves='2' stitchTiles='stitch'/><feColorMatrix values='0 0 0 0 .35  0 0 0 0 .3  0 0 0 0 .24  0 0 0 .07 0'/></filter><rect width='100%' height='100%' filter='url(%23n)'/></svg>");
         background-attachment:fixed; }
  body.modal-open { overflow:hidden; }
  button { font-family:inherit; cursor:pointer; -webkit-appearance:none; }

  header { display:flex; align-items:flex-end; justify-content:space-between; gap:12px; margin-bottom:24px; }
  h1 { font-family:var(--serif); font-size:24px; font-weight:500; margin:0; letter-spacing:5px; }
  .meta { font-size:12px; color:var(--muted); margin-top:6px; letter-spacing:.5px; }
  .pill { flex:none; display:inline-flex; align-items:center; gap:7px; font-size:12px; letter-spacing:2px;
          color:var(--muted); padding-bottom:3px; }
  .dot { width:6px; height:6px; border-radius:50%; background:var(--faint); opacity:.8; }
  .pill.live { color:var(--clay); }
  .pill.live .dot { background:var(--clay); animation:breathe 2.4s ease-in-out infinite; }
  .pill.off { color:var(--rust); }
  .pill.off .dot { background:var(--rust); }
  @keyframes breathe { 0%,100%{opacity:.9} 50%{opacity:.25} }

  .card { background:var(--card); border-radius:var(--r-card); box-shadow:var(--shadow);
          padding:20px 20px 18px; margin-bottom:14px; }
  .card:nth-child(3n+2) { border-radius:4px 7px 3px 8px; }
  .card:nth-child(3n)   { border-radius:6px 4px 7px 2px; }
  .row1 { display:flex; align-items:baseline; justify-content:space-between; gap:12px; }
  .title { font-family:var(--serif); font-size:16.5px; font-weight:500; line-height:1.6; word-break:break-word; }
  /* 狀態只用字與一顆小點，不用色塊徽章。 */
  .badge { flex:none; font-size:11.5px; letter-spacing:2px; color:var(--faint); white-space:nowrap; }
  .b-running { color:var(--clay); }
  .b-running:before { content:""; display:inline-block; width:6px; height:6px; border-radius:50%;
                      background:var(--clay); margin-right:6px; vertical-align:middle; animation:breathe 2.4s ease-in-out infinite; }
  .b-success { color:var(--moss); } .b-failed { color:var(--rust); } .b-waiting { color:var(--ochre); }
  .sub { font-size:12px; color:var(--muted); margin-top:6px; display:flex; flex-wrap:wrap; gap:2px 0; }
  .sub span + span:before { content:"·"; margin:0 7px; color:var(--faint); }
  .sub b { color:var(--ink); font-weight:500; }
  .hint { font-size:13px; color:var(--clay); margin-top:10px; }
  .preview { display:block; width:100%; text-align:left; font:inherit; font-size:13.5px; color:#4A443D;
             line-height:1.9; margin-top:14px; padding:14px 0 0; background:none; border:0; border-radius:0;
             border-top:1px solid var(--hair); white-space:pre-wrap; word-break:break-word; }
  .more { display:block; color:var(--clay); font-size:12px; letter-spacing:1px; margin-top:8px; }
  .files { margin-top:12px; display:flex; flex-wrap:wrap; gap:6px; }
  .file { font-size:12px; background:rgba(43,39,35,.05); color:var(--ink); padding:5px 9px; border-radius:var(--r-btn);
          max-width:100%; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .empty { text-align:center; color:var(--faint); font-family:var(--serif); font-size:15px; letter-spacing:2px; margin-top:80px; }

  .confirm-banner { background:var(--deep); color:var(--on-deep); border-radius:var(--r-card);
                    box-shadow:var(--shadow); padding:20px; margin-bottom:18px; }
  .cb-title { font-family:var(--serif); font-size:17px; font-weight:500; line-height:1.55; }
  .cb-what { font-size:13px; color:#C7BCAB; margin:4px 0 16px; line-height:1.6; word-break:break-word; }
  .cb-count { font-size:12.5px; color:#D2B48C; margin:-8px 0 16px; }
  .cb-actions { display:flex; gap:10px; }
  .confirm-banner .btn { background:transparent; color:var(--on-deep); border:1px solid rgba(238,232,220,.3); box-shadow:none; }
  .confirm-banner .btn.ok { background:var(--on-deep); color:var(--ink); border-color:var(--on-deep); }

  .actions { margin-top:14px; display:flex; gap:10px; padding-top:14px; border-top:1px solid var(--hair); }
  .btn { flex:1; min-height:44px; font-size:14px; font-weight:500; letter-spacing:1px; padding:0 12px;
         border-radius:var(--r-btn); border:1px solid rgba(43,39,35,.18); background:transparent; color:var(--ink); }
  .btn:active, .send:active, .modal-close:active { opacity:.6; }
  .btn.stop { color:var(--rust); border-color:rgba(147,67,47,.35); }

  .cmdbar { position:fixed; left:0; right:0; bottom:0; display:flex; flex-direction:column; gap:8px;
            padding:12px 18px calc(12px + env(safe-area-inset-bottom));
            background:rgba(238,232,220,.94); -webkit-backdrop-filter:blur(8px); backdrop-filter:blur(8px);
            box-shadow:0 -1px 0 var(--hair); }
  .cmdrow, .cmdopts { display:flex; gap:8px; }
  /* 16px 以下 iOS 一點輸入框就會放大整頁。 */
  .cmdopts select { flex:1; min-width:0; font-size:16px; padding:7px 10px; border-radius:var(--r-btn);
                    border:0; background:rgba(248,244,237,.8); color:var(--muted); outline:none;
                    -webkit-appearance:none; text-overflow:ellipsis; }
  .cmdbar input { flex:1; min-width:0; font-size:16px; padding:12px 14px; border-radius:var(--r-btn);
                  border:1px solid transparent; background:rgba(248,244,237,.9); color:var(--ink); outline:none; }
  .cmdbar input::placeholder { color:var(--faint); }
  .cmdbar input:focus, .cmdopts select:focus { border-color:rgba(154,111,80,.5); }
  .send { flex:none; min-height:46px; font-size:14px; font-weight:500; letter-spacing:3px; padding:0 18px;
          border-radius:3px 4px 2px 5px; border:0; background:var(--deep); color:var(--on-deep); }

  .modal { position:fixed; inset:0; background:var(--bg); z-index:50; display:none; flex-direction:column;
           padding:calc(18px + env(safe-area-inset-top)) 18px calc(18px + env(safe-area-inset-bottom)); }
  .modal.show { display:flex; }
  .modal-head { display:flex; align-items:center; justify-content:space-between; gap:12px; margin-bottom:14px; }
  .modal-title { font-family:var(--serif); font-size:18px; font-weight:500; letter-spacing:1px; min-width:0;
                 overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .modal-close { flex:none; min-height:44px; font-size:14px; letter-spacing:2px; padding:0 16px;
                 border-radius:var(--r-btn); border:1px solid rgba(43,39,35,.18); background:transparent; color:var(--ink); }
  .modal-body { flex:1; overflow-y:auto; background:var(--card); border-radius:var(--r-card); box-shadow:var(--shadow);
                padding:20px; font-size:15px; line-height:1.95; color:var(--ink);
                white-space:pre-wrap; word-break:break-word; -webkit-overflow-scrolling:touch; }

  .toast { position:fixed; left:50%; bottom:calc(160px + env(safe-area-inset-bottom)); transform:translateX(-50%);
           background:var(--deep); color:var(--on-deep); font-size:14px; padding:11px 18px; border-radius:var(--r-btn);
           box-shadow:0 10px 30px -10px rgba(43,39,35,.5); opacity:0; transition:opacity .3s; pointer-events:none;
           max-width:84%; z-index:60; }
  .toast.show { opacity:1; }
  footer { text-align:center; color:var(--faint); font-size:11px; letter-spacing:1px; margin-top:28px; }
</style>
</head>
<body>
  <header>
    <div>
      <h1>工作台</h1>
      <div class="meta" id="meta">連線中…</div>
    </div>
    <div class="pill" id="livePill"><span class="dot"></span><span id="liveText">唯讀鏡像</span></div>
  </header>
  <div id="list"><div class="empty">讀取中…</div></div>

  <!-- §17：點卡片預覽 → 看完整輸出 -->
  <div class="modal" id="fullModal" role="dialog" aria-modal="true" aria-labelledby="fullTitle">
    <div class="modal-head">
      <div class="modal-title" id="fullTitle">完整輸出</div>
      <button class="modal-close" onclick="closeFull()">關閉</button>
    </div>
    <div class="modal-body" id="fullBody">載入中…</div>
  </div>

  <div class="toast" id="toast" role="status" aria-live="polite"></div>
  <footer id="footer">手機鏡像 · 每 1.5 秒更新</footer>

  <!-- §17 階段三：手機直接下指令 → 桌面畫布建節點並執行；可選連接節點與模型 -->
  <div class="cmdbar">
    <div class="cmdopts">
      <select id="parentSel" aria-label="接在哪個節點後面">
        <option value="">接在：自動（最後執行的節點）</option>
      </select>
      <select id="modelSel" aria-label="使用哪個模型">
        <option value="">模型：預設</option>
      </select>
    </div>
    <div class="cmdrow">
      <input id="newCmd" type="text" placeholder="下指令給工作台（會花 token）"
             enterkeyhint="send" onkeydown="if(event.key==='Enter')sendNew()">
      <button class="send" onclick="sendNew()">送出</button>
    </div>
  </div>

<script>
  const TOKEN = new URLSearchParams(location.search).get('t') || '';
  const STATUS_CLASS = { idle:'b-idle', running:'b-running', success:'b-success', failed:'b-failed', waiting:'b-waiting' };
  // 引號也要跳脫：標題含 " 或 ' 時，放進屬性會把 HTML 弄壞。
  function esc(s){ return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }

  let lastNodes = [];
  let lastSig = '';

  function render(snap){
    syncSelects(snap);
    // 唯讀：不顯示任何操作（伺服器端也會拒絕，這裡只是不讓人按到沒反應的按鈕）。
    const ro = !!snap.readOnly;
    document.querySelector('.cmdbar').style.display = ro ? 'none' : '';
    document.body.style.paddingBottom = ro ? '32px' : '';
    document.getElementById('footer').textContent =
      '手機鏡像 · 每 1.5 秒更新 · ' + (ro ? '唯讀（只能查看）' : '可下指令／停止／重跑');
    const list = document.getElementById('list');
    const nodes = snap.nodes || [];
    lastNodes = nodes;
    const pill = document.getElementById('livePill');
    const liveText = document.getElementById('liveText');
    pill.classList.remove('off');
    if (snap.anyRunning){ pill.classList.add('live'); liveText.textContent='執行中'; }
    else { pill.classList.remove('live'); liveText.textContent='閒置'; }

    const pendingCount = (snap.pending && snap.pending.remainingSeconds >= 0)
      ? `<div class="cb-count">約 ${snap.pending.remainingSeconds} 秒後未回應將自動執行</div>` : '';
    const pendingActions = ro
      ? `<div class="cb-count">唯讀模式：請在電腦上回答</div>`
      : `<div class="cb-actions">
          <button class="btn ok" data-act="confirm">是，執行</button>
          <button class="btn" data-act="reject">否，只要文字</button>
        </div>`;
    const pendingHtml = snap.pending ? `
      <div class="confirm-banner">
        <div class="cb-title">要產生檔案或媒體嗎？</div>
        <div class="cb-what">即將產生：${esc(snap.pending.what)}</div>
        ${pendingCount}
        ${pendingActions}
      </div>` : '';

    if (!nodes.length){ list.innerHTML = pendingHtml + '<div class="empty">畫布上還沒有節點</div>'; return; }

    list.innerHTML = pendingHtml + nodes.map(n => {
      const st = n.status || 'idle';
      const id = esc(n.id);
      const files = (n.files||[]).map(f =>
        `<span class="file" data-act="file">${esc(f)}</span>`).join('');
      const total = (n.inputTokens||0) + (n.outputTokens||0);
      const tokens = n.hasRealTokens
        ? `<span><b>${total.toLocaleString()}</b> tokens</span>`
        : (total ? `<span>約 ${total.toLocaleString()} tokens（估算）</span>` : '');
      const cost = n.mediaCostUsd ? `<span>媒體 <b>US$${Number(n.mediaCostUsd).toFixed(2)}</b></span>` : '';
      const model = n.model ? `<span>${esc(n.model)}</span>` : '';
      const sub = model + tokens + cost;
      const hint = (st==='running' && n.loadingHint) ? `<div class="hint">${esc(n.loadingHint)}</div>` : '';
      const prev = n.outputPreview
        ? `<button class="preview" data-act="full" data-id="${id}">${esc(n.outputPreview)}<span class="more">看完整輸出 ›</span></button>`
        : '';
      const acts = [];
      if (n.canStop && !ro)  acts.push(`<button class="btn stop" data-act="stop" data-id="${id}">停止</button>`);
      if (n.canRerun && !ro) acts.push(`<button class="btn" data-act="rerun" data-id="${id}">重跑</button>`);
      const actions = acts.length ? `<div class="actions">${acts.join('')}</div>` : '';
      return `<div class="card ${esc(st)}">
        <div class="row1">
          <div class="title">${esc(n.title) || '(未命名節點)'}</div>
          <div class="badge ${STATUS_CLASS[st]||'b-idle'}">${esc(n.statusLabel||st)}</div>
        </div>
        ${sub ? `<div class="sub">${sub}</div>` : ''}
        ${hint}${prev}
        ${files ? `<div class="files">${files}</div>` : ''}
        ${actions}
      </div>`;
    }).join('');
  }

  // 卡片上的按鈕一律走事件委派：不把節點 ID 或標題拼進 onclick 字串。
  document.getElementById('list').addEventListener('click', e => {
    const el = e.target.closest('[data-act]');
    if (!el) return;
    const act = el.dataset.act, id = el.dataset.id || '';
    if (act === 'file') toast('檔案在電腦上，請到桌面開啟');
    else if (act === 'full') viewFull(id);
    else cmd(id, act);
  });

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
      tick(true);
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
        o.textContent = '接在：' + ((n.title || '(未命名)').slice(0, 18));
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
        o.textContent = '模型：' + m.name;
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
      if (res && res.ok) { box.value = ''; box.blur(); }
      tick(true);
    } catch(e){ toast('連線失敗'); }
  }

  // §17：看完整輸出
  async function viewFull(id){
    const node = lastNodes.find(n => n.id === id);
    const modal = document.getElementById('fullModal');
    document.getElementById('fullTitle').textContent = (node && node.title) || '完整輸出';
    document.getElementById('fullBody').textContent = '載入中…';
    modal.classList.add('show');
    document.body.classList.add('modal-open');
    try {
      const r = await fetch('/api/node/' + encodeURIComponent(id) + '/text?t=' + encodeURIComponent(TOKEN), { cache:'no-store' });
      document.getElementById('fullBody').textContent = r.ok ? (await r.text() || '（沒有輸出）') : '（讀取失敗）';
    } catch(e){
      document.getElementById('fullBody').textContent = '（連線失敗）';
    }
  }
  function closeFull(){
    document.getElementById('fullModal').classList.remove('show');
    document.body.classList.remove('modal-open');
  }
  document.addEventListener('keydown', e => { if (e.key === 'Escape') closeFull(); });

  let failCount = 0;
  async function tick(force){
    try {
      const r = await fetch('/api/snapshot?t=' + encodeURIComponent(TOKEN), { cache:'no-store' });
      const snap = await r.json();
      const wasOffline = failCount > 3;   // 斷線後恢復：內容可能沒變，但「離線」標示要換回來
      failCount = 0;
      if (wasOffline) force = true;
      document.getElementById('meta').textContent =
        `${snap.nodeCount||0} 個節點 · 更新於 ${snap.generatedAtLocal||''}`;
      // 內容沒變就不重畫：整頁重建會讓正要按的按鈕被換掉、選取的文字消失。
      const sig = JSON.stringify(Object.assign({}, snap, { generatedAtLocal: '' }));
      if (force || sig !== lastSig){ lastSig = sig; render(snap); }
    } catch(e){
      failCount++;
      if (failCount > 3){
        document.getElementById('meta').textContent = '連線中斷，重試中…';
        const pill = document.getElementById('livePill');
        pill.classList.remove('live'); pill.classList.add('off');
        document.getElementById('liveText').textContent = '離線';
      }
    }
  }
  // 手機切到別的 App 或關螢幕時不輪詢（省電）；回來立刻更新一次。
  document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
  tick();
  setInterval(() => { if (!document.hidden) tick(); }, 1500);
</script>
</body>
</html>
""";

        // 未帶正確權杖時回這頁（避免同區網其他人裸連就看到工作區）。
        private const string DeniedHtml = """
<!DOCTYPE html>
<html lang="zh-Hant"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="theme-color" content="#EEE8DC">
<title>需要授權</title>
<style>body{margin:0;font-family:-apple-system,"Segoe UI","PingFang TC","Microsoft JhengHei",sans-serif;
background:#EEE8DC;color:#2B2723;display:flex;align-items:center;justify-content:center;min-height:100vh;text-align:center}
div{max-width:300px;padding:28px 22px;background:#F8F4ED;border-radius:7px 3px 8px 4px;margin:18px;
box-shadow:0 1px 0 rgba(43,39,35,.05),0 12px 26px -16px rgba(60,48,36,.4)}
h1{font-family:"Noto Serif TC","Songti TC",Georgia,PMingLiU,serif;font-size:20px;font-weight:500;letter-spacing:4px;margin:0 0 12px}
p{color:#6E665B;font-size:14px;line-height:1.85;margin:0}</style>
</head><body><div><h1>需要授權</h1>
<p>請用電腦「設定 → 手機鏡像」裡的 QR 碼或完整網址（含權杖）連線，不要只打 IP。</p></div></body></html>
""";
    }
}
