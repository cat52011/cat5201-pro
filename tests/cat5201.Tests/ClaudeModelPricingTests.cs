using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 價目與快取倍率以 Anthropic 官方 pricing 頁為準（2026-09-25 查證）。
    /// 這些數字錯了，餘額就會一路偏掉，而且沒有人會發現。
    /// </summary>
    public class ClaudeModelPricingTests
    {
        [Fact]
        public void OpusIsNowFivePointFiveWithItsOwnPrice()
        {
            Assert.Equal("claude-opus-5-5", AiModels.Claude_Opus5);
            Assert.Equal("claude-sonnet-5", AiModels.Claude_Sonnet5);

            // Opus 5.5：$4 / $20；Opus 5：$5 / $25；Sonnet 5：$2 / $10。
            var opus55 = ModelCostEstimator.FromUsage("claude-opus-5-5", 1_000_000, 1_000_000);
            Assert.Equal(24.0, opus55.UsdCost, 4);

            var opus5 = ModelCostEstimator.FromUsage("claude-opus-5", 1_000_000, 1_000_000);
            Assert.Equal(30.0, opus5.UsdCost, 4);

            var sonnet5 = ModelCostEstimator.FromUsage("claude-sonnet-5", 1_000_000, 1_000_000);
            Assert.Equal(12.0, sonnet5.UsdCost, 4);
        }

        [Theory]
        [InlineData("claude-opus-5-5", 0.05)]   // 官方：Opus 5.5 的快取讀取是 0.05x
        [InlineData("claude-sonnet-5", 0.10)]
        [InlineData("claude-opus-5", 0.10)]
        [InlineData("claude-fable-5-1", 0.025)]
        public void CacheReadMultiplierIsPerModel(string modelId, double expected)
            => Assert.Equal(expected, ModelCostEstimator.CacheReadMultiplier(modelId), 4);

        [Fact]
        public void HistoricalRecordsKeepTheModelTheyActuallyRanOn()
        {
            // 舊紀錄跑的是 Opus 5，不能因為預設改成 5.5 就被改寫——成本可稽查的底線。
            Assert.Equal("Claude Opus 5", AiModelRegistry.TryGetLegacyDisplayName("claude-opus-5"));
        }
    }
}
