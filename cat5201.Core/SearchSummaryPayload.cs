using System.Collections.Generic;

namespace Cat5201
{
    public sealed class SearchSummaryPayload
    {
        public string Query { get; init; } = "";

        public string Summary { get; init; } = "";

        public IReadOnlyList<SearchSummaryItem> Items { get; init; }
            = new List<SearchSummaryItem>();

        /// <summary>
        /// 搜尋實際結果：succeeded / no_results / service_failed（見 <see cref="SearchStatus"/>）。
        /// 「有這個物件」不等於「拿到資料」——下游判斷有沒有即時資料一律看這裡，不看物件存不存在。
        /// </summary>
        public string Status { get; init; } = SearchStatus.Succeeded;

        /// <summary>失敗或沒結果時的原因（給決策窗與最終答案的提示用）。</summary>
        public string StatusDetail { get; init; } = "";

        /// <summary>真的拿到可用的搜尋結果。</summary>
        public bool HasUsableResults =>
            Status == SearchStatus.Succeeded && Items != null && Items.Count > 0;
    }

    public static class SearchStatus
    {
        public const string Succeeded = "succeeded";
        public const string NoResults = "no_results";
        public const string ServiceFailed = "service_failed";

        public static string ToLabel(string? status) => status switch
        {
            NoResults => "沒有搜尋結果",
            ServiceFailed => "搜尋服務失敗",
            _ => "搜尋成功"
        };
    }

    public sealed class SearchSummaryItem
    {
        public string Title { get; init; } = "";

        public string KeyPoint { get; init; } = "";

        public string Source { get; init; } = "";

        public string Date { get; init; } = "";
    }
}
