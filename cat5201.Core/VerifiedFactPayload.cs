using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    public static class FactOwnership
    {
        public const string ResearchAgent = "research-agent";
        public const string SearchCapability = "search-capability";

        public const string AuthorityOfficial = "official";
        public const string AuthorityMarketQuote = "market_quote";
        public const string AuthorityTrustedNews = "trusted_news";
        public const string AuthoritySearchContext = "search_context";
        public const string AuthorityModelGenerated = "model_generated";

        public const string UsageNumericFactSource = "numeric_fact_source";
        public const string UsageBackgroundContext = "background_context";
        public const string UsageAnalysisOnly = "analysis_only";

        public static int AuthorityRank(string authorityLevel)
        {
            if (string.IsNullOrWhiteSpace(authorityLevel))
                return 0;

            return authorityLevel.Trim().ToLowerInvariant() switch
            {
                AuthorityOfficial => 500,
                AuthorityMarketQuote => 400,
                AuthorityTrustedNews => 300,
                AuthoritySearchContext => 200,
                AuthorityModelGenerated => 50,
                _ => 0
            };
        }

        public static bool CanOwnNumericFacts(VerifiedFactItem? fact)
        {
            if (fact == null)
                return false;

            return string.Equals(
                fact.UsageRole,
                UsageNumericFactSource,
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsNumericFinanceFactType(string factType)
        {
            if (string.IsNullOrWhiteSpace(factType))
                return false;

            factType = factType.Trim();

            return string.Equals(factType, "regular_close_price", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "after_hours_price", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "pre_market_price", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "latest_fiscal_quarter", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "revenue", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "eps", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "gross_margin", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "guidance", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 事實的「實際查證程度」白話標籤。payload 名稱叫 verified_facts 是歷史包袱（程式內部 key 不改），
        /// 但對使用者顯示時必須說清楚：搜尋摘錄不是逐筆獨立驗證過的事實。
        /// </summary>
        public static string DescribeAuthority(string? authorityLevel) => (authorityLevel ?? "").Trim().ToLowerInvariant() switch
        {
            AuthorityOfficial => "官方來源",
            AuthorityMarketQuote => "報價資料",
            AuthorityTrustedNews => "新聞來源",
            AuthoritySearchContext => "搜尋摘錄（未獨立查證）",
            AuthorityModelGenerated => "模型生成（未查證）",
            _ => "來源等級未標示"
        };

        /// <summary>例：「官方來源 3・報價資料 2・搜尋摘錄（未獨立查證） 1」。</summary>
        public static string DescribeVerification(IEnumerable<VerifiedFactItem>? facts)
        {
            var groups = new List<(int Rank, string Label, int Count)>();
            foreach (var g in (facts ?? Array.Empty<VerifiedFactItem>())
                         .Where(x => x != null)
                         .GroupBy(x => (x.AuthorityLevel ?? "").Trim().ToLowerInvariant()))
            {
                groups.Add((AuthorityRank(g.Key), DescribeAuthority(g.Key), g.Count()));
            }

            return string.Join("・", groups.OrderByDescending(x => x.Rank).Select(x => $"{x.Label} {x.Count}"));
        }

        /// <summary>全部都只是搜尋摘錄（或更低）＝沒有任何一筆是有權威來源的事實。</summary>
        public static bool IsSearchContextOnly(IEnumerable<VerifiedFactItem>? facts)
        {
            var list = (facts ?? Array.Empty<VerifiedFactItem>()).Where(x => x != null).ToList();
            return list.Count > 0 && list.All(x => AuthorityRank(x.AuthorityLevel) <= AuthorityRank(AuthoritySearchContext));
        }

        public static string ResolveFinanceUsageRole(string factType)
        {
            return IsNumericFinanceFactType(factType)
                ? UsageNumericFactSource
                : UsageBackgroundContext;
        }
    }

    public sealed class VerifiedFactPayload
    {
        public string Query { get; init; } = "";

        public IReadOnlyList<VerifiedFactItem> Facts { get; init; }
            = new List<VerifiedFactItem>();

        public string Summary { get; init; } = "";
    }

    public sealed class VerifiedFactItem
    {
        public string Subject { get; init; } = "";

        public string FactType { get; init; } = "";
        // quote / earnings / news / date / general

        public string Value { get; init; } = "";

        public string Unit { get; init; } = "";

        public string AsOf { get; init; } = "";

        public string SourceTitle { get; init; } = "";

        public string SourceUrl { get; init; } = "";

        public string Confidence { get; init; } = "medium";

        public string OwnerAgentId { get; init; } = "";

        public string OwnerCapabilityId { get; init; } = "";

        public string AuthorityLevel { get; init; } = "";

        public string UsageRole { get; init; } = "";

        public string ConflictStatus { get; init; } = "";
    }
}
