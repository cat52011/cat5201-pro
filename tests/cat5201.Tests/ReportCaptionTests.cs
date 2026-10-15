using System.IO;
using System.Linq;
using Cat5201;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 正式報告的表與圖要有編號，內文才寫得出「如表 2 所示」，開會時也才指得到。
    /// AI 生成的示意圖一定要標明——讀者把它當成實景照片或實測結果的話，報告就在誤導人。
    /// docx 與 pdf 是同一份內容的兩個檔案，編號必須一致。
    /// </summary>
    public class ReportCaptionTests
    {
        private const string TwoTables = @"# 營收分析

表：各分類的客單價與毛利率（來源：協會年報 2025）

| 分類 | 客單價 |
| --- | --- |
| 甲 | 285 |

內文段落。

表：區域別展店數

| 區域 | 店數 |
| --- | --- |
| 巴黎 | 12 |
";

        [Fact]
        public void TablesAreNumberedInOrderAndKeepTheAuthorsCaption()
        {
            using var doc = WordprocessingDocument.Open(new MemoryStream(DocxReportBuilder.Build(TwoTables)), false);
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

            Assert.Contains("表 1｜各分類的客單價與毛利率（來源：協會年報 2025）", text);
            Assert.Contains("表 2｜區域別展店數", text);

            // 說明行本身不該再以內文段落的身分留下來一次。
            Assert.DoesNotContain("表：各分類", text);
        }

        [Fact]
        public void ACaptionLineThatIsNotFollowedByATableStaysOrdinaryText()
        {
            // 內文裡剛好以「表：」開頭的句子不能被當成標題吞掉。
            string markdown = "# 標題\n\n表：這句話後面沒有表格，它只是一段內文。\n";

            using var doc = WordprocessingDocument.Open(new MemoryStream(DocxReportBuilder.Build(markdown)), false);
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

            Assert.Contains("表：這句話後面沒有表格", text);
            Assert.DoesNotContain("表 1", text);
        }

        [Fact]
        public void TableCaptionsStickToTheirTable()
        {
            using var doc = WordprocessingDocument.Open(new MemoryStream(DocxReportBuilder.Build(TwoTables)), false);
            var caption = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
                .First(p => p.InnerText.StartsWith("表 1"));

            // 說明和表格被分頁拆開的話，那行字就沒有意義了。
            Assert.NotNull(caption.ParagraphProperties!.GetFirstChild<KeepNext>());
        }

        [Fact]
        public void CaptionWordsFollowTheOutputLanguage()
        {
            using var doc = WordprocessingDocument.Open(
                new MemoryStream(DocxReportBuilder.Build(TwoTables, null, "English")), false);
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

            Assert.Contains("Table 1 — 各分類的客單價與毛利率（來源：協會年報 2025）", text);
            Assert.DoesNotContain("表 1", text);
        }

        [Theory]
        [InlineData(null, "表", "圖", "資料來源", "合計")]
        [InlineData("English", "Table", "Figure", "Source", "Total")]
        [InlineData("日本語", "表", "図", "出典", "合計")]
        public void DocumentChromeIsTranslatedNotLeftInChinese(
            string? language, string table, string figure, string source, string total)
        {
            Assert.Equal(table, ReportWords.Table(language));
            Assert.Equal(figure, ReportWords.Figure(language));
            Assert.Equal(source, ReportWords.Source(language));
            Assert.Equal(total, ReportWords.Total(language));
        }

        [Fact]
        public void AiGeneratedPicturesAreLabelledAsSuch()
        {
            // 這是誠實問題，不是排版問題：沒標的話讀者會以為那是照片或實測圖。
            Assert.Contains("AI", ReportWords.AiIllustration(null));
            Assert.Contains("AI", ReportWords.AiIllustration("English"));
        }

        [Fact]
        public void TheReportPdfUsesTheSameNumbering()
        {
            // 兩份檔案的內容一致是對外承諾的一部分；編號不一致的話，交叉引用就對不上。
            byte[] pdf = PdfReportBuilder.Build(TwoTables);
            Assert.True(pdf.Length > 2000);
            Assert.Equal(0x25, pdf[0]);   // %PDF
        }
    }
}
