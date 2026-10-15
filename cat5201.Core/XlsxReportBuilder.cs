using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace Cat5201
{
    /// <summary>
    /// §6 Excel：把 AI 回答裡的表格資料轉成 .xlsx（OpenXml SpreadsheetML）。
    /// 偵測 Markdown 表格（| 欄 | 欄 |）→ 轉成工作表的列 / 儲存格。
    /// 若文字裡沒有表格，退回「每行一格」放進 A 欄，至少不開天窗。
    ///
    /// 版面：標題 → 來源註記 → 空列 → 標題列 → 資料 → 合計 → 圖表。
    /// 一份交出去的表格要看得出「這是什麼、資料哪來的、總和多少、長什麼趨勢」，
    /// 只有一片裸資料的話，收到的人得自己再做一次。
    /// </summary>
    public static class XlsxReportBuilder
    {
        // 版面位置（1-based，跟 Excel 的列號一致）。
        private const uint TitleRow = 1;
        private const uint NoteRow = 2;
        private const uint HeaderRow = 4;   // 第 3 列留白，讓標題區跟資料分開

        public static byte[] Build(string answerText, string sheetName = "報告", DeckArtDirection? art = null)
            => Build(answerText, sheetName, art, null, null);

        /// <param name="sourceNote">來源註記（空＝只寫產生日期）。</param>
        /// <param name="language">本次輸出語言（空＝繁體中文），決定「合計」等用語。</param>
        public static byte[] Build(
            string answerText, string sheetName, DeckArtDirection? art, string? sourceNote, string? language)
        {
            var design = art ?? DeckArtDirection.ForTopic(sheetName);
            var rows = ExtractRows(answerText ?? "");
            string title = string.IsNullOrWhiteSpace(sheetName) ? "報告" : sheetName.Trim();

            using var ms = new MemoryStream();
            using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
            {
                var wbPart = doc.AddWorkbookPart();
                wbPart.Workbook = new Workbook();

                var stylesPart = wbPart.AddNewPart<WorkbookStylesPart>();
                stylesPart.Stylesheet = BuildStylesheet(design);
                stylesPart.Stylesheet.Save();

                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();

                int columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);

                // ── 標題區 ──
                sheetData.Append(new Row(
                    new Cell
                    {
                        CellReference = "A" + TitleRow,
                        StyleIndex = StyleTitle,
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(new Text(title) { Space = SpaceProcessingModeValues.Preserve })
                    })
                { RowIndex = TitleRow, Height = 26, CustomHeight = true });

                sheetData.Append(new Row(
                    new Cell
                    {
                        CellReference = "A" + NoteRow,
                        StyleIndex = StyleNote,
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(
                            new Text(BuildNote(sourceNote)) { Space = SpaceProcessingModeValues.Preserve })
                    })
                { RowIndex = NoteRow, Height = 15, CustomHeight = true });

                sheetData.Append(new Row { RowIndex = 3, Height = 6, CustomHeight = true });

                // ── 標題列 + 資料 ──
                // 每一欄記下解析後的數值，才知道哪幾欄能加總（百分比不能加，加起來沒有意義）。
                var columnValues = new List<List<(double Value, uint StyleIndex)?>>();
                for (int c = 0; c < columnCount; c++)
                    columnValues.Add(new List<(double, uint)?>());

                uint rowIndex = HeaderRow;
                for (int r = 0; r < rows.Count; r++)
                {
                    bool isHeader = r == 0;
                    var row = new Row { RowIndex = rowIndex };
                    if (isHeader) { row.Height = 22; row.CustomHeight = true; }

                    var cells = rows[r];
                    for (int c = 0; c < cells.Count; c++)
                    {
                        var parsed = isHeader ? null : ParseNumber(cells[c]);
                        if (!isHeader && c < columnValues.Count)
                            columnValues[c].Add(parsed);

                        row.Append(DataCell(
                            ColumnLetter(c) + rowIndex,
                            cells[c],
                            parsed,
                            styleIndex: isHeader ? StyleHeader : StyleBody));
                    }

                    sheetData.Append(row);
                    rowIndex++;
                }

                uint lastDataRow = rowIndex - 1;               // 標題列也算在內，空表時等於 HeaderRow-1
                bool hasData = rows.Count > 1;

                // ── 合計列 ──
                var totalColumns = hasData ? SummableColumns(columnValues) : new List<int>();
                uint totalRow = 0;
                if (totalColumns.Count > 0)
                {
                    totalRow = rowIndex;
                    var row = new Row { RowIndex = totalRow };
                    for (int c = 0; c < columnCount; c++)
                    {
                        string reference = ColumnLetter(c) + totalRow;
                        if (c == 0)
                        {
                            row.Append(new Cell
                            {
                                CellReference = reference,
                                StyleIndex = StyleTotalLabel,
                                DataType = CellValues.InlineString,
                                InlineString = new InlineString(new Text(ReportWords.Total(language)))
                            });
                        }
                        else if (totalColumns.Contains(c))
                        {
                            // 寫成真的 SUM 公式：使用者改了某一列，合計會跟著動。
                            // 同時放一份算好的值，不會計算公式的檢視器也看得到數字。
                            string range = $"{ColumnLetter(c)}{HeaderRow + 1}:{ColumnLetter(c)}{lastDataRow}";
                            double sum = columnValues[c].Where(v => v != null).Sum(v => v!.Value.Value);
                            row.Append(new Cell
                            {
                                CellReference = reference,
                                StyleIndex = StyleTotalNumber,
                                CellFormula = new CellFormula($"SUM({range})"),
                                CellValue = new CellValue(sum.ToString(CultureInfo.InvariantCulture))
                            });
                        }
                        else
                        {
                            row.Append(new Cell { CellReference = reference, StyleIndex = StyleTotalLabel });
                        }
                    }
                    sheetData.Append(row);
                    rowIndex++;
                }

                // 欄寬只看標題列與資料（標題那一列是整份的大標，拿它算會把 A 欄撐爆）。
                var columns = new Columns();
                for (int c = 0; c < columnCount; c++)
                {
                    double width = 10;
                    foreach (var r in rows)
                    {
                        if (c >= r.Count) continue;
                        double w = (r[c] ?? "").Sum(ch => ch > 0x2E80 ? 2.1 : 1.05) + 4;
                        width = Math.Max(width, w);
                    }
                    columns.Append(new Column
                    {
                        Min = (uint)(c + 1), Max = (uint)(c + 1),
                        Width = Math.Min(48, width), CustomWidth = true
                    });
                }

                var views = new SheetViews(new SheetView
                {
                    WorkbookViewId = 0U,
                    Pane = new Pane
                    {
                        VerticalSplit = HeaderRow,
                        TopLeftCell = "A" + (HeaderRow + 1),
                        ActivePane = PaneValues.BottomLeft,
                        State = PaneStateValues.Frozen
                    }
                });

                // 列印／轉 PDF 時縮成一頁寬——欄位被切到第二頁是最常見的「檔案很醜」來源。
                var sheetProps = new SheetProperties(new PageSetupProperties { FitToPage = true });
                var pageSetup = new PageSetup
                {
                    FitToWidth = 1U,
                    FitToHeight = 0U,
                    Orientation = columnCount >= 5 ? OrientationValues.Landscape : OrientationValues.Portrait
                };
                var margins = new PageMargins
                {
                    Left = 0.5D, Right = 0.5D, Top = 0.6D, Bottom = 0.6D, Header = 0.3D, Footer = 0.3D
                };

                // 自動篩選只蓋標題列 + 資料列：把合計列也框進去的話，一排序它就被當成資料混進中間。
                var autoFilter = columnCount > 0 && hasData
                    ? new AutoFilter { Reference = $"A{HeaderRow}:{ColumnLetter(columnCount - 1)}{lastDataRow}" }
                    : null;

                // 標題橫跨整個表寬，看起來才像一份文件的抬頭而不是 A1 的一格字。
                var mergeCells = columnCount > 1
                    ? new MergeCells(
                        new MergeCell { Reference = $"A{TitleRow}:{ColumnLetter(columnCount - 1)}{TitleRow}" },
                        new MergeCell { Reference = $"A{NoteRow}:{ColumnLetter(columnCount - 1)}{NoteRow}" })
                    : null;

                if (columnCount > 0)
                {
                    var parts = new List<OpenXmlElement> { sheetProps, views, columns, sheetData };
                    if (autoFilter != null) parts.Add(autoFilter);
                    if (mergeCells != null) parts.Add(mergeCells);
                    parts.Add(margins);
                    parts.Add(pageSetup);
                    wsPart.Worksheet = new Worksheet(parts);
                }
                else
                {
                    wsPart.Worksheet = new Worksheet(sheetData);
                }

                string safeSheetName = Trim31(title);

                // 原生 Excel 圖表：資料長得適合畫圖時才畫。圖是活的，改資料圖會跟著動。
                if (hasData && columnCount > 0)
                    TryAttachChart(wsPart, safeSheetName, rows, columnValues, lastDataRow,
                        anchorRow: (int)rowIndex + 1, art: design);

                wsPart.Worksheet.Save();

                var sheets = wbPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet
                {
                    Id = wbPart.GetIdOfPart(wsPart),
                    SheetId = 1U,
                    Name = safeSheetName
                });

                wbPart.Workbook.Save();
            }

            return ms.ToArray();
        }

        /// <summary>文字裡是否含可轉成表格的 Markdown 表格。</summary>
        public static bool HasTable(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (IsTableRow(lines[i]) && IsTableSeparator(lines[i + 1]))
                    return true;
            }
            return false;
        }

        private static string BuildNote(string? sourceNote)
        {
            string generated = $"產生時間：{DateTime.Now:yyyy-MM-dd}";
            string note = (sourceNote ?? "").Trim();
            return note.Length == 0 ? generated : $"{generated}　｜　{note}";
        }

        /// <summary>
        /// 哪幾欄可以加總：整欄都是數字，且不是百分比或比率。
        /// 把毛利率那一欄加起來會得到 102.3%，那是沒有意義的數字，寧可留白。
        /// </summary>
        private static List<int> SummableColumns(List<List<(double Value, uint StyleIndex)?>> columnValues)
        {
            var result = new List<int>();
            for (int c = 0; c < columnValues.Count; c++)
            {
                var values = columnValues[c];
                if (values.Count == 0 || values.Any(v => v == null))
                    continue;
                if (values.Any(v => v!.Value.StyleIndex == StylePercent))
                    continue;
                result.Add(c);
            }
            return result;
        }

        // ---- 解析 ----

        private static List<List<string>> ExtractRows(string text)
        {
            var result = new List<List<string>>();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            bool foundAnyTable = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsTableRow(lines[i]) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    foundAnyTable = true;
                    result.Add(SplitRowCells(lines[i])); // header
                    int j = i + 2;
                    while (j < lines.Length && IsTableRow(lines[j]))
                    {
                        result.Add(SplitRowCells(lines[j]));
                        j++;
                    }
                    i = j - 1;
                }
            }

            if (foundAnyTable)
                return result;

            // 無表格：每個非空行一格放 A 欄。
            var fallback = new List<List<string>> { new List<string> { "內容" } };
            foreach (var line in lines)
            {
                string s = line.Trim();
                if (s.Length > 0)
                    fallback.Add(new List<string> { s });
            }
            return fallback.Count > 1 ? fallback : new List<List<string>>();
        }

        private static bool IsTableRow(string line)
        {
            string t = (line ?? "").Trim();
            return t.Length >= 3 && t.StartsWith("|", StringComparison.Ordinal) && t.IndexOf('|', 1) >= 1;
        }

        private static bool IsTableSeparator(string line)
        {
            string t = (line ?? "").Trim();
            if (!t.StartsWith("|", StringComparison.Ordinal))
                return false;

            bool sawDash = false;
            foreach (char c in t)
            {
                if (c == '-') sawDash = true;
                else if (c != '|' && c != ':' && c != ' ' && c != '\t')
                    return false;
            }
            return sawDash;
        }

        private static List<string> SplitRowCells(string line)
        {
            string t = (line ?? "").Trim();
            if (t.StartsWith("|", StringComparison.Ordinal))
                t = t.Substring(1);
            if (t.EndsWith("|", StringComparison.Ordinal))
                t = t.Substring(0, t.Length - 1);

            // 去掉 Markdown 粗體標記，xlsx 不需要。
            return t.Split('|').Select(c => c.Trim().Replace("**", "")).ToList();
        }

        // ---- 原生圖表 ----

        /// <summary>
        /// 加一張原生 Excel 直條圖：第一個文字欄當類別，第一個可加總的數字欄當數值。
        /// 用原生圖表（不是貼一張圖片）才會跟著資料變動，使用者也能自己換圖表類型。
        /// 資料不適合畫圖時（沒有數字欄、類別太多、只有一列）就不畫——硬畫只會多一張看不懂的圖。
        /// </summary>
        private static void TryAttachChart(
            WorksheetPart wsPart, string sheetName, List<List<string>> rows,
            List<List<(double Value, uint StyleIndex)?>> columnValues, uint lastDataRow,
            int anchorRow, DeckArtDirection art)
        {
            int dataRows = rows.Count - 1;
            if (dataRows < 2 || dataRows > 20)
                return;

            int labelColumn = Enumerable.Range(0, columnValues.Count)
                .FirstOrDefault(c => columnValues[c].Count > 0 && columnValues[c].All(v => v == null), -1);
            int valueColumn = SummableColumns(columnValues).FirstOrDefault(c => c != labelColumn, -1);

            if (labelColumn < 0 || valueColumn < 0)
                return;

            var drawingsPart = wsPart.AddNewPart<DrawingsPart>();
            var chartPart = drawingsPart.AddNewPart<ChartPart>();

            string sheetRef = "'" + sheetName.Replace("'", "''") + "'";
            string categories = $"{sheetRef}!${ColumnLetter(labelColumn)}${HeaderRow + 1}:${ColumnLetter(labelColumn)}${lastDataRow}";
            string values = $"{sheetRef}!${ColumnLetter(valueColumn)}${HeaderRow + 1}:${ColumnLetter(valueColumn)}${lastDataRow}";
            string seriesName = $"{sheetRef}!${ColumnLetter(valueColumn)}${HeaderRow}";
            string seriesTitle = rows[0].Count > valueColumn ? rows[0][valueColumn] : "";

            const uint categoryAxisId = 111111111U;
            const uint valueAxisId = 222222222U;

            chartPart.ChartSpace = new C.ChartSpace(
                new C.EditingLanguage { Val = "zh-TW" },
                new C.Chart(
                    ChartTitle(seriesTitle, art),
                    new C.AutoTitleDeleted { Val = false },
                    new C.PlotArea(
                        new C.Layout(),
                        new C.BarChart(
                            new C.BarDirection { Val = C.BarDirectionValues.Column },
                            new C.BarGrouping { Val = C.BarGroupingValues.Clustered },
                            new C.VaryColors { Val = false },
                            new C.BarChartSeries(
                                new C.Index { Val = 0U },
                                new C.Order { Val = 0U },
                                new C.SeriesText(new C.StringReference(new C.Formula(seriesName))),
                                new C.ChartShapeProperties(
                                    new A.SolidFill(new A.RgbColorModelHex { Val = art.Accent })),
                                new C.CategoryAxisData(new C.StringReference(new C.Formula(categories))),
                                new C.Values(new C.NumberReference(new C.Formula(values)))),
                            new C.GapWidth { Val = 90 },
                            new C.AxisId { Val = categoryAxisId },
                            new C.AxisId { Val = valueAxisId }),
                        new C.CategoryAxis(
                            new C.AxisId { Val = categoryAxisId },
                            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
                            new C.Delete { Val = false },
                            new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
                            new C.CrossingAxis { Val = valueAxisId }),
                        new C.ValueAxis(
                            new C.AxisId { Val = valueAxisId },
                            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
                            new C.Delete { Val = false },
                            new C.AxisPosition { Val = C.AxisPositionValues.Left },
                            new C.CrossingAxis { Val = categoryAxisId })),
                    new C.PlotVisibleOnly { Val = true }));
            chartPart.ChartSpace.Save();

            int columnSpan = Math.Max(4, rows[0].Count);
            drawingsPart.WorksheetDrawing = new Xdr.WorksheetDrawing(
                new Xdr.TwoCellAnchor(
                    new Xdr.FromMarker(
                        new Xdr.ColumnId("0"), new Xdr.ColumnOffset("0"),
                        new Xdr.RowId(anchorRow.ToString()), new Xdr.RowOffset("0")),
                    new Xdr.ToMarker(
                        new Xdr.ColumnId(columnSpan.ToString()), new Xdr.ColumnOffset("0"),
                        new Xdr.RowId((anchorRow + 18).ToString()), new Xdr.RowOffset("0")),
                    new Xdr.GraphicFrame(
                        new Xdr.NonVisualGraphicFrameProperties(
                            new Xdr.NonVisualDrawingProperties { Id = 2U, Name = "Chart" },
                            new Xdr.NonVisualGraphicFrameDrawingProperties()),
                        new Xdr.Transform(
                            new A.Offset { X = 0L, Y = 0L },
                            new A.Extents { Cx = 0L, Cy = 0L }),
                        new A.Graphic(
                            new A.GraphicData(
                                new C.ChartReference { Id = drawingsPart.GetIdOfPart(chartPart) })
                            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" })),
                    new Xdr.ClientData()));
            drawingsPart.WorksheetDrawing.Save();

            // drawing 在 CT_Worksheet 的順序裡排在 pageSetup 之後，所以直接接在最後。
            wsPart.Worksheet.Append(new Drawing { Id = wsPart.GetIdOfPart(drawingsPart) });
        }

        private static C.Title ChartTitle(string text, DeckArtDirection art)
            => new(
                new C.ChartText(
                    new C.RichText(
                        new A.BodyProperties(),
                        new A.ListStyle(),
                        new A.Paragraph(
                            new A.Run(
                                new A.RunProperties(new A.SolidFill(new A.RgbColorModelHex { Val = art.Ink }))
                                { Language = "zh-TW", FontSize = 1100, Bold = false },
                                new A.Text(text ?? ""))))),
                new C.Overlay { Val = false });

        // ---- OpenXml 輔助 ----

        /// <summary>
        /// 一格資料。看得出是數字就寫成數字（帶對應的顯示格式），其餘照文字寫。
        /// 全部寫成文字的話，Excel 不能加總、排序會變字典序（100 排在 9 前面），
        /// 也做不了樞紐分析——那份檔案就只是「看起來像表格」。
        /// </summary>
        private static Cell DataCell(string reference, string text, (double Value, uint StyleIndex)? parsed, uint styleIndex)
        {
            if (parsed != null)
            {
                return new Cell
                {
                    CellReference = reference,
                    StyleIndex = parsed.Value.StyleIndex,
                    DataType = CellValues.Number,
                    CellValue = new CellValue(parsed.Value.Value.ToString(CultureInfo.InvariantCulture)),
                };
            }

            return new Cell
            {
                CellReference = reference,
                StyleIndex = styleIndex,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(
                    new Text(text ?? "") { Space = SpaceProcessingModeValues.Preserve })
            };
        }

        /// <summary>
        /// 把一格文字解析成數值 + 顯示格式。認得：純數字、千分位、百分比、正負號。
        /// 帶單位的（426 億元、3 天）保持文字——單位是意義的一部分，拆掉反而失真。
        /// </summary>
        private static (double Value, uint StyleIndex)? ParseNumber(string? text)
        {
            string t = (text ?? "").Trim().Replace("−", "-");
            if (t.Length == 0) return null;

            bool percent = t.EndsWith("%", StringComparison.Ordinal) || t.EndsWith("％", StringComparison.Ordinal);
            if (percent) t = t[..^1].Trim();

            bool hasThousands = t.Contains(',');
            string cleaned = t.Replace(",", "");

            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return null;

            if (percent) return (value / 100.0, StylePercent);
            if (hasThousands) return (value, StyleThousands);
            return (value, cleaned.Contains('.') ? StyleDecimal : StyleInteger);
        }

        private static string ColumnLetter(int zeroBasedColumn)
        {
            int n = zeroBasedColumn;
            string s = "";
            do
            {
                s = (char)('A' + (n % 26)) + s;
                n = n / 26 - 1;
            } while (n >= 0);
            return s;
        }

        private static string Trim31(string name)
        {
            // Excel 工作表名稱上限 31 字，且不可含 : \ / ? * [ ]
            var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
            string cleaned = new string((name ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim();
            if (cleaned.Length == 0) cleaned = "報告";
            return cleaned.Length > 31 ? cleaned.Substring(0, 31) : cleaned;
        }

        // 樣式索引（順序要跟 BuildStylesheet 的 CellFormats 一致）。
        private const uint StyleBody = 2U;
        private const uint StyleHeader = 1U;
        private const uint StyleTitle = 4U;
        private const uint StyleInteger = 5U;
        private const uint StyleDecimal = 6U;
        private const uint StyleThousands = 7U;
        private const uint StylePercent = 8U;
        private const uint StyleNote = 9U;
        private const uint StyleTotalLabel = 10U;
        private const uint StyleTotalNumber = 11U;

        /// <summary>
        /// 表格的視覺語言跟簡報／報告同一套（<see cref="DeckArtDirection"/>）：
        /// 標題列用強調色底白字、資料列只有底部髮絲線、數字右對齊。
        /// 原本是「粗體 + 淺灰底」的 Excel 預設樣子，跟重做過的簡報擺在一起像兩個產品。
        /// </summary>
        private static Stylesheet BuildStylesheet(DeckArtDirection art)
        {
            string ink = "FF" + art.Ink, accent = "FF" + art.Accent, muted = "FF" + art.Muted;
            string body = art.BodyEaFont;

            return new Stylesheet(
                // 數字格式：整數、小數、千分位、百分比（0 保留給通用格式）。
                new NumberingFormats(
                    new NumberingFormat { NumberFormatId = 164U, FormatCode = "#,##0" },
                    new NumberingFormat { NumberFormatId = 165U, FormatCode = "#,##0.##" },
                    new NumberingFormat { NumberFormatId = 166U, FormatCode = "0.0%" }),
                new Fonts(
                    new Font(new FontSize { Val = 11 }, new Color { Rgb = ink }, new FontName { Val = body }),           // 0：內文
                    new Font(new Bold(), new FontSize { Val = 11 }, new Color { Rgb = "FFFFFFFF" }, new FontName { Val = body }), // 1：標題列
                    new Font(new Bold(), new FontSize { Val = 14 }, new Color { Rgb = ink }, new FontName { Val = body }),        // 2：大標
                    new Font(new FontSize { Val = 9 }, new Color { Rgb = muted }, new FontName { Val = body }),                   // 3：來源註記
                    new Font(new Bold(), new FontSize { Val = 11 }, new Color { Rgb = ink }, new FontName { Val = body })),       // 4：合計列
                new Fills(
                    new Fill(new PatternFill { PatternType = PatternValues.None }),     // 0（規格保留）
                    new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),  // 1（規格保留）
                    new Fill(new PatternFill(new ForegroundColor { Rgb = accent })      // 2：標題列強調色
                    { PatternType = PatternValues.Solid })),
                new Borders(
                    new Border(),                                                       // 0：無框線
                    new Border(new BottomBorder(new Color { Rgb = muted })              // 1：列間髮絲線
                    { Style = BorderStyleValues.Hair }),
                    new Border(new TopBorder(new Color { Rgb = ink })                   // 2：合計列上方實線
                    { Style = BorderStyleValues.Thin })),
                new CellFormats(
                    new CellFormat { FontId = 0U, ApplyFont = true },                   // 0：內文
                    new CellFormat                                                      // 1：標題列
                    {
                        FontId = 1U, FillId = 2U, ApplyFont = true, ApplyFill = true,
                        Alignment = new Alignment { Vertical = VerticalAlignmentValues.Center },
                        ApplyAlignment = true
                    },
                    new CellFormat                                                      // 2：資料列（左）
                    {
                        FontId = 0U, BorderId = 1U, ApplyFont = true, ApplyBorder = true,
                        Alignment = new Alignment { Vertical = VerticalAlignmentValues.Center, WrapText = true },
                        ApplyAlignment = true
                    },
                    new CellFormat                                                      // 3：資料列（數字右對齊）
                    {
                        FontId = 0U, BorderId = 1U, ApplyFont = true, ApplyBorder = true,
                        Alignment = new Alignment
                        {
                            Horizontal = HorizontalAlignmentValues.Right,
                            Vertical = VerticalAlignmentValues.Center
                        },
                        ApplyAlignment = true
                    },
                    new CellFormat                                                      // 4：大標
                    {
                        FontId = 2U, ApplyFont = true,
                        Alignment = new Alignment { Vertical = VerticalAlignmentValues.Center },
                        ApplyAlignment = true
                    },
                    NumberFormat(0U),                                                   // 5：整數
                    NumberFormat(165U),                                                 // 6：小數
                    NumberFormat(164U),                                                 // 7：千分位
                    NumberFormat(166U),                                                 // 8：百分比
                    new CellFormat                                                      // 9：來源註記
                    {
                        FontId = 3U, ApplyFont = true,
                        Alignment = new Alignment { Vertical = VerticalAlignmentValues.Center },
                        ApplyAlignment = true
                    },
                    new CellFormat                                                      // 10：合計列（文字）
                    {
                        FontId = 4U, BorderId = 2U, ApplyFont = true, ApplyBorder = true,
                        Alignment = new Alignment { Vertical = VerticalAlignmentValues.Center },
                        ApplyAlignment = true
                    },
                    new CellFormat                                                      // 11：合計列（數字）
                    {
                        FontId = 4U, BorderId = 2U, NumberFormatId = 165U,
                        ApplyFont = true, ApplyBorder = true, ApplyNumberFormat = true,
                        Alignment = new Alignment
                        {
                            Horizontal = HorizontalAlignmentValues.Right,
                            Vertical = VerticalAlignmentValues.Center
                        },
                        ApplyAlignment = true
                    }));
        }

        /// <summary>數字欄位的共用格式：右對齊、列間髮絲線，加上指定的數字顯示格式。</summary>
        private static CellFormat NumberFormat(uint numberFormatId) => new()
        {
            FontId = 0U,
            BorderId = 1U,
            NumberFormatId = numberFormatId,
            ApplyFont = true,
            ApplyBorder = true,
            ApplyNumberFormat = true,
            Alignment = new Alignment
            {
                Horizontal = HorizontalAlignmentValues.Right,
                Vertical = VerticalAlignmentValues.Center,
            },
            ApplyAlignment = true,
        };
    }
}
