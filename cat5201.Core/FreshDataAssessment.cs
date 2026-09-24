using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 「需要最新資料的任務，到底拿到了沒有」的單一判斷。
    ///
    /// 舊版只檢查 capabilityData 裡「有沒有 search_summary 這個 key」——但搜尋服務失敗時也會放一個
    /// 帶警告文字的 search_summary 讓流程繼續，於是「服務掛了」被當成「已取得即時資料」，
    /// 最終答案與決策窗都看起來像查證過。這裡改看實際狀態與內容。
    /// </summary>
    public sealed class FreshDataAssessment
    {
        public const string StatusVerified = "verified";          // 有官方/報價/可信新聞等級的事實
        public const string StatusSearchResults = "search_results"; // 只有搜尋摘錄（未逐筆獨立查證）
        public const string StatusNoResults = SearchStatus.NoResults;
        public const string StatusServiceFailed = SearchStatus.ServiceFailed;
        public const string StatusNotRun = "not_run";

        public string Status { get; private init; } = StatusNotRun;
        public string Detail { get; private init; } = "";

        public bool HasUsableData => Status == StatusVerified || Status == StatusSearchResults;

        public static FreshDataAssessment Evaluate(IReadOnlyDictionary<string, object>? capabilityData)
        {
            capabilityData ??= new Dictionary<string, object>();

            var facts = capabilityData.TryGetValue("verified_facts", out var f) ? f as VerifiedFactPayload : null;
            var search = capabilityData.TryGetValue("search_summary", out var s) ? s as SearchSummaryPayload : null;

            var factItems = facts?.Facts?.Where(x => x != null).ToList() ?? new List<VerifiedFactItem>();
            if (factItems.Count > 0)
            {
                bool anyAuthoritative = factItems.Any(x =>
                    FactOwnership.AuthorityRank(x.AuthorityLevel) > FactOwnership.AuthorityRank(FactOwnership.AuthoritySearchContext));

                return new FreshDataAssessment
                {
                    Status = anyAuthoritative ? StatusVerified : StatusSearchResults,
                    Detail = FactOwnership.DescribeVerification(factItems)
                };
            }

            if (search != null)
            {
                if (search.HasUsableResults)
                    return new FreshDataAssessment { Status = StatusSearchResults, Detail = $"搜尋摘錄 {search.Items.Count} 筆（未逐筆獨立查證）" };

                return new FreshDataAssessment
                {
                    Status = search.Status == SearchStatus.ServiceFailed ? StatusServiceFailed : StatusNoResults,
                    Detail = search.StatusDetail ?? ""
                };
            }

            return new FreshDataAssessment { Status = StatusNotRun };
        }

        private string ReasonText => Status switch
        {
            StatusServiceFailed => string.IsNullOrWhiteSpace(Detail) ? "搜尋服務暫時失敗" : $"搜尋服務暫時失敗（{Detail}）",
            StatusNoResults => "搜尋沒有找到相關結果",
            _ => "這次沒有執行搜尋"
        };

        /// <summary>顯示在最終答案最上方的提示（由程式加上，不靠模型自覺）。</summary>
        public string BuildUserBanner()
            => HasUsableData
                ? ""
                : $"⚠️ **即時資料未取得**：{ReasonText}。以下內容未經即時查證，涉及最新數字、價格、日期或事件的部分，請以官方來源再確認。\n\n";

        /// <summary>給最終整合模型的硬規則：資料不足時交付「部分結果」，不得假裝查證過。</summary>
        public string BuildSynthesisNotice()
            => HasUsableData
                ? ""
                : "【即時資料不足（系統判定，必須遵守）】\n" +
                  $"這個任務需要最新資料，但本次{ReasonText}。\n" +
                  "1. 不得聲稱已搜尋、已查證或取得最新資料。\n" +
                  "2. 需要最新數字、價格、日期、事件的部分，明確寫出「未取得即時資料」；不可用訓練資料中的舊數字冒充最新值。若提供背景知識，須標明可能已過時。\n" +
                  "3. 仍要完成不依賴即時資料的部分（概念說明、分析框架、可以去哪裡查證）。\n" +
                  "4. 開頭不必再重複警告（系統會自動加上），直接交付內容。";
    }
}
