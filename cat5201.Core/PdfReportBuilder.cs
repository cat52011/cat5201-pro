using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Cat5201
{
    /// <summary>
    /// 書面報告 PDF。
    ///
    /// 2026-09-25 改版：原本只是把 Markdown 逐行倒出來（預設字體、深藍標題、灰格線表格），
    /// 跟重做過的簡報擺在一起像兩個產品。現在跟簡報吃同一套 <see cref="DeckArtDirection"/>：
    /// 雜誌家族用襯線大標與髮絲線，商業家族用嚴謹格線與克制的強調色；封面區塊、頁首頁尾、
    /// 章節層級、引述、表格都照排版規矩處理。
    /// </summary>
    public static class PdfReportBuilder
    {
        // QuestPDF 社群授權：年營收 < $1M 免費。必須在產生前設定一次。
        static PdfReportBuilder() => QuestPDF.Settings.License = LicenseType.Community;

        private const string CjkBody = "Microsoft JhengHei";

        public static byte[] Build(string markdownContent) => Build(markdownContent, null);

        public static byte[] Build(string markdownContent, DeckArtDirection? art)
        {
            string markdown = (markdownContent ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = markdown.Split('\n').ToList();

            // 沒指定藝術方向時，從報告自己的大標題推（跟簡報同一套判斷）。
            string title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? "";
            string subtitle = lines.FirstOrDefault(l => l.StartsWith("> ", StringComparison.Ordinal))?[2..].Trim() ?? "";
            art ??= DeckArtDirection.ForTopic(title);

            bool editorial = art.Family == "editorial";
            string ink = "#" + art.Ink, muted = "#" + art.Muted, accent = "#" + art.Accent;
            string displayFont = editorial ? art.DisplayEaFont : CjkBody;

            // 封面區塊只在第一頁出現，之後的內容從標題之後接著排。
            int bodyStart = 0;
            if (title.Length > 0)
            {
                bodyStart = lines.FindIndex(l => l.StartsWith("# ", StringComparison.Ordinal)) + 1;
                while (bodyStart < lines.Count &&
                       (string.IsNullOrWhiteSpace(lines[bodyStart]) ||
                        lines[bodyStart].StartsWith("> ", StringComparison.Ordinal)))
                {
                    bodyStart++;
                }
            }

            return Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.MarginVertical(editorial ? 2.4f : 2.2f, Unit.Centimetre);
                    page.MarginHorizontal(editorial ? 2.8f : 2.2f, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontFamily(CjkBody).FontSize(10.5f).LineHeight(1.62f).FontColor(ink));

                    // 頁首：報告名稱 + 髮絲線（跟簡報的頁首同一個語氣）。
                    page.Header().Column(head =>
                    {
                        if (title.Length == 0) return;
                        head.Item().Text(t => t.Span(Shorten(title, 48))
                            .FontSize(8.5f).FontColor(muted).LetterSpacing(editorial ? 0.28f : 0.16f));
                        head.Item().PaddingTop(5).LineHorizontal(0.6f).LineColor(muted);
                        head.Item().PaddingBottom(14);
                    });

                    page.Content().Column(col =>
                    {
                        col.Spacing(5);

                        if (title.Length > 0)
                        {
                            col.Item().PaddingTop(editorial ? 10 : 4).Width(44).Height(2.5f).Background(accent);
                            col.Item().PaddingTop(14).Text(title)
                                .FontFamily(displayFont).FontSize(editorial ? 27 : 23)
                                .FontColor(ink).Style(editorial ? TextStyle.Default : TextStyle.Default.SemiBold()).LineHeight(1.28f);

                            if (subtitle.Length > 0)
                                col.Item().PaddingTop(10).Text(subtitle)
                                    .FontSize(11.5f).FontColor(muted).LineHeight(1.55f);

                            col.Item().PaddingTop(16).LineHorizontal(0.6f).LineColor(muted);
                            col.Item().PaddingBottom(6);
                        }

                        RenderLines(col, lines.Skip(bodyStart).ToArray(), art, displayFont);
                    });

                    // 頁尾：髮絲線 + 頁碼（商業家族加上 X / Y，雜誌只給頁碼）。
                    page.Footer().Column(foot =>
                    {
                        foot.Item().PaddingBottom(6).LineHorizontal(0.6f).LineColor(muted);
                        foot.Item().Row(row =>
                        {
                            row.RelativeItem().Text(Shorten(title, 40))
                                .FontSize(8).FontColor(muted).LetterSpacing(0.14f);
                            row.AutoItem().Text(t =>
                            {
                                t.DefaultTextStyle(x => x.FontSize(8.5f).FontColor(muted));
                                t.CurrentPageNumber();
                                if (!editorial) { t.Span(" / "); t.TotalPages(); }
                            });
                        });
                    });
                });
            }).GeneratePdf();
        }

        private static string Shorten(string? text, int max)
        {
            string t = (text ?? "").Trim();
            return t.Length <= max ? t : t[..max] + "…";
        }

        private static void RenderLines(ColumnDescriptor col, string[] lines, DeckArtDirection art, string displayFont)
        {
            bool editorial = art.Family == "editorial";
            string ink = "#" + art.Ink, muted = "#" + art.Muted, accent = "#" + art.Accent;
            bool inFence = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (rawLine.TrimStart().StartsWith("```"))
                {
                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    col.Item().Background("#F6F6F3").PaddingVertical(2).PaddingHorizontal(10)
                        .Text(rawLine).FontFamily("Consolas").FontSize(9.5f).FontColor(ink);
                    continue;
                }

                // Markdown 圖片：![alt](本機絕對路徑)
                var imgMatch = Regex.Match(rawLine.Trim(), @"^!\[([^\]]*)\]\(([^)]+)\)$");
                if (imgMatch.Success)
                {
                    string imgPath = imgMatch.Groups[2].Value.Trim();
                    try
                    {
                        if (System.IO.File.Exists(imgPath))
                        {
                            byte[] imgBytes = System.IO.File.ReadAllBytes(imgPath);
                            col.Item().PaddingVertical(10).Column(image =>
                            {
                                image.Item().MaxHeight(300).Image(imgBytes).FitArea();
                                image.Item().PaddingTop(5).Text("圖為 AI 生成的示意畫面")
                                    .FontSize(8.5f).FontColor(muted).LetterSpacing(0.12f);
                            });
                        }
                    }
                    catch (Exception ex) { AppLog.Warn("PdfBuilder", $"配圖嵌入失敗，PDF 將缺這張圖：{imgPath}", ex); }
                    continue;
                }

                // Markdown 表格
                if (IsTableRow(rawLine) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    var rows = new List<List<string>> { SplitRowCells(rawLine) };
                    int j = i + 2;
                    while (j < lines.Length && IsTableRow(lines[j]))
                    {
                        rows.Add(SplitRowCells(lines[j]));
                        j++;
                    }
                    RenderTable(col, rows, art);
                    i = j - 1;
                    continue;
                }

                if (rawLine.StartsWith("# ", StringComparison.Ordinal))
                {
                    col.Item().PaddingTop(20).Text(rawLine[2..].Trim())
                        .FontFamily(displayFont).FontSize(editorial ? 20 : 17).Style(editorial ? TextStyle.Default : TextStyle.Default.SemiBold()).FontColor(ink);
                    col.Item().PaddingTop(7).Width(38).Height(2).Background(accent);
                    col.Item().PaddingBottom(4);
                }
                else if (rawLine.StartsWith("## ", StringComparison.Ordinal))
                {
                    col.Item().PaddingTop(16).Row(row =>
                    {
                        row.AutoItem().PaddingTop(editorial ? 7 : 6).PaddingRight(9)
                            .Width(14).Height(1.6f).Background(accent);
                        row.RelativeItem().Text(rawLine[3..].Trim())
                            .FontFamily(displayFont).FontSize(editorial ? 15.5f : 14).Style(editorial ? TextStyle.Default : TextStyle.Default.SemiBold()).FontColor(ink);
                    });
                    col.Item().PaddingBottom(2);
                }
                else if (rawLine.StartsWith("### ", StringComparison.Ordinal))
                {
                    col.Item().PaddingTop(11).Text(rawLine[4..].Trim())
                        .FontSize(12).Bold().FontColor(ink);
                }
                else if (rawLine.StartsWith("> ", StringComparison.Ordinal))
                {
                    // 引述／重點：左側強調條 + 放大字級——報告裡「這句話要被看見」的處理。
                    col.Item().PaddingVertical(8).Row(row =>
                    {
                        row.AutoItem().Width(2.5f).Background(accent);
                        row.RelativeItem().PaddingLeft(12).Text(t =>
                        {
                            t.DefaultTextStyle(x => x.FontSize(12).FontColor(ink).LineHeight(1.6f));
                            RenderInline(t, rawLine[2..].Trim());
                        });
                    });
                }
                else if (Regex.IsMatch(rawLine, @"^\s*\d+\.\s+"))
                {
                    var m = Regex.Match(rawLine, @"^\s*(\d+)\.\s+(.*)$");
                    col.Item().PaddingLeft(2).Row(row =>
                    {
                        row.ConstantItem(24).Text(m.Groups[1].Value.PadLeft(2, '0'))
                            .FontFamily(displayFont).FontSize(editorial ? 11.5f : 10).FontColor(accent).Style(editorial ? TextStyle.Default : TextStyle.Default.SemiBold());
                        row.RelativeItem().Text(t => RenderInline(t, m.Groups[2].Value));
                    });
                }
                else if (Regex.IsMatch(rawLine, @"^[-*]\s+"))
                {
                    string body = Regex.Replace(rawLine, @"^[-*]\s+", "");
                    col.Item().PaddingLeft(2).Row(row =>
                    {
                        row.ConstantItem(16).PaddingTop(6.5f).Width(6).Height(1.4f).Background(accent);
                        row.RelativeItem().Text(t => RenderInline(t, body));
                    });
                }
                else if (string.IsNullOrWhiteSpace(rawLine))
                {
                    // 空行交給 Column.Spacing
                }
                else if (rawLine.Trim().Length > 0 && rawLine.Trim().Replace("-", "").Length == 0)
                {
                    col.Item().PaddingVertical(8).LineHorizontal(0.6f).LineColor(muted);
                }
                else
                {
                    col.Item().Text(t => RenderInline(t, rawLine));
                }
            }
        }

        private static void RenderTable(ColumnDescriptor col, List<List<string>> rows, DeckArtDirection art)
        {
            if (rows.Count == 0) return;
            int columnCount = rows.Max(r => r.Count);
            if (columnCount == 0) return;

            string ink = "#" + art.Ink, muted = "#" + art.Muted;

            // 年報式表格：只有橫線，沒有直線與網格底色——直線一多就變成報稅單。
            col.Item().PaddingVertical(10).Table(table =>
            {
                table.ColumnsDefinition(def =>
                {
                    for (int c = 0; c < columnCount; c++) def.RelativeColumn();
                });

                for (int r = 0; r < rows.Count; r++)
                {
                    bool isHeader = r == 0;
                    var cells = rows[r];

                    for (int c = 0; c < columnCount; c++)
                    {
                        string cellText = c < cells.Count ? cells[c] : "";
                        bool numeric = Regex.IsMatch(cellText.Trim(), @"^[+\-−]?[\d,.]+\s*[%％]?$");

                        var cell = table.Cell()
                            .BorderBottom(isHeader ? 1.1f : 0.5f)
                            .BorderColor(isHeader ? ink : muted)
                            .PaddingVertical(isHeader ? 7 : 6)
                            .PaddingHorizontal(4);

                        if (numeric) cell = cell.AlignRight();

                        cell.Text(t =>
                        {
                            if (isHeader)
                                t.Span(cellText).FontSize(9.5f).Bold().FontColor(ink).LetterSpacing(0.1f);
                            else
                            {
                                t.DefaultTextStyle(x => x.FontSize(10).LineHeight(1.5f));
                                RenderInline(t, cellText);
                            }
                        });
                    }
                }
            });
        }

        private static bool IsTableRow(string line)
        {
            string t = (line ?? "").Trim();
            return t.Length >= 3 && t.StartsWith("|", StringComparison.Ordinal) && t.IndexOf('|', 1) >= 1;
        }

        private static bool IsTableSeparator(string line)
        {
            string t = (line ?? "").Trim();
            if (!t.StartsWith("|", StringComparison.Ordinal)) return false;

            bool sawDash = false;
            foreach (char c in t)
            {
                if (c == '-') sawDash = true;
                else if (c != '|' && c != ':' && c != ' ' && c != '\t') return false;
            }
            return sawDash;
        }

        private static List<string> SplitRowCells(string line)
        {
            string t = (line ?? "").Trim();
            if (t.StartsWith("|", StringComparison.Ordinal)) t = t[1..];
            if (t.EndsWith("|", StringComparison.Ordinal)) t = t[..^1];
            return t.Split('|').Select(c => c.Trim()).ToList();
        }

        // 把一行裡的 **粗體** 拆成 spans。
        private static void RenderInline(TextDescriptor text, string line)
        {
            var parts = Regex.Split(line ?? "", @"\*\*(.+?)\*\*");
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                if (i % 2 == 1) text.Span(parts[i]).Bold();
                else text.Span(parts[i]);
            }
        }
    }
}
