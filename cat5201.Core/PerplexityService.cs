using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// Perplexity API 封裝：
    /// 1. Agent（可含 web_search）
    /// 2. Search
    /// 3. Embeddings
    /// API Key: 環境變數 PERPLEXITY_API_KEY
    /// </summary>
    public sealed class PerplexityService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly string _apiKey;
        private readonly string _agentModel;

        public PerplexityService(string agentModel = "openai/gpt-5.6-sol") // Perplexity Agent API 官方已支援（2026-09 核對）
        {
            _agentModel = agentModel;
            _apiKey = ApiKeyStore.Resolve("PERPLEXITY_API_KEY");

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "找不到 PERPLEXITY_API_KEY。請先在系統環境變數設定 PERPLEXITY_API_KEY。");
            }
        }

        private HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, object payload, bool sse = false)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            if (sse)
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            req.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            return req;
        }

        // =========================
        // Agent API
        // =========================

        public async Task<string> GenerateAgentAsync(
            string instructions,
            string userText,
            bool enableWebSearch = true,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return "";

            var payload = new
            {
                model = _agentModel,
                instructions = instructions,
                input = userText,
                max_output_tokens = maxOutputTokens,
                tools = enableWebSearch ? new object[]
                {
                    new { type = "web_search" }
                } : Array.Empty<object>()
            };

            using var req = CreateJsonRequest(
                HttpMethod.Post,
                "https://api.perplexity.ai/v1/agent",
                payload);

            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Perplexity Agent API 失敗 ({(int)resp.StatusCode}): {body}");

            string text = ExtractAgentOutputText(body);
            RecordAgentUsage(ParseAgentUsage(body), instructions, text, enableWebSearch);
            return text;
        }

        public async Task<string> GenerateAgentStreamAsync(
            string instructions,
            string userText,
            Action<string>? onDelta,
            bool enableWebSearch = true,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return "";

            var payload = new
            {
                model = _agentModel,
                instructions = instructions,
                input = userText,
                stream = true,
                max_output_tokens = maxOutputTokens,
                tools = enableWebSearch ? new object[]
                {
                    new { type = "web_search" }
                } : Array.Empty<object>()
            };

            using var req = CreateJsonRequest(
                HttpMethod.Post,
                "https://api.perplexity.ai/v1/agent",
                payload,
                sse: true);

            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException($"Perplexity Agent 串流失敗 ({(int)resp.StatusCode}): {err}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? currentEvent = null;
            var dataBuilder = new StringBuilder();
            var finalText = new StringBuilder();
            var usage = new AgentUsage();

            try
            {
                while (!reader.EndOfStream)
                {
                    ct.ThrowIfCancellationRequested();

                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;

                    if (line.Length == 0)
                    {
                        if (dataBuilder.Length > 0)
                        {
                            var data = dataBuilder.ToString().Trim();

                            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                                break;

                            ProcessAgentSseEvent(currentEvent, data, onDelta, finalText, usage);
                        }

                        currentEvent = null;
                        dataBuilder.Clear();
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEvent = line.Substring("event:".Length).Trim();
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
                RecordAgentUsage(usage, instructions, finalText.ToString(), enableWebSearch, "已取消（部分用量）");
                throw;
            }

            RecordAgentUsage(usage, instructions, finalText.ToString(), enableWebSearch);
            return finalText.ToString().Trim();
        }

        private static void ProcessAgentSseEvent(
            string? eventName,
            string data,
            Action<string>? onDelta,
            StringBuilder finalText,
            AgentUsage usage)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                // 結束事件（response.completed）帶整個 response：usage 與 output（含搜尋工具呼叫）都在裡面。
                if (root.TryGetProperty("response", out var responseEl) && responseEl.ValueKind == JsonValueKind.Object)
                    usage.MergeFrom(ReadAgentUsage(responseEl));

                string type = root.TryGetProperty("type", out var typeEl)
                    ? (typeEl.GetString() ?? "")
                    : "";

                bool isTextDelta =
                    string.Equals(type, "response.output_text.delta", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(eventName, "response.output_text.delta", StringComparison.OrdinalIgnoreCase);

                if (isTextDelta && root.TryGetProperty("delta", out var deltaEl))
                {
                    var delta = deltaEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(delta))
                    {
                        finalText.Append(delta);
                        onDelta?.Invoke(delta);
                    }
                }
            }
            catch
            {
                // 單一 SSE event 失敗不終止
            }
        }

        // ===== 成本可稽查：Agent API 用量 =====
        // 官方計價：第三方模型 token 依原廠直售價（價目表 openai/gpt-5.6-sol）+ web_search 每次 $0.0025。
        // usage 若直接帶 cost（美元）就用它；否則用 token × 價目表 + 搜尋次數 × 工具費。

        private sealed class AgentUsage
        {
            public int Input;
            public int Output;
            public int WebSearches;
            public double? ReportedCostUsd;

            public void MergeFrom(AgentUsage other)
            {
                if (other.Input > 0) Input = other.Input;
                if (other.Output > 0) Output = other.Output;
                if (other.WebSearches > 0) WebSearches = other.WebSearches;
                if (other.ReportedCostUsd.HasValue) ReportedCostUsd = other.ReportedCostUsd;
            }
        }

        private static AgentUsage ParseAgentUsage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return ReadAgentUsage(doc.RootElement);
            }
            catch
            {
                return new AgentUsage();
            }
        }

        private static AgentUsage ReadAgentUsage(JsonElement response)
        {
            var result = new AgentUsage();

            if (response.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                if (usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.TryGetInt32(out var iv)) result.Input = iv;
                if (usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.TryGetInt32(out var ov)) result.Output = ov;

                if (usageEl.TryGetProperty("cost", out var costEl))
                {
                    if (costEl.ValueKind == JsonValueKind.Number && costEl.TryGetDouble(out var c))
                        result.ReportedCostUsd = c;
                    else if (costEl.ValueKind == JsonValueKind.Object &&
                             costEl.TryGetProperty("total_cost", out var totalEl) &&
                             totalEl.TryGetDouble(out var tc))
                        result.ReportedCostUsd = tc;
                }
            }

            if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    string type = item.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                    if (type.Contains("search", StringComparison.OrdinalIgnoreCase))
                        result.WebSearches++;
                }
            }

            return result;
        }

        private void RecordAgentUsage(AgentUsage usage, string instructions, string text, bool enableWebSearch, string note = "")
        {
            // 有開搜尋工具但回應裡數不到呼叫次數時，保守算 1 次（寧可高估一點不漏記）。
            int searches = usage.WebSearches > 0 ? usage.WebSearches : (enableWebSearch ? 1 : 0);
            double toolUsd = searches * ModelCostEstimator.PerplexityAgentWebSearchUsd;

            if (usage.ReportedCostUsd.HasValue && usage.ReportedCostUsd.Value > 0)
            {
                UsageMeter.RecordLlm(_agentModel, usage.Input, usage.Output, instructions, text,
                    note: AppendNote(note, "費用取自 API 回報"), extraUsd: 0);
                return;
            }

            UsageMeter.RecordLlm(_agentModel, usage.Input, usage.Output, instructions, text,
                note: AppendNote(note, searches > 0 ? $"含網路搜尋 {searches} 次" : ""), extraUsd: toolUsd);
        }

        private static string AppendNote(string a, string b)
            => string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : $"{a}；{b}";

        private static string ExtractAgentOutputText(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("output", out var output) ||
                    output.ValueKind != JsonValueKind.Array)
                    return "";

                var sb = new StringBuilder();

                foreach (var item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var c in content.EnumerateArray())
                    {
                        if (!c.TryGetProperty("type", out var typeEl))
                            continue;

                        var type = typeEl.GetString() ?? "";
                        if (!string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (c.TryGetProperty("text", out var textEl))
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
                }

                return sb.ToString().Trim();
            }
            catch
            {
                return "";
            }
        }

        // =========================
        // Search API
        // =========================

        public sealed class SearchResultItem
        {
            public string Title { get; set; } = "";
            public string Url { get; set; } = "";
            public string Snippet { get; set; } = "";
            public string Date { get; set; } = "";
        }

        public async Task<List<SearchResultItem>> SearchAsync(
            string query,
            int maxResults = 10,
            string? country = null,
            int maxTokens = 10000,
            int maxTokensPerPage = 4096,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<SearchResultItem>();

            var payload = new
            {
                query = query,
                country = country,
                max_results = maxResults,
                max_tokens = maxTokens,
                max_tokens_per_page = maxTokensPerPage
            };

            using var req = CreateJsonRequest(
                HttpMethod.Post,
                "https://api.perplexity.ai/search",
                payload);

            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Perplexity Search API 失敗 ({(int)resp.StatusCode}): {body}");

            // Search API 按次收費（$5/千次），成功回應即計費——就算結果是空的也一樣。
            UsageMeter.RecordMetered(UsageKinds.Search, "perplexity-search", 1, "次",
                ModelCostEstimator.PerplexitySearchRequestUsd, isActual: true);

            return ExtractSearchResults(body);
        }

        private static List<SearchResultItem> ExtractSearchResults(string json)
        {
            var results = new List<SearchResultItem>();

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Perplexity Search 回傳欄位可能依版本調整，所以這裡做寬鬆解析
                foreach (var propName in new[] { "results", "search_results", "data" })
                {
                    if (!root.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var item in arr.EnumerateArray())
                    {
                        string title = item.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
                        string url = item.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
                        string snippet = item.TryGetProperty("snippet", out var s) ? (s.GetString() ?? "") : "";
                        string date = item.TryGetProperty("date", out var d) ? (d.GetString() ?? "") : "";

                        results.Add(new SearchResultItem
                        {
                            Title = title,
                            Url = url,
                            Snippet = snippet,
                            Date = date
                        });
                    }

                    if (results.Count > 0)
                        return results;
                }
            }
            catch
            {
            }

            return results;
        }

        // =========================
        // Embeddings API
        // =========================

        public sealed class EmbeddingResponse
        {
            public string Model { get; set; } = "";
            public List<EmbeddingItem> Data { get; set; } = new();
        }

        public sealed class EmbeddingItem
        {
            public int Index { get; set; }
            public string RawBase64 { get; set; } = "";
            public sbyte[] Int8Vector { get; set; } = Array.Empty<sbyte>();
        }

        public async Task<EmbeddingResponse> CreateEmbeddingsAsync(
    IEnumerable<string> input,
    string model = "pplx-embed-v1-0.6b",
    int? dimensions = null,
    string encodingFormat = "base64_int8",
    CancellationToken ct = default)
        {
            var inputArray = input == null
                ? new List<string>()
                : new List<string>(input);

            if (inputArray.Count == 0)
                throw new InvalidOperationException("Embedding input 不可為空。");

            var payload = new
            {
                input = inputArray,
                model = model,
                dimensions = dimensions,
                encoding_format = encodingFormat
            };

            using var req = CreateJsonRequest(
                HttpMethod.Post,
                "https://api.perplexity.ai/v1/embeddings",
                payload);

            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Perplexity Embeddings API 失敗 ({(int)resp.StatusCode}): {body}");

            // Embeddings $0.004/百萬 token：金額極小，但照樣入帳（估算 token，用途＝記憶索引）。
            int embedTokens = inputArray.Sum(ModelCostEstimator.EstimateTokens);
            UsageMeter.RecordMetered(UsageKinds.Search, model, embedTokens, "tokens",
                embedTokens / 1_000_000.0 * 0.004, isActual: false, note: "embeddings");

            return ExtractEmbeddings(body, encodingFormat);
        }

        private static EmbeddingResponse ExtractEmbeddings(string json, string encodingFormat)
        {
            var result = new EmbeddingResponse();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("model", out var modelEl))
                result.Model = modelEl.GetString() ?? "";

            if (root.TryGetProperty("data", out var dataEl) &&
                dataEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataEl.EnumerateArray())
                {
                    var emb = new EmbeddingItem();

                    if (item.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var idx))
                        emb.Index = idx;

                    if (item.TryGetProperty("embedding", out var vectorEl))
                    {
                        if (vectorEl.ValueKind == JsonValueKind.String)
                        {
                            emb.RawBase64 = vectorEl.GetString() ?? "";

                            if (string.Equals(encodingFormat, "base64_int8", StringComparison.OrdinalIgnoreCase))
                                emb.Int8Vector = DecodeBase64Int8(emb.RawBase64);
                        }
                    }

                    result.Data.Add(emb);
                }
            }

            return result;
        }

        private static sbyte[] DecodeBase64Int8(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                return Array.Empty<sbyte>();

            var bytes = Convert.FromBase64String(base64);
            var result = new sbyte[bytes.Length];

            for (int i = 0; i < bytes.Length; i++)
                result[i] = unchecked((sbyte)bytes[i]);

            return result;
        }

        public static double CosineSimilarity(IReadOnlyList<sbyte> a, IReadOnlyList<sbyte> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0 || a.Count != b.Count)
                return 0;

            double dot = 0;
            double na = 0;
            double nb = 0;

            for (int i = 0; i < a.Count; i++)
            {
                double da = a[i];
                double db = b[i];
                dot += da * db;
                na += da * da;
                nb += db * db;
            }

            if (na <= 0 || nb <= 0)
                return 0;

            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }
    }
}

