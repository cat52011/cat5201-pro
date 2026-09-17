using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-09-17 模型世代升級（GPT-5.6 Sol / GPT-6 Astra / Claude Sonnet 5 / Opus 5）的相容性釘：
    /// ① 舊專案/舊偏好存的舊 ID 要路由到「同廠牌」後繼模型（不能掉到預設、默默換廠牌）；
    /// ② 歷史執行紀錄的顯示名稱必須照實（不能把 Sonnet 4.6 的紀錄改寫成 Sonnet 5）——可稽查底線；
    /// ③ 新 ID 全部可用、預設模型是 GPT-5.6。
    /// </summary>
    public class ModelGenerationMigrationTests
    {
        [Theory]
        [InlineData("gpt-5.5", "gpt-5.6-sol")]
        [InlineData("claude-sonnet-4-6", "claude-sonnet-5")]
        [InlineData("claude-opus-4-8", "claude-opus-5")]
        public void LegacyId_RoutesToSameVendorSuccessor(string legacy, string successor)
        {
            Assert.Equal(successor, AiModelHelper.NormalizeNodeModel(legacy));
            Assert.Equal(
                AiModelHelper.GetDefinition(successor).Provider,
                AiModelHelper.GetDefinition(legacy).Provider); // 廠牌不變
        }

        [Theory]
        [InlineData("claude-sonnet-4-6", "Claude Sonnet 4.6")]
        [InlineData("claude-opus-4-8", "Claude Opus 4.8")]
        [InlineData("gpt-5.5", "GPT-5.5")]
        public void LegacyId_KeepsOriginalDisplayName_ForHistory(string legacy, string original)
            => Assert.Equal(original, AiModelRegistry.TryGetLegacyDisplayName(legacy));

        [Theory]
        [InlineData("claude-sonnet-5")]
        [InlineData("gpt-6-astra")]
        public void CurrentId_IsNotTreatedAsLegacy(string current)
            => Assert.Null(AiModelRegistry.TryGetLegacyDisplayName(current));

        [Theory]
        [InlineData("gpt-5.6-sol")]
        [InlineData("gpt-6-astra")]
        [InlineData("claude-sonnet-5")]
        [InlineData("claude-opus-5")]
        public void NewModels_AreKnownAndAvailable(string id)
        {
            Assert.True(AiModelRegistry.IsKnown(id));
            Assert.True(AiModelRegistry.IsAvailable(id));
        }

        [Fact]
        public void DefaultModel_IsGpt56()
            => Assert.Equal("gpt-5.6-sol", AiModelRegistry.Default.Id);

        [Fact]
        public void Gpt6_IsPremiumTier_SoCheapestPickNeverSelectsIt()
        {
            // GPT-6 Astra（$10/$50）不得成為任何「挑最便宜」路徑的首選。
            Assert.Equal(AiCostTier.Premium, AiModelHelper.GetDefinition("gpt-6-astra").CostTier);
            var cheapestCode = AiModelRegistry.CheapestWithCapability(AiModelCapability.Code);
            Assert.NotEqual("gpt-6-astra", cheapestCode?.Id);
        }
    }
}
