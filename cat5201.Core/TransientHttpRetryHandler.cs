using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// 模型服務回「暫時性錯誤」時自動再送一次（最多兩次）。
    ///
    /// 2026-11-13 手動測試：資料分析跑了 6 分鐘、花了 US$0.70，接下來的獨立審查
    /// 碰到 OpenAI 一次 520（Cloudflare 與後端之間的暫時錯誤），沒有重試就直接放棄，
    /// 使用者拿到一份「沒審查到」的報告。這類錯誤通常幾秒內就恢復。
    ///
    /// 只重試「伺服器明確回了錯誤狀態碼」的情況：那代表請求沒有完成，供應商不會收費，
    /// 再送一次不會重複扣錢。連線中斷、逾時這類例外不重試——請求可能已經在伺服器上跑了，
    /// 重送有可能付兩次。
    /// </summary>
    public sealed class TransientHttpRetryHandler : DelegatingHandler
    {
        public const int MaxRetries = 2;

        /// <summary>第一次、第二次重試前各等多久（伺服器有給 Retry-After 時以它為準，但不超過 MaxRetryAfter）。</summary>
        private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6) };
        private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(20);

        /// <summary>測試用：把等待時間換掉，免得測試真的等好幾秒。</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

        public TransientHttpRetryHandler() : base(new HttpClientHandler()) { }

        internal TransientHttpRetryHandler(HttpMessageHandler inner) : base(inner) { }

        /// <summary>一個不設逾時（交給 CancellationToken 控制）、會自動重試暫時性錯誤的 HttpClient。</summary>
        public static HttpClient CreateClient()
            => new(new TransientHttpRetryHandler()) { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>
        /// 哪些狀態碼算「暫時的、再試一次可能就好」。
        /// 429 限流、500～504 伺服器錯誤、520～524 Cloudflare 與後端之間的錯誤、529 Anthropic 過載。
        /// 400、401、403、404 這類是請求本身的問題，重送只會得到同樣的結果。
        /// </summary>
        public static bool IsTransient(HttpStatusCode code)
        {
            int c = (int)code;
            return c == 408 || c == 429 || (c >= 500 && c <= 504) || (c >= 520 && c <= 524) || c == 529;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // 請求內容要能重送：先整包讀進記憶體（我們送的都是 JSON 字串，本來就在記憶體裡）。
            if (request.Content != null)
                await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);

            for (int attempt = 0; ; attempt++)
            {
                var response = await base.SendAsync(request, ct).ConfigureAwait(false);

                if (attempt >= MaxRetries || !IsTransient(response.StatusCode))
                    return response;

                TimeSpan wait = RetryAfter(response) ?? Backoff[Math.Min(attempt, Backoff.Length - 1)];
                if (wait > MaxRetryAfter)
                    return response;   // 要等太久（例如限流叫你等一分鐘）就不在這裡空等，交給呼叫端處理

                AppLog.Warn("Http",
                    $"{request.RequestUri?.Host} 回 {(int)response.StatusCode}，{wait.TotalSeconds:0} 秒後重試（第 {attempt + 1} 次，最多 {MaxRetries} 次）");
                response.Dispose();
                await Delay(wait, ct).ConfigureAwait(false);
            }
        }

        private static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var ra = response.Headers.RetryAfter;
            if (ra == null) return null;
            if (ra.Delta is TimeSpan d) return d < TimeSpan.Zero ? TimeSpan.Zero : d;
            if (ra.Date is DateTimeOffset at)
            {
                var d2 = at - DateTimeOffset.UtcNow;
                return d2 < TimeSpan.Zero ? TimeSpan.Zero : d2;
            }
            return null;
        }
    }
}
