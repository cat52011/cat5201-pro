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

        // ── 證據到哪一步（2026-10-28 第三輪 AI 輔助審查）──
        //
        // 「網址在白名單上」「原文真的抓得到」「原文支持這個數字」是三件不同的事。
        // 上一版把第一件當成第三件用：https://www.sec.gov/not-a-real-document 這種
        // 編造的路徑照樣得到 official / high——白名單只證明「網域是那個機關的」，
        // 不證明那一頁存在，更不證明它支持模型寫出來的數字。
        //
        // 本系統目前只做得到第一步：不抓原文，也不做主張比對。
        // 所以這三個常數的意義不只是標籤，而是誠實地標出「我們到底驗到哪裡」。
        public const string SourceUnverified = "unverified";                 // 連網址都沒有，或網域不明
        public const string SourceDomainAllowlisted = "domain_allowlisted";  // 網域在白名單上（目前上限）
        public const string SourceDocumentFetched = "document_fetched";      // 原文已取得（尚未實作）
        public const string SourceClaimSupported = "claim_supported";        // 原文支持此主張（尚未實作）

        /// <summary>證據階段 → 給人看的短句。</summary>
        public static string DescribeSourceVerification(string? stage) =>
            (stage ?? "").Trim().ToLowerInvariant() switch
            {
                SourceClaimSupported => "原文支持此數字",
                SourceDocumentFetched => "原文已取得，未比對內容",
                SourceDomainAllowlisted => "來源網域為官方，未取原文核對",
                _ => "來源未經查證",
            };

        /// <summary>
        /// 信心上限由證據階段決定，而不是由「有沒有附網址」決定。
        /// 沒有核對過原文就不可能是 high——那個字在使用者眼裡等於「查過了」。
        /// </summary>
        public static string CapConfidence(string? confidence, string? verificationStage)
        {
            string c = (confidence ?? "").Trim().ToLowerInvariant();
            string stage = (verificationStage ?? "").Trim().ToLowerInvariant();

            if (stage == SourceClaimSupported)
                return c;

            return c == "high" ? "medium" : c;
        }

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
            // 「官方網域」而不是「官方來源」：白名單只證明網域歸屬，沒有人去把那一頁抓下來讀過。
            // 2026-10-28 第三輪審查：https://www.sec.gov/not-a-real-document 這種編造的路徑
            // 一樣通過網域比對——標成「官方來源」會讓使用者以為內容查證過了。
            AuthorityOfficial => "官方網域（未核對原文）",
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

        /// <summary>
        /// 這筆事實的證據走到哪一階段（見 <see cref="FactOwnership.SourceDomainAllowlisted"/>）。
        /// 目前系統最高只到「網域在白名單上」——不抓原文、不做主張比對。
        /// </summary>
        public string SourceVerification { get; init; } = FactOwnership.SourceUnverified;

        public string OwnerAgentId { get; init; } = "";

        public string OwnerCapabilityId { get; init; } = "";

        public string AuthorityLevel { get; init; } = "";

        public string UsageRole { get; init; } = "";

        public string ConflictStatus { get; init; } = "";
    }
}
