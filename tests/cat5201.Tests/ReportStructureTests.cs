using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 報告的品質有一半在「內容骨架」，不在排版。只規定「別畫 ASCII 線框」的話，
    /// 模型交回來的常常只是把主答案換行重排——版面再好看也不是一份報告。
    /// </summary>
    public class ReportStructureTests
    {
        [Fact]
        public void ReportPromptSpecifiesTheSkeletonNotJustFormatting()
        {
            string prompt = DocumentAuthor.BuildReportPrompt("分析 2025 年營收", "一些素材");

            Assert.Contains("摘要", prompt);
            Assert.Contains("關鍵發現", prompt);
            Assert.Contains("結論與建議", prompt);
            Assert.Contains("結論式標題", prompt);
        }

        [Fact]
        public void NumbersMustCarryUnitsAndAPeriodAndMissingDataMustBeSaidOutLoud()
        {
            string prompt = DocumentAuthor.BuildReportPrompt("分析營收", "素材");

            Assert.Contains("單位", prompt);
            Assert.Contains("期間", prompt);
            // 缺資料要寫「未取得」，而不是編一個看起來合理的數字。
            Assert.Contains("未取得", prompt);
        }

        [Fact]
        public void SourcesAreTheSystemsJobSoTheAuthorMustNotInventThem()
        {
            // 來源由系統依實際查證紀錄附上；讓模型自己寫，它會憑印象編參考書目。
            Assert.Contains("不要自己寫「資料來源」", DocumentAuthor.BuildReportPrompt("主題", "素材"));
        }

        [Theory]
        [InlineData(null, "繁體中文")]
        [InlineData("", "繁體中文")]
        [InlineData("English", "English")]
        [InlineData("日本語", "日本語")]
        public void OutputLanguageComesFromThisRequestNotAHardCodedDefault(string? language, string expected)
        {
            Assert.Contains($"（{expected}）", DocumentAuthor.BuildReportPrompt("主題", "素材", language));
            Assert.Contains($"用{expected}；", DocumentAuthor.BuildTablePrompt("主題", "素材", language));
        }

        [Fact]
        public void TheAssembledReportHasNoEmptyWrapperSection()
        {
            string markdown = MarkdownReportBuilder.Build(new MarkdownReportBuilder.Request
            {
                UserInput = "2025 年營收分析",
                FinalAnswer = "## 摘要\n\n營收成長 12%。\n\n## 結論與建議\n\n維持投入。",
            });

            // 「## 內容」是沒有意義的外層標題，只會出現在 Word 導覽窗格與目錄裡。
            Assert.DoesNotContain("## 內容", markdown);
            Assert.Contains("## 摘要", markdown);
            Assert.Contains("## 結論與建議", markdown);
        }
    }
}
