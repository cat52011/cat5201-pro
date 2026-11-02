using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// 最小可用的 Claude Messages API 呼叫封裝
    /// 支援：
    /// 1. 一次性完整回傳
    /// 2. SSE 串流逐步輸出
    /// API Key 讀取：環境變數 ANTHROPIC_API_KEY
    /// </summary>
    public sealed class ClaudeChatService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly string _apiKey;
        private readonly string _model;

        // ===== Claude 5 思考策略（2026-09-17 升級 Sonnet 5 / Opus 5）=====
        // Claude 5 家族「不帶 thinking 欄位＝自動開 adaptive 思考、預設 effort=high」（舊 4.6/4.8 是不思考）。
        // max_tokens 是「思考＋回答」共用的硬上限，不處理會出兩個問題：
        //   ①小預算的工具性呼叫（關鍵字/分段規劃等幾百 token）可能整包被思考吃光 → 回答變空；
        //   ②主回答預設 high effort 思考太多 → 延遲與成本暴增、我們的續寫判斷也會誤判「被截斷」。
        // 策略：主回答（預算夠大）開 adaptive＋medium effort（與 OpenAI 端 reasoning=medium 對稱）；
        //      小預算呼叫關閉思考（disabled 在預設 high effort 下合法；xhigh/max 才會 400，我們不送）。
        // 回應解析只挑 type=="text"/"text_delta"，思考區塊天然被忽略，不需改解析。
        private const int ThinkingMinOutputTokens = 4000;

        // ===== 小預算呼叫怎麼「關思考」——每個型號不一樣（2026-10-29 補）=====
        // 上面的策略寫於 Sonnet 5 / Opus 5 時代，當時 {type:"disabled"} 對兩者都合法。
        // 但 2026-09-25 把 Claude_Opus5 升成 claude-opus-5-5 之後，這條路就壞了：
        //   Opus 5.5 / Fable 5 / Fable 5.1：沒有關閉選項，disabled 在任何 effort 都回 400
        //                                  → 只能不帶 thinking、把 effort 降到 low
        //   Sonnet 5.5：disabled 回 400；關法是 {type:"between_tools"}，而且不得帶任何其他欄位
        //              （effort 必須 ≤ high，所以 output_config 一律不送，用預設 high）
        //   Sonnet 5 / Opus 5 / Haiku 5.5 / 4.x：disabled 合法（effort ≤ high）
        // 小預算呼叫一旦 400，會被 fallback 默默換成別的模型重跑——不報錯，只是多花一次錢、
        // 決策窗顯示的模型也跟使用者選的不一樣。所以這裡要依型號給出各自合法的設定。
        internal enum SmallBudgetThinking { Disable, BetweenTools, LowEffortOnly }

        internal static SmallBudgetThinking ResolveSmallBudgetThinking(string? model)
        {
            string m = (model ?? "").Trim().ToLowerInvariant();

            if (m.StartsWith("claude-opus-5-5", StringComparison.Ordinal)
                || m.StartsWith("claude-fable-5", StringComparison.Ordinal)
                || m.StartsWith("claude-mythos-5", StringComparison.Ordinal))
                return SmallBudgetThinking.LowEffortOnly;

            if (m.StartsWith("claude-sonnet-5-5", StringComparison.Ordinal))
                return SmallBudgetThinking.BetweenTools;

            return SmallBudgetThinking.Disable;
        }

        internal static object? BuildThinking(string? model, int maxOutputTokens)
        {
            if (maxOutputTokens >= ThinkingMinOutputTokens)
                return new { type = "adaptive" };

            return ResolveSmallBudgetThinking(model) switch
            {
                SmallBudgetThinking.LowEffortOnly => null,                       // 不帶 thinking 欄位
                SmallBudgetThinking.BetweenTools => new { type = "between_tools" },
                _ => new { type = "disabled" },
            };
        }

        // effort 只在開思考時送（disabled 時沿用預設，避免誤觸 disabled+xhigh/max 的 400）；
        // 唯一例外是關不掉思考的型號，小預算時要明確送 low，否則思考會吃掉那幾百 token 的預算。
        internal static object? BuildOutputConfig(string? model, int maxOutputTokens)
        {
            if (maxOutputTokens >= ThinkingMinOutputTokens)
                return new { effort = "medium" };

            return ResolveSmallBudgetThinking(model) == SmallBudgetThinking.LowEffortOnly
                ? new { effort = "low" }
                : null;
        }

        private static readonly JsonSerializerOptions PayloadJsonOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public ClaudeChatService(string model = AiModels.DefaultClaudeModel)
        {
            _model = model;
            _apiKey = ApiKeyStore.Resolve("ANTHROPIC_API_KEY");

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "找不到 ANTHROPIC_API_KEY。請先在系統環境變數設定 ANTHROPIC_API_KEY。");
            }
        }

        public Task<string> GenerateAsync(
            string instructions,
            string userText,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return Task.FromResult("");

            var content = new List<object>
            {
                new { type = "text", text = userText }
            };

            return GenerateAsync(instructions, content, maxOutputTokens, ct);
        }

        /// <summary>
        /// contentBlocks 格式：
        /// Claude message content block 陣列
        /// 例如：
        /// new { type = "text", text = "hello" }
        /// new { type = "image", source = ... }
        /// new { type = "document", source = ... }
        /// </summary>
        public async Task<string> GenerateAsync(
            string instructions,
            IEnumerable<object> contentBlocks,
            int maxOutputTokens = 8000,
            CancellationToken ct = default,
            Action<int, int>? onUsage = null)
        {
            var payload = new
            {
                model = _model,
                max_tokens = maxOutputTokens,
                thinking = BuildThinking(_model, maxOutputTokens),
                output_config = BuildOutputConfig(_model, maxOutputTokens),
                system = instructions,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = contentBlocks
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload, PayloadJsonOptions),
                Encoding.UTF8,
                "application/json");

            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                ProviderStatusMonitor.ReportFailure("anthropic", $"{(int)resp.StatusCode} {body}");
                throw new InvalidOperationException($"Claude API 失敗 ({(int)resp.StatusCode}): {body}");
            }

            string text = ExtractTextFromClaudeJson(body);
            var (inTok, outTok, cacheRead, cacheWrite) = ParseUsage(body, _model);
            ReportUsage(inTok, outTok, instructions, text, onUsage, cacheRead: cacheRead, cacheWrite: cacheWrite);
            return text;
        }

        // 從非串流回應 JSON 的 usage 區塊取真實 token 數（input_tokens / output_tokens）；解析失敗回 (0,0)。
        private static (int Input, int Output, int CacheRead, int CacheWrite) ParseUsage(string json, string modelId)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (0, 0, 0, 0);

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
                    return (0, 0, 0, 0);

                int input = usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.TryGetInt32(out var iv) ? iv : 0;
                int output = usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.TryGetInt32(out var ov) ? ov : 0;
                int cacheWrite = usageEl.TryGetProperty("cache_creation_input_tokens", out var w) && w.TryGetInt32(out var wv) ? wv : 0;
                int cacheRead = usageEl.TryGetProperty("cache_read_input_tokens", out var r) && r.TryGetInt32(out var rv) ? rv : 0;

                // 四個數字原樣往上傳，不在這裡折算。
                // 快取要收錢沒錯，但把「乘過倍率的等值量」塞進 input_tokens，
                // 顯示出來的「實際 token」就不是實際的了（2026-10-27 第三方審查）。
                return (input, output, cacheRead, cacheWrite);
            }
            catch
            {
                return (0, 0, 0, 0);
            }
        }

        // 成本可稽查：每次呼叫完成當下記一筆帳（模型、用途、token、費用），再回報給呼叫端。
        private void ReportUsage(int input, int output, string? instructions, string text,
            Action<int, int>? onUsage, string note = "", int cacheRead = 0, int cacheWrite = 0)
        {
            // Anthropic 的 input_tokens 不含快取，所以走「另計」這組參數。
            UsageMeter.RecordLlm(_model, input, output, instructions, text, note,
                cacheReadTokens: cacheRead, cacheWriteTokens: cacheWrite);
            if (onUsage != null && (input > 0 || output > 0))
                onUsage(input, output);
        }

        public Task<string> GenerateStreamAsync(
            string instructions,
            string userText,
            Action<string>? onDelta,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return Task.FromResult("");

            var content = new List<object>
            {
                new { type = "text", text = userText }
            };

            return GenerateStreamAsync(instructions, content, onDelta, maxOutputTokens, ct);
        }

        public async Task<string> GenerateStreamAsync(
            string instructions,
            IEnumerable<object> contentBlocks,
            Action<string>? onDelta,
            int maxOutputTokens = 8000,
            CancellationToken ct = default,
            Action<int, int>? onUsage = null)
        {
            var payload = new
            {
                model = _model,
                max_tokens = maxOutputTokens,
                thinking = BuildThinking(_model, maxOutputTokens),
                output_config = BuildOutputConfig(_model, maxOutputTokens),
                system = instructions,
                stream = true,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = contentBlocks
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload, PayloadJsonOptions),
                Encoding.UTF8,
                "application/json");

            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                ProviderStatusMonitor.ReportFailure("anthropic", $"{(int)resp.StatusCode} {err}");
                throw new InvalidOperationException($"Claude API 串流失敗 ({(int)resp.StatusCode}): {err}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? currentEventName = null;
            var dataBuilder = new StringBuilder();
            var finalText = new StringBuilder();
            var usage = new StreamUsage();

            try
            {
                while (!reader.EndOfStream)
                {
                    ct.ThrowIfCancellationRequested();

                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                        break;

                    if (line.Length == 0)
                    {
                        if (dataBuilder.Length > 0)
                        {
                            var data = dataBuilder.ToString().Trim();
                            ProcessSseEvent(currentEventName, data, onDelta, finalText, usage, _model);
                        }

                        currentEventName = null;
                        dataBuilder.Clear();
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEventName = line.Substring("event:".Length).Trim();
                        continue;
                    }

                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (dataBuilder.Length > 0)
                            dataBuilder.Append('\n');

                        dataBuilder.Append(line.Substring("data:".Length).TrimStart());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消時已產生的 token 照樣計費：用已知用量（或已收到的文字估算）記一筆再往外丟。
                ReportUsage(usage.Input, usage.Output, instructions, finalText.ToString(), null, "已取消（部分用量）",
                    cacheRead: usage.CacheRead, cacheWrite: usage.CacheWrite);
                throw;
            }

            // 串流結束：記帳並把真實 token 用量回報給呼叫端。
            ReportUsage(usage.Input, usage.Output, instructions, finalText.ToString(), onUsage,
                cacheRead: usage.CacheRead, cacheWrite: usage.CacheWrite);

            return finalText.ToString();
        }

        // 串流期間累積的真實 token 用量：input 來自 message_start，output 取 message_delta 的最後一筆。
        private sealed class StreamUsage
        {
            public int Input;
            public int Output;
            public int CacheRead;
            public int CacheWrite;
        }

        private static void ProcessSseEvent(
            string? eventName,
            string data,
            Action<string>? onDelta,
            StringBuilder finalText,
            StreamUsage usage,
            string modelId)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                string type = "";
                if (root.TryGetProperty("type", out var typeEl))
                    type = typeEl.GetString() ?? "";

                // message_start：帶 input_tokens（真實輸入，含系統提示/上下文）+ 初始 output_tokens。
                if (string.Equals(type, "message_start", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("message", out var msgEl) &&
                    msgEl.ValueKind == JsonValueKind.Object &&
                    msgEl.TryGetProperty("usage", out var startUsage) &&
                    startUsage.ValueKind == JsonValueKind.Object)
                {
                    if (startUsage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv))
                        usage.Input = iv;
                    if (startUsage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov))
                        usage.Output = ov;

                    // 串流一樣原值分開記：快取量不併進 Input，否則顯示的「實際 token」就不是實際的。
                    if (startUsage.TryGetProperty("cache_creation_input_tokens", out var cw) && cw.TryGetInt32(out var cwv))
                        usage.CacheWrite = cwv;
                    if (startUsage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt32(out var crv))
                        usage.CacheRead = crv;
                }

                // message_delta：output_tokens 為到目前為止的累計，取最後一筆即為最終輸出量。
                if (string.Equals(type, "message_delta", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("usage", out var deltaUsage) &&
                    deltaUsage.ValueKind == JsonValueKind.Object &&
                    deltaUsage.TryGetProperty("output_tokens", out var od) && od.TryGetInt32(out var odv))
                {
                    usage.Output = odv;
                }

                bool isTextDelta =
                    string.Equals(type, "content_block_delta", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase);

                if (isTextDelta &&
                    root.TryGetProperty("delta", out var deltaEl) &&
                    deltaEl.ValueKind == JsonValueKind.Object)
                {
                    var deltaType = deltaEl.TryGetProperty("type", out var dt) ? (dt.GetString() ?? "") : "";
                    if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var delta = deltaEl.TryGetProperty("text", out var textEl) ? (textEl.GetString() ?? "") : "";
                        if (!string.IsNullOrEmpty(delta))
                        {
                            finalText.Append(delta);
                            onDelta?.Invoke(delta);
                        }
                    }
                }
            }
            catch
            {
                // 不因單一事件解析失敗而中止整條串流
            }
        }

        private static string ExtractTextFromClaudeJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                    return "";

                var sb = new StringBuilder();

                foreach (var item in content.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var typeEl))
                        continue;

                    var type = typeEl.GetString() ?? "";
                    if (!string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (item.TryGetProperty("text", out var textEl))
                    {
                        var t = textEl.GetString();
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            if (sb.Length > 0)
                                sb.AppendLine();

                            sb.Append(t.Trim());
                        }
                    }
                }

                return sb.ToString().Trim();
            }
            catch
            {
                return "";
            }
        }

        public static object BuildTextBlock(string text)
            => new { type = "text", text = text ?? "" };

        public static object BuildImageBlock(byte[] bytes, string mimeType)
            => new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = mimeType,
                    data = Convert.ToBase64String(bytes)
                }
            };

        public static object BuildPdfBlock(byte[] bytes)
            => new
            {
                type = "document",
                source = new
                {
                    type = "base64",
                    media_type = "application/pdf",
                    data = Convert.ToBase64String(bytes)
                }
            };

        public static object BuildPlainTextDocumentBlock(byte[] bytes, string mimeType)
            => new
            {
                type = "document",
                source = new
                {
                    type = "base64",
                    media_type = mimeType,
                    data = Convert.ToBase64String(bytes)
                }
            };
    }
}