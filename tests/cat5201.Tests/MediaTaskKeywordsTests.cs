using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// MediaTaskKeywords 是媒體關鍵字的單一真相(#7 收斂第一刀)：
    /// OrchestrationPlanner(觸發生成)與 NodeControl(逾時策略)都引用這裡。
    /// 釘住三組清單的語義差異：命令式嚴格(生成) vs 提及寬鬆(逾時)。
    /// </summary>
    public class MediaTaskKeywordsTests
    {
        [Theory]
        [InlineData("幫我畫一張未來城市的夜景")]
        [InlineData("生成一張貓的照片")]
        [InlineData("generate image of a cat")]
        public void ImageCommand_Matches(string text)
            => Assert.True(MediaTaskKeywords.IsImageGenerationCommand(text));

        [Theory]
        [InlineData("幫我製作影片：台積電 30 秒介紹")]
        [InlineData("生成影片：產品開箱")]
        [InlineData("make a video about our product")]
        public void VideoCommand_Matches(string text)
            => Assert.True(MediaTaskKeywords.IsVideoGenerationCommand(text));

        [Fact]
        public void VideoCommand_IsStrict_MereMentionDoesNotTrigger()
        {
            // 「他發了個影片限動」是聊天不是任務——嚴格清單不可命中(誤觸=花冤枉錢)。
            const string chat = "他昨天發了個影片限動超好笑";
            Assert.False(MediaTaskKeywords.IsVideoGenerationCommand(chat));
            // 但寬鬆的逾時提示可以命中(多給時間無害)。
            Assert.True(MediaTaskKeywords.MentionsVideoForTimeout(chat));
        }

        [Fact]
        public void PlannerAndTimeout_UseSameImageSource()
        {
            // 收斂驗收：同一句話,planner 的任務判定與逾時判定來自同一組清單,不再各養各的。
            const string text = "產生圖片：一隻太空貓";
            Assert.Equal(
                OrchestrationTaskType.ImageGeneration,
                OrchestrationPlanner.ResolveTaskType(text, NodeTaskMode.Chat));
            Assert.True(MediaTaskKeywords.IsImageGenerationCommand(text));
        }

        [Theory]
        [InlineData("解釋一下量子糾纏")]
        [InlineData("翻譯這段文字")]
        public void PlainText_MatchesNothing(string text)
        {
            Assert.False(MediaTaskKeywords.IsImageGenerationCommand(text));
            Assert.False(MediaTaskKeywords.IsVideoGenerationCommand(text));
            Assert.False(MediaTaskKeywords.MentionsVideoForTimeout(text));
        }
    }
}
