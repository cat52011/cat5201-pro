using test;
using Xunit;

namespace cat5201.Tests
{
    /// <summary>下游自動模式的儲存 round-trip——個人化存 _preferences.json 靠這對函式，走樣＝設定跑掉。</summary>
    public class DownstreamAutoModeHelperTests
    {
        [Theory]
        [InlineData(DownstreamAutoMode.OneClick)]
        [InlineData(DownstreamAutoMode.FullyAuto)]
        [InlineData(DownstreamAutoMode.Off)]
        public void RoundTrip_Preserves(DownstreamAutoMode mode)
            => Assert.Equal(mode, DownstreamAutoModeHelper.Parse(DownstreamAutoModeHelper.ToStorageValue(mode)));

        [Theory]
        [InlineData("FullyAuto")]
        [InlineData("fully_auto")]
        [InlineData("auto")]
        [InlineData("2")]
        public void Parse_Aliases_FullyAuto(string raw)
            => Assert.Equal(DownstreamAutoMode.FullyAuto, DownstreamAutoModeHelper.Parse(raw));

        [Theory]
        [InlineData("off")]
        [InlineData("disabled")]
        [InlineData("none")]
        public void Parse_Aliases_Off(string raw)
            => Assert.Equal(DownstreamAutoMode.Off, DownstreamAutoModeHelper.Parse(raw));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("garbage")]
        public void Parse_Unknown_DefaultsToOneClick(string? raw)
            => Assert.Equal(DownstreamAutoMode.OneClick, DownstreamAutoModeHelper.Parse(raw));
    }
}
