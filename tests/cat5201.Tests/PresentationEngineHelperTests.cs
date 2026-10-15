using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>簡報生成器選擇的儲存 round-trip + 別名解析（個人化設定，走樣＝引擎跑掉）。</summary>
    public class PresentationEngineHelperTests
    {
        [Theory]
        [InlineData(PresentationEngine.Claude)]
        [InlineData(PresentationEngine.Gpt)]
        public void RoundTrip_Preserves(PresentationEngine engine)
            => Assert.Equal(engine, PresentationEngineHelper.Parse(PresentationEngineHelper.ToStorageValue(engine)));

        [Theory]
        [InlineData("gpt", PresentationEngine.Gpt)]
        [InlineData("openai", PresentationEngine.Gpt)]
        // Gamma 已從介面下架：舊的存檔值要安靜地轉回 Claude，不能讓人卡在一個選不到的引擎上。
        [InlineData("gamma", PresentationEngine.Claude)]
        [InlineData("GAMMA", PresentationEngine.Claude)]
        [InlineData("2", PresentationEngine.Claude)]
        public void Parse_KnownAliases(string raw, PresentationEngine expected)
            => Assert.Equal(expected, PresentationEngineHelper.Parse(raw));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("something-unknown")]
        public void Parse_Unknown_DefaultsToClaude(string? raw)
            => Assert.Equal(PresentationEngine.Claude, PresentationEngineHelper.Parse(raw));

        [Fact]
        public void ToDisplayName_IsNonEmpty_ForAllEngines()
        {
            foreach (PresentationEngine e in System.Enum.GetValues(typeof(PresentationEngine)))
                Assert.False(string.IsNullOrWhiteSpace(PresentationEngineHelper.ToDisplayName(e)));
        }
    }
}
