using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// 用 Claude API 的 Agent Skills（Anthropic 官方 pptx / docx / xlsx 技能）＋程式執行沙盒產出文件——
    /// 和 Claude App 做簡報/報告是同一套機制：模型讀技能的設計準則、在沙盒裡寫程式產檔、轉圖檢查、修正後交付。
    ///
    /// 內建 PptxBuilder 只能把 JSON 大綱套進固定版型（標題列＋條列文字），品質上限就在那裡；
    /// 這條路徑讓模型自己決定每頁版面、畫圖表、做視覺 QA。
    ///
    /// 協定（官方 skills guide / tool-use 文件，2026-09-17 核對）：
    ///   POST /v1/messages，header anthropic-beta: code-execution-2025-08-25
    ///   body.container.skills = [{type:"anthropic", skill_id:"pptx", version:"latest"}]
    ///   body.tools = [{type:"code_execution_20260521", name:"code_execution"}]（舊版 ID 20250825 作退路）
    ///   stop_reason=pause_turn → 帶著同一個 container.id、把 assistant content 接回 messages 再送（不要加「繼續」）
    ///   產出檔 file_id 在 bash_code_execution_tool_result.content.content[].file_id → GET /v1/files/{id}(/content)
    /// 用 raw HTTP 與專案內既有的 ClaudeChatService 一致。
    /// </summary>
    public sealed class ClaudeDocumentSkillsService
    {
        private const string ApiBase = "https://api.anthropic.com/v1";
        private const string ApiVersion = "2023-06-01";
        private const string CodeExecutionBeta = "code-execution-2025-08-25";
        private const string FallbackBeta = "server-side-fallback-2026-07-01";
        public const string ToolTypeCurrent = "code_execution_20260521";
        public const string ToolTypeLegacy = "code_execution_20250825";

        // 伺服器端工具迴圈每 10 步會 pause_turn 一次；一份精緻簡報常需要數十步（讀技能、寫、轉圖、修）。
        private const int MaxRounds = 16;
        private const int MaxOutputTokensPerRound = 64000;

        private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

        private readonly string _apiKey;
        private readonly string _model;

        public ClaudeDocumentSkillsService(string model)
        {
            _apiKey = ApiKeyStore.Resolve("ANTHROPIC_API_KEY");
            _model = model;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);
        public string Model => _model;

        public sealed class SkillFile
        {
            public string FileName { get; init; } = "";
            public string Extension { get; init; } = "";
            public byte[] Bytes { get; init; } = Array.Empty<byte>();
        }

        public sealed class Result
        {
            public bool Success { get; init; }
            public IReadOnlyList<SkillFile> Files { get; init; } = Array.Empty<SkillFile>();
            /// <summary>Claude 最後給使用者的說明文字。</summary>
            public string Summary { get; init; } = "";
            public string ErrorMessage { get; init; } = "";
            public int Rounds { get; init; }
            public int ToolSteps { get; init; }
        }

        /// <summary>
        /// 產出文件。wantedExtensions 例：[".pptx", ".pdf"]；同副檔名有多個時取最後產生的那個（通常是修正後的最終版）。
        /// onProgress(輪次, 已執行步驟數) 供 loading 提示用。
        /// </summary>
        public async Task<Result> GenerateAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyList<string> skillIds,
            IReadOnlyCollection<string> wantedExtensions,
            Action<int, int>? onProgress,
            CancellationToken ct)
        {
            if (!IsConfigured)
                return Fail("找不到 ANTHROPIC_API_KEY。");

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = userPrompt ?? "" }
            };

            string? containerId = null;
            string toolType = ToolTypeCurrent;
            bool useFallbacks = true;
            bool useCache = true;
            var fileIds = new List<string>();
            var finalText = new StringBuilder();
            int toolSteps = 0;
            int round = 0;

            while (round < MaxRounds)
            {
                ct.ThrowIfCancellationRequested();
                round++;
                onProgress?.Invoke(round, toolSteps);

                var body = BuildRequestBody(_model, systemPrompt, messages, skillIds, containerId, toolType, useFallbacks, useCache);

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/messages");
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", ApiVersion);
                req.Headers.Add("anthropic-beta", useFallbacks ? $"{CodeExecutionBeta},{FallbackBeta}" : CodeExecutionBeta);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    string err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    // 相容退路：伺服器不認得 fallbacks 參數或新版工具 ID 時，退一步重送同一輪（不計輪次）。
                    if ((int)resp.StatusCode == 400 && useFallbacks && err.Contains("fallback", StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Warn("DocSkills", "fallbacks 參數不被接受，改為不帶重送：" + Shorten(err, 300));
                        useFallbacks = false;
                        round--;
                        continue;
                    }
                    if ((int)resp.StatusCode == 400 && useCache && err.Contains("cache", StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Warn("DocSkills", "自動快取參數不被接受，改為不帶重送：" + Shorten(err, 300));
                        useCache = false;
                        round--;
                        continue;
                    }
                    if ((int)resp.StatusCode == 400 && toolType == ToolTypeCurrent && err.Contains("code_execution", StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Warn("DocSkills", "新版 code_execution 工具 ID 不被接受，改用舊版：" + Shorten(err, 300));
                        toolType = ToolTypeLegacy;
                        round--;
                        continue;
                    }

                    return Fail($"Claude 文件技能 API {(int)resp.StatusCode}：{Shorten(err, 400)}", round, toolSteps);
                }

                var assembler = new SseMessageAssembler();
                try
                {
                    using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    await assembler.ReadAsync(reader, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 已產生的用量照樣計費。
                    RecordUsage(assembler.Message, "已取消（部分用量）");
                    throw;
                }

                var message = assembler.Message;
                RecordUsage(message);

                if (!string.IsNullOrWhiteSpace(assembler.StreamError))
                    return Fail($"Claude 文件技能串流錯誤：{assembler.StreamError}", round, toolSteps);

                containerId = ReadContainerId(message) ?? containerId;

                var content = message["content"] as JsonArray ?? new JsonArray();
                toolSteps += content.Count(b => (string?)b?["type"] == "server_tool_use");
                fileIds.AddRange(ExtractFileIds(content));

                string roundText = ExtractFinalText(content);
                string stopReason = (string?)message["stop_reason"] ?? "";

                if (stopReason == "pause_turn")
                {
                    // 官方做法：把這輪 assistant 內容原樣接回，用同一個 container 再送，伺服器會從中斷處續跑。
                    var replay = new JsonArray(content.Where(b => b != null).Select(b => (JsonNode?)b!.DeepClone()).ToArray());
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = replay });
                    continue;
                }

                if (stopReason == "refusal")
                    return Fail("Claude 拒絕了這個文件產出請求。", round, toolSteps);

                if (!string.IsNullOrWhiteSpace(roundText))
                    finalText.Append(roundText);

                if (stopReason == "max_tokens" && fileIds.Count == 0)
                    return Fail("Claude 單輪輸出達上限，文件未完成。", round, toolSteps);

                break; // end_turn（或 max_tokens 但已有檔案）
            }

            if (round >= MaxRounds && fileIds.Count == 0)
                return Fail($"超過 {MaxRounds} 輪仍未完成文件。", round, toolSteps);

            onProgress?.Invoke(round, toolSteps);

            // 下載：先讀 metadata 取檔名（依產生順序），再挑出最終交付檔。
            var named = new List<(string Id, string Name)>();
            foreach (var id in fileIds.Distinct())
            {
                string? name = await TryGetFileNameAsync(id, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(name))
                    named.Add((id, name!));
            }

            var files = new List<SkillFile>();
            foreach (var (id, name) in SelectDeliverables(named, wantedExtensions))
            {
                byte[] bytes = await DownloadAsync(id, ct).ConfigureAwait(false);
                if (bytes.Length > 0)
                    files.Add(new SkillFile
                    {
                        FileName = Path.GetFileName(name),
                        Extension = Path.GetExtension(name).ToLowerInvariant(),
                        Bytes = bytes
                    });
            }

            if (files.Count == 0)
                return Fail("Claude 已執行但沒有產出可下載的文件檔。", round, toolSteps, finalText.ToString());

            return new Result
            {
                Success = true,
                Files = files,
                Summary = finalText.ToString().Trim(),
                Rounds = round,
                ToolSteps = toolSteps
            };
        }

        // ===== 可測試的純函式 =====

        public static JsonObject BuildRequestBody(
            string model,
            string systemPrompt,
            JsonArray messages,
            IReadOnlyList<string> skillIds,
            string? containerId,
            string toolType,
            bool useFallbacks,
            bool useCache = true)
        {
            var skills = new JsonArray();
            foreach (var id in skillIds.Distinct(StringComparer.OrdinalIgnoreCase))
                skills.Add(new JsonObject { ["type"] = "anthropic", ["skill_id"] = id, ["version"] = "latest" });

            var container = new JsonObject { ["skills"] = skills };
            if (!string.IsNullOrWhiteSpace(containerId))
                container["id"] = containerId;

            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = MaxOutputTokensPerRound,
                ["stream"] = true,
                // Opus 5 / Sonnet 5：自適應思考；effort 不帶＝預設 high（文件設計屬高品質需求）。
                ["thinking"] = new JsonObject { ["type"] = "adaptive" },
                ["system"] = systemPrompt ?? "",
                ["messages"] = messages.DeepClone(),
                ["container"] = container,
                ["tools"] = new JsonArray { new JsonObject { ["type"] = toolType, ["name"] = "code_execution" } }
            };

            if (useFallbacks)
                body["fallbacks"] = "default";

            // 自動提示快取：pause_turn 續跑每輪都重送整段對話與技能內容，快取讀取只收 1 折（實測不開快取時一張標題頁就吃 55k 輸入 token）。
            if (useCache)
                body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

            return body;
        }

        /// <summary>
        /// 從沙盒產生過的所有檔案（依產生順序）挑出最終交付檔：
        /// 每種主檔（pptx/docx/xlsx）取最後產生的一個（通常是修正後的版本）；
        /// PDF 依「同主檔名」配對（deck.pptx ↔ deck.pdf），只有一種主檔時配不到就取最後一個 PDF。
        /// 中途的縮圖、HTML、草稿檔一律不收。
        /// </summary>
        public static IReadOnlyList<(string Id, string Name)> SelectDeliverables(
            IReadOnlyList<(string Id, string Name)> producedInOrder,
            IReadOnlyCollection<string> wantedExtensions)
        {
            static string Ext(string name) => Path.GetExtension(name).ToLowerInvariant();
            static string Base(string name) => Path.GetFileNameWithoutExtension(name);

            var primaries = new List<(string Id, string Name)>();
            foreach (var ext in wantedExtensions.Where(e => !string.Equals(e, ".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                var last = producedInOrder.LastOrDefault(f => Ext(f.Name) == ext.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(last.Id))
                    primaries.Add(last);
            }

            var result = new List<(string Id, string Name)>(primaries);
            if (!wantedExtensions.Contains(".pdf", StringComparer.OrdinalIgnoreCase))
                return result;

            var pdfs = producedInOrder.Where(f => Ext(f.Name) == ".pdf").ToList();
            foreach (var primary in primaries)
            {
                var match = pdfs.LastOrDefault(p => string.Equals(Base(p.Name), Base(primary.Name), StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Id))
                    result.Add(match);
            }

            if (primaries.Count == 1 && result.Count == 1 && pdfs.Count > 0)
                result.Add(pdfs[^1]);

            return result;
        }

        /// <summary>遞迴找出工具結果區塊裡所有 file_id（依出現順序，後面的＝較晚產生）。</summary>
        public static IReadOnlyList<string> ExtractFileIds(JsonArray content)
        {
            var ids = new List<string>();

            void Walk(JsonNode? node)
            {
                switch (node)
                {
                    case JsonObject obj:
                        foreach (var kv in obj)
                        {
                            if (kv.Key == "file_id" && kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                                ids.Add(s);
                            else
                                Walk(kv.Value);
                        }
                        break;
                    case JsonArray arr:
                        foreach (var item in arr) Walk(item);
                        break;
                }
            }

            foreach (var block in content)
            {
                string type = (string?)block?["type"] ?? "";
                if (type.EndsWith("_tool_result", StringComparison.Ordinal))
                    Walk(block);
            }

            return ids;
        }

        public static string ExtractText(JsonArray content)
        {
            var sb = new StringBuilder();
            foreach (var block in content)
            {
                if ((string?)block?["type"] == "text")
                    sb.Append((string?)block["text"] ?? "");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 給使用者看的收尾說明：只取「最後一次工具動作之後」的文字——前面的文字是過程旁白（例如「我先讀技能說明」）。
        /// </summary>
        public static string ExtractFinalText(JsonArray content)
        {
            int lastTool = -1;
            for (int i = 0; i < content.Count; i++)
            {
                string type = (string?)content[i]?["type"] ?? "";
                if (type == "server_tool_use" || type.EndsWith("_tool_result", StringComparison.Ordinal))
                    lastTool = i;
            }

            var sb = new StringBuilder();
            for (int i = lastTool + 1; i < content.Count; i++)
            {
                if ((string?)content[i]?["type"] == "text")
                    sb.Append((string?)content[i]!["text"] ?? "");
            }
            return sb.ToString();
        }

        public static string? ReadContainerId(JsonObject message)
            => (string?)message["container"]?["id"];

        // ===== 內部 =====

        private void RecordUsage(JsonObject message, string note = "")
        {
            var usage = message["usage"] as JsonObject;
            if (usage == null)
                return;

            int input = ReadInt(usage, "input_tokens");
            int output = ReadInt(usage, "output_tokens");
            int cacheWrite = ReadInt(usage, "cache_creation_input_tokens");
            int cacheRead = ReadInt(usage, "cache_read_input_tokens");

            // 快取寫入 1.25 倍、讀取 0.1 倍輸入單價（Anthropic 官方計價）；沙盒時間每月 1,550 小時免費，超過 $0.05/小時，此處不計。
            string modelId = ModelCostEstimator.ResolvePricingModelId((string?)message["model"] ?? _model);
            double cacheUsd =
                ModelCostEstimator.FromUsage(modelId, cacheWrite, 0).UsdCost * 1.25 +
                ModelCostEstimator.FromUsage(modelId, cacheRead, 0).UsdCost * 0.10;

            UsageMeter.RecordLlm(modelId, input, output, note: note, extraUsd: cacheUsd);
        }

        private static int ReadInt(JsonObject obj, string key)
            => obj[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

        private async Task<string?> TryGetFileNameAsync(string fileId, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/files/{Uri.EscapeDataString(fileId)}");
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", ApiVersion);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return null;
                var meta = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return (string?)meta?["filename"];
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("DocSkills", $"讀取檔案資訊失敗：{fileId}", ex);
                return null;
            }
        }

        private async Task<byte[]> DownloadAsync(string fileId, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/files/{Uri.EscapeDataString(fileId)}/content");
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", ApiVersion);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode
                    ? await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)
                    : Array.Empty<byte>();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("DocSkills", $"下載產出檔失敗：{fileId}", ex);
                return Array.Empty<byte>();
            }
        }

        private static Result Fail(string error, int rounds = 0, int steps = 0, string summary = "")
            => new() { Success = false, ErrorMessage = error, Rounds = rounds, ToolSteps = steps, Summary = summary };

        private static string Shorten(string? s, int max)
        {
            s ??= "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>
        /// 把 Messages API 串流事件組回完整 message（含 thinking 簽章、server_tool_use 參數、工具結果），
        /// pause_turn 續跑時必須原樣回傳這些區塊。
        /// </summary>
        public sealed class SseMessageAssembler
        {
            public JsonObject Message { get; private set; } = new() { ["content"] = new JsonArray() };
            public string StreamError { get; private set; } = "";

            private readonly Dictionary<int, StringBuilder> _partialJson = new();

            public async Task ReadAsync(TextReader reader, CancellationToken ct)
            {
                string? eventName = null;
                var data = new StringBuilder();

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                        break;

                    if (line.Length == 0)
                    {
                        if (data.Length > 0)
                            Apply(eventName, data.ToString());
                        eventName = null;
                        data.Clear();
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.Ordinal))
                        eventName = line.Substring(6).Trim();
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(line.Substring(5).TrimStart());
                    }
                }

                if (data.Length > 0)
                    Apply(eventName, data.ToString());
            }

            public void Apply(string? eventName, string data)
            {
                JsonNode? evt;
                try { evt = JsonNode.Parse(data); }
                catch { return; }
                if (evt is not JsonObject e)
                    return;

                string type = (string?)e["type"] ?? eventName ?? "";
                var content = (JsonArray)Message["content"]!;

                switch (type)
                {
                    case "message_start":
                        if (e["message"] is JsonObject start)
                        {
                            Message = (JsonObject)start.DeepClone();
                            if (Message["content"] is not JsonArray)
                                Message["content"] = new JsonArray();
                        }
                        break;

                    case "content_block_start":
                    {
                        int index = (int?)e["index"] ?? content.Count;
                        var block = e["content_block"]?.DeepClone() as JsonObject ?? new JsonObject();
                        while (content.Count <= index) content.Add(null);
                        content[index] = block;
                        break;
                    }

                    case "content_block_delta":
                    {
                        int index = (int?)e["index"] ?? -1;
                        if (index < 0 || index >= content.Count || content[index] is not JsonObject block)
                            break;
                        var delta = e["delta"] as JsonObject;
                        switch ((string?)delta?["type"])
                        {
                            case "text_delta":
                                block["text"] = ((string?)block["text"] ?? "") + ((string?)delta!["text"] ?? "");
                                break;
                            case "thinking_delta":
                                block["thinking"] = ((string?)block["thinking"] ?? "") + ((string?)delta!["thinking"] ?? "");
                                break;
                            case "signature_delta":
                                block["signature"] = (string?)delta!["signature"] ?? "";
                                break;
                            case "input_json_delta":
                                if (!_partialJson.TryGetValue(index, out var sb))
                                    _partialJson[index] = sb = new StringBuilder();
                                sb.Append((string?)delta!["partial_json"] ?? "");
                                break;
                            case "citations_delta":
                                if (block["citations"] is not JsonArray cites)
                                    block["citations"] = cites = new JsonArray();
                                if (delta!["citation"] != null)
                                    cites.Add(delta["citation"]!.DeepClone());
                                break;
                        }
                        break;
                    }

                    case "content_block_stop":
                    {
                        int index = (int?)e["index"] ?? -1;
                        if (index >= 0 && index < content.Count && content[index] is JsonObject block &&
                            _partialJson.TryGetValue(index, out var sb))
                        {
                            try { block["input"] = sb.Length == 0 ? new JsonObject() : JsonNode.Parse(sb.ToString()); }
                            catch { block["input"] = new JsonObject(); }
                            _partialJson.Remove(index);
                        }
                        break;
                    }

                    case "message_delta":
                        if (e["delta"] is JsonObject md)
                        {
                            foreach (var kv in md)
                                Message[kv.Key] = kv.Value?.DeepClone();
                        }
                        if (e["usage"] is JsonObject du)
                        {
                            if (Message["usage"] is not JsonObject mu)
                                Message["usage"] = mu = new JsonObject();
                            foreach (var kv in du)
                                mu[kv.Key] = kv.Value?.DeepClone();
                        }
                        break;

                    case "error":
                        StreamError = (string?)e["error"]?["message"] ?? data;
                        break;
                }
            }
        }
    }
}
