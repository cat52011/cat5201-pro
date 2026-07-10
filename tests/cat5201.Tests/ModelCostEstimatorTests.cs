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
    }
}
