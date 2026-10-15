using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// 「我接的這些 AI 服務，現在到底能不能用？」
    ///
    /// 誠實的前提：Anthropic / OpenAI / Google / Perplexity 的**一般金鑰都沒有查餘額的 API**
    /// （只有管理員金鑰能拉用量報表）。所以這裡做兩種檢查：
    ///   快速檢查（免費）：打各家的 models 清單端點 → 金鑰有效嗎、網路通嗎、有沒有被限流。
    ///   深入檢查（每家不到 NT$0.3）：送一個最小的真實請求 → 餘額不足這種「只有真的呼叫才會知道」的狀態才抓得到。
    /// 花費則用本機帳本（UsageMeter 記的每一筆）分服務加總，補上餘額查不到的缺口。
    /// </summary>
    public static class ApiHealthChecker
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public sealed class ProviderInfo
        {
            public string Id { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string EnvName { get; init; } = "";
            public string BillingUrl { get; init; } = "";
            public string Note { get; init; } = "";

            // 這裡刻意沒有 Currency。
            //
            // 幣別是「使用者選的」，不是服務的固有屬性——出廠預設只是還沒選過時的起點，
            // 放在這個隨處拿得到的物件上，任何一個畫面都可能順手讀它而繞過使用者的選擇。
            // 2026-10-13 就出過事：設定頁照選擇顯示 A$，右下角小面板讀這裡所以永遠顯示 US$，
            // 同一筆錢兩個講法。現在唯一的入口是 ProviderBalance.CurrencyOf()。
        }

        /// <summary>幣別代號 → 顯示符號。</summary>
        public static string CurrencySymbol(string? currency) => (currency ?? "USD").ToUpperInvariant() switch
        {
            "AUD" => "A$",
            "TWD" => "NT$",
            "EUR" => "€",
            "JPY" => "¥",
            "GBP" => "£",
            _ => "US$",
        };

        public enum HealthState { NoKey, Ok, InvalidKey, NoCredit, RateLimited, Offline, Unknown }

        public sealed class HealthResult
        {
            public ProviderInfo Provider { get; init; } = new();
            public HealthState State { get; init; } = HealthState.Unknown;
            public string Message { get; init; } = "";
            public string Action { get; init; } = "";
            public double TodayUsd { get; init; }
            public double MonthUsd { get; init; }
            public DateTime CheckedAt { get; init; } = DateTime.Now;

            public string StateLabel => State switch
            {
                HealthState.Ok => "可用",
                HealthState.NoKey => "未設定金鑰",
                HealthState.InvalidKey => "金鑰無效",
                HealthState.NoCredit => "餘額不足",
                HealthState.RateLimited => "已達速率上限",
                HealthState.Offline => "連不上網路",
                _ => "狀態未知"
            };
        }

        public static IReadOnlyList<ProviderInfo> Providers { get; } = new[]
        {
            new ProviderInfo
            {
                Id = "anthropic", DisplayName = "Anthropic（Claude）", EnvName = "ANTHROPIC_API_KEY",
                BillingUrl = "https://console.anthropic.com/settings/billing",
                Note = "文件技能、主回覆"
            },
            new ProviderInfo
            {
                Id = "openai", DisplayName = "OpenAI（GPT・圖片）", EnvName = "OPENAI_API_KEY",
                BillingUrl = "https://platform.openai.com/settings/organization/billing/overview",
                Note = "GPT 模型、圖片生成"
            },
            new ProviderInfo
            {
                Id = "google", DisplayName = "Google（Gemini・Veo 影片）", EnvName = "GEMINI_API_KEY",
                // 「帳單頁」就該開帳單頁。apikey 那一頁是金鑰管理，看不到餘額。
                BillingUrl = "https://aistudio.google.com/app/billing",
                Note = "Gemini 模型、Veo 影片"
            },
            new ProviderInfo
            {
                Id = "perplexity", DisplayName = "Perplexity（搜尋研究）", EnvName = "PERPLEXITY_API_KEY",
                // 同上：settings/api 是金鑰頁，餘額在 console 的帳單頁。
                BillingUrl = "https://console.perplexity.ai/settings/billing",
                Note = "即時搜尋、研究"
            },
        };

        /// <summary>
        /// 模型 ID → 服務代號（帳本分服務加總、餘額扣款、狀態對應用）。
        ///
        /// 對不到就回空字串，而 SpendLedger 與 ProviderBalance 收到空字串都是直接忽略——
        /// 也就是說，這裡漏掉一個前綴，那個模型花的每一塊錢都會從帳本上消失，而且畫面上
        /// 完全看不出來。所以 ① 前綴要列全（含不走 chat 的那些：圖片、影片、語音、嵌入），
        /// ② 真的對不到時由 UsageMeter 留一筆警告，見 UsageMeter.Commit。
        /// </summary>
        public static string ProviderIdForModel(string? modelId)
        {
            string m = (modelId ?? "").Trim().ToLowerInvariant();
            if (m.Length == 0) return "";

            // Perplexity Agent 代跑第三方模型，錢付給 Perplexity——要排在 openai 前綴之前判斷。
            if (m.StartsWith("openai/") || m.StartsWith("anthropic/") || m.StartsWith("google/")) return "perplexity";
            if (m.StartsWith("pplx") || m.StartsWith("sonar") || m.StartsWith("perplexity")) return "perplexity";

            if (m.StartsWith("claude")) return "anthropic";

            if (m.StartsWith("gpt") || m.StartsWith("chatgpt") ||
                m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") ||
                m.StartsWith("dall-e") || m.StartsWith("sora") || m.StartsWith("codex") ||
                m.StartsWith("text-embedding") || m.StartsWith("whisper") ||
                m.StartsWith("tts-") || m.StartsWith("omni-moderation")) return "openai";

            if (m.StartsWith("gemini") || m.StartsWith("gemma") ||
                m.StartsWith("veo") || m.StartsWith("models/veo") ||
                m.StartsWith("imagen")) return "google";

            return "";
        }

        public static async Task<IReadOnlyList<HealthResult>> CheckAllAsync(bool deep, CancellationToken ct)
        {
            var tasks = Providers.Select(p => CheckAsync(p, deep, ct)).ToList();
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public static async Task<HealthResult> CheckAsync(ProviderInfo provider, bool deep, CancellationToken ct)
        {
            string key = provider.Id == "google"
                ? ApiKeyStore.ResolveAny("GEMINI_API_KEY", "GOOGLE_API_KEY")
                : ApiKeyStore.Resolve(provider.EnvName);

            double today = SpendLedger.TodayUsdFor(provider.Id);
            double month = SpendLedger.MonthUsdFor(provider.Id);

            if (string.IsNullOrWhiteSpace(key))
            {
                return new HealthResult
                {
                    Provider = provider,
                    State = HealthState.NoKey,
                    Message = "還沒設定金鑰",
                    Action = "到 設定 → API 貼上金鑰",
                    TodayUsd = today,
                    MonthUsd = month
                };
            }

            try
            {
                using var req = deep ? BuildDeepRequest(provider, key) : BuildQuickRequest(provider, key);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    // 深入檢查送的是真實請求，服務真的會收費。不記帳的話，餘額會比實際多——
                    // 使用者按得越多，顯示的剩餘就越不準。
                    if (deep)
                        RecordDeepProbeUsage(provider, body);

                    return new HealthResult
                    {
                        Provider = provider,
                        State = HealthState.Ok,
                        Message = deep ? "可以正常呼叫" : "金鑰有效、連線正常",
                        Action = deep ? "" : "餘額狀況要「深入檢查」才看得出來",
                        TodayUsd = today,
                        MonthUsd = month
                    };
                }

                // 有些服務沒有公開的清單端點（404/405）：不算錯誤，只是查不到。
                if (!deep && ((int)resp.StatusCode == 404 || (int)resp.StatusCode == 405))
                {
                    return new HealthResult
                    {
                        Provider = provider,
                        State = HealthState.Unknown,
                        Message = "這個服務沒有提供狀態查詢",
                        Action = "用「深入檢查」送一個最小請求才知道",
                        TodayUsd = today,
                        MonthUsd = month
                    };
                }

                return Classify(provider, $"{(int)resp.StatusCode} {body}", today, month);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Classify(provider, ex.Message, today, month);
            }
        }

        private static HealthResult Classify(ProviderInfo provider, string rawError, double today, double month)
        {
            var explained = FailureExplainer.Explain(rawError);
            var state = explained?.Title switch
            {
                "AI 服務的餘額用完了" => HealthState.NoCredit,
                "金鑰無效或沒有權限" => HealthState.InvalidKey,
                "這把金鑰沒有這項功能的權限" => HealthState.InvalidKey,
                "呼叫太頻繁或額度已達上限" => HealthState.RateLimited,
                "連不上網路或服務暫時無法連線" => HealthState.Offline,
                _ => HealthState.Unknown
            };

            return new HealthResult
            {
                Provider = provider,
                State = state,
                Message = explained?.Title ?? FailureExplainer.Describe(rawError, 80),
                Action = explained?.Action ?? "",
                TodayUsd = today,
                MonthUsd = month
            };
        }

        // 快速檢查：各家的「列出模型」端點，不產生 token 費用。
        private static HttpRequestMessage BuildQuickRequest(ProviderInfo provider, string key)
        {
            switch (provider.Id)
            {
                case "anthropic":
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1");
                    req.Headers.Add("x-api-key", key);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    return req;
                }
                case "openai":
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    return req;
                }
                case "google":
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1");
                    req.Headers.Add("x-goog-api-key", key);
                    return req;
                }
                default:
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "https://api.perplexity.ai/models");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    return req;
                }
            }
        }

        /// <summary>把深入檢查那一次真實呼叫的用量記進帳本（金額極小，但不能是黑帳）。</summary>
        private static void RecordDeepProbeUsage(ProviderInfo provider, string body)
        {
            string model = provider.Id switch
            {
                "anthropic" => "claude-haiku-4-5",
                "openai" => "gpt-5.6-sol",
                "google" => "gemini-3.5-flash",
                _ => "sonar",
            };

            int input = 0, output = 0;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    input = ReadUsageInt(usage, "input_tokens", "prompt_tokens", "input_token_count");
                    output = ReadUsageInt(usage, "output_tokens", "completion_tokens", "output_token_count");
                }
                else if (root.TryGetProperty("usageMetadata", out var meta))   // Gemini
                {
                    input = ReadUsageInt(meta, "promptTokenCount");
                    output = ReadUsageInt(meta, "candidatesTokenCount");
                }
            }
            catch { /* 解析不出來就用最小估計，不要因為記帳把檢查弄壞 */ }

            using var purpose = UsageMeter.Purpose("服務可用性檢查");
            UsageMeter.RecordLlm(model, Math.Max(input, 1), Math.Max(output, 1), note: "深入檢查");
        }

        private static int ReadUsageInt(System.Text.Json.JsonElement obj, params string[] names)
        {
            foreach (var n in names)
                if (obj.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out int i))
                    return i;
            return 0;
        }

        // 深入檢查：最小的真實請求（1 個 token 上限），餘額不足這類狀態只有真的呼叫才會回報。
        private static HttpRequestMessage BuildDeepRequest(ProviderInfo provider, string key)
        {
            switch (provider.Id)
            {
                case "anthropic":
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                    req.Headers.Add("x-api-key", key);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    req.Content = Json("{\"model\":\"claude-haiku-4-5\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
                    return req;
                }
                case "openai":
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    req.Content = Json("{\"model\":\"gpt-5.6-sol\",\"input\":\"hi\",\"max_output_tokens\":16}");
                    return req;
                }
                case "google":
                {
                    var req = new HttpRequestMessage(HttpMethod.Post,
                        "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash:generateContent");
                    req.Headers.Add("x-goog-api-key", key);
                    req.Content = Json("{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"hi\"}]}],\"generationConfig\":{\"maxOutputTokens\":1}}");
                    return req;
                }
                default:
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, "https://api.perplexity.ai/v1/sonar");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    req.Content = Json("{\"model\":\"sonar\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
                    return req;
                }
            }
        }

        private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
    }
}
