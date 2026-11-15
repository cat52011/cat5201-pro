using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-13 手動測試：獨立審查碰到 OpenAI 一次 520 就放棄。
    /// 暫時性錯誤要自動再送；請求本身有問題的錯誤不能重送。
    /// </summary>
    public class TransientHttpRetryTests
    {
        private sealed class Scripted : HttpMessageHandler
        {
            private readonly Queue<HttpStatusCode> _codes;
            public int Calls { get; private set; }
            public List<string> Bodies { get; } = new();

            public Scripted(params HttpStatusCode[] codes) => _codes = new Queue<HttpStatusCode>(codes);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++;
                Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
                var code = _codes.Count > 0 ? _codes.Dequeue() : HttpStatusCode.OK;
                return new HttpResponseMessage(code) { Content = new StringContent("{}") };
            }
        }

        private static (HttpClient Client, Scripted Inner, List<TimeSpan> Waits) Build(params HttpStatusCode[] codes)
        {
            var inner = new Scripted(codes);
            var waits = new List<TimeSpan>();
            var handler = new TransientHttpRetryHandler(inner)
            {
                Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; }
            };
            return (new HttpClient(handler), inner, waits);
        }

        private static HttpRequestMessage Post() => new(HttpMethod.Post, "https://api.example.test/v1/x")
        {
            Content = new StringContent("{\"q\":\"審查\"}", Encoding.UTF8, "application/json")
        };

        [Fact]
        public async Task A520ThenSuccess_IsRetried_AndTheSameBodyIsSentAgain()
        {
            var (client, inner, waits) = Build((HttpStatusCode)520, HttpStatusCode.OK);

            using var resp = await client.SendAsync(Post());

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(2, inner.Calls);
            Assert.Single(waits);
            Assert.All(inner.Bodies, b => Assert.Equal("{\"q\":\"審查\"}", b));
        }

        [Fact]
        public async Task StopsAfterTwoRetries_AndReturnsTheLastError()
        {
            var (client, inner, _) = Build(
                HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway, (HttpStatusCode)529, HttpStatusCode.OK);

            using var resp = await client.SendAsync(Post());

            Assert.Equal((HttpStatusCode)529, resp.StatusCode);
            Assert.Equal(1 + TransientHttpRetryHandler.MaxRetries, inner.Calls);
        }

        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.NotFound)]
        public async Task RequestProblems_AreNotRetried(HttpStatusCode code)
        {
            var (client, inner, waits) = Build(code, HttpStatusCode.OK);

            using var resp = await client.SendAsync(Post());

            Assert.Equal(code, resp.StatusCode);
            Assert.Equal(1, inner.Calls);
            Assert.Empty(waits);
        }

        [Theory]
        [InlineData(429, true)]
        [InlineData(500, true)]
        [InlineData(502, true)]
        [InlineData(503, true)]
        [InlineData(504, true)]
        [InlineData(520, true)]
        [InlineData(524, true)]
        [InlineData(529, true)]
        [InlineData(400, false)]
        [InlineData(401, false)]
        [InlineData(413, false)]
        [InlineData(200, false)]
        public void WhichCodesAreTransient(int code, bool expected)
            => Assert.Equal(expected, TransientHttpRetryHandler.IsTransient((HttpStatusCode)code));
    }

    public class BalanceDriftTests
    {
        // 2026-11-13 的實際情況：上次對帳後本機記了約 9.5，實際餘額卻比本機多 17.15。
        // 多記不可能超過實際記到的花費，多出的那一截是儲值，不能說成「本機多記了 17.15」。
        [Fact]
        public void GapLargerThanEverythingRecorded_IsATopUp_NotAnAccountingError()
        {
            var (kind, message) = ProviderBalance.DescribeDrift(-17.15, 9.53);
            Assert.Equal(ProviderBalance.DriftKind.LikelyTopUp, kind);
            Assert.Contains("儲值", message);
        }

        [Fact]
        public void GapWithinRecordedSpend_IsOverRecording()
            => Assert.Equal(ProviderBalance.DriftKind.OverRecorded, ProviderBalance.DescribeDrift(-1.20, 9.53).Kind);

        [Fact]
        public void PositiveDrift_IsUnderRecording()
            => Assert.Equal(ProviderBalance.DriftKind.UnderRecorded, ProviderBalance.DescribeDrift(0.80, 9.53).Kind);
    }
}
