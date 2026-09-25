using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 關鍵字判斷是「第一層語意判斷不能用時」的保底（沒金鑰、API 不通）。
    /// 正常情況下要不要配圖是由模型讀使用者那句話決定的，跟語言無關；
    /// 這個保底只認得繁體中文的常見講法，所以只求別把明顯的講法漏掉、別誤判圖表與地圖。
    /// </summary>
    public class ImageRequestDetectionTests
    {
        [Theory]
        [InlineData("內容需部分附圖")]
        [InlineData("多放一些圖片")]
        [InlineData("希望圖文並茂")]
        [InlineData("每頁配一張照片")]
        [InlineData("可以加一些插畫嗎")]
        [InlineData("簡報要有圖")]
        [InlineData("幫我做一份有圖片的簡報")]
        [InlineData("make it illustrated with images")]
        // 預覽裡「重新生成這一頁」的修改要求也吃同一個判斷：提到圖就要真的去生一張。
        [InlineData("增加符合此頁內容的附圖")]
        [InlineData("這頁幫我配一張圖")]
        public void WantsImages_RecognisesNaturalPhrasing(string input)
            => Assert.True(AgentRuntime.PresentationWantsCoverImage(input));

        [Theory]
        [InlineData("不要圖片，純文字就好")]
        [InlineData("不用配圖")]
        [InlineData("不需要任何圖片")]
        [InlineData("text only please")]
        public void WantsImages_RespectsExplicitRefusal(string input)
            => Assert.False(AgentRuntime.PresentationWantsCoverImage(input));

        [Theory]
        // 沒提到圖就是不配圖（使用者的規則），而且「圖表／地圖／藍圖」不算要配圖。
        [InlineData("給我一份五頁的塔斯馬尼亞簡報")]
        [InlineData("用圖表呈現營收成長")]
        [InlineData("說明公司的發展藍圖")]
        [InlineData("介紹台北捷運路線圖的設計")]
        public void WantsImages_DoesNotFireOnChartsOrMaps(string input)
            => Assert.False(AgentRuntime.PresentationWantsCoverImage(input));
    }
}
