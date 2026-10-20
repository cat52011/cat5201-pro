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
        private const string ContextEditingBeta = "context-management-2025-06-27";
        public const string ToolTypeCurrent = "code_execution_20260521";
        public const string ToolTypeLegacy = "code_execution_20250825";

        // 伺服器端工具迴圈每 10 步會 pause_turn 一次；一份精緻簡報常需要數十步（讀技能、寫、轉圖、修）。
        private const int MaxRounds = 16;

        /// <summary>
        /// 伺服器端清理舊工具結果的門檻（context editing）。
        ///
        /// 沙箱迴圈每多跑一步，前面所有的 stdout、追蹤回溯、檔案清單就要再當作輸入送一次，
        /// 費用隨步數平方成長——2026-10-15 一份京都簡報 12 分鐘 US$10.48，幾乎全是這樣堆出來的。
        /// 超過門檻就把最舊的工具結果換成佔位字串（Claude 寫過的程式碼本身留著），
        /// 只保留最近幾步；它要的是「我剛剛跑出什麼」，不是二十步前的那一段 stdout。
        /// </summary>
        private const int ContextEditTriggerTokens = 60_000;
        private const int ContextEditKeepToolUses = 4;
        private const int ContextEditClearAtLeastTokens = 10_000;

        /// <summary>
        /// 一份文件（含重試）最多可以花多少錢。超過就停手，改用內建版型。
        ///
        /// 原本只有「預留額度 US$3」——那只是記帳用的保留，不會真的攔住任何東西：
        /// 2026-10-15 有一趟實際跑到 US$10.48 完全沒人喊停。
        /// </summary>
        public const double DefaultJobBudgetUsd = 3.0;
        private const int MaxOutputTokensPerRound = 64000;

        /// <summary>
        /// 真的送出去之前，用免費的 count_tokens 端點問一次「這包到底多大」。
        ///
        /// 我們自己估得到的只有打出去的字；伺服器端注入的技能內容估不到，
        /// 而 2026-10-15 被退回的那幾筆正是倒在這上面：等 6～8 分鐘、傳完整包，
        /// 換到一行 prompt is too long: 1,634,773 tokens，然後重試再來一次。
        /// 預檢不改變任何送出去的內容，只是不要把錢花在注定被退回的請求上。
        /// </summary>
        private const int HardPromptTokenLimit = 900_000;

        /// <summary>
        /// 送得出去的提示上限（估算 token）。模型的硬上限是 100 萬，這裡抓得保守一些，
        /// 留空間給技能內容、工具結果與多輪對話——快要滿的請求就算送得出去也跑不完。
        /// </summary>
        private const int MaxPromptTokens = 400_000;

        /// <summary>
        /// 整趟技能產出的時間上限。沒有這個上限時，單一輪就可能在伺服器端跑十幾分鐘、燒掉幾美元，
        /// 最後還是沒有檔案（2026-09-25 實測：1 輪 45 步、15 分 21 秒、US$4.71、零產出）。
        /// 時間到就停下來改用內建版型——晚交不如早交一份能用的。
        /// </summary>
        /// <summary>
        /// 一次「產出這份文件」的總時間預算——含重試。
        ///
        /// 以前是單次上限：一次卡住就等完 8 分鐘，然後退回內建版型。現在呼叫端會在這個
        /// 預算內一直重來（卡住的那次由 StallTimeout 提早結束，不會吃掉整個預算），
        /// 所以把總額拉高一點，換到的是「大部分情況真的拿得到高品質檔案」。
        /// </summary>
        public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(14);

        /// <summary>
        /// 串流「多久沒有任何動靜」就當它斷了。
        ///
        /// 2026-10-13 的紀錄：連續四次簡報產出都停在「1 輪 / 0 步」，每一次都把 8 分鐘的
        /// 總上限等完才退回內建版型——合計三十幾分鐘，使用者看到的只是「簡報很慢」。
        /// 查下去是 message_start 之後串流就沒有再送任何事件（連輸入 token 都記下來了），
        /// 也就是連線掛住，不是工作很久。總上限用來擋「真的跑很久」，擋不住「卡死」：
        /// 卡死要用「上一個事件到現在隔了多久」來判斷。
        /// </summary>
        public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(150);

        /// <summary>
        /// 串流被中止時，用來補記「伺服器已經生成但我們看不到」的輸出量（token/秒）。
        ///
        /// Anthropic 的 output_tokens 只在 message_delta 送出，而中止的那一輪根本等不到那一刻——
        /// 於是一整輪只會記到 message_start 的輸入 token。2026-10-13 實測：三次 8 分鐘的中止
        /// 各只記到 US$0.03，官網那邊合計少算了約 US$5.4。
        /// 伺服器那邊照樣在跑、照樣收費，記 0 等於把這筆錢從帳上抹掉。
        /// 只有在「連一個字都沒串流出來」時才用時間推算；有串流內容時直接量那些字，那是真的。
        /// 30 token/秒是保守值——2026-10-15 對帳顯示上一版的 60 會高估（Anthropic 多記約 17%）。
        /// 上限不超過單輪的 max_tokens。
        /// </summary>
        private const double AbortedOutputTokensPerSecond = 30;

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
            /// <summary>這一次是「串流卡住」而不是真的做不出來——呼叫端據此決定要不要重來。</summary>
            public bool Stalled { get; init; }

            public bool Success { get; init; }
            public IReadOnlyList<SkillFile> Files { get; init; } = Array.Empty<SkillFile>();
            /// <summary>Claude 最後給使用者的說明文字。</summary>
            public string Summary { get; init; } = "";
            public string ErrorMessage { get; init; } = "";
            public int Rounds { get; init; }
            public int ToolSteps { get; init; }

            /// <summary>這一趟（所有輪次）實際記到的費用，呼叫端用來決定還能不能再重試一次。</summary>
            public double UsdCost { get; set; }
        }

        /// <summary>跨輪累計的花費（Result 是回傳值，統計要在迴圈裡邊跑邊累加）。</summary>
        private sealed class Spent { public double Usd; }

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
            CancellationToken ct,
            TimeSpan? budget = null,
            IReadOnlyList<ContainerFile>? containerFiles = null,
            double maxUsd = 0,
            string? retryNote = null)
        {
            var spent = new Spent();
            Result result;
            try
            {
                result = await RunAsync(systemPrompt, userPrompt, skillIds, wantedExtensions,
                                        onProgress, ct, budget, containerFiles, maxUsd, spent, retryNote)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (spent.Usd > 0)
                    AppLog.Info("DocSkills", $"這一趟文件技能共計 US${spent.Usd:0.000}");
            }

            result.UsdCost = spent.Usd;
            return result;
        }

        private async Task<Result> RunAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyList<string> skillIds,
            IReadOnlyCollection<string> wantedExtensions,
            Action<int, int>? onProgress,
            CancellationToken ct,
            TimeSpan? budget,
            IReadOnlyList<ContainerFile>? containerFiles,
            double maxUsd,
            Spent spent,
            string? retryNote)
        {
            if (!IsConfigured)
                return Fail("找不到 ANTHROPIC_API_KEY。");

            // 送出之前先量一次。超過上限就當場停下來，不要花好幾分鐘把它傳上去、
            // 再收一句 prompt is too long —— 那幾分鐘的上傳與處理是會計費的，
            // 而且畫面上跟「模型在思考」完全分不出來（2026-10-13：8.8 分鐘換到一行錯誤）。
            int promptTokens = ModelCostEstimator.EstimateTokens(systemPrompt) +
                               ModelCostEstimator.EstimateTokens(userPrompt) +
                               ModelCostEstimator.EstimateTokens(retryNote);
            if (promptTokens > MaxPromptTokens)
            {
                AppLog.Warn("DocSkills",
                    $"提示過長（約 {promptTokens:N0} token，上限 {MaxPromptTokens:N0}），不送出。" +
                    "請減少附件或縮短上游內容。");
                return Fail(
                    $"這次的內容太長了（約 {promptTokens:N0} token，模型上限 {MaxPromptTokens:N0}）。" +
                    "常見原因是附件太大：可以拆成幾份、或先讓上一個節點做摘要再接過來。");
            }

            var limit = budget ?? DefaultBudget;
            var started = DateTime.UtcNow;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(limit);
            var userToken = ct;
            ct = deadline.Token;

            // 使用者取消 → 照原樣往外丟；時間到 → 當成一次失敗，讓呼叫端接手內建版型。
            Result Deadline(int round, int steps) => Fail(
                $"已達 {limit.TotalMinutes:0} 分鐘上限仍未產出檔案（{round} 輪 / {steps} 步，實際 {(DateTime.UtcNow - started).TotalMinutes:0.0} 分），已停止以免繼續累積費用。",
                round, steps);

            Result StalledResult(int round, int steps)
            {
                var r = Fail($"Claude 的回應中斷了（{StallTimeout.TotalSeconds:0} 秒沒有任何動靜，{round} 輪 / {steps} 步）。",
                             round, steps);
                return new Result
                {
                    Success = false,
                    ErrorMessage = r.ErrorMessage,
                    Rounds = r.Rounds,
                    ToolSteps = r.ToolSteps,
                    Stalled = true,
                };
            }

            var messages = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = BuildFirstMessageContent(userPrompt, retryNote, containerFiles)
                }
            };

            string? containerId = null;
            string toolType = ToolTypeCurrent;
            bool useFallbacks = true;
            bool useCache = true;
            bool useContextEditing = true;
            var fileIds = new List<string>();
            var finalText = new StringBuilder();
            int toolSteps = 0;
            int round = 0;

            try
            {
                while (round < MaxRounds)
                {
                    ct.ThrowIfCancellationRequested();
                    round++;
                    onProgress?.Invoke(round, toolSteps);

                    var body = BuildRequestBody(_model, systemPrompt, messages, skillIds, containerId, toolType, useFallbacks, useCache, useContextEditing);

                    // 送出去的整包有多大。
                    //
                    // 2026-10-15：我們的提示只有 1,419 字，伺服器卻回
                    // prompt is too long: 1,270,713 tokens。膨脹不在 messages 裡，
                    // 但沒有量過整包就只能猜。第一輪量一次就好，不要每輪洗版。
                    if (round == 1)
                    {
                        string json = body.ToJsonString();
                        AppLog.Info("DocSkills",
                            $"請求整包 {json.Length:N0} 字元（system {systemPrompt.Length:N0}、" +
                            $"messages {messages.ToJsonString().Length:N0}、技能 {string.Join("/", skillIds)}）");
                    }

                    var betas = new List<string> { CodeExecutionBeta };
                    if (useFallbacks) betas.Add(FallbackBeta);
                    if (useContextEditing) betas.Add(ContextEditingBeta);

                    // 預檢只做第一輪：爆掉的是整包的大小，後面幾輪只會更大但那時已經有產出了。
                    if (round == 1)
                    {
                        int? counted = await TryCountTokensAsync(body, betas, ct).ConfigureAwait(false);
                        if (counted is int n)
                        {
                            AppLog.Info("DocSkills", $"預檢：伺服器算出這包 {n:N0} token（我們送的內容，不含技能注入）");
                            if (n > HardPromptTokenLimit)
                                return Fail(
                                    $"這一包送出去會是 {n:N0} token，超過模型上限，送了也只會被退回（prompt is too long）。" +
                                    "已在送出前停止，沒有產生費用。",
                                    round, toolSteps);
                        }
                    }

                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/messages");
                    req.Headers.Add("x-api-key", _apiKey);
                    req.Headers.Add("anthropic-version", ApiVersion);
                    req.Headers.Add("anthropic-beta", string.Join(",", betas));
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
                        if ((int)resp.StatusCode == 400 && useContextEditing && err.Contains("context", StringComparison.OrdinalIgnoreCase))
                        {
                            AppLog.Warn("DocSkills", "context editing 參數不被接受，改為不帶重送：" + Shorten(err, 300));
                            useContextEditing = false;
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

                        ProviderStatusMonitor.ReportFailure("anthropic", $"{(int)resp.StatusCode} {err}");
                        return Fail($"Claude 文件技能 API {(int)resp.StatusCode}：{Shorten(err, 400)}", round, toolSteps);
                    }

                    var assembler = new SseMessageAssembler();
                    var roundStarted = DateTime.UtcNow;

                    // 這一輪自己的逾時：每收到一個事件就重新計時，連續沒動靜才算卡死。
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(StallTimeout);

                    try
                    {
                        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        await assembler.ReadAsync(
                            reader,
                            stall.Token,
                            onActivity: () => { try { stall.CancelAfter(StallTimeout); } catch (ObjectDisposedException) { } })
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 已產生的用量照樣計費，而且要補上「看不到的那一段」——
                        // 伺服器在我們放棄之前一直在生成，那些 output token 不會因為我們斷線就不收錢。
                        double abortedUsd = EstimateAbortedOutputUsd(
                            (string?)assembler.Message["model"] ?? _model,
                            DateTime.UtcNow - roundStarted,
                            StreamedTextLength(assembler.Message));

                        spent.Usd += RecordUsage(assembler.Message, round, "串流中止（輸出量為估算）", abortedUsd);

                        // 卡死（這一輪沒動靜）與真的跑超過總上限，對使用者是兩件不同的事，
                        // 而且只有前者應該馬上放棄。使用者自己按取消則照原樣往外丟。
                        if (stall.IsCancellationRequested && !deadline.IsCancellationRequested)
                        {
                            AppLog.Warn("DocSkills",
                                $"文件技能串流卡住 {StallTimeout.TotalSeconds:0} 秒無回應，提前放棄：{round} 輪 / {toolSteps} 步");
                            return StalledResult(round, toolSteps);
                        }

                        throw;
                    }

                    var message = assembler.Message;
                    spent.Usd += RecordUsage(message, round);

                    if (!string.IsNullOrWhiteSpace(assembler.StreamError))
                    {
                        ProviderStatusMonitor.ReportFailure("anthropic", assembler.StreamError);
                        return Fail($"Claude 文件技能串流錯誤：{assembler.StreamError}", round, toolSteps);
                    }

                    containerId = ReadContainerId(message) ?? containerId;

                    var content = message["content"] as JsonArray ?? new JsonArray();
                    toolSteps += content.Count(b => (string?)b?["type"] == "server_tool_use");
                    fileIds.AddRange(ExtractFileIds(content));

                    string roundText = ExtractFinalText(content);
                    string stopReason = (string?)message["stop_reason"] ?? "";

                    // 還沒做完、而且錢已經花到上限：再送一輪只會更貴，而且多半還是做不出來。
                    if (stopReason == "pause_turn" && maxUsd > 0 && spent.Usd >= maxUsd)
                    {
                        AppLog.Warn("DocSkills",
                            $"已達費用上限 US${maxUsd:0.00}（實際 US${spent.Usd:0.000}，{round} 輪 / {toolSteps} 步），停止。");
                        return Fail(
                            $"已達這份文件的費用上限 US${maxUsd:0.00}（實際已用 US${spent.Usd:0.00}，{round} 輪 / {toolSteps} 步），" +
                            "已停止以免繼續累積費用。",
                            round, toolSteps);
                    }

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
            }
            catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
            {
                AppLog.Warn("DocSkills", $"文件技能逾時中止：{round} 輪 / {toolSteps} 步");
                return Deadline(round, toolSteps);
            }

            // 逾時只管「產出」階段；已經生出來的檔案照樣下載回來，不要白跑。
            ct = userToken;

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

            // 失敗時要看得出卡在哪一段：沒有 file_id、讀不到檔名、副檔名不符、還是下載回空的。
            AppLog.Info("DocSkills",
                $"產出盤點：file_id {fileIds.Distinct().Count()} 個、取得檔名 {named.Count} 個" +
                (named.Count > 0 ? "（" + string.Join("、", named.Select(n => n.Name).Take(12)) + "）" : "") +
                $"、要的副檔名 {string.Join("/", wantedExtensions)}");

            var selected = SelectDeliverables(named, wantedExtensions).ToList();

            // 想要的副檔名沒中，但確實有其他文件檔：交出來比丟掉整趟好（呼叫端會照實說明）。
            if (selected.Count == 0 && named.Count > 0)
            {
                string[] usable = { ".pptx", ".docx", ".xlsx", ".pdf" };
                selected = named.Where(n => usable.Contains(Path.GetExtension(n.Name).ToLowerInvariant())).ToList();
                if (selected.Count > 0)
                    AppLog.Warn("DocSkills", "產出的副檔名與要求不同，改為交付實際產出：" +
                        string.Join("、", selected.Select(s => s.Name)));
            }

            var files = new List<SkillFile>();
            foreach (var (id, name) in selected)
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
                return Fail(
                    named.Count == 0
                        ? $"Claude 執行了 {toolSteps} 個步驟，但沒有回傳任何可下載的檔案。"
                        : $"Claude 產出了 {named.Count} 個檔案（{string.Join("、", named.Select(n => n.Name).Take(6))}），但都不是可交付的文件檔。",
                    round, toolSteps, finalText.ToString());

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

        /// <summary>
        /// 第一則訊息：容器檔案（我們自己生的照片）→ 主提示 → 重試提示。
        ///
        /// 三段各自一塊，順序固定。重試提示**不可以**黏進主提示那一塊：
        /// 快取比對的是前綴，黏在一起的話整塊會因為那幾句話而改變，
        /// 前面幾千 token 的共同前綴跟著失效，同一份文件重試三次就重買三次。
        /// </summary>
        public static JsonArray BuildFirstMessageContent(
            string? userPrompt,
            string? retryNote,
            IReadOnlyList<ContainerFile>? containerFiles)
        {
            var content = new JsonArray();

            foreach (var file in containerFiles ?? Array.Empty<ContainerFile>())
            {
                if (string.IsNullOrWhiteSpace(file.FileId)) continue;
                content.Add(new JsonObject
                {
                    ["type"] = "container_upload",
                    ["file_id"] = file.FileId,
                });
            }

            content.Add(new JsonObject { ["type"] = "text", ["text"] = userPrompt ?? "" });

            if (!string.IsNullOrWhiteSpace(retryNote))
                content.Add(new JsonObject { ["type"] = "text", ["text"] = retryNote });

            return content;
        }

        public static JsonObject BuildRequestBody(
            string model,
            string systemPrompt,
            JsonArray messages,
            IReadOnlyList<string> skillIds,
            string? containerId,
            string toolType,
            bool useFallbacks,
            bool useCache = true,
            bool useContextEditing = true)
        {
            var skills = new JsonArray();
            foreach (var id in skillIds.Distinct(StringComparer.OrdinalIgnoreCase))
                skills.Add(new JsonObject { ["type"] = "anthropic", ["skill_id"] = id, ["version"] = "latest" });

            // 沒有要掛技能時就不要送 skills（空陣列意義不明）。
            // 簡報走的就是這條路：一樣由 Claude 在沙箱裡排版，只是不載入 pptx 技能那包
            // ——2026-10-15 量到它一個人就注入 118 萬 token，而我們的請求只有 1.5 萬。
            var container = new JsonObject();
            if (skills.Count > 0)
                container["skills"] = skills;
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

            // 伺服器端清掉舊的工具結果：沙箱迴圈的費用幾乎全來自「每一步都把前面所有 stdout 再送一次」。
            // clear_tool_inputs 不開＝Claude 自己寫過的程式碼留著，被清掉的只有輸出。
            if (useContextEditing)
                body["context_management"] = new JsonObject
                {
                    ["edits"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "clear_tool_uses_20250919",
                            ["trigger"] = new JsonObject { ["type"] = "input_tokens", ["value"] = ContextEditTriggerTokens },
                            ["keep"] = new JsonObject { ["type"] = "tool_uses", ["value"] = ContextEditKeepToolUses },
                            ["clear_at_least"] = new JsonObject { ["type"] = "input_tokens", ["value"] = ContextEditClearAtLeastTokens },
                        }
                    }
                };

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

        /// <summary>要帶進沙箱的檔案：已經上傳到 Files API，用 file_id 引用。</summary>
        public sealed record ContainerFile(string FileId, string FileName);

        /// <summary>
        /// 這一次的失敗值得重來嗎。
        ///
        /// 卡住、跑了步驟卻沒交檔＝值得重來（同一份提示再送一次就成功過）。
        /// 但「提示太長」「金鑰無效」「被拒絕」這種是送幾次都一樣的——重試只是再浪費一次
        /// 幾分鐘的等待。2026-10-13 的紀錄：第 1 次被退回 prompt is too long（花了 8.8 分鐘），
        /// 第 2 次原封不動再送，又燒掉剩下的 5.2 分鐘。
        /// </summary>
        public static bool IsRetryable(Result result)
        {
            if (result == null || result.Success)
                return false;

            string err = result.ErrorMessage ?? "";
            string[] permanent =
            {
                "too long", "max_tokens", "invalid_request", "authentication", "費用上限",
                "permission", "not_found", "拒絕", "找不到 ANTHROPIC_API_KEY",
            };
            foreach (var p in permanent)
                if (err.Contains(p, StringComparison.OrdinalIgnoreCase))
                    return false;

            return result.Stalled || result.ToolSteps > 0 || result.Rounds <= 1;
        }

        /// <summary>
        /// 記這一輪的用量，並回傳金額。
        ///
        /// 逐輪寫進日誌是刻意的：快取有沒有命中、伺服器有沒有幫我們清掉舊的工具結果，
        /// 不寫下來就只能看到最後一個總額，然後對著它猜。
        /// </summary>
        private double RecordUsage(JsonObject message, int round, string note = "", double extraUsd = 0)
        {
            var usage = message["usage"] as JsonObject;
            if (usage == null)
                return 0;

            int input = ReadInt(usage, "input_tokens");
            int output = ReadInt(usage, "output_tokens");
            int cacheWrite = ReadInt(usage, "cache_creation_input_tokens");
            int cacheRead = ReadInt(usage, "cache_read_input_tokens");

            // adaptive 沒有 budget_tokens 可設，深度由 output_config.effort 決定（Sonnet 5 預設 high）。
            // 這個數字是唯一能回答「該不該把 effort 調低」的依據：思考以輸出計價（$10/M），
            // 而且之後每一輪還會以輸入的身分再被買一次。串流時它只出現在最後一個 message_delta。
            int thinking = ReadInt(usage["output_tokens_details"] as JsonObject, "thinking_tokens");

            // 快取寫入 1.25 倍、讀取 0.1 倍輸入單價（Anthropic 官方計價）；沙盒時間每月 1,550 小時免費，超過 $0.05/小時，此處不計。
            string modelId = ModelCostEstimator.ResolvePricingModelId((string?)message["model"] ?? _model);
            double cacheUsd =
                ModelCostEstimator.FromUsage(modelId, cacheWrite, 0).UsdCost * ModelCostEstimator.CacheWriteMultiplier +
                ModelCostEstimator.FromUsage(modelId, cacheRead, 0).UsdCost * ModelCostEstimator.CacheReadMultiplier(modelId);

            var record = UsageMeter.RecordLlm(modelId, input, output, note: note, extraUsd: cacheUsd + Math.Max(0, extraUsd));

            int cleared = 0;
            if (message["context_management"]?["applied_edits"] is JsonArray edits)
                foreach (var e in edits)
                    cleared += ReadInt(e as JsonObject, "cleared_input_tokens");

            AppLog.Info("DocSkills",
                $"第 {round} 輪用量：輸入 {input:N0}（快取讀 {cacheRead:N0}／寫 {cacheWrite:N0}）、輸出 {output:N0}" +
                (thinking > 0 ? $"（其中思考 {thinking:N0}）" : "") +
                (cleared > 0 ? $"、伺服器清掉舊工具結果 {cleared:N0}" : "") +
                $" → US${(record?.UsdCost ?? 0):0.000}" +
                (string.IsNullOrEmpty(note) ? "" : $"（{note}）"));

            return record?.UsdCost ?? 0;
        }

        /// <summary>
        /// 中止的那一輪，伺服器已經生成多少輸出（以美元計）。
        ///
        /// 有串流出來的文字就量那些字——那是真的，不是猜的。
        /// 一個字都沒收到時才退回「時間 × 保守速率」，並標示為估算。
        /// </summary>
        private static double EstimateAbortedOutputUsd(string? model, TimeSpan elapsed, int streamedChars)
        {
            string modelId = ModelCostEstimator.ResolvePricingModelId(model);

            int tokens = streamedChars > 0
                ? ModelCostEstimator.EstimateTokens(new string('x', Math.Min(streamedChars, 400_000)))
                : (int)Math.Max(0, elapsed.TotalSeconds * AbortedOutputTokensPerSecond);

            tokens = Math.Min(tokens, MaxOutputTokensPerRound);
            return tokens <= 0 ? 0 : ModelCostEstimator.FromUsage(modelId, 0, tokens).UsdCost;
        }

        /// <summary>這一輪實際串流出來的文字長度（含思考段）——中止時用來估算已經產生的輸出。</summary>
        private static int StreamedTextLength(JsonObject message)
        {
            if (message["content"] is not JsonArray content)
                return 0;

            int total = 0;
            foreach (var block in content)
            {
                if (block is not JsonObject obj) continue;
                total += ((string?)obj["text"] ?? "").Length;
                total += ((string?)obj["thinking"] ?? "").Length;
            }

            return total;
        }

        private static int ReadInt(JsonObject? obj, string key)
            => obj?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

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

        /// <summary>
        /// 免費端點 /v1/messages/count_tokens：回傳這包的實際 input_tokens（含伺服器端注入的技能）。
        ///
        /// 永遠不讓它擋住正常流程——端點不收某個欄位、逾時、網路問題，一律回 null 當作沒量過。
        /// 它只能幫忙省錢，不能製造新的失敗。
        /// </summary>
        private async Task<int?> TryCountTokensAsync(JsonObject body, IReadOnlyList<string> betas, CancellationToken ct)
        {
            try
            {
                var probe = body.DeepClone().AsObject();
                // count_tokens 不收 container（2026-10-15 實測 400：container: Extra inputs are not permitted），
                // 所以量到的是「我們自己送的內容」，不含伺服器端的技能注入。
                foreach (var field in new[] { "max_tokens", "stream", "fallbacks", "cache_control", "container" })
                    probe.Remove(field);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/messages/count_tokens");
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", ApiVersion);
                req.Headers.Add("anthropic-beta", string.Join(",", betas));
                req.Content = new StringContent(probe.ToJsonString(), Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    AppLog.Info("DocSkills", $"預檢沒做成（{(int)resp.StatusCode}），照常送出：{Shorten(json, 200)}");
                    return null;
                }

                var node = JsonNode.Parse(json) as JsonObject;
                return ReadInt(node, "input_tokens") is int n && n > 0 ? n : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppLog.Info("DocSkills", "預檢沒做成（" + ex.GetType().Name + "），照常送出");
                return null;
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

            /// <param name="onActivity">
            /// 每讀到一行就呼叫一次。呼叫端用它重設「沒動靜就放棄」的計時器——
            /// 沒有這個回報，就只能用一個很長的總上限去等，而卡死跟工作很久看起來一模一樣。
            /// </param>
            public async Task ReadAsync(TextReader reader, CancellationToken ct, Action? onActivity = null)
            {
                string? eventName = null;
                var data = new StringBuilder();

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    // 帶 token 的多載：不帶的話，串流完全沒有資料時會卡在這裡，
                    // 上面那行檢查永遠不會再被執行到。
                    string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null)
                        break;

                    onActivity?.Invoke();

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
