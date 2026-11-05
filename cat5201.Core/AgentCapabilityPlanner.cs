using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 決定這次要跑哪些能力（搜尋、檔案、程式、推理、任務拆解）以及順序。
    ///
    /// 要不要搜尋／推理／寫程式／拆步驟，以第一層語意判斷（OutputIntent.Capabilities）為準——
    /// 它跟輸出判斷是同一次模型呼叫，不另外花錢。
    ///
    /// 2026-11-05 使用者指示：關鍵字判斷只留在「Auto＋關閉 API」（規則式 Auto）。
    ///  - 規則式 Auto：語意判斷拿不到時，退回下面的關鍵字規則（allowKeywordFallback=true）。
    ///  - API Auto、手動：不用關鍵字。語意判斷拿不到就不加任何額外能力。
    ///  - 手動另外只准搜尋（searchOnly=true）：語意判斷說要即時資料時才查，查到的結果由使用者選的模型讀、寫答案，
    ///    其他能力（推理、程式、任務拆解、附件整理）一律不加。跟 ChatGPT 網頁版「模型自己上網查」同一回事。
    /// 不論哪一種，來源都寫進 Reason 與摘要，決策紀錄看得到。
    /// </summary>
    public static class AgentCapabilityPlanner
    {
        public const string SourceSemantic = "semantic";
        public const string SourceKeywords = "keywords";
        /// <summary>語意判斷拿不到、這個模式又不准用關鍵字：不加額外能力。</summary>
        public const string SourceNone = "none";

        public static AgentCapabilityExecutionPlan Build(
            AgentDefinition agent,
            string topText,
            NodeTaskMode taskMode,
            bool hasAttachments,
            OutputIntent? intent = null,
            bool allowKeywordFallback = true,
            bool searchOnly = false)
        {
            topText ??= "";

            var semantic = intent?.Capabilities;
            string source = semantic != null ? SourceSemantic
                : allowKeywordFallback ? SourceKeywords
                : SourceNone;
            var needs = source switch
            {
                SourceSemantic => semantic!,
                SourceKeywords => FromKeywords(topText, taskMode, hasAttachments),
                _ => new CapabilityNeeds(false, false, false)
            };

            // 任務模式是「研究」不代表要搜尋：Auto 模式下它是系統選的（API Auto 由選模型的 API 判斷），
            // 2026-11-05 實測「台積電企業文化簡報」被判成研究 → 強制搜尋、還跳了即時資料提醒。
            // 要不要搜尋只看語意判斷；關鍵字後援（FromKeywords）裡原本「研究模式一律搜尋」的規則照舊保留。
            bool needsFreshFacts = needs.NeedsFreshFacts;
            bool needsReasoning = needs.NeedsReasoning && !searchOnly;
            bool needsCode = needs.NeedsCode && !searchOnly;
            if (searchOnly)
                hasAttachments = false;   // 手動：附件照舊直接交給所選模型，不另外跑附件整理

            // 任務拆解：關鍵字後援時照舊一律排進去，由 TaskPlanningCapability.CanHandle（關鍵字）決定；
            // 其他時候改看語意判斷——同時要做兩件以上的事（搜尋、推理、程式、處理附件）才算複合任務，
            // 跟 CanHandle 的「兩類以上」同一個標準，只是不再看字面。
            bool useKeywordTaskPlanning = source == SourceKeywords;
            int parts = (needsFreshFacts ? 1 : 0) + (needsReasoning ? 1 : 0) + (needsCode ? 1 : 0) + (hasAttachments ? 1 : 0);
            bool needsTaskPlanning = !searchOnly && (useKeywordTaskPlanning || parts >= 2);

            var ordered = new List<string>();
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 重要：需要新資料時，search 永遠第一優先
            if (needsFreshFacts)
            {
                ordered.Add("search-capability");
                required.Add("search-capability");
            }

            if (hasAttachments)
                ordered.Add("file-capability");

            if (needsCode)
                ordered.Add("code-capability");

            // task planning 只是輔助，不應該搶在 search 前面
            if (needsTaskPlanning)
                ordered.Add("task-planning-capability");

            if (needsReasoning)
                ordered.Add("reasoning-capability");

            ordered = ordered
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string sourceNote = source switch
            {
                SourceSemantic => " [source=semantic intent]",
                SourceKeywords => " [source=keyword fallback: semantic intent unavailable]",
                _ => " [source=none: semantic intent unavailable; keyword fallback disabled in this mode]"
            };

            return new AgentCapabilityExecutionPlan
            {
                OrderedCapabilityIds = ordered,
                RequiredCapabilityIds = required,
                RequiresFreshFacts = needsFreshFacts,
                Source = source,
                // 不是關鍵字後援時，計畫選了的能力執行時不再被各能力自己的關鍵字 CanHandle 否決（見 AgentRuntime）。
                IntentSelectedCapabilityIds = useKeywordTaskPlanning
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : ordered
                        .Where(id =>
                            (needsFreshFacts && id == "search-capability") ||
                            (needsCode && id == "code-capability") ||
                            (needsTaskPlanning && id == "task-planning-capability") ||
                            (needsReasoning && id == "reasoning-capability"))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                Reason = (needsFreshFacts
                    ? "Task requires fresh/current external facts; search-capability is required."
                    : "Task does not strictly require fresh facts.") + sourceNote
            };
        }

        /// <summary>
        /// 後援：語意判斷拿不到時用的關鍵字規則（原本的主要路徑，原樣保留）。
        /// 只認得列出來的講法，換說法或換語言會漏——所以只當後援。
        /// </summary>
        internal static CapabilityNeeds FromKeywords(string topText, NodeTaskMode taskMode, bool hasAttachments)
        {
            topText ??= "";

            bool isWorkspaceInternalRequest = IsWorkspaceInternalRequest(topText, hasAttachments);

            bool needsFreshFacts =
                FinanceTaskDetector.RequiresFreshFacts(topText, taskMode) ||
                (!isWorkspaceInternalRequest &&
                    ContainsAny(topText,
                        "最新", "即時", "今天", "現在", "目前",
                        "股價", "財報", "新聞", "市場", "匯率", "天氣",
                        "查詢", "搜尋", "查證",
                        "latest", "current", "today", "news", "stock", "earnings",
                        "price", "quote", "close", "after-hours", "pre-market"));

            bool needsReasoning =
                ContainsAny(topText,
                    "分析", "比較", "預測", "推論", "走勢", "建議",
                    "analyze", "compare", "predict", "forecast", "recommend");

            bool needsCode =
                ContainsAny(topText,
                    "程式", "程式碼", "bug", "debug", "class", "method", "code") ||
                (hasAttachments &&
                    ContainsAny(topText,
                        "修正", "修改", "改成", "新增", "加入", "重構", "錯誤",
                        "fix", "modify", "update", "patch", "diff", "refactor"));

            return new CapabilityNeeds(needsFreshFacts, needsReasoning, needsCode);
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword) &&
                    text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsWorkspaceInternalRequest(string text, bool hasAttachments)
        {
            if (hasAttachments &&
                ContainsAny(text,
                    "程式", "程式碼", "檔案", "附件", "bug", "debug", "修正", "修改",
                    "說明", "解釋", "一句話", "code", "file", "attachment", "explain"))
            {
                return true;
            }

            return ContainsAny(text,
                "workspace", "目前 workspace", "當前 workspace",
                "附件", "附檔", "上傳的檔案", "這個檔案", "這份檔案",
                "這個程式", "這份程式", "目前的程式分析結果");
        }
    }
}
