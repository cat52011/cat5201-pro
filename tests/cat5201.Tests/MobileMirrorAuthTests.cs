using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 手機鏡像的授權門檻（Codex #14）：這是唯一對外開埠的元件，
    /// 「錯 token 必須 401、對 token 才放行」是安全底線，用真 Kestrel 端到端釘住。
    /// </summary>
    public class MobileMirrorAuthTests : IAsyncLifetime
    {
        // 避開常用埠；固定埠讓失敗訊息可讀（真撞埠時 StartAsync 會直接炸，測試如實失敗）。
        private const int TestPort = 19217;

        private readonly MobileMirrorServer _server = new();
        private readonly HttpClient _http = new();
        private string _token = "";

        public async Task InitializeAsync()
        {
            await _server.StartAsync(TestPort);
            // token 是 private，從對外 URL（http://ip:port/?t=xxx）解析出來 —— 跟手機拿到的一樣。
            var m = Regex.Match(_server.GetLanUrl(), @"[?&]t=([0-9a-f]+)");
            Assert.True(m.Success, "GetLanUrl 應含 ?t= 權杖");
            _token = m.Groups[1].Value;
        }

        public async Task DisposeAsync()
        {
            _http.Dispose();
            await _server.StopAsync();
        }

        private string Url(string pathAndQuery) => $"http://127.0.0.1:{TestPort}{pathAndQuery}";

        [Fact]
        public async Task Root_WithoutToken_Returns401()
        {
            var resp = await _http.GetAsync(Url("/"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task Snapshot_WithWrongToken_Returns401()
        {
            var resp = await _http.GetAsync(Url("/api/snapshot?t=deadbeef0000"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task Command_WithoutToken_Returns401_AndExecutesNothing()
        {
            bool handlerCalled = false;
            _server.CommandHandler = _ => { handlerCalled = true; return Task.FromResult(new MirrorCommandResult { Ok = true }); };

            var resp = await _http.PostAsync(
                Url("/api/command"),
                new StringContent("""{"action":"stop","nodeId":"x"}""", System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.False(handlerCalled); // 未授權絕不能碰到 handler（那會動使用者的工作區）
        }

        [Fact]
        public async Task Root_WithCorrectToken_Returns200()
        {
            var resp = await _http.GetAsync(Url($"/?t={_token}"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task Ping_NoTokenNeeded_ButLeaksNothing()
        {
            var resp = await _http.GetAsync(Url("/api/ping"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("ok", await resp.Content.ReadAsStringAsync()); // 連線測試端點只回 ok，不洩漏任何狀態
        }
    }
}
