using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// §15 Kling Image-to-Video（休眠擴充點，比照 Gamma 前例：服務先備好，給金鑰即啟用）。
    ///
    /// 定位：頂級 AI 影片工作流是「靜態英雄圖 → I2V 動畫化」而非直接 T2V——
    /// 先用 gpt-image 生成構圖/角色/氛圍已定的底稿，再交 Kling 讓畫面輕微活起來，
    /// 角色一致性遠高於 T2V。Veo 仍是 T2V 主力（長片/快剪），Kling 是 I2V 精製主力。
    ///
    /// 啟用步驟：
    /// 1. 到 Kling 開放平台申請 AccessKey + SecretKey。
    /// 2. 設環境變數 KLING_API_KEY，格式「AccessKey:SecretKey」（冒號分隔），重啟程式。
    /// 3. IsConfigured 變 true 後，於 AgentRuntime 圖片生成完成處接 CreateImageToVideoAsync
    ///    （接線點：GenerateImageFile 成功後提供「動畫化」選項；本服務刻意不依賴 UI）。
    ///
    /// API 形狀（Kling 開放平台 v1）：
    ///   認證＝以 AK/SK 簽 HS256 JWT（iss=AccessKey，exp/nbf 短效），Authorization: Bearer &lt;jwt&gt;
    ///   建立＝POST https://api.klingai.com/v1/videos/image2video { model_name, image(base64), prompt, duration }
    ///   查詢＝GET  https://api.klingai.com/v1/videos/image2video/{task_id} → status: submitted/processing/succeed/failed
    /// </summary>
    public sealed class KlingVideoService
    {
        private const string BaseUrl = "https://api.klingai.com";
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

        /// <summary>金鑰存在即視為已配置（KLING_API_KEY=「AccessKey:SecretKey」）。</summary>
        public bool IsConfigured
        {
            get
            {
                var raw = ApiKeyStore.Resolve("KLING_API_KEY");
                return !string.IsNullOrWhiteSpace(raw) && raw.Contains(':');
            }
        }

        /// <summary>送出圖生影片任務；回傳 task_id，失敗丟例外（呼叫端比照 Veo 的失敗處理）。</summary>
        public async Task<string> CreateImageToVideoAsync(
            byte[] imageBytes, string prompt, int durationSeconds, CancellationToken ct)
        {
            var payload = new
            {
                model_name = "kling-v1-6",
                image = Convert.ToBase64String(imageBytes),
                prompt = prompt ?? "",
                duration = durationSeconds >= 10 ? "10" : "5",   // Kling 只收 5 / 10 秒
                mode = "std",
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/videos/image2video")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BuildJwt());

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Kling I2V 建立失敗（{(int)resp.StatusCode}）：{body}");

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("data").GetProperty("task_id").GetString()
                   ?? throw new InvalidOperationException("Kling 回應缺 task_id：" + body);
        }

        /// <summary>輪詢任務；完成回傳影片下載網址，進行中回 null，失敗丟例外。</summary>
        public async Task<string?> TryGetResultUrlAsync(string taskId, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/videos/image2video/{taskId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BuildJwt());

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Kling 查詢失敗（{(int)resp.StatusCode}）：{body}");

            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            string status = data.GetProperty("task_status").GetString() ?? "";

            return status switch
            {
                "succeed" => data.GetProperty("task_result").GetProperty("videos")[0].GetProperty("url").GetString(),
                "failed" => throw new InvalidOperationException(
                    "Kling 任務失敗：" + (data.TryGetProperty("task_status_msg", out var m) ? m.GetString() : body)),
                _ => null, // submitted / processing
            };
        }

        // Kling 認證：AK/SK 簽短效 HS256 JWT。
        private static string BuildJwt()
        {
            var raw = ApiKeyStore.Resolve("KLING_API_KEY") ?? "";
            int i = raw.IndexOf(':');
            if (i <= 0)
                throw new InvalidOperationException("KLING_API_KEY 格式須為「AccessKey:SecretKey」。");
            string ak = raw[..i], sk = raw[(i + 1)..];

            static string B64Url(byte[] b) =>
                Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string header = B64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
            string body = B64Url(Encoding.UTF8.GetBytes(
                $$"""{"iss":"{{ak}}","exp":{{now + 1800}},"nbf":{{now - 5}}}"""));

            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(sk));
            string sig = B64Url(h.ComputeHash(Encoding.UTF8.GetBytes($"{header}.{body}")));
            return $"{header}.{body}.{sig}";
        }
    }
}
