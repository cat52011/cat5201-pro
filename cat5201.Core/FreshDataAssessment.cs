using System;
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
        public const string StatusVerified = "verified";          // 原文已核對且支持該數字（＝證據第三階段）
        public const string StatusDomainChecked = "domain_checked"; // 只確認來源網域，未取原文（目前系統的上限）
        public const string StatusSearchResults = "search_results"; // 只有搜尋摘錄（未逐筆獨立查證）
        public const string StatusNoResults = SearchStatus.NoResults;
        public const string StatusServiceFailed = SearchStatus.ServiceFailed;
        public const string StatusNotRun = "not_run";

        public string Status { get; private init; } = StatusNotRun;
        public string Detail { get; private init; } = "";

        /// <summary>這次到底有沒有拿到可用的資料（跟「查證到什麼程度」是兩個問題）。</summary>
        public bool HasUsableData =>
            Status == StatusVerified || Status == StatusDomainChecked || Status == StatusSearchResults;

        /// <summary>
        /// 資料拿到了，但查證沒有走完——使用者仍須自己點開來源核對。
        /// 這個狀態要有自己的提示；沿用「有資料就不提示」的舊規則等於默認已查證。
        /// </summary>
        public bool NeedsSourceCaution => Status == StatusDomainChecked;

        /// <summary>
        /// 這次要不要對使用者講話（警告橫幅 + 給模型的硬規則）。
        ///
        /// 單獨存在的理由：2026-10-29 實測九次評測執行，發現新增的「僅核對網域」橫幅
        /// 一次都沒有出現。原因不在橫幅本身，而在呼叫端——它只在 !HasUsableData 時才去取橫幅，
        /// 而 domain_checked 的 HasUsableData 是 true，於是整段被無聲跳過。
        /// 這跟前一輪被指出的毛病是同一種：新增了正確的狀態，卻讓舊的判斷繼續決定要不要用它。
        /// 把「要不要講話」收斂成這一個屬性，呼叫端就只有一個地方會寫錯，而且測得到。
        /// </summary>
        /// 另外，只有走到「原文支持主張」才什麼都不必說。
        /// 原本 search_results（只有搜尋摘錄）也是靜默的，但它比 domain_checked 更弱——
        /// 弱的狀態不出聲、強的狀態反而出聲，是說不通的。
        public bool NeedsUserNotice => Status != StatusVerified;

        /// <summary>測試用：直接造出某個狀態，不必拼出會導向它的那一堆事實。</summary>
        internal static FreshDataAssessment ForStatus(string status)
            => new() { Status = status, Detail = "" };

        /// <summary>這一格是不是真的有值（而不是「未取得」「not_available」這種佔位字）。</summary>
        internal static bool HasRealValue(string? value)
        {
            string v = (value ?? "").Trim();
            if (v.Length == 0) return false;

            string[] placeholders =
            {
                "未取得", "不適用", "無資料", "查無", "n/a", "na", "none", "null",
                "not_available", "not available", "unavailable", "unknown", "-", "—",
            };

            string lower = v.ToLowerInvariant();
            if (placeholders.Any(p => lower == p)) return false;

            // 「regular_close_price=not_available; …」這種整串都是佔位字的也算沒有值。
            if (lower.Contains("=", StringComparison.Ordinal))
            {
                var parts = lower.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts.All(p =>
                        p.Contains("not_available", StringComparison.Ordinal) ||
                        p.Contains("未取得", StringComparison.Ordinal)))
                    return false;
            }

            return true;
        }

        public static FreshDataAssessment Evaluate(IReadOnlyDictionary<string, object>? capabilityData)
        {
            capabilityData ??= new Dictionary<string, object>();

            var facts = capabilityData.TryGetValue("verified_facts", out var f) ? f as VerifiedFactPayload : null;
            var search = capabilityData.TryGetValue("search_summary", out var s) ? s as SearchSummaryPayload : null;

            var factItems = facts?.Facts?.Where(x => x != null).ToList() ?? new List<VerifiedFactItem>();
            if (factItems.Count > 0)
            {
                // 「已查證」要同時成立兩件事：來源等級夠高，**而且真的有一個數字**。
                //
                // 2026-10-27 第三方審查實測：所有欄位都是「未取得」時，
                // 一筆 quote_availability 就能讓整次查詢被判成 verified。
                // 只看來源等級不看內容，等於替空答案蓋章。
                // 門檻訂在 market_quote：新聞或部落格等級的引用只能算「搜尋結果」，
                // 不能叫「已查證」——那兩個詞在使用者心裡的份量差很多。
                bool anyAuthoritative = factItems.Any(x =>
                    FactOwnership.AuthorityRank(x.AuthorityLevel) >= FactOwnership.AuthorityRank(FactOwnership.AuthorityMarketQuote)
                    && string.Equals(x.UsageRole, FactOwnership.UsageNumericFactSource, StringComparison.OrdinalIgnoreCase)
                    && HasRealValue(x.Value));

                // 證據階段必須貫穿到整體判定，否則單筆承認「只確認網域」、整體仍自稱 verified。
                //
                // 2026-10-28 第四輪 AI 輔助審查實測：以真實主管機關網域配編造路徑送進來，
                // 單筆是 domain_allowlisted / medium（上一輪已修好），
                // 但 Evaluate 只看來源等級與有無數值，Status 仍是 verified、警告橫幅仍是空的。
                // 新增一個正確的欄位卻讓舊決策繼續忽略它，使用者看到的結論沒有任何改變。
                //
                // 「已查證」從此要求至少一筆走到第三階段（原文支持此主張）。
                // 本系統目前做不到第三階段，所以 verified 實際上不會出現——
                // 這跟 high 信心一樣：達不到的標準就不該發放，而不是放寬標準去符合現況。
                bool anyClaimSupported = factItems.Any(x =>
                    string.Equals(x.SourceVerification, FactOwnership.SourceClaimSupported, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.UsageRole, FactOwnership.UsageNumericFactSource, StringComparison.OrdinalIgnoreCase)
                    && HasRealValue(x.Value));

                // 有沒有「任何一個真的數字」——沒有的話，這次就是沒拿到資料。
                //
                // 2026-10-28 AI 輔助審查：所有欄位都是「未取得」時，上一版只是把標籤從
                // verified 降成 search_results，HasUsableData 仍是 true、警告橫幅仍是空的。
                // 使用者看到的還是「查過了」。降級標籤不等於承認沒資料。
                //
                // 2026-10-29 更正：這條規則只適用於「本來就該有數字」的查詢（有數字型欄位的那種）。
                // 一般研究（例如介紹某地美食）只有背景摘錄、沒有任何數字型欄位——
                // 那不是「每一個欄位都未取得」，是根本沒有數字可取。上一版把它也判成「沒拿到資料」，
                // 答案頂端便出現「查詢有回應，但每一個欄位都是未取得」，搜尋明明有結果。
                var numericFacts = factItems
                    .Where(x => string.Equals(x.UsageRole, FactOwnership.UsageNumericFactSource, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                bool anyRealValue = numericFacts.Any(x => HasRealValue(x.Value));

                if (numericFacts.Count > 0 && !anyRealValue)
                {
                    return new FreshDataAssessment
                    {
                        Status = StatusNoResults,
                        Detail = "搜尋有回應，但需要的數字一個都沒有取得",
                    };
                }

                // 沒有任何數字型欄位：交給搜尋摘錄判斷（有結果就是搜尋結果，沒有就是沒結果）。
                if (numericFacts.Count == 0 && search != null)
                    return FromSearch(search);

                return new FreshDataAssessment
                {
                    Status = anyClaimSupported ? StatusVerified
                           : anyAuthoritative ? StatusDomainChecked
                           : StatusSearchResults,
                    Detail = FactOwnership.DescribeVerification(factItems)
                };
            }

            if (search != null)
                return FromSearch(search);

            return new FreshDataAssessment { Status = StatusNotRun };
        }

        private static FreshDataAssessment FromSearch(SearchSummaryPayload search)
        {
            if (search.HasUsableResults)
                return new FreshDataAssessment { Status = StatusSearchResults, Detail = $"搜尋摘錄 {search.Items.Count} 筆（未逐筆獨立查證）" };

            return new FreshDataAssessment
            {
                Status = search.Status == SearchStatus.ServiceFailed ? StatusServiceFailed : StatusNoResults,
                Detail = search.StatusDetail ?? ""
            };
        }

        private string ReasonText => Status switch
        {
            StatusServiceFailed => string.IsNullOrWhiteSpace(Detail) ? "搜尋服務暫時失敗" : $"搜尋服務暫時失敗（{Detail}）",
            StatusNoResults => string.IsNullOrWhiteSpace(Detail) ? "搜尋沒有找到相關結果" : Detail,
            _ => "這次沒有執行搜尋"
        };

        /// <summary>
        /// 顯示在最終答案最上方的提示（由程式加上，不靠模型自覺）。
        ///
        /// 三種情形要講三種話：沒拿到資料、拿到了但只核對過網域、原文已核對。
        /// 中間那一種以前是空白的——那等於默認「查證過了」。
        /// </summary>
        public string BuildUserBanner()
        {
            if (NeedsSourceCaution)
                return "⚠️【來源僅核對網域】以下數字取自可信網域，但系統並未取得原文、" +
                       "也未比對原文是否支持這些數字。請點開來源自行確認後再引用。\n\n";

            if (Status == StatusSearchResults)
                return "⚠️【僅有搜尋摘錄】以下內容來自搜尋結果摘要，系統未逐筆查證來源，" +
                       "也未確認任何一筆的出處頁面。涉及數字、價格、日期的部分請自行核對。\n\n";

            return HasUsableData
                ? ""
                : $"⚠️【即時資料未取得】{ReasonText}。以下內容未經即時查證，涉及最新數字、價格、日期或事件的部分，請以官方來源再確認。\n\n";
        }

        /// <summary>給最終整合模型的硬規則：資料不足時交付「部分結果」，不得假裝查證過。</summary>
        public string BuildSynthesisNotice()
        {
            if (NeedsSourceCaution || Status == StatusSearchResults)
                return (NeedsSourceCaution
                           ? "【來源僅核對網域（系統判定，必須遵守）】\n" +
                             "本次引用的數字來自可信網域，但系統未取得原文、未比對原文是否支持這些數字。\n"
                           : "【僅有搜尋摘錄（系統判定，必須遵守）】\n" +
                             "本次只拿到搜尋結果摘要，系統未逐筆查證，也未確認任何一筆的出處頁面。\n") +
                       "1. 不得聲稱「已查證」「已核實」或「經官方確認」。\n" +
                       "2. 引用數字時一併附上來源連結，並說明讀者應自行核對原文。\n" +
                       "3. 開頭不必重複此警告（系統會自動加上）。";

            return HasUsableData
                ? ""
                : "【即時資料不足（系統判定，必須遵守）】\n" +
                  $"這個任務需要最新資料，但本次{ReasonText}。\n" +
                  "1. 不得聲稱已搜尋、已查證或取得最新資料。\n" +
                  "2. 需要最新數字、價格、日期、事件的部分，明確寫出「未取得即時資料」；不可用訓練資料中的舊數字冒充最新值。若提供背景知識，須標明可能已過時。\n" +
                  "3. 仍要完成不依賴即時資料的部分（概念說明、分析框架、可以去哪裡查證）。\n" +
                  "4. 開頭不必再重複警告（系統會自動加上），直接交付內容。";
        }
    }
}
