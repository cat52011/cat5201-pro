using System.IO;
using System.Linq;
using Cat5201;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 把字調大調粗只是「看起來像標題」。Word 不知道那是標題，導覽窗格就是空的、
    /// 插不了目錄、章節折疊沒反應、螢幕閱讀器讀不出層級——那份 .docx 就不是專業文件。
    /// </summary>
    public class DocxStyleTests
    {
        private const string LongReport = @"# 2025 年營收分析

> 產生時間：2026-01-01

## 摘要

營收成長 12%。

## 關鍵發現

要點一。

## 市場結構

| 分類 | 營收 |
| --- | --- |
| 甲 | 100 |
| 乙 | 200 |

### 區域拆解

細節。

## 結論與建議

維持投入。
";

        private static WordprocessingDocument Open(byte[] docx)
            => WordprocessingDocument.Open(new MemoryStream(docx), false);

        [Fact]
        public void HeadingsAreRealStylesWithAnOutlineLevel()
        {
            using var doc = Open(DocxReportBuilder.Build(LongReport));
            var styles = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!;

            foreach (int level in new[] { 1, 2, 3 })
            {
                var style = styles.Elements<Style>().Single(s => s.StyleId == "Heading" + level);

                // Word 認得標題靠的是這兩樣：樣式名稱與大綱層級。
                Assert.Equal($"heading {level}", style.StyleName!.Val!.Value);
                Assert.Equal(level - 1,
                    style.StyleParagraphProperties!.GetFirstChild<OutlineLevel>()!.Val!.Value);

                // 標題不能單獨留在頁尾、內文卻跑到次頁。
                Assert.NotNull(style.StyleParagraphProperties.GetFirstChild<KeepNext>());
                Assert.NotNull(style.StyleParagraphProperties.GetFirstChild<KeepLines>());
            }
        }

        [Fact]
        public void HeadingParagraphsReferenceTheStyleInsteadOfFakingIt()
        {
            using var doc = Open(DocxReportBuilder.Build(LongReport));
            var body = doc.MainDocumentPart!.Document!.Body!;

            var summary = body.Elements<Paragraph>()
                .First(p => p.InnerText.Trim() == "摘要");

            Assert.Equal("Heading2",
                summary.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        }

        [Fact]
        public void LongReportsGetATableOfContentsShortOnesDoNot()
        {
            using (var doc = Open(DocxReportBuilder.Build(LongReport)))
            {
                var field = doc.MainDocumentPart!.Document!.Body!
                    .Descendants<SimpleField>()
                    .FirstOrDefault(f => (f.Instruction?.Value ?? "").Contains("TOC"));

                Assert.NotNull(field);
                // Dirty：Word 開檔時自己更新頁碼，不必叫使用者按 F9。
                Assert.True(field!.Dirty!.Value);
                Assert.Contains("目錄", doc.MainDocumentPart.Document.Body.InnerText);
            }

            // 三兩節的報告加目錄只是多一頁。
            string shortReport = "# 小報告\n\n## 摘要\n\n一句話。\n\n## 結論\n\n另一句。\n";
            using (var doc = Open(DocxReportBuilder.Build(shortReport)))
            {
                Assert.DoesNotContain(
                    doc.MainDocumentPart!.Document!.Body!.Descendants<SimpleField>(),
                    f => (f.Instruction?.Value ?? "").Contains("TOC"));
            }
        }

        [Fact]
        public void TheContentsHeadingFollowsTheReportsOwnLanguage()
        {
            using var doc = Open(DocxReportBuilder.Build(LongReport, null, "English"));
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

            Assert.Contains("Contents", text);
            Assert.DoesNotContain("目錄", text);
        }

        [Fact]
        public void LongTablesRepeatTheirHeaderAndDoNotSplitRows()
        {
            using var doc = Open(DocxReportBuilder.Build(LongReport));
            var table = doc.MainDocumentPart!.Document!.Body!.Descendants<Table>().First();
            var rows = table.Elements<TableRow>().ToList();

            // 表格跨頁時，第二頁沒有標題列就看不懂哪一欄是什麼。
            Assert.NotNull(rows[0].TableRowProperties!.GetFirstChild<TableHeader>());
            Assert.All(rows, r => Assert.NotNull(r.TableRowProperties!.GetFirstChild<CantSplit>()));
        }
    }
}
