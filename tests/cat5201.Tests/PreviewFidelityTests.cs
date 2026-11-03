using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 預覽存在的理由是「不用打開檔案就知道交出去的東西長怎樣」。
    /// 只要預覽跟成品長得不一樣，使用者還是得自己開一次——那預覽就白做了。
    /// </summary>
    public class PreviewFidelityTests
    {
        private static string WriteTemp(byte[] bytes, string extension)
        {
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + extension);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        [Fact]
        public void WideReportTables_ScrollInsideThePage_InsteadOfSpillingOut()
        {
            // 2026-11-03：沙箱報告的敘述統計（9 欄）在預覽裡超出頁面右邊。
            string html = ArtifactHtmlRenderer.BuildDocxHtml(
                "| 變數 | count | mean | std | min | 25% | 50% | 75% | max |\n|---|---|---|---|---|---|---|---|---|\n" +
                "| 總價元 | 2,767 | 29,562,186 | 20,718,272 | 4,320,000 | 15,680,000 | 23,900,000 | 35,000,000 | 280,000,000 |");

            Assert.Contains("<div class='table-wrap'><table>", html);
            Assert.Contains("</table></div>", html);
            Assert.Contains(".table-wrap { overflow-x: auto;", html);
        }

        [Fact]
        public void RawHtmlInContent_IsShownAsText_NotExecuted()
        {
            // 內容來自 Word 檔或模型輸出；預覽裡不能執行其中的 <script>。
            string html = ArtifactHtmlRenderer.BuildDocxHtml("報告\n\n<script>alert(1)</script>\n\n<iframe src=x></iframe>");
            Assert.DoesNotContain("<script>alert", html);
            Assert.DoesNotContain("<iframe", html);
            Assert.Contains("&lt;script&gt;", html);
        }

        [Fact]
        public void BoldNextToChinesePunctuation_IsRenderedAsBold_NotAsAsterisks()
        {
            // 「**敘述統計：**總價…」：標準 Markdown 不認全形標點後緊接中文的粗體，預覽會露出 **。
            string html = ArtifactHtmlRenderer.BuildDocxHtml("**敘述統計：**總價平均 29.56 百萬元。");

            Assert.Contains("<strong>敘述統計：</strong>", html);
            Assert.DoesNotContain("**", html);
        }

        [Fact]
        public void ReportParagraphs_KeepTheirBold_InThePreview()
        {
            // 2026-11-03 沙箱報告（python-docx 產生）的預覽有 27 處露出 **：
            // 一句粗體被切成好幾個 run、粗體裡頭前後帶全形空白、連續段落只換一行就接著圖。
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".docx");
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                       path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                static DocumentFormat.OpenXml.Wordprocessing.Run Run(string text, bool bold)
                {
                    var r = new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text(text)
                        { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
                    if (bold)
                        r.PrependChild(new DocumentFormat.OpenXml.Wordprocessing.RunProperties(
                            new DocumentFormat.OpenXml.Wordprocessing.Bold()));
                    return r;
                }
                static DocumentFormat.OpenXml.Wordprocessing.Paragraph Para(params DocumentFormat.OpenXml.Wordprocessing.Run[] runs)
                    => new(runs);

                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body(
                        Para(Run("資科系　　學號：　　姓名：　　", true)),
                        Para(Run("清理步驟", true), Run("與剩餘筆數：", true)),
                        Para(Run("圖表說明：取對數後偏態降為 0.09。", true)),
                        Para(Run("• 第一點", false)),
                        Para(Run("• 第二點", false)),
                        Para(Run("清單後面的一般段落。", false))));
                main.Document.Save();
            }

            string markdown = ArtifactTextExtractor.ExtractDocx(path);
            string html = ArtifactHtmlRenderer.BuildDocxHtml(markdown);
            File.Delete(path);

            Assert.DoesNotContain("**", html);
            Assert.Contains("<strong>資科系　　學號：　　姓名：</strong>", html);
            Assert.Contains("<strong>清理步驟與剩餘筆數：</strong>", html);
            Assert.Contains("<li>第二點</li>", html);
            Assert.Contains("<p>清單後面的一般段落。</p>", html);
        }

        [Fact]
        public void LayoutTables_ShowAsParagraphs_DataTablesKeepTheirCellsApart()
        {
            // 2026-11-03 BMI 報告（Claude 文件技能）用表格排版：一格放「標題＋整段說明」、中間一欄空白當間隔。
            // 當成資料表轉出來，整段文字擠在一格；同一格的兩段還黏成「18.5 – 24健康體重」。
            static DocumentFormat.OpenXml.Wordprocessing.Paragraph P(string text, bool bold = false)
            {
                var run = new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
                if (bold) run.PrependChild(new DocumentFormat.OpenXml.Wordprocessing.RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold()));
                return new DocumentFormat.OpenXml.Wordprocessing.Paragraph(run);
            }
            static DocumentFormat.OpenXml.Wordprocessing.TableCell Cell(params DocumentFormat.OpenXml.Wordprocessing.Paragraph[] ps)
                => new(ps.Length == 0 ? new[] { new DocumentFormat.OpenXml.Wordprocessing.Paragraph() } : ps);

            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".docx");
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                string longText = "BMI（Body Mass Index，身體質量指數）是國際常用的體位評估指標，用以初步判斷體重相對於身高是否落在健康範圍。計算簡單、不需特殊儀器，廣泛用於學校健康檢查。";
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Table(new DocumentFormat.OpenXml.Wordprocessing.TableRow(
                        Cell(P("01  前言", bold: true), P(longText)),
                        Cell(),                                                        // 空白間隔欄
                        Cell(P("02  計算公式", bold: true), P("BMI ＝ 體重 ÷ 身高²"), P("例：60 ÷ 1.7² ≈ 20.8")))),
                    new DocumentFormat.OpenXml.Wordprocessing.Table(new DocumentFormat.OpenXml.Wordprocessing.TableRow(
                        Cell(P("< 18.5"), P("體重過輕")),
                        Cell(P("18.5 – 24"), P("健康體重"))))));
                main.Document.Save();
            }

            string md = ArtifactTextExtractor.ExtractDocx(path);
            string html = ArtifactHtmlRenderer.BuildDocxHtml(md);
            File.Delete(path);

            // 排版表格：每一段各自成段，不是一格塞一整段
            Assert.Contains("<strong>01  前言</strong>", html);
            Assert.Contains("<p>" + "BMI（Body Mass Index", html);
            Assert.Contains("<p>例：60 ÷ 1.7² ≈ 20.8</p>", html);
            Assert.DoesNotContain("前言BMI", md);

            // 資料表格：照樣是表格，同一格的兩段用空白隔開
            Assert.Contains("18.5 – 24 健康體重", md);
            Assert.DoesNotContain("18.5 – 24健康體重", md);
            Assert.Contains("<table>", html);
        }

        [Fact]
        public void PreviewShowsWhatExcelShowsNotTheRawStoredValue()
        {
            // 百分比在檔案裡存的是 0.285，Excel 依格式顯示 28.5%。
            // 預覽照原始值印的話，使用者會以為數字錯了。
            string path = WriteTemp(
                XlsxReportBuilder.Build(
                    "| 品項 | 毛利率 | 營收 |\n| --- | --- | --- |\n| 甲 | 28.5% | 1,234 |",
                    "測試"),
                ".xlsx");

            try
            {
                var grid = ArtifactTextExtractor.ExtractXlsxGrid(path);

                // 欄位名稱那一列是從檔案的樣式找出來的，不是假設「第一列一定是表頭」。
                Assert.Equal("品項", grid.Rows[grid.HeaderRowIndex][0].Text);
                var first = grid.Rows[grid.HeaderRowIndex + 1];

                Assert.Equal("28.5%", first[1].Text);
                Assert.Equal("1,234", first[2].Text);

                // 數字要標記出來，預覽才能右對齊（跟 Excel 一樣）。
                Assert.False(first[0].IsNumber);
                Assert.True(first[1].IsNumber);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void HeaderColourComesFromTheWorkbookNotAGuess()
        {
            var art = DeckArtDirection.ForTopic("法國高檔餐點");
            string path = WriteTemp(
                XlsxReportBuilder.Build("| 欄 | 值 |\n| --- | --- |\n| 甲 | 1 |", "測試", art),
                ".xlsx");

            try
            {
                var grid = ArtifactTextExtractor.ExtractXlsxGrid(path);

                // 直接讀檔案裡的樣式，不必猜這份表當初套哪個藝術方向。
                Assert.Equal("#" + art.Accent, grid.HeaderColorHex);

                string html = ArtifactHtmlRenderer.BuildXlsxHtml(grid);
                Assert.Contains("#" + art.Accent, html);
                Assert.Contains("class='num'", html);

                // 標題與來源註記是文件的抬頭，不是表格的一列。
                Assert.Contains("sheet-title", html);
                Assert.Contains("sheet-note", html);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ThePreviewDrawsTheSameChartTheWorkbookHas()
        {
            string path = WriteTemp(
                XlsxReportBuilder.Build(
                    "| 分類 | 營收 |\n| --- | --- |\n| 甲 | 100 |\n| 乙 | 250 |\n| 丙 | 180 |", "測試"),
                ".xlsx");

            try
            {
                var grid = ArtifactTextExtractor.ExtractXlsxGrid(path);

                // 畫哪兩欄是從檔案裡的圖表公式讀出來的，預覽不會自己重新猜一次。
                Assert.NotNull(grid.Chart);
                Assert.Equal(0, grid.Chart!.CategoryColumn);
                Assert.Equal(1, grid.Chart.ValueColumn);

                string html = ArtifactHtmlRenderer.BuildXlsxHtml(grid);
                Assert.Contains("<svg", html);
                // 三根長條，合計列不算進去。
                Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<rect").Count);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NoChartInTheWorkbookMeansNoChartInThePreview()
        {
            string path = WriteTemp(
                XlsxReportBuilder.Build("| 分類 | 備註 |\n| --- | --- |\n| 甲 | 好 |\n| 乙 | 普通 |", "測試"),
                ".xlsx");

            try
            {
                var grid = ArtifactTextExtractor.ExtractXlsxGrid(path);
                Assert.Null(grid.Chart);
                Assert.DoesNotContain("<svg", ArtifactHtmlRenderer.BuildXlsxHtml(grid));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void TheOldStringOnlyCallStillWorks()
        {
            // 附件文字快取還是只要字串；換介面不能把它弄壞。
            string path = WriteTemp(
                XlsxReportBuilder.Build("| 欄 | 值 |\n| --- | --- |\n| 甲 | 28.5% |", "測試"),
                ".xlsx");

            try
            {
                var rows = ArtifactTextExtractor.ExtractXlsxRows(path);
                Assert.Contains(rows, r => r.Contains("28.5%"));
                Assert.Contains("28.5%", ArtifactHtmlRenderer.BuildXlsxHtml(rows));
            }
            finally { File.Delete(path); }
        }
    }
}
