using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 把 App 的專案存檔轉成一次評測 run 的資料。
    ///
    /// 為什麼不讓人從決策視窗抄數字：抄寫會出錯，而且抄下來的數字沒有來源可追。
    /// 專案存檔本來就逐筆記了每次執行的實際 token、USD、耗時、選模型方式與實際模型，
    /// 直接讀它既精確又可稽查——任何人拿同一個存檔重跑，都會得到同一份 metrics。
    ///
    /// 這裡只放「判斷規則」（哪一筆屬於哪一題、哪一組），不碰檔案 I/O：
    /// 規則是方法論的一部分，必須能被測試釘住，不能藏在命令列程式裡。
    /// </summary>
    public static class EvalRunIngest
    {
        public const string GroupSingleGpt = "A";       // 手動固定 GPT
        public const string GroupSingleClaude = "B";    // 手動固定 Claude
        public const string GroupAutoProduct = "D";     // API Auto（本產品）
        public const string GroupUnknown = "?";

        /// <summary>一次執行對應的一列資料。</summary>
        public sealed class Row
        {
            public string TaskId { get; init; } = "";
            public string Group { get; init; } = "";
            public int Rep { get; set; }
            public string Output { get; init; } = "";
            public DateTime StartedAtUtc { get; init; }
            public int Tokens { get; init; }
            public double CostUsd { get; init; }
            public double Seconds { get; init; }
            public string Models { get; init; } = "";

            /// <summary>
            /// 這次是不是真的只有一個語言模型參與。手動組（A／B）的前提就是單一模型；
            /// 所選模型失敗而被替補、或使用者在確認框改選了撰寫者，都會讓這次不再是單一模型。
            /// Auto 組本來就是多模型，這一欄對它沒有意義（一律 false）。
            /// </summary>
            public bool SingleModel { get; init; }

            /// <summary>手動組這次用到的、所選以外的語言模型（內部 id）。</summary>
            public IReadOnlyList<string> OtherModels { get; init; } = Array.Empty<string>();
        }

        /// <summary>
        /// 手動執行裡，所選以外的語言模型有哪些。
        /// 經 Perplexity Agent API 跑的（anthropic/claude-sonnet-5-5）先轉回內部 id，那是同一個模型；
        /// 舊 id 路由到後繼者也算同一個。圖片、影片、搜尋 API 不是語言模型，不算。
        /// </summary>
        public static IReadOnlyList<string> OtherLlmModels(string? selectedModelId, IEnumerable<UsageRecord>? usage)
        {
            string selected = Canonical(selectedModelId);
            return (usage ?? Array.Empty<UsageRecord>())
                .Where(u => u != null && string.Equals(u.Kind, UsageKinds.Llm, StringComparison.OrdinalIgnoreCase))
                .Select(u => Canonical(ModelScope.FromAgentApiModel(u.ModelId)))
                .Where(id => id.Length > 0 && !string.Equals(id, selected, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Canonical(string? id)
        {
            string s = (id ?? "").Trim();
            return s.Length == 0 ? "" : AiModelRegistry.ResolveAlias(s) ?? s;
        }

        public sealed class Result
        {
            public List<Row> Rows { get; } = new();
            public List<string> Warnings { get; } = new();
        }

        /// <summary>
        /// 節點的提示詞對應哪一題。
        ///
        /// 比對忽略空白與全形半形差異，並允許提示詞後面附上固定素材
        /// （T06／T09 要把素材貼進同一個節點），所以用「包含」而非「完全相等」；
        /// 多題都對得上時取 prompt 較長的那一題。
        /// 對不上就是對不上——寧可略過並出聲，也不要猜，猜錯會把別的工作算進實驗。
        /// </summary>
        public static EvalRubricSpec? MatchSpec(IEnumerable<EvalRubricSpec> specs, string? prompt)
        {
            string p = EvalRubric.Normalize(prompt);
            if (p.Length == 0) return null;

            return (specs ?? Array.Empty<EvalRubricSpec>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Prompt))
                .Select(s => (Spec: s, Key: EvalRubric.Normalize(s.Prompt)))
                .Where(x => x.Key.Length > 0 && p.Contains(x.Key, StringComparison.Ordinal))
                .OrderByDescending(x => x.Key.Length)
                .Select(x => x.Spec)
                .FirstOrDefault();
        }

        /// <summary>
        /// 組別由存檔自己決定，不由操作者填——這樣就沒有填錯、或填成自己想要的空間。
        /// 選模型方式含 Auto 即為產品組；手動時看實際模型屬於哪一家。
        /// </summary>
        public static string ResolveGroup(string? selectionMode, string? actualModelId)
        {
            // 協作模式是使用者指定好幾個模型一起回答——不是單一模型組，也不是產品的 Auto 組。
            // 不排除的話會依第一個模型被分進 A 或 B，汙染單一模型的比較。
            if (string.Equals((selectionMode ?? "").Trim(), "Collaboration", StringComparison.OrdinalIgnoreCase))
                return GroupUnknown;

            if ((selectionMode ?? "").Contains("Auto", StringComparison.OrdinalIgnoreCase))
                return GroupAutoProduct;

            string model = (actualModelId ?? "").ToLowerInvariant();
            if (model.Contains("gpt", StringComparison.Ordinal)) return GroupSingleGpt;
            if (model.Contains("claude", StringComparison.Ordinal)
                || model.Contains("sonnet", StringComparison.Ordinal)
                || model.Contains("opus", StringComparison.Ordinal)) return GroupSingleClaude;

            return GroupUnknown;
        }

        public static string ResolveGroup(ExecutionLogState log)
            => log == null ? GroupUnknown : ResolveGroup(log.SelectionMode, log.ActualModelId);

        /// <summary>
        /// 把整份存檔攤成可寫檔的列。第幾次（Rep）依同題同組的實際執行時間排序，
        /// 所以同一個存檔重跑幾次都會得到同一份結果。
        /// </summary>
        public static Result Build(AppState? state, IEnumerable<EvalRubricSpec> specs)
        {
            var result = new Result();
            if (state == null) return result;

            var specList = (specs ?? Array.Empty<EvalRubricSpec>()).ToList();

            var logsByNode = (state.ExecutionLogs ?? new List<ExecutionLogState>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.NodeId))
                .GroupBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.EndedAtUtc).ToList(),
                              StringComparer.OrdinalIgnoreCase);

            var rows = new List<Row>();

            foreach (var node in state.Nodes ?? new List<NodeState>())
            {
                if (node == null) continue;

                string prompt = node.TopText ?? "";
                string output = node.BottomText ?? "";
                if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(output)) continue;

                var spec = MatchSpec(specList, prompt);
                if (spec == null)
                {
                    result.Warnings.Add("略過節點（提示詞對不上任何題目）：" + Head(prompt));
                    continue;
                }

                if (!logsByNode.TryGetValue(node.Id ?? "", out var logs) || logs.Count == 0)
                {
                    result.Warnings.Add($"略過 {spec.TaskId}（這個節點沒有執行紀錄，客觀欄無從查證）：" + Head(prompt));
                    continue;
                }

                // 一個節點只能對應一次執行：節點被重跑過時，BottomText 只剩最後一次的產出，
                // 先前幾次的文字已經回不來——那種資料不能混進來當成多次重複。
                if (logs.Count > 1)
                    result.Warnings.Add(
                        $"注意 {spec.TaskId}：這個節點有 {logs.Count} 筆執行紀錄（被重跑過），" +
                        "只採用最後一次；重複執行請每次開新節點。");

                var log = logs[^1];

                if (!log.Success)
                {
                    result.Warnings.Add($"略過 {spec.TaskId}（這次執行失敗：{log.ErrorMessage}）");
                    continue;
                }

                string group = ResolveGroup(log);
                if (group == GroupUnknown &&
                    string.Equals((log.SelectionMode ?? "").Trim(), "Collaboration", StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add($"略過 {spec.TaskId}（協作模式的執行不屬於 A／B／D 任何一組）");
                    continue;
                }
                if (group == GroupUnknown)
                {
                    result.Warnings.Add(
                        $"略過 {spec.TaskId}（分不出組別：SelectionMode={log.SelectionMode}／模型={log.ActualModelId}）");
                    continue;
                }

                var usage = log.UsageRecords ?? new List<UsageRecord>();

                // 手動組要確認真的只有所選的模型參與——規則允許「所選模型確定失敗時換模型並告知」，
                // 換過的那一次就不是單一模型執行，分析時必須排除或另外標記。
                IReadOnlyList<string> others = Array.Empty<string>();
                if (group != GroupAutoProduct)
                {
                    others = OtherLlmModels(log.RequestedModelId, usage);
                    if (others.Count > 0)
                        result.Warnings.Add(
                            $"注意 {spec.TaskId}（{group}）：手動組這次用到了所選以外的模型（{string.Join("、", others)}），" +
                            "不是單一模型執行——可能是所選模型失敗後被替補，或在確認框改選了撰寫者。分析時應排除或另外標記。");
                }

                rows.Add(new Row
                {
                    SingleModel = group != GroupAutoProduct && others.Count == 0,
                    OtherModels = others,
                    TaskId = spec.TaskId,
                    Group = group,
                    Output = output,
                    StartedAtUtc = log.StartedAtUtc,
                    Tokens = log.InputTokens + log.OutputTokens,
                    CostUsd = usage.Sum(u => u.UsdCost),
                    Seconds = log.DurationMs / 1000.0,
                    Models = usage.Select(u => u.ModelId)
                                  .Where(m => !string.IsNullOrWhiteSpace(m))
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .DefaultIfEmpty(log.ActualModelId ?? "")
                                  .Aggregate((a, b) => a + "|" + b),
                });

                if (usage.Count == 0)
                    result.Warnings.Add(
                        $"注意 {spec.TaskId}（{group}）：這筆執行紀錄沒有逐筆用量，成本欄會是 0。" +
                        "舊版專案檔才會這樣，請確認是本次實驗產生的存檔。");
            }

            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.OrderBy(x => x.TaskId, StringComparer.Ordinal)
                                    .ThenBy(x => x.Group, StringComparer.Ordinal)
                                    .ThenBy(x => x.StartedAtUtc))
            {
                string key = row.TaskId + "-" + row.Group;
                counters[key] = counters.TryGetValue(key, out int n) ? n + 1 : 1;
                row.Rep = counters[key];
                result.Rows.Add(row);
            }

            return result;
        }

        private static string Head(string? text)
        {
            string t = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return t.Length <= 28 ? t : t[..28] + "…";
        }
    }
}
