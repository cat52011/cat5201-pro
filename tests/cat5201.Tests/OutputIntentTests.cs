using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 「要不要配圖」「幾張投影片」現在由第一層的語意判斷決定（任何語言都認得），
    /// 關鍵字只剩 API 不通時的保底。這裡驗證兩條路各自的行為。
    /// </summary>
    public class OutputIntentTests
    {
        [Fact]
        public void ApiAnswerCarriesIllustrationsAndSlideCount()
        {
            var intent = OutputIntentResolver.TryParse(
                "{\"presentation\":true,\"report\":false,\"table\":false,\"image\":false," +
                "\"video\":false,\"illustrations\":true,\"slides\":7}");

            Assert.NotNull(intent);
            Assert.True(intent!.WantsPresentation);
            Assert.True(intent.WantsIllustrations);
            Assert.Equal(7, intent.RequestedSlides);
            Assert.False(intent.WantsImage);      // 文件裡配圖 ≠ 要一張獨立圖檔
        }

        [Theory]
        // 模型可能把數字寫成字串，或整個欄位不給——都不能炸，也不能亂猜。
        [InlineData("{\"presentation\":true,\"illustrations\":\"true\",\"slides\":\"5\"}", true, 5)]
        [InlineData("{\"presentation\":true}", false, 0)]
        [InlineData("{\"presentation\":true,\"illustrations\":false,\"slides\":-3}", false, 0)]
        public void ParserToleratesLooseJson(string json, bool illustrations, int slides)
        {
            var intent = OutputIntentResolver.TryParse(json);

            Assert.NotNull(intent);
            Assert.Equal(illustrations, intent!.WantsIllustrations);
            Assert.Equal(slides, intent.RequestedSlides);
        }

        [Fact]
        public void KeywordFallbackStillFillsTheSameFields()
        {
            // API 不通時的保底：只認得繁中講法，但至少不能是空的。
            var intent = OutputIntent.FromKeywords("給我一份約五頁的簡報，內容需附圖");

            Assert.True(intent.WantsPresentation);
            Assert.True(intent.WantsIllustrations);
            Assert.Equal(5, intent.RequestedSlides);
            Assert.Equal("keywords", intent.Source);
        }
    }
}
