using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using D = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using Ch = DocumentFormat.OpenXml.Drawing.Charts;

namespace Cat5201
{
    /// <summary>
    /// 從已生成的 Office 檔案抽出純文字，給即時預覽面板顯示用（Phase 1：文字 / 大綱層級）。
    /// 用專案既有的 DocumentFormat.OpenXml，不引入額外渲染依賴。
    /// 高擬真渲染（DOCX 版面、PPTX 縮圖）屬後續階段。
    /// </summary>
    public static class ArtifactTextExtractor
    {
        /// <summary>
        /// DOCX → Markdown（保留字形層級）：依 run 的粗體 + 字級還原成 # / ## / ### 標題、**粗體**、
        /// 斜體中繼列、項目符號與表格，讓預覽看得到字形變化（不再是無格式純文字）。
        /// </summary>
        public static string ExtractDocx(string path)
        {
            using var docFile = ReadOnlyOfficeFile.Docx(path);
            var doc = docFile.Document;
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null)
                return "";

            var sb = new StringBuilder();
            var mainPart = doc.MainDocumentPart;
            // 依文件順序逐一處理段落與表格（表格要還原成 Markdown 表格才看得到結構）。
            foreach (var el in body.Elements())
            {
                if (el is W.Paragraph para)
                {
                    // 段落含內嵌圖 → 以 base64 圖片語法輸出，讓預覽也看得到報告配圖。
                    string imgMd = ParagraphImageMarkdown(mainPart, para);
                    if (!string.IsNullOrEmpty(imgMd))
                        sb.AppendLine(imgMd);
                    else
                        sb.AppendLine(ParagraphToMarkdown(para));
                }
                else if (el is W.Table table)
                    AppendTableMarkdown(sb, table);
            }

            return CollapseBlankRuns(sb.ToString());
        }

        private static string ParagraphToMarkdown(W.Paragraph para)
        {
            var runs = para.Elements<W.Run>().ToList();
            if (runs.Count == 0)
                return "";

            int maxSize = 0;
            bool anyBold = false, anyItalic = false;
            foreach (var r in runs)
            {
                var rp = r.RunProperties;
                if (rp?.Bold != null) anyBold = true;
                if (rp?.Italic != null) anyItalic = true;
                if (int.TryParse(rp?.FontSize?.Val?.Value, out int sz) && sz > maxSize)
                    maxSize = sz;
            }

            string text = string.Concat(runs.Select(r => r.InnerText));
            if (string.IsNullOrWhiteSpace(text))
                return "";

            // DocxReportBuilder：標題 = 粗體 + 字級 36/28/24（half-point）。
            if (anyBold && maxSize >= 36) return "# " + text.Trim();
            if (anyBold && maxSize >= 28) return "## " + text.Trim();
            if (anyBold && maxSize >= 24) return "### " + text.Trim();

            // 中繼資料列：斜體小字 → 以斜體呈現。
            if (anyItalic && maxSize > 0 && maxSize <= 18)
                return "*" + text.Trim() + "*";

            // 一般段落：粗體 run 包成 **...**；項目符號「• 」轉成 Markdown「- 」。
            var inline = new StringBuilder();
            foreach (var r in runs)
            {
                string rt = r.InnerText;
                if (string.IsNullOrEmpty(rt)) continue;
                bool bold = r.RunProperties?.Bold != null;
                if (bold && rt.Trim() != "•")
                    inline.Append("**").Append(rt).Append("**");
                else
                    inline.Append(rt);
            }

            string body = inline.ToString();
            string trimmedStart = body.TrimStart();
            if (trimmedStart.StartsWith("• "))
                return "- " + trimmedStart.Substring(2);

            return body;
        }

        // 段落含內嵌圖片時，取出圖片 bytes 轉成 base64 的 Markdown 圖片語法；無圖回空字串。
        private static string ParagraphImageMarkdown(MainDocumentPart? mainPart, W.Paragraph para)
        {
            if (mainPart == null) return "";

            var blip = para.Descendants<D.Blip>().FirstOrDefault();
            string? embed = blip?.Embed?.Value;
            if (string.IsNullOrEmpty(embed)) return "";

            try
            {
                if (mainPart.GetPartById(embed) is not ImagePart imgPart) return "";
                using var s = imgPart.GetStream();
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                return "![圖](data:image/png;base64," + System.Convert.ToBase64String(ms.ToArray()) + ")";
            }
            catch
            {
                return "";
            }
        }

        private static void AppendTableMarkdown(StringBuilder sb, W.Table table)
        {
            var rows = table.Elements<W.TableRow>().ToList();
            if (rows.Count == 0)
                return;

            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r].Elements<W.TableCell>()
                    .Select(c => c.InnerText.Trim().Replace("|", "\\|"))
                    .ToList();
                if (cells.Count == 0)
                    continue;

                sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
                if (r == 0)
                {
                    sb.Append('|');
                    foreach (var _ in cells) sb.Append(" --- |");
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
        }

        /// <summary>PPTX → 逐張投影片的文字行（每張一個清單，第一行通常是標題），供卡片渲染用。</summary>
        public static System.Collections.Generic.List<System.Collections.Generic.List<string>> ExtractPptxSlides(string path)
        {
            var result = new System.Collections.Generic.List<System.Collections.Generic.List<string>>();

            using var presFile = ReadOnlyOfficeFile.Pptx(path);
            var pres = presFile.Document;
            var presPart = pres.PresentationPart;
            var slideIds = presPart?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>();
            if (presPart == null || slideIds == null)
                return result;

            foreach (var slideId in slideIds)
            {
                var relId = slideId.RelationshipId?.Value;
                if (string.IsNullOrEmpty(relId))
                    continue;

                if (presPart.GetPartById(relId) is not SlidePart slidePart || slidePart.Slide == null)
                    continue;

                var lines = new System.Collections.Generic.List<string>();
                foreach (var t in slidePart.Slide.Descendants<D.Text>())
                {
                    if (!string.IsNullOrWhiteSpace(t.Text))
                        lines.Add(t.Text.Trim());
                }

                result.Add(lines);
            }

            return result;
        }

        /// <summary>PPTX → 第一張含圖投影片的圖片位元組（通常是封面圖），供預覽顯示嵌入的封面圖；無圖回 null。</summary>
        public static byte[]? ExtractPptxFirstImage(string path)
        {
            try
            {
                using var presFile = ReadOnlyOfficeFile.Pptx(path);
            var pres = presFile.Document;
                var presPart = pres.PresentationPart;
                var slideIds = presPart?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>();
                if (presPart == null || slideIds == null)
                    return null;

                foreach (var slideId in slideIds)
                {
                    var relId = slideId.RelationshipId?.Value;
                    if (string.IsNullOrEmpty(relId))
                        continue;

                    if (presPart.GetPartById(relId) is not SlidePart slidePart)
                        continue;

                    var imagePart = slidePart.ImageParts?.FirstOrDefault();
                    if (imagePart == null)
                        continue;

                    using var s = imagePart.GetStream();
                    using var ms = new System.IO.MemoryStream();
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>PPTX → 每張投影片的第一張圖（封面 + 內容頁配圖），順序與 ExtractPptxSlides 對齊；該張無圖則為 null。</summary>
        public static List<byte[]?> ExtractPptxSlideImages(string path)
        {
            var result = new List<byte[]?>();
            try
            {
                using var presFile = ReadOnlyOfficeFile.Pptx(path);
            var pres = presFile.Document;
                var presPart = pres.PresentationPart;
                var slideIds = presPart?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>();
                if (presPart == null || slideIds == null)
                    return result;

                foreach (var slideId in slideIds)
                {
                    byte[]? img = null;
                    var relId = slideId.RelationshipId?.Value;
                    if (!string.IsNullOrEmpty(relId) && presPart.GetPartById(relId) is SlidePart slidePart)
                    {
                        var imagePart = slidePart.ImageParts?.FirstOrDefault();
                        if (imagePart != null)
                        {
                            using var s = imagePart.GetStream();
                            using var ms = new System.IO.MemoryStream();
                            s.CopyTo(ms);
                            img = ms.ToArray();
                        }
                    }
                    result.Add(img);
                }
            }
            catch (Exception ex) { AppLog.Warn("ArtifactExtract", $"PPTX 配圖抽取失敗（預覽/引用將無圖）：{path}", ex); }
            return result;
        }

        /// <summary>一格試算表內容：已套用顯示格式的文字，以及它本來是不是數字（決定對齊）。</summary>
        public sealed record XlsxCell(string Text, bool IsNumber);

        /// <summary>
        /// 一張工作表：逐列儲存格，外加預覽需要知道的結構——
        /// 欄位名稱在第幾列、合計列在第幾列、標題列的底色是什麼。
        /// 全部直接從活頁簿自己的樣式讀出來，不假設「第一列一定是表頭」，
        /// 預覽也不用猜這份表格當初套的是哪個藝術方向。
        /// </summary>
        public sealed record XlsxGrid(
            List<List<XlsxCell>> Rows,
            string? HeaderColorHex,
            int HeaderRowIndex = 0,
            int TotalRowIndex = -1,
            XlsxChart? Chart = null);

        /// <summary>活頁簿裡那張原生圖表畫的是哪兩欄（欄位索引），以及它的標題。</summary>
        public sealed record XlsxChart(string Title, int CategoryColumn, int ValueColumn);

        /// <summary>XLSX → 第一個工作表的逐列儲存格文字（給預覽渲染成 HTML 表格）。</summary>
        public static List<List<string>> ExtractXlsxRows(string path)
            => ExtractXlsxGrid(path).Rows
                .Select(r => r.Select(c => c.Text).ToList())
                .ToList();

        /// <summary>
        /// XLSX → 帶顯示格式的工作表內容。
        ///
        /// 為什麼不能直接印 CellValue：數字儲存格存的是原始值（百分比存 0.285、千分位存 1234），
        /// Excel 依 number format 顯示成 28.5% 與 1,234。預覽照原始值印，使用者看到的就會跟
        /// 他打開檔案看到的不一樣——那預覽就失去意義了。
        /// </summary>
        public static XlsxGrid ExtractXlsxGrid(string path)
        {
            var rows = new List<List<XlsxCell>>();

            using var docFile = ReadOnlyOfficeFile.Xlsx(path);
            var doc = docFile.Document;
            var wbPart = doc.WorkbookPart;
            var sheet = wbPart?.Workbook?.Sheets?.Elements<S.Sheet>().FirstOrDefault();
            if (wbPart == null || sheet?.Id?.Value == null)
                return new XlsxGrid(rows, null);

            if (wbPart.GetPartById(sheet.Id.Value) is not WorksheetPart wsPart)
                return new XlsxGrid(rows, null);

            var sst = wbPart.SharedStringTablePart?.SharedStringTable;
            var styles = wbPart.WorkbookStylesPart?.Stylesheet;
            var sheetData = wsPart.Worksheet?.Elements<S.SheetData>().FirstOrDefault();
            if (sheetData == null)
                return new XlsxGrid(rows, null);

            foreach (var row in sheetData.Elements<S.Row>())
            {
                var cells = new List<XlsxCell>();
                foreach (var cell in row.Elements<S.Cell>())
                    cells.Add(ReadCell(cell, sst, styles));
                rows.Add(cells);
            }

            int headerRow = FindStyledRow(styles, sheetData, IsFilledHeader);
            return new XlsxGrid(
                rows,
                rows.Count > 0 ? HeaderFillHex(styles, sheetData, headerRow) : null,
                Math.Max(0, headerRow),
                FindStyledRow(styles, sheetData, IsTotalRow, last: true),
                ReadChart(wsPart));
        }

        /// <summary>
        /// 讀出工作表上那張原生圖表畫的是哪兩欄。欄位是從圖表自己的公式解析的
        /// （'表'!$A$5:$A$9 → A 欄），預覽因此一定跟 Excel 畫的是同一份資料。
        /// </summary>
        private static XlsxChart? ReadChart(WorksheetPart wsPart)
        {
            try
            {
                var chart = wsPart.DrawingsPart?.ChartParts?.FirstOrDefault()?.ChartSpace;
                if (chart == null)
                    return null;

                var series = chart.Descendants<Ch.BarChartSeries>().FirstOrDefault();
                if (series == null)
                    return null;

                int category = ColumnOf(series.GetFirstChild<Ch.CategoryAxisData>()
                    ?.StringReference?.Formula?.Text);
                int value = ColumnOf(series.GetFirstChild<Ch.Values>()
                    ?.NumberReference?.Formula?.Text);
                if (category < 0 || value < 0)
                    return null;

                return new XlsxChart(chart.Descendants<Ch.Title>().FirstOrDefault()?.InnerText ?? "", category, value);
            }
            catch (Exception ex)
            {
                AppLog.Warn("ArtifactExtract", "XLSX 圖表資訊讀取失敗（預覽將不畫圖）", ex);
                return null;
            }
        }

        /// <summary>'表'!$B$5:$B$9 → B 欄的索引（1）。解析不出來回 -1。</summary>
        private static int ColumnOf(string? formula)
        {
            var match = Regex.Match(formula ?? "", @"\$([A-Z]+)\$\d+");
            if (!match.Success)
                return -1;

            int index = 0;
            foreach (char c in match.Groups[1].Value)
                index = index * 26 + (c - 'A' + 1);
            return index - 1;
        }

        private static XlsxCell ReadCell(S.Cell cell, S.SharedStringTable? sst, S.Stylesheet? styles)
        {
            if (cell == null)
                return new XlsxCell("", false);

            if (cell.DataType?.Value == S.CellValues.InlineString)
                return new XlsxCell(cell.InlineString?.Text?.Text ?? cell.InlineString?.InnerText ?? "", false);

            if (cell.DataType?.Value == S.CellValues.SharedString)
            {
                if (sst != null &&
                    int.TryParse(cell.CellValue?.InnerText, out int idx) &&
                    idx >= 0 && idx < sst.ChildElements.Count)
                {
                    return new XlsxCell(sst.ChildElements[idx].InnerText, false);
                }
                return new XlsxCell("", false);
            }

            string raw = cell.CellValue?.InnerText ?? cell.InnerText ?? "";
            if (cell.DataType?.Value != S.CellValues.String &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return new XlsxCell(FormatNumber(value, NumberFormatCode(styles, cell.StyleIndex?.Value)), true);
            }

            return new XlsxCell(raw, false);
        }

        /// <summary>儲存格樣式 → number format 代碼（自訂格式優先，其次常用內建格式）。</summary>
        private static string? NumberFormatCode(S.Stylesheet? styles, uint? styleIndex)
        {
            if (styles == null || styleIndex == null)
                return null;

            var format = styles.CellFormats?.Elements<S.CellFormat>().ElementAtOrDefault((int)styleIndex.Value);
            uint id = format?.NumberFormatId?.Value ?? 0U;
            if (id == 0U)
                return null;

            var custom = styles.NumberingFormats?.Elements<S.NumberingFormat>()
                .FirstOrDefault(n => n.NumberFormatId?.Value == id);
            if (custom?.FormatCode?.Value is string code && code.Length > 0)
                return code;

            return id switch
            {
                1U => "0",
                2U => "0.00",
                3U => "#,##0",
                4U => "#,##0.00",
                9U => "0%",
                10U => "0.00%",
                _ => null
            };
        }

        /// <summary>
        /// 套用 Excel 的 number format。我們自己產的檔案用的是 #,##0 / #,##0.## / 0.0% 這類
        /// 在 .NET 自訂數值格式裡語意相同的代碼（% 一樣會乘 100），所以清掉 Excel 專屬的
        /// 區段、語系標籤與留白符號之後就能直接套；套不上就退回原始值，不要讓預覽開天窗。
        /// </summary>
        private static string FormatNumber(double value, string? formatCode)
        {
            string plain = value.ToString("0.############", CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(formatCode))
                return plain;

            string code = formatCode!.Split(';')[0];
            code = Regex.Replace(code, "\"[^\"]*\"", "");       // 字面字串
            code = Regex.Replace(code, @"\\[[^]]*]", "");    // [$-409]、[Red]
            code = Regex.Replace(code, @"[_*].", "");        // 對齊留白
            code = code.Replace("\\", "").Trim();
            if (code.Length == 0)
                return plain;

            try
            {
                return value.ToString(code, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return plain;
            }
        }

        /// <summary>標題列的實心底色，預覽用它套同一個強調色。</summary>
        private static string? HeaderFillHex(S.Stylesheet? styles, S.SheetData sheetData, int headerRow)
        {
            if (headerRow < 0)
                return null;

            var cell = sheetData.Elements<S.Row>().ElementAtOrDefault(headerRow)?.Elements<S.Cell>().FirstOrDefault();
            var fill = FillOf(styles, cell);
            string? rgb = fill?.PatternFill?.ForegroundColor?.Rgb?.Value;
            if (string.IsNullOrWhiteSpace(rgb))
                return null;

            // OpenXml 是 AARRGGBB，HTML 要 #RRGGBB。
            return "#" + (rgb!.Length == 8 ? rgb.Substring(2) : rgb);
        }

        /// <summary>找出第一列（或最後一列）符合條件的列；找不到回 -1。</summary>
        private static int FindStyledRow(
            S.Stylesheet? styles, S.SheetData sheetData,
            Func<S.Stylesheet?, S.Cell?, bool> match, bool last = false)
        {
            var rows = sheetData.Elements<S.Row>().ToList();
            int found = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!match(styles, rows[i].Elements<S.Cell>().FirstOrDefault()))
                    continue;
                found = i;
                if (!last) break;
            }
            return found;
        }

        /// <summary>欄位名稱那一列：第一格帶實心底色。</summary>
        private static bool IsFilledHeader(S.Stylesheet? styles, S.Cell? cell)
            => FillOf(styles, cell)?.PatternFill?.PatternType?.Value == S.PatternValues.Solid;

        /// <summary>合計列：第一格是粗體且上方有框線（跟資料列的髮絲線不同）。</summary>
        private static bool IsTotalRow(S.Stylesheet? styles, S.Cell? cell)
        {
            var format = FormatOf(styles, cell);
            if (format?.BorderId?.Value is not uint borderId)
                return false;

            var border = styles?.Borders?.Elements<S.Border>().ElementAtOrDefault((int)borderId);
            if (border?.TopBorder?.Style?.Value is not S.BorderStyleValues)
                return false;

            var font = styles?.Fonts?.Elements<S.Font>()
                .ElementAtOrDefault((int)(format.FontId?.Value ?? 0U));
            return font?.Bold != null;
        }

        private static S.CellFormat? FormatOf(S.Stylesheet? styles, S.Cell? cell)
            => styles == null || cell?.StyleIndex?.Value == null
                ? null
                : styles.CellFormats?.Elements<S.CellFormat>().ElementAtOrDefault((int)cell.StyleIndex.Value);

        private static S.Fill? FillOf(S.Stylesheet? styles, S.Cell? cell)
        {
            var format = FormatOf(styles, cell);
            return format?.FillId?.Value is uint fillId
                ? styles?.Fills?.Elements<S.Fill>().ElementAtOrDefault((int)fillId)
                : null;
        }

        /// <summary>PPTX → 逐張投影片的文字大綱，每張以「— 投影片 N —」分隔。</summary>
        public static string ExtractPptx(string path)
        {
            using var presFile = ReadOnlyOfficeFile.Pptx(path);
            var pres = presFile.Document;
            var presPart = pres.PresentationPart;
            var slideIds = presPart?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>();
            if (presPart == null || slideIds == null)
                return "";

            var sb = new StringBuilder();
            int n = 0;
            foreach (var slideId in slideIds)
            {
                n++;
                var relId = slideId.RelationshipId?.Value;
                if (string.IsNullOrEmpty(relId))
                    continue;

                if (presPart.GetPartById(relId) is not SlidePart slidePart || slidePart.Slide == null)
                    continue;

                sb.AppendLine($"—— 投影片 {n} ——");

                foreach (var t in slidePart.Slide.Descendants<D.Text>())
                {
                    if (!string.IsNullOrWhiteSpace(t.Text))
                        sb.AppendLine(t.Text);
                }

                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }

        // 把連續 3 行以上空行壓成一行空行，避免 OpenXml 段落產生過多空白。
        private static string CollapseBlankRuns(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            int blankStreak = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    blankStreak++;
                    if (blankStreak <= 1)
                        sb.AppendLine();
                }
                else
                {
                    blankStreak = 0;
                    sb.AppendLine(line);
                }
            }

            return sb.ToString().Trim();
        }
    }
}
