using System;
using System.IO;
using System.Linq;
using Cat5201;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 表格交付物的底線：數字要真的是數字，而且看得出「這是什麼、資料哪來的、總和多少」。
    /// 一片裸資料的話，收到的人還得自己再整理一次。
    /// </summary>
    public class XlsxDataTests
    {
        // 版面：第 1 列標題、第 2 列來源註記、第 3 列留白、第 4 列欄位名稱、第 5 列起資料。
        private const string HeaderRow = "4";
        private const string FirstDataRow = "5";

        private static Worksheet Sheet(byte[] xlsx, out SpreadsheetDocument doc)
        {
            doc = SpreadsheetDocument.Open(new MemoryStream(xlsx), false);
            return doc.WorkbookPart!.WorksheetParts.First().Worksheet;
        }

        private static Cell CellAt(Worksheet sheet, string reference)
            => sheet.Descendants<Cell>().First(c => c.CellReference == reference);

        [Fact]
        public void NumbersPercentsAndThousandsBecomeRealNumbers()
        {
            byte[] xlsx = XlsxReportBuilder.Build(
                "| 品項 | 數量 | 毛利率 | 營收 |\n" +
                "| --- | --- | --- | --- |\n" +
                "| 甲 | 42 | 28.5% | 1,234 |\n" +
                "| 乙 | 9 | 7.25% | 980 |",
                "測試");

            var sheet = Sheet(xlsx, out var doc);
            using (doc)
            {
                // 文字欄照樣是文字
                Assert.Equal(CellValues.InlineString, CellAt(sheet, "A" + FirstDataRow).DataType!.Value);

                // 整數
                var qty = CellAt(sheet, "B" + FirstDataRow);
                Assert.Equal(CellValues.Number, qty.DataType!.Value);
                Assert.Equal("42", qty.CellValue!.Text);

                // 百分比：存 0.285，用百分比格式顯示（不是存字串 "28.5%"）
                var margin = CellAt(sheet, "C" + FirstDataRow);
                Assert.Equal(CellValues.Number, margin.DataType!.Value);
                Assert.Equal(0.285, double.Parse(margin.CellValue!.Text), 6);

                // 千分位：逗號要拿掉才是數字
                var revenue = CellAt(sheet, "D" + FirstDataRow);
                Assert.Equal(CellValues.Number, revenue.DataType!.Value);
                Assert.Equal("1234", revenue.CellValue!.Text);
            }
        }

        [Fact]
        public void UnitsStayTextBecauseTheUnitIsPartOfTheMeaning()
        {
            byte[] xlsx = XlsxReportBuilder.Build(
                "| 項目 | 金額 |\n| --- | --- |\n| 營收 | 426 億元 |",
                "測試");

            var sheet = Sheet(xlsx, out var doc);
            using (doc)
                Assert.Equal(CellValues.InlineString, CellAt(sheet, "B" + FirstDataRow).DataType!.Value);
        }

        [Fact]
        public void HeaderRowIsFilterableAndFrozen()
        {
            byte[] xlsx = XlsxReportBuilder.Build(
                "| 欄 | 值 |\n| --- | --- |\n| 甲 | 1 |\n| 乙 | 2 |", "測試");

            var sheet = Sheet(xlsx, out var doc);
            using (doc)
            {
                // 篩選範圍只蓋欄位列 + 資料列；把合計列框進去的話，一排序它就混進資料中間。
                Assert.Equal($"A{HeaderRow}:B6", sheet.Descendants<AutoFilter>().First().Reference!.Value);
                Assert.Equal(PaneStateValues.Frozen, sheet.Descendants<Pane>().First().State!.Value);
                Assert.Equal("A5", sheet.Descendants<Pane>().First().TopLeftCell!.Value);
            }
        }

        [Fact]
        public void TheSheetSaysWhatItIsAndWhereTheDataCameFrom()
        {
            byte[] xlsx = XlsxReportBuilder.Build(
                "| 欄 | 值 |\n| --- | --- |\n| 甲 | 1 |",
                "法國高檔餐點選品表", null, "資料來源：協會年報 2025", null);

            var sheet = Sheet(xlsx, out var doc);
            using (doc)
            {
                Assert.Equal("法國高檔餐點選品表", CellAt(sheet, "A1").InnerText);

                string note = CellAt(sheet, "A2").InnerText;
                Assert.Contains("產生時間", note);
                Assert.Contains("協會年報 2025", note);

                // 抬頭橫跨整個表寬，看起來才像文件標題而不是 A1 的一格字。
                Assert.Contains(sheet.Descendants<MergeCell>(), m => m.Reference!.Value == "A1:B1");
            }
        }

        [Fact]
        public void TotalsAreLiveFormulasAndSkipPercentages()
        {
            byte[] xlsx = XlsxReportBuilder.Build(
                "| 品項 | 營收 | 毛利率 |\n" +
                "| --- | --- | --- |\n" +
                "| 甲 | 100 | 20.0% |\n" +
                "| 乙 | 250 | 30.0% |",
                "測試");

            var sheet = Sheet(xlsx, out var doc);
            using (doc)
            {
                var totalRow = sheet.Descendants<Row>().First(r => r.RowIndex!.Value == 7);

                Assert.Equal("合計", totalRow.Elements<Cell>().First().InnerText);

                // 真的 SUM 公式：使用者改了某一列，合計會跟著動；同時放算好的值給不算公式的檢視器。
                var revenue = totalRow.Elements<Cell>().ElementAt(1);
                Assert.Equal("SUM(B5:B6)", revenue.CellFormula!.Text);
                Assert.Equal("350", revenue.CellValue!.Text);

                // 毛利率加起來會得到 50%，那是沒有意義的數字——寧可留白。
                Assert.Null(totalRow.Elements<Cell>().ElementAt(2).CellFormula);
            }
        }

        [Fact]
        public void ANativeChartIsAttachedSoItMovesWithTheData()
        {
            byte[] xlsx = XlsxReportBuilder.Build(
                "| 分類 | 營收 |\n| --- | --- |\n| 甲 | 100 |\n| 乙 | 250 |\n| 丙 | 180 |",
                "測試");

            using var doc = SpreadsheetDocument.Open(new MemoryStream(xlsx), false);
            var wsPart = doc.WorkbookPart!.WorksheetParts.First();
            var chart = wsPart.DrawingsPart!.ChartParts.Single().ChartSpace!;

            // 原生圖表（不是貼一張圖片）：公式指向儲存格，改資料圖就跟著變。
            string xml = chart.InnerXml;
            Assert.Contains("$A$5:$A$7", xml);   // 類別
            Assert.Contains("$B$5:$B$7", xml);   // 數值
        }

        [Fact]
        public void NoChartWhenTheDataWouldNotMakeOne()
        {
            // 只有一列資料、或沒有數字欄，畫出來的圖看不出任何東西。
            byte[] single = XlsxReportBuilder.Build("| 分類 | 營收 |\n| --- | --- |\n| 甲 | 100 |", "測試");
            byte[] textOnly = XlsxReportBuilder.Build(
                "| 分類 | 備註 |\n| --- | --- |\n| 甲 | 好 |\n| 乙 | 普通 |\n| 丙 | 差 |", "測試");

            foreach (byte[] xlsx in new[] { single, textOnly })
            {
                using var doc = SpreadsheetDocument.Open(new MemoryStream(xlsx), false);
                Assert.Null(doc.WorkbookPart!.WorksheetParts.First().DrawingsPart);
            }
        }
    }
}
