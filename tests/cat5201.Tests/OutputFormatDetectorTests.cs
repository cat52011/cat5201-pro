using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// §6 多輸出偵測是「要產哪些檔」的關鍵字判斷（OrchestrationPlanner 與 AgentRuntime 共用），
    /// 走樣會直接導致多產或漏產檔案。這是 pro 的第一批回歸測試，把這層行為釘住。
    /// </summary>
    public class OutputFormatDetectorTests
    {
        [Theory]
        [InlineData("幫我做一份簡報")]
        [InlineData("生成一個 PPT")]
        [InlineData("make a slide deck")]
        [InlineData("PPTX please")]
        public void WantsPresentation_True_ForDeckRequests(string input)
            => Assert.True(OutputFormatDetector.WantsPresentation(input));

        [Theory]
        [InlineData("寫一段文字回答我就好")]
        [InlineData("翻譯這句話")]
        [InlineData("")]
        [InlineData(null)]
        public void WantsPresentation_False_ForNonDeck(string? input)
            => Assert.False(OutputFormatDetector.WantsPresentation(input));

        [Theory]
        [InlineData("給我一份書面報告")]
        [InlineData("輸出成 Word")]
        [InlineData("export as a document")]
        public void WantsWrittenReport_True_ForReportRequests(string input)
            => Assert.True(OutputFormatDetector.WantsWrittenReport(input));

        [Theory]
        [InlineData("做一個 Excel 試算表")]
        [InlineData("整理成表格")]
        [InlineData("output a spreadsheet")]
        public void WantsSpreadsheet_True_ForTableRequests(string input)
            => Assert.True(OutputFormatDetector.WantsSpreadsheet(input));

        [Fact]
        public void WantsBothDeckAndReport_True_WhenBothMentioned()
            => Assert.True(OutputFormatDetector.WantsBothDeckAndReport("包括簡報跟書面報告"));

        [Fact]
        public void WantsBothDeckAndReport_False_WhenOnlyDeck()
            => Assert.False(OutputFormatDetector.WantsBothDeckAndReport("只要一份簡報就好"));

        [Fact]
        public void Detector_IsCaseInsensitive()
            => Assert.True(OutputFormatDetector.WantsPresentation("Please build a PRESENTATION"));
    }
}
