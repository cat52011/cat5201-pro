using System.Linq;
using test;
using Xunit;

namespace cat5201.Tests
{
    /// <summary>模型註冊表是路由/fallback/UI 的單一真相；查詢行為要正確且容錯。</summary>
    public class AiModelRegistryTests
    {
        [Fact]
        public void Default_IsKnown_AndAvailable()
        {
            var def = AiModelRegistry.Default;
            Assert.False(string.IsNullOrWhiteSpace(def.Id));
            Assert.True(AiModelRegistry.IsKnown(def.Id));
            Assert.True(AiModelRegistry.IsAvailable(def.Id));
        }

        [Fact]
        public void Find_IsCaseInsensitive_AndTrims()
        {
            var id = AiModelRegistry.Default.Id;
            Assert.NotNull(AiModelRegistry.Find("  " + id.ToUpperInvariant() + "  "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("definitely-not-a-real-model-xyz")]
        public void IsKnown_False_ForUnknown(string? id)
            => Assert.False(AiModelRegistry.IsKnown(id));

        [Fact]
        public void Available_NonEmpty_AndAllMarkedAvailable()
        {
            var avail = AiModelRegistry.Available;
            Assert.NotEmpty(avail);
            Assert.All(avail, m => Assert.True(m.IsAvailable));
        }
    }
}
