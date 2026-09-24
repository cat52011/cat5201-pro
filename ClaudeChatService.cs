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

        private static object BuildThinking(int maxOutputTokens)
            => maxOutputTokens >= ThinkingMinOutputTokens
                ? new { type = "adaptive" }
                : new { type = "disabled" };

        // effort 只在開思考時送（disabled 時沿用預設，避免誤觸 disabled+xhigh/max 的 400）。
        private static object? BuildOutputConfig(int maxOutputTokens)
            => maxOutputTokens >= ThinkingMinOutputTokens
                ? new { effort = "medium" }
                : null;

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
                thinking = BuildThinking(maxOutputTokens),
                output_config = BuildOutputConfig(maxOutputTokens),
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
            var (inTok, outTok) = ParseUsage(body);
            ReportUsage(inTok, outTok, instructions, text, onUsage);
            return text;
        }

        // 從非串流回應 JSON 的 usage 區塊取真實 token 數（input_tokens / output_tokens）；解析失敗回 (0,0)。
        private static (int Input, int Output) ParseUsage(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (0, 0);

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
                    return (0, 0);

                int input = usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.TryGetInt32(out var iv) ? iv : 0;
                int output = usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.TryGetInt32(out var ov) ? ov : 0;
                return (input, output);
            }
            catch
            {
                return (0, 0);
            }
        }

        // 成本可稽查：每次呼叫完成當下記一筆帳（模型、用途、token、費用），再回報給呼叫端。
        private void ReportUsage(int input, int output, string? instructions, string text,
            Action<int, int>? onUsage, string note = "")
        {
            UsageMeter.RecordLlm(_model, input, output, instructions, text, note);
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
                thinking = BuildThinking(maxOutputTokens),
                output_config = BuildOutputConfig(maxOutputTokens),
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
                            ProcessSseEvent(currentEventName, data, onDelta, finalText, usage);
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
                ReportUsage(usage.Input, usage.Output, instructions, finalText.ToString(), null, "已取消（部分用量）");
                throw;
            }

            // 串流結束：記帳並把真實 token 用量回報給呼叫端。
            ReportUsage(usage.Input, usage.Output, instructions, finalText.ToString(), onUsage);

            return finalText.ToString();
        }

        // 串流期間累積的真實 token 用量：input 來自 message_start，output 取 message_delta 的最後一筆。
        private sealed class StreamUsage
        {
            public int Input;
            public int Output;
        }

        private static void ProcessSseEvent(
            string? eventName,
            string data,
            Action<string>? onDelta,
            StringBuilder finalText,
            StreamUsage usage)
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