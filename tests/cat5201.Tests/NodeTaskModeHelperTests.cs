using test;
using Xunit;

namespace cat5201.Tests
{
    /// <summary>節點任務模式的解析/儲存要容錯且 round-trip 穩定（存專案檔、跨版本讀取都靠它）。</summary>
    public class NodeTaskModeHelperTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-real-mode")]
        public void ParseOrDefault_FallsBackToChat(string? raw)
            => Assert.Equal(NodeTaskMode.Chat, NodeTaskModeHelper.ParseOrDefault(raw));

        [Theory]
        [InlineData("research", NodeTaskMode.Research)]
        [InlineData("RESEARCH", NodeTaskMode.Research)]
        [InlineData("code", NodeTaskMode.Code)]
        public void ParseOrDefault_IsCaseInsensitive(string raw, NodeTaskMode expected)
            => Assert.Equal(expected, NodeTaskModeHelper.ParseOrDefault(raw));

        [Fact]
        public void RoundTrip_AllModes_Preserved()
        {
            foreach (var opt in NodeTaskModeHelper.All)
                Assert.Equal(
                    opt.Value,
                    NodeTaskModeHelper.ParseOrDefault(NodeTaskModeHelper.ToStorageValue(opt.Value)));
        }
    }
}
