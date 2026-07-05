using test;
using Xunit;

namespace cat5201.Tests
{
    /// <summary>
    /// 影片參數解析：秒數/快剪偵測/合法基底量化。量化錯了會被 Veo API 400 拒（踩過），故釘住。
    /// </summary>
    public class VideoPlanBuilderTests
    {
        [Theory]
        [InlineData("幫我做一支預告片")]
        [InlineData("來個快剪")]
        [InlineData("做一個MV")]
        [InlineData("make a trailer")]
        public void DetectCutMode_FastCut_ForFastCutSignals(string text)
            => Assert.Equal(VideoCutMode.FastCut, VideoPlanBuilder.DetectCutMode(text));

        [Theory]
        [InlineData("拍一段風景影片")]
        [InlineData("")]
        [InlineData(null)]
        public void DetectCutMode_Continuous_ByDefault(string? text)
            => Assert.Equal(VideoCutMode.Continuous, VideoPlanBuilder.DetectCutMode(text));

        [Theory]
        [InlineData("拍一個15秒的影片", 15)]
        [InlineData("2分鐘的短片", 120)]
        [InlineData("1 minute clip", 60)]
        public void ParseRequestedSeconds_ReadsExplicitDuration(string text, int expected)
            => Assert.Equal(expected, VideoPlanBuilder.ParseRequestedSeconds(text));

        [Fact]
        public void ParseRequestedSeconds_ClampsToMax148()
            => Assert.Equal(148, VideoPlanBuilder.ParseRequestedSeconds("拍1000秒"));

        [Fact]
        public void ParseRequestedSeconds_NoNumber_ReturnsConsistentDefault()
        {
            int a = VideoPlanBuilder.ParseRequestedSeconds("");
            int b = VideoPlanBuilder.ParseRequestedSeconds("沒有講秒數的一句話");
            Assert.Equal(a, b);
            Assert.InRange(a, 1, 148);
        }

        [Theory]
        [InlineData(3, 4)]   // ≤4 → 4
        [InlineData(5, 4)]   // 5 偏 4
        [InlineData(6, 6)]
        [InlineData(7, 6)]   // 7 偏 6
        [InlineData(8, 8)]
        [InlineData(30, 8)]
        public void QuantizeBaseSeconds_SnapsToLegalVeoValues(int input, int expected)
            => Assert.Equal(expected, VideoPlanBuilder.QuantizeBaseSeconds(input));

        [Fact]
        public void FastCutShotCount_RoundsAndClamps()
        {
            Assert.Equal(4, VideoPlanBuilder.FastCutShotCount(10));      // round(10/2.5)=4
            Assert.True(VideoPlanBuilder.FastCutShotCount(1) >= 2);       // 下限
            // 極大值會撞上限：兩個都超過上限 → 相等（不必知道確切上限值）
            Assert.Equal(
                VideoPlanBuilder.FastCutShotCount(500),
                VideoPlanBuilder.FastCutShotCount(1000));
        }
    }
}
