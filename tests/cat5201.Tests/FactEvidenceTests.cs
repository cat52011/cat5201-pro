using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 金融事實的「信心」與「來源等級」必須由證據決定。
    ///
    /// 原本每一筆都固定 Confidence="high"、SourceUrl=""，而來源等級的預設分支是 official——
    /// 模型掰出來的營收數字會掛著「官方來源・高信心」進入報告。對一個主打
    /// 「每一步可稽查」的產品，那不是小瑕疵，是賣點本身沒有兌現。
    /// </summary>
    public class FactEvidenceTests
    {
        [Theory]
        [InlineData("https://investor.tsmc.com/q1-2026.pdf", "high")]
        [InlineData("見 TSMC 投資人關係頁 https://investor.tsmc.com/", "high")]
        [InlineData("TSMC 2026 Q1 法說會簡報", "medium")]   // 有敘述、沒有網址
        [InlineData("", "low")]                              // 什麼都沒有
        [InlineData("   ", "low")]
        public void ConfidenceFollowsTheEvidence(string evidence, string expected)
            => Assert.Equal(expected, SearchCapability.ResolveConfidence(evidence));

        [Fact]
        public void WithoutAClickableSourceNothingMayClaimToBeOfficial()
        {
            // 這是整個修正的重點：說得出「官方」，就要拿得出那份官方文件。
            Assert.Equal(
                FactOwnership.AuthoritySearchContext,
                SearchCapability.ResolveFinanceAuthority("revenue", ""));

            Assert.Equal(
                FactOwnership.AuthoritySearchContext,
                SearchCapability.ResolveFinanceAuthority("revenue", "模型說這是官方數字"));
        }

        [Fact]
        public void WithASourceTheNormalRulesApply()
        {
            Assert.Equal(
                FactOwnership.AuthorityOfficial,
                SearchCapability.ResolveFinanceAuthority("revenue", "https://investor.example.com/q1.pdf"));

            // 報價即使有來源也只是市場報價，不會升成官方。
            Assert.Equal(
                FactOwnership.AuthorityMarketQuote,
                SearchCapability.ResolveFinanceAuthority("regular_close_price", "https://finance.example.com/quote"));
        }

        [Theory]
        [InlineData("來源：https://a.example.com/x.pdf 。", "https://a.example.com/x.pdf")]
        [InlineData("(https://b.example.com/y)", "https://b.example.com/y")]
        [InlineData("沒有網址", "")]
        [InlineData("", "")]
        public void UrlExtractionStopsAtPunctuation(string evidence, string expected)
            => Assert.Equal(expected, SearchCapability.ExtractUrl(evidence));
    }
}
