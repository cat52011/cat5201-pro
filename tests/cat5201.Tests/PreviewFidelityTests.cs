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
