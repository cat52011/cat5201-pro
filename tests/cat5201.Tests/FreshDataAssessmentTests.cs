using System.Collections.Generic;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 「需要最新資料的任務到底拿到了沒」：舊版只看有沒有 search_summary 物件，
    /// 搜尋服務失敗時的警告佔位也被當成已取得即時資料。這裡釘住看實際狀態與內容。
    /// </summary>
    public class FreshDataAssessmentTests
    {
        private static Dictionary<string, object> Data(params (string Key, object Value)[] items)
        {
            var d = new Dictionary<string, object>();
            foreach (var (k, v) in items) d[k] = v;
            return d;
        }

        [Fact]
        public void ServiceFailedSearch_IsNotUsableData()
        {
            var a = FreshDataAssessment.Evaluate(Data(("search_summary", new SearchSummaryPayload
            {
                Query = "台積電最新股價",
                Status = SearchStatus.ServiceFailed,
                StatusDetail = "503",
                Summary = "⚠️ 搜尋服務暫時無法使用"
            })));

            Assert.False(a.HasUsableData);
            Assert.Equal(FreshDataAssessment.StatusServiceFailed, a.Status);
            Assert.Contains("即時資料未取得", a.BuildUserBanner());
            Assert.Contains("不得聲稱已搜尋、已查證", a.BuildSynthesisNotice());
        }

        [Fact]
        public void SucceededStatusButNoItems_IsNotUsableData()
        {
            var a = FreshDataAssessment.Evaluate(Data(("search_summary", new SearchSummaryPayload { Query = "x" })));
            Assert.False(a.HasUsableData);
        }

        [Fact]
        public void NothingProduced_IsNotRun()
        {
            var a = FreshDataAssessment.Evaluate(Data());
            Assert.Equal(FreshDataAssessment.StatusNotRun, a.Status);
            Assert.False(a.HasUsableData);
        }

        [Fact]
        public void SearchWithItems_IsUsable_ButLabeledAsSearchResults()
        {
            var a = FreshDataAssessment.Evaluate(Data(("search_summary", new SearchSummaryPayload
            {
                Query = "x",
                Items = new List<SearchSummaryItem> { new() { Title = "t", KeyPoint = "k" } }
            })));

            Assert.True(a.HasUsableData);
            Assert.Equal(FreshDataAssessment.StatusSearchResults, a.Status);

            // 2026-10-29：這裡原本斷言橫幅為空。但「只有搜尋摘錄」比「只核對過網域」更弱，
            // 弱的不出聲、強的反而出聲是說不通的——只有原文核對過（verified）才可以什麼都不說。
            Assert.True(a.NeedsUserNotice);
            Assert.Contains("僅有搜尋摘錄", a.BuildUserBanner());
        }

        [Fact]
        public void SearchContextFactsOnly_AreNotCalledVerified()
        {
            var facts = new VerifiedFactPayload
            {
                Facts = new List<VerifiedFactItem>
                {
                    // 要有一個真的數字，這次才算拿到資料（2026-10-28 起）。
                    new()
                    {
                        Subject = "s", Value = "v",
                        AuthorityLevel = FactOwnership.AuthoritySearchContext,
                        UsageRole = FactOwnership.UsageNumericFactSource,
                    }
                }
            };

            var a = FreshDataAssessment.Evaluate(Data(("verified_facts", facts)));

            Assert.True(a.HasUsableData);
            Assert.Equal(FreshDataAssessment.StatusSearchResults, a.Status);
            Assert.True(FactOwnership.IsSearchContextOnly(facts.Facts));
            Assert.Contains("未獨立查證", FactOwnership.DescribeVerification(facts.Facts));
        }

        [Fact]
        public void OfficialFacts_AreVerified_AndLabelRanksOfficialFirst()
        {
            // 「已查證」除了來源等級，還要真的有一個數字——只有等級沒有值不算。
            var items = new List<VerifiedFactItem>
            {
                new() { AuthorityLevel = FactOwnership.AuthoritySearchContext },
                new()
                {
                    AuthorityLevel = FactOwnership.AuthorityOfficial,
                    UsageRole = FactOwnership.UsageNumericFactSource,
                    FactType = "revenue", Value = "US$23.86B",
                },
                new()
                {
                    AuthorityLevel = FactOwnership.AuthorityOfficial,
                    UsageRole = FactOwnership.UsageNumericFactSource,
                    FactType = "eps", Value = "US$12.07",
                },
            };

            var a = FreshDataAssessment.Evaluate(Data(("verified_facts", new VerifiedFactPayload { Facts = items })));

            // 2026-10-28 第四輪 AI 輔助審查：這裡原本斷言 StatusVerified。
            // 但這兩筆事實的證據階段只到「網域在白名單上」（沒有人取過原文），
            // 整體判定若仍自稱 verified，就等於讓舊決策忽略新加入的 SourceVerification 欄位。
            // 「已查證」現在要求至少一筆走到第三階段（原文支持此主張），而系統做不到第三階段，
            // 所以這裡的正確答案是 domain_checked——資料可用，但查證沒走完。
            Assert.Equal(FreshDataAssessment.StatusDomainChecked, a.Status);
            Assert.True(a.HasUsableData);              // 資料是拿到了
            Assert.True(a.NeedsSourceCaution);         // 但要提醒使用者自己核對原文
            Assert.Contains("僅核對網域", a.BuildUserBanner());

            // 標籤 2026-10-28 改成「官方網域（未核對原文）」：白名單只證明網域歸屬。
            Assert.Equal("官方網域（未核對原文） 2・搜尋摘錄（未獨立查證） 1",
                FactOwnership.DescribeVerification(items));
        }

        /// <summary>
        /// 2026-10-27 第三方審查：所有欄位都是「未取得」時仍被判成已查證。
        /// 來源等級再高，沒有數字就不是查證。
        /// </summary>
        [Fact]
        public void OfficialSourceButNoRealValue_IsNotVerified()
        {
            var items = new List<VerifiedFactItem>
            {
                new()
                {
                    AuthorityLevel = FactOwnership.AuthorityOfficial,
                    UsageRole = FactOwnership.UsageNumericFactSource,
                    FactType = "revenue", Value = "未取得",
                },
                new()
                {
                    AuthorityLevel = FactOwnership.AuthorityMarketQuote,
                    UsageRole = FactOwnership.UsageBackgroundContext,
                    FactType = "quote_availability",
                    Value = "regular_close_price=not_available; after_hours_price=not_available",
                },
            };

            var a = FreshDataAssessment.Evaluate(Data(("verified_facts", new VerifiedFactPayload { Facts = items })));

            Assert.NotEqual(FreshDataAssessment.StatusVerified, a.Status);
        }
    }
}
