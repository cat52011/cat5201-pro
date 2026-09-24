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
            Assert.Equal("", a.BuildUserBanner());
        }

        [Fact]
        public void SearchContextFactsOnly_AreNotCalledVerified()
        {
            var facts = new VerifiedFactPayload
            {
                Facts = new List<VerifiedFactItem>
                {
                    new() { Subject = "s", Value = "v", AuthorityLevel = FactOwnership.AuthoritySearchContext }
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
            var items = new List<VerifiedFactItem>
            {
                new() { AuthorityLevel = FactOwnership.AuthoritySearchContext },
                new() { AuthorityLevel = FactOwnership.AuthorityOfficial },
                new() { AuthorityLevel = FactOwnership.AuthorityOfficial },
            };

            var a = FreshDataAssessment.Evaluate(Data(("verified_facts", new VerifiedFactPayload { Facts = items })));

            Assert.Equal(FreshDataAssessment.StatusVerified, a.Status);
            Assert.Equal("官方來源 2・搜尋摘錄（未獨立查證） 1", FactOwnership.DescribeVerification(items));
        }
    }
}
