using test;
using Xunit;

namespace cat5201.Tests
{
    /// <summary>
    /// 第一層意圖閘門：寬鬆關鍵字（高召回）放行後才交 LLM 精準判斷。
    /// 這層漏放＝「給我一個15秒的影片」被當純文字漏判（曾踩過的坑），故釘住。
    /// </summary>
    public class OrchestrationGateTests
    {
        [Theory]
        [InlineData("做一個15秒的影片")]
        [InlineData("畫一張貓咪的圖")]
        [InlineData("generate an image of a sunset")]
        [InlineData("生成一張關於這個作曲家的照片")]   // 2026-07-04 實測漏判案例，釘住
        public void MentionsVideoOrImage_True_ForMediaRequests(string text)
            => Assert.True(OrchestrationPlanner.MentionsVideoOrImage(text));

        [Fact]
        public void ResolveTaskType_Image_ForPhotoRequest()
            => Assert.Equal(
                OrchestrationTaskType.ImageGeneration,
                OrchestrationPlanner.ResolveTaskType("生成一張關於這個作曲家的照片", NodeTaskMode.Chat));

        [Theory]
        [InlineData("幫我寫一段自我介紹")]
        [InlineData("")]
        [InlineData(null)]
        public void MentionsVideoOrImage_False_ForPlainText(string? text)
            => Assert.False(OrchestrationPlanner.MentionsVideoOrImage(text));

        [Theory]
        [InlineData("把這張圖改成夜景")]
        [InlineData("幫我把這張照片去背")]
        public void IsImageEditIntent_True_ForEditRequests(string text)
            => Assert.True(OrchestrationPlanner.IsImageEditIntent(text));

        [Fact]
        public void IsImageEditIntent_False_ForPlainText()
            => Assert.False(OrchestrationPlanner.IsImageEditIntent("翻譯這段文字"));
    }
}
