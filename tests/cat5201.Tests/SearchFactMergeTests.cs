using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 財經研究的事實合併規則：決定哪一個數字最後會進到報告裡。
    /// 這一塊一直沒有測試保護，但它直接決定「可稽查」這個賣點成不成立——
    /// 合併錯了，報告上的數字就是錯的，而且還掛著來源。
    /// </summary>
    public class SearchFactMergeTests
    {
        [Theory]
        [InlineData("TSM", "TSM", true)]
        [InlineData("TSM 台積電", "TSM", true)]
        [InlineData("TSM(台積電)", "TSM", true)]
        [InlineData("tsm", "TSM", true)]
        [InlineData("TSMC", "TSM", false)]   // 不能只比前綴，不然 TSMC 會被當成 TSM
        [InlineData("", "TSM", false)]
        [InlineData("TSM", "", false)]
        public void TickerMatchingDoesNotOverReach(string subject, string ticker, bool expected)
            => Assert.Equal(expected, SearchCapability.SubjectMatchesTicker(subject, ticker));

        [Theory]
        [InlineData("TSM 台積電", "TSM")]
        [InlineData("TSM(台積電)", "TSM")]
        [InlineData("  AAPL  ", "AAPL")]
        [InlineData("", "")]
        public void SubjectsAreNormalisedToTheTickerBeforeMerging(string subject, string expected)
            => Assert.Equal(expected, SearchCapability.NormalizeSubjectForMerge(subject));

        [Theory]
        [InlineData("revenue", true)]
        [InlineData("eps", true)]
        [InlineData("gross_margin", true)]
        [InlineData("guidance", true)]
        [InlineData("latest_fiscal_quarter", true)]
        [InlineData("REVENUE", true)]
        [InlineData("analyst_opinion", false)]
        [InlineData("", false)]
        public void OnlyOfficialMetricsCountAsAuthoritative(string factType, bool expected)
            => Assert.Equal(expected, SearchCapability.IsOfficialFinanceMetric(factType));

        [Fact]
        public void ARepairedFactNeverOverwritesAGoodOneWithNothing()
        {
            var good = new VerifiedFactItem { FactType = "revenue", Value = "US$35.90B" };
            var empty = new VerifiedFactItem { FactType = "revenue", Value = "未取得" };

            // 補抓回來的是空的就不能蓋掉已經有的數字。
            Assert.False(SearchCapability.ShouldReplaceFinanceFact(good, empty));
            // 反過來，原本是空的就該被補上。
            Assert.True(SearchCapability.ShouldReplaceFinanceFact(empty, good));
        }

        [Fact]
        public void MissingCurrentFactIsAlwaysReplaced()
            => Assert.True(SearchCapability.ShouldReplaceFinanceFact(
                null!, new VerifiedFactItem { FactType = "revenue", Value = "US$1B" }));
    }
}
