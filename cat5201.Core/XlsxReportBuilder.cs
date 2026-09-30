using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Cat5201
{
    /// <summary>
    /// §6 Excel：把 AI 回答裡的表格資料轉成 .xlsx（OpenXml SpreadsheetML）。
    /// 偵測 Markdown 表格（| 欄 | 欄 |）→ 轉成工作表的列 / 儲存格；標題列套粗體 + 淺灰底。
    /// 若文字裡沒有表格，退回「每行一格」放進 A 欄，至少不開天窗。
    /// 用 InlineString 寫字串，避免共用字串表的額外複雜度；中文以 Space=Preserve 保留。
    /// </summary>
    public static class XlsxReportBuilder
    {
        public static byte[] Build(string answerText, string sheetName = "報告", DeckArtDirection? art = null)
        {
            _art = art ?? DeckArtDirection.ForTopic(sheetName);
            var rows = ExtractRows(answerText ?? "");

            using var ms = new MemoryStream();
            using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
            {
                var wbPart = doc.AddWorkbookPart();
                wbPart.Workbook = new Workbook();

                var stylesPart = wbPart.AddNewPart<WorkbookStylesPart>();
                stylesPart.Stylesheet = BuildStylesheet();
                stylesPart.Stylesheet.Save();

                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();

                uint rowIndex = 1;
                for (int r = 0; r < rows.Count; r++)
                {
                    bool isHeader = r == 0;
                    var row = new Row { RowIndex = rowIndex };
                    var cells = rows[r];

                    if (isHeader) row.Height = 22; row.CustomHeight = isHeader;

                    for (int c = 0; c < cells.Count; c++)
                    {
                        bool numeric = System.Text.RegularExpressions.Regex.IsMatch(
                            (cells[c] ?? "").Trim(), @"^[+\-−]?[\d,.]+\s*[%％]?$");

                        row.Append(TextCell(
                            ColumnLetter(c) + rowIndex.ToString(),
                            cells[c],
                            styleIndex: isHeader ? 1U : numeric ? 3U : 2U));
                    }

                    sheetData.Append(row);
                    rowIndex++;
                }

                // 欄寬依內容估算（中文算兩格），並凍結標題列——不然一捲就不知道欄位是什麼。
                var columns = new Columns();
                int columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
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
                        VerticalSplit = 1D,
                        TopLeftCell = "A2",
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

                wsPart.Worksheet = columnCount > 0
                    ? new Worksheet(sheetProps, views, columns, sheetData, margins, pageSetup)
                    : new Worksheet(sheetData);
                wsPart.Worksheet.Save();

                var sheets = wbPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet
                {
                    Id = wbPart.GetIdOfPart(wsPart),
                    SheetId = 1U,
                    Name = string.IsNullOrWhiteSpace(sheetName) ? "報告" : Trim31(sheetName)
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

        // ---- OpenXml 輔助 ----

        private static Cell TextCell(string reference, string text, uint styleIndex)
        {
            return new Cell
            {
                CellReference = reference,
                StyleIndex = styleIndex,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(
                    new Text(text ?? "") { Space = SpaceProcessingModeValues.Preserve })
            };
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

        /// <summary>
        /// 表格的視覺語言跟簡報／報告同一套（<see cref="DeckArtDirection"/>）：
        /// 標題列用強調色底白字、資料列只有底部髮絲線、數字右對齊。
        /// 原本是「粗體 + 淺灰底」的 Excel 預設樣子，跟重做過的簡報擺在一起像兩個產品。
        /// </summary>
        private static DeckArtDirection _art = DeckArtDirection.ById("modern");

        private static Stylesheet BuildStylesheet()
        {
            string ink = "FF" + _art.Ink, accent = "FF" + _art.Accent, muted = "FF" + _art.Muted;
            string body = _art.BodyEaFont;

            return new Stylesheet(
                new Fonts(
                    new Font(new FontSize { Val = 11 }, new Color { Rgb = ink }, new FontName { Val = body }),           // 0：內文
                    new Font(new Bold(), new FontSize { Val = 11 }, new Color { Rgb = "FFFFFFFF" }, new FontName { Val = body }), // 1：標題列
                    new Font(new Bold(), new FontSize { Val = 14 }, new Color { Rgb = ink }, new FontName { Val = body })),       // 2：大標
                new Fills(
                    new Fill(new PatternFill { PatternType = PatternValues.None }),     // 0（規格保留）
                    new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),  // 1（規格保留）
                    new Fill(new PatternFill(new ForegroundColor { Rgb = accent })      // 2：標題列強調色
                    { PatternType = PatternValues.Solid })),
                new Borders(
                    new Border(),                                                       // 0：無框線
                    new Border(new BottomBorder(new Color { Rgb = muted })              // 1：列間髮絲線
                    { Style = BorderStyleValues.Hair })),
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
                    new CellFormat { FontId = 2U, ApplyFont = true }));                 // 4：大標
        }
    }
}
