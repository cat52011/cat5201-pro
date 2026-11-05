using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    public sealed class AgentCapabilityExecutionPlan
    {
        public IReadOnlyList<string> OrderedCapabilityIds { get; init; } = Array.Empty<string>();
        public IReadOnlySet<string> RequiredCapabilityIds { get; init; } = new HashSet<string>();
        public bool RequiresFreshFacts { get; init; }
        public string Reason { get; init; } = "";

        /// <summary>
        /// 能力判斷從哪來：semantic（第一層語意判斷）、keywords（語意判斷拿不到時的後援，只有規則式 Auto 用）、
        /// none（語意判斷拿不到、這個模式不用關鍵字 → 不加額外能力）。
        /// </summary>
        public string Source { get; init; } = "";

        /// <summary>
        /// 語意判斷明確選了的能力（例如它判斷要寫程式 → code-capability）。
        /// 這些能力執行時不再看各自 CanHandle 裡的關鍵字——那份清單只認得特定字，會把語意判斷推翻。
        /// 跟 Required 不同：不會越過代理（agent）的權限設定。只有關鍵字後援時是空的，照舊由 CanHandle 決定。
        /// </summary>
        public IReadOnlySet<string> IntentSelectedCapabilityIds { get; init; } = new HashSet<string>();

        /// <summary>關鍵字後援時摘要裡的註記；AI 使用揭露表靠它分辨「誰判斷的」。</summary>
        public const string KeywordFallbackNote = "關鍵字後援：語意判斷沒有結果";

        /// <summary>語意判斷沒有結果、這個模式又不用關鍵字時的註記。</summary>
        public const string NoJudgementNote = "語意判斷沒有結果，此模式不用關鍵字，未加額外能力";

        /// <summary>手動模式（含協作）、這次不需要即時資料時的摘要：不加任何能力，只用使用者選的模型回答。</summary>
        public const string ManualModeSummary = "手動模式：只用你選的模型回答，不加用其他能力";

        /// <summary>手動模式要查即時資料時接在摘要後面：只搜尋，答案仍由使用者選的模型寫。</summary>
        public const string ManualSearchNote = "；手動模式只加搜尋，由你選的模型讀結果回答";

        /// <summary>白話摘要，給決策窗與 AI 使用揭露顯示。例如「查即時資料、推理分析（語意判斷）」。</summary>
        public string ToSummary()
        {
            var parts = new List<string>();
            if (RequiresFreshFacts) parts.Add("查即時資料");
            if (OrderedCapabilityIds.Contains("reasoning-capability", StringComparer.OrdinalIgnoreCase)) parts.Add("推理分析");
            if (OrderedCapabilityIds.Contains("code-capability", StringComparer.OrdinalIgnoreCase)) parts.Add("程式");

            string what = parts.Count == 0 ? "不需要搜尋、推理或程式" : string.Join("、", parts);
            string from = Source switch
            {
                AgentCapabilityPlanner.SourceSemantic => "語意判斷",
                AgentCapabilityPlanner.SourceKeywords => KeywordFallbackNote,
                _ => NoJudgementNote
            };
            return $"{what}（{from}）";
        }

        public bool IsIntentSelected(string capabilityId)
        {
            return IntentSelectedCapabilityIds.Contains(capabilityId);
        }

        /// <summary>
        /// 規則式 Auto（Auto＋關閉 API）只在「需要即時資料」時才跑能力層：這次要不要跑。
        /// 語意判斷有結果就看它；關鍵字後援時照舊用財經關鍵字（原本的規則，原樣保留）。
        /// </summary>
        public bool ShouldRunWhenSearchGated(string? topText)
        {
            return Source == AgentCapabilityPlanner.SourceKeywords
                ? FinanceTaskDetector.IsFinanceLike(topText)
                : RequiresFreshFacts;
        }

        public bool IsRequired(string capabilityId)
        {
            return RequiredCapabilityIds.Contains(capabilityId);
        }
    }
}