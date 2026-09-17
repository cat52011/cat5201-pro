using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>成本/token 估算是決策窗與成本控制的數字來源，行為要穩定。</summary>
    public class ModelCostEstimatorTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EstimateTokens_Zero_ForEmpty(string? text)
            => Assert.Equal(0, ModelCostEstimator.EstimateTokens(text));

        [Theory]
        [InlineData("hello world")]
        [InlineData("你好，這是一段中文輸入")]
        public void EstimateTokens_Positive_ForText(string text)
            => Assert.True(ModelCostEstimator.EstimateTokens(text) > 0);

        [Fact]
        public void FromUsage_ClampsNegatives_AndFlagsActual()
        {
            var e = ModelCostEstimator.FromUsage("some-model", -5, 100);
            Assert.Equal(0, e.InputTokens);      // 負數被夾成 0
            Assert.Equal(100, e.OutputTokens);
            Assert.Equal(100, e.TotalTokens);
            Assert.True(e.IsActual);             // 來自真實用量
            Assert.True(e.UsdCost >= 0);
        }

        [Fact]
        public void FromUsage_MoreTokens_CostsAtLeastAsMuch()
        {
            var small = ModelCostEstimator.FromUsage("some-model", 100, 100);
            var big = ModelCostEstimator.FromUsage("some-model", 10_000, 10_000);
            Assert.True(big.UsdCost >= small.UsdCost);
        }

        [Fact]
        public void ImageCostUsd_Positive_EvenForUnknownSize()
        {
            Assert.True(ModelCostEstimator.ImageCostUsd(null, null) > 0);        // 退回預設
            Assert.True(ModelCostEstimator.ImageCostUsd("9999x9999", "weird") > 0);
        }

        // ===== 價目表回歸釘（官方價核對 2026-07-13）=====
        // 你的賣點是「成本可稽查」，價目寫錯＝直接打臉。改價時同步更新這裡。

        [Theory]
        // 現役（官方價核對 2026-09-17）
        [InlineData("claude-opus-5", 30.00)]    // $5 in + $25 out
        [InlineData("claude-sonnet-5", 12.00)]  // $2 in + $10 out
        [InlineData("gpt-5.6-sol", 24.00)]      // $4 in + $20 out（促銷價，至少到 2026-11-21）
        [InlineData("gpt-6-astra", 60.00)]      // $10 in + $50 out
        // 舊世代：價目必須保留，舊專案的歷史執行紀錄靠它算成本
        [InlineData("claude-opus-4-8", 30.00)]
        [InlineData("gpt-5.5", 35.00)]
        [InlineData("gemini-3.1-pro", 14.00)]   // $2 in + $12 out
        public void FromUsage_OneMillionEach_MatchesOfficialPricing(string modelId, double expectedUsd)
        {
            var e = ModelCostEstimator.FromUsage(modelId, 1_000_000, 1_000_000);
            Assert.Equal(expectedUsd, e.UsdCost, precision: 2);
        }

        [Fact]
        public void FromUsage_Perplexity_IncludesPerRequestFee()
        {
            // token 費（0.1+0.1）之外要含 $0.008/次 的 search/request fee —— 只算 token 會系統性低估。
            var e = ModelCostEstimator.FromUsage("pplx-sonar", 100_000, 100_000);
            Assert.Equal(0.208, e.UsdCost, precision: 3);
        }

        [Fact]
        public void FromUsage_ZeroUsage_NoPerRequestFee()
        {
            // 沒有任何用量＝沒真的呼叫，不得收固定費。
            Assert.Equal(0, ModelCostEstimator.FromUsage("pplx-sonar", 0, 0).UsdCost);
        }
    }
}
