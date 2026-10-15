using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Cat5201
{
    /// <summary>
    /// File Generation v1 DOCX：把 MarkdownReportBuilder 產生的 Markdown 字串
    /// 轉成 .docx 二進位（Word Open XML），不需要額外 LLM 或樣板檔。
    /// 支援：H1/H2/H3 標題、blockquote 中繼資料、清單（-/*）、編號清單、**粗體**、一般段落。
    /// </summary>
    public static class DocxReportBuilder
    {
        // 視覺語言（跟簡報同一套：雜誌家族 / 商業家族）一律用參數傳遞，不放 static 欄位——
        // 多個節點同時產文件時，共用的 static 會被後來者蓋掉，產出的風格就會隨機錯亂。

        public static byte[] Build(string markdownContent) => Build(markdownContent, null);

        public static byte[] Build(string markdownContent, DeckArtDirection? art) => Build(markdownContent, art, null);

        /// <param name="language">本次輸出語言（空＝繁體中文）。目錄標題要跟報告本文同一種語言。</param>
        public static byte[] Build(string markdownContent, DeckArtDirection? art, string? language)
        {
            string markdown = (markdownContent ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            string title = markdown.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? "";
            var design = art ?? DeckArtDirection.ForTopic(title);

            using var stream = new MemoryStream();

            using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                var body = new Body();

                AddStyles(mainPart, design);
                AppendParagraphs(mainPart, body, markdown, design, language);

                // 版面：頁邊留白 + 頁首（報告名）+ 頁尾（頁碼），跟 PDF 版對齊。
                var section = new SectionProperties(
                    new PageMargin { Top = 1418, Bottom = 1418, Left = 1418, Right = 1418, Header = 709, Footer = 709 });
                AttachHeaderFooter(mainPart, section, title, design);
                body.AppendChild(section);

                mainPart.Document = new Document(body);
                mainPart.Document.Save();
            }

            return stream.ToArray();
        }

        /// <summary>頁首：報告名 + 底線；頁尾：頁碼。Word 的頁首頁尾是獨立 part，要自己掛上去。</summary>
        private static void AttachHeaderFooter(MainDocumentPart mainPart, SectionProperties section, string title, DeckArtDirection art)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                var headerPart = mainPart.AddNewPart<HeaderPart>();
                var headerPara = new Paragraph(new ParagraphProperties(
                    new ParagraphBorders(new BottomBorder
                    {
                        Val = BorderValues.Single, Size = 4, Space = 6, Color = art.Muted
                    }),
                    new SpacingBetweenLines { After = "120" }));
                headerPara.AppendChild(new Run(
                    new RunProperties(
                        Fonts(art.BodyLatinFont, art.BodyEaFont),
                        new Color { Val = art.Muted },
                        new FontSize { Val = "16" },
                        new Spacing { Val = 10 }),
                    new Text(title.Length <= 48 ? title : title[..48] + "…") { Space = SpaceProcessingModeValues.Preserve }));
                headerPart.Header = new Header(headerPara);
                headerPart.Header.Save();

                section.AppendChild(new HeaderReference
                {
                    Type = HeaderFooterValues.Default,
                    Id = mainPart.GetIdOfPart(headerPart)
                });
            }

            var footerPart = mainPart.AddNewPart<FooterPart>();
            var footerPara = new Paragraph(new ParagraphProperties(
                new Justification { Val = JustificationValues.Right }));
            var pageRun = new Run(new RunProperties(
                Fonts(art.BodyLatinFont, art.BodyEaFont),
                new Color { Val = art.Muted },
                new FontSize { Val = "17" }));
            footerPara.AppendChild(new SimpleField { Instruction = "PAGE" });
            footerPara.AppendChild(pageRun);
            footerPart.Footer = new Footer(footerPara);
            footerPart.Footer.Save();

            section.AppendChild(new FooterReference
            {
                Type = HeaderFooterValues.Default,
                Id = mainPart.GetIdOfPart(footerPart)
            });
        }

        private static RunFonts Fonts(string latin, string eastAsian)
            => new() { Ascii = latin, HighAnsi = latin, EastAsia = eastAsian };

        /// <summary>
        /// 真正的段落樣式。把字調大調粗只是「看起來像標題」——Word 並不知道那是標題，
        /// 於是導覽窗格是空的、插不了目錄、章節折疊沒反應、螢幕閱讀器也讀不出層級。
        /// 有了 outlineLvl 的 Heading 1-3，這些才會全部生效。
        /// </summary>
        private static void AddStyles(MainDocumentPart mainPart, DeckArtDirection art)
        {
            var part = mainPart.AddNewPart<StyleDefinitionsPart>();

            part.Styles = new Styles(
                new DocDefaults(
                    new RunPropertiesDefault(new RunPropertiesBaseStyle(
                        Fonts(art.BodyLatinFont, art.BodyEaFont),
                        new Color { Val = art.Ink },
                        new FontSize { Val = "21" })),
                    new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                        new SpacingBetweenLines { Before = "0", After = "120" }))),
                NormalStyle(art),
                HeadingStyle(1, art),
                HeadingStyle(2, art),
                HeadingStyle(3, art));

            part.Styles.Save();
        }

        private static Style NormalStyle(DeckArtDirection art) => new(
            new StyleName { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { Before = "0", After = "120" }),
            new StyleRunProperties(
                Fonts(art.BodyLatinFont, art.BodyEaFont),
                new Color { Val = art.Ink },
                new FontSize { Val = "21" }))
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true
        };

        /// <summary>
        /// 標題樣式。雜誌家族用明體大標且不加粗（襯線字加粗會糊成一團），商業家族用粗黑；
        /// 第一層標題下面補一條強調色細線，就是簡報裡那個語彙。
        /// keepNext / keepLines 讓標題不會單獨留在頁尾、內文卻跑到次頁。
        /// </summary>
        private static Style HeadingStyle(int level, DeckArtDirection art)
        {
            bool editorial = art.Family == "editorial";
            int halfPtSize = level == 1 ? 40 : level == 2 ? 30 : 24;

            var paraProps = new StyleParagraphProperties(
                new KeepNext(),
                new KeepLines(),
                new OutlineLevel { Val = level - 1 },
                new SpacingBetweenLines
                {
                    Before = level == 3 ? "240" : "360",
                    After = level == 2 ? "140" : "120"
                });

            if (level == 1)
            {
                paraProps.AppendChild(new ParagraphBorders(new BottomBorder
                {
                    Val = BorderValues.Single, Size = 12, Space = 6, Color = art.Accent
                }));
            }

            var runProps = new StyleRunProperties(
                Fonts(editorial ? art.DisplayLatinFont : art.BodyLatinFont,
                      editorial ? art.DisplayEaFont : art.BodyEaFont),
                new Color { Val = art.Ink },
                new FontSize { Val = halfPtSize.ToString() });

            if (!editorial) runProps.AppendChild(new Bold());

            return new Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new UIPriority { Val = 9 },
                new PrimaryStyle(),
                paraProps,
                runProps)
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading" + level
            };
        }

        private static void AppendParagraphs(
            MainDocumentPart mainPart, Body body, string markdown, DeckArtDirection art, string? language)
        {
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inFence = false;

            // 章節夠多才放目錄。三兩節的報告加目錄只是多一頁。
            int chapters = lines.Count(l => l.StartsWith("## ", StringComparison.Ordinal));
            bool tocPending = chapters >= 4;

            // 表與圖各自連號，內文才能寫「如表 2 所示」。
            int tableNumber = 0, figureNumber = 0;
            string? pendingCaption = null;

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
                    body.AppendChild(MakeCodeParagraph(rawLine));
                    continue;
                }

                // Markdown 圖片：![alt](本機絕對路徑) → 嵌入 Word 圖片（報告智慧配圖用）。
                var imgMatch = Regex.Match(rawLine.Trim(), @"^!\[([^\]]*)\]\(([^)]+)\)$");
                if (imgMatch.Success)
                {
                    var imgPara = MakeImageParagraph(mainPart, imgMatch.Groups[2].Value.Trim());
                    if (imgPara != null)
                    {
                        body.AppendChild(imgPara);

                        // 圖說在圖下方。一定要標明是 AI 生成的示意圖——
                        // 讀者把它當成實景照片或實測結果的話，這份報告就在誤導人。
                        string alt = imgMatch.Groups[1].Value.Trim();
                        string note = ReportWords.AiIllustration(language);
                        string caption = ReportWords.Caption(
                            language, ReportWords.Figure(language), ++figureNumber,
                            alt.Length > 0 ? $"{alt}（{note}）" : note);
                        body.AppendChild(MakeCaptionParagraph(caption, art, keepWithNext: false));
                    }
                    continue;
                }

                // 「表：各分類毛利率（來源：…）」這種說明行，只有下一個非空行真的是表格時才吃掉，
                // 免得把內文裡剛好這樣開頭的句子吞掉。
                string? captionText = ReportWords.TryCaptionText(rawLine);
                if (captionText != null && NextNonEmptyStartsTable(lines, i + 1))
                {
                    pendingCaption = captionText;
                    continue;
                }

                // Markdown 表格：本行與下一行分別是「表頭列」「分隔列(---)」，整塊轉成真正的 Word 表格。
                if (IsTableRow(rawLine) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    var tableLines = new List<string> { rawLine };
                    int j = i + 2; // 跳過分隔列
                    while (j < lines.Length && IsTableRow(lines[j]))
                    {
                        tableLines.Add(lines[j]);
                        j++;
                    }

                    // 表說在表格上方（一般排版慣例），並黏住表格不被分頁拆開。
                    body.AppendChild(MakeCaptionParagraph(
                        ReportWords.Caption(language, ReportWords.Table(language), ++tableNumber, pendingCaption),
                        art, keepWithNext: true));
                    pendingCaption = null;

                    body.AppendChild(MakeTable(tableLines, art));
                    // 表格後補一個小間距段落，避免和後續內容黏在一起。
                    body.AppendChild(new Paragraph(new ParagraphProperties(
                        new SpacingBetweenLines { Before = "0", After = "120" })));

                    i = j - 1;
                    continue;
                }

                if (rawLine.StartsWith("# "))
                    body.AppendChild(MakeHeadingParagraph(rawLine.Substring(2).Trim(), level: 1));
                else if (rawLine.StartsWith("## "))
                {
                    // 目錄放在第一個章節之前（標題與中繼資料之後）。
                    if (tocPending)
                    {
                        foreach (var element in MakeTableOfContents(art, language))
                            body.AppendChild(element);
                        tocPending = false;
                    }
                    body.AppendChild(MakeHeadingParagraph(rawLine.Substring(3).Trim(), level: 2));
                }
                else if (rawLine.StartsWith("### "))
                    body.AppendChild(MakeHeadingParagraph(rawLine.Substring(4).Trim(), level: 3));
                else if (rawLine.StartsWith("> "))
                    body.AppendChild(MakeMetaParagraph(rawLine.Substring(2).Trim(), art));
                else if (Regex.IsMatch(rawLine, @"^[-*]\s+"))
                    body.AppendChild(MakeListParagraph(Regex.Replace(rawLine, @"^[-*]\s+", ""), art));
                else if (Regex.IsMatch(rawLine, @"^\d+\.\s+"))
                    body.AppendChild(MakeNumberedParagraph(rawLine, art));
                else if (rawLine.Trim().Length > 0 && rawLine.Trim().Replace("-", "").Length == 0)
                { /* horizontal rule — skip */ }
                else if (string.IsNullOrWhiteSpace(rawLine))
                    body.AppendChild(new Paragraph(new ParagraphProperties(
                        new SpacingBetweenLines { Before = "0", After = "120" })));
                else
                    body.AppendChild(MakeBodyParagraph(rawLine, art));
            }
        }

        /// <summary>
        /// 章節標題。外觀全部由 Heading 1-3 樣式決定（見 <see cref="HeadingStyle"/>），
        /// 這裡只掛樣式——這樣 Word 才認得它是標題，導覽窗格、目錄、折疊才會動。
        /// </summary>
        private static Paragraph MakeHeadingParagraph(string text, int level)
            => new(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading" + level }),
                new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

        /// <summary>
        /// 目錄欄位。Dirty=true 讓 Word 開檔時自己更新頁碼，不必叫使用者按 F9，
        /// 也不會跳出「這份文件含有欄位…」的更新對話框。
        /// 欄位裡先放一行提示文字，萬一沒更新也不會開一個空白。
        /// </summary>
        private static IEnumerable<OpenXmlElement> MakeTableOfContents(DeckArtDirection art, string? language)
        {
            var (label, hint) = ReportWords.Contents(language);

            yield return new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines { Before = "240", After = "120" }),
                new Run(
                    new RunProperties(
                        Fonts(art.BodyLatinFont, art.BodyEaFont),
                        new Color { Val = art.Muted },
                        new FontSize { Val = "18" },
                        new Spacing { Val = 60 }),
                    new Text(label) { Space = SpaceProcessingModeValues.Preserve }));

            yield return new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "280" }),
                new SimpleField(
                    new Run(
                        new RunProperties(
                            Fonts(art.BodyLatinFont, art.BodyEaFont),
                            new Color { Val = art.Muted },
                            new FontSize { Val = "20" }),
                        new Text(hint) { Space = SpaceProcessingModeValues.Preserve }))
                {
                    Instruction = "TOC \\o \"1-3\" \\h \\z \\u",
                    Dirty = true
                });
        }

        /// <summary>
        /// 說明文字段落（表 N｜… / 圖 N｜…）。小字、灰色、字距略開，
        /// 表格說明用 keepNext 黏住下方表格——說明和表格被分頁拆開就沒有意義了。
        /// </summary>
        private static Paragraph MakeCaptionParagraph(string text, DeckArtDirection art, bool keepWithNext)
        {
            var props = new ParagraphProperties(
                new SpacingBetweenLines { Before = keepWithNext ? "200" : "60", After = keepWithNext ? "80" : "200" });
            if (keepWithNext) props.AppendChild(new KeepNext());

            return new Paragraph(props,
                new Run(
                    new RunProperties(
                        Fonts(art.BodyLatinFont, art.BodyEaFont),
                        new Color { Val = art.Muted },
                        new FontSize { Val = "17" },
                        new Spacing { Val = 20 }),
                    new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        }

        private static Paragraph MakeMetaParagraph(string text, DeckArtDirection art)
        {
            var para = new Paragraph();
            para.AppendChild(new ParagraphProperties(
                new ParagraphBorders(new LeftBorder
                {
                    Val = BorderValues.Single, Size = 18, Space = 8, Color = art.Accent
                }),
                new Indentation { Left = "240" },
                new SpacingBetweenLines { Before = "160", After = "200" }));

            var run = new Run();
            // 中文斜體是模擬出來的，很醜；用字級與顏色做層級即可。
            run.AppendChild(new RunProperties(
                Fonts(art.BodyLatinFont, art.BodyEaFont),
                new Color { Val = art.Ink },
                new FontSize { Val = "24" }));
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            para.AppendChild(run);
            return para;
        }

        private static Paragraph MakeListParagraph(string text, DeckArtDirection art)
        {
            var para = new Paragraph();
            para.AppendChild(new ParagraphProperties(
                new Indentation { Left = "360", Hanging = "240" },
                new SpacingBetweenLines { Before = "0", After = "80" }));

            var bulletRun = new Run(new RunProperties(
                Fonts(art.BodyLatinFont, art.BodyEaFont),
                new Color { Val = art.Accent }));
            bulletRun.AppendChild(new Text("— ") { Space = SpaceProcessingModeValues.Preserve });
            para.AppendChild(bulletRun);

            foreach (var run in ParseInlineRuns(text, art))
                para.AppendChild(run);

            return para;
        }

        /// <summary>編號清單：數字用強調色，跟 PDF 版同一個語彙。</summary>
        private static Paragraph MakeNumberedParagraph(string text, DeckArtDirection art)
        {
            var m = Regex.Match(text, @"^\s*(\d+)\.\s+(.*)$");
            string number = m.Success ? m.Groups[1].Value.PadLeft(2, '0') : "";
            string bodyText = m.Success ? m.Groups[2].Value : text;

            var para = new Paragraph(new ParagraphProperties(
                new Indentation { Left = "420", Hanging = "420" },
                new SpacingBetweenLines { Before = "0", After = "100" }));

            para.AppendChild(new Run(
                new RunProperties(
                    Fonts(art.DisplayLatinFont, art.BodyEaFont),
                    new Color { Val = art.Accent },
                    new FontSize { Val = "21" }),
                new Text(number + "   ") { Space = SpaceProcessingModeValues.Preserve }));

            foreach (var run in ParseInlineRuns(bodyText, art))
                para.AppendChild(run);

            return para;
        }

        private static Paragraph MakeBodyParagraph(string text, DeckArtDirection art)
        {
            var para = new Paragraph();
            para.AppendChild(new ParagraphProperties(
                new SpacingBetweenLines { Before = "0", After = "120" }));

            foreach (var run in ParseInlineRuns(text, art))
                para.AppendChild(run);

            return para;
        }

        private static Paragraph MakeCodeParagraph(string text)
        {
            var para = new Paragraph();
            para.AppendChild(new ParagraphProperties(
                new Indentation { Left = "720" }));

            var run = new Run();
            run.AppendChild(new RunProperties(
                new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New" },
                new FontSize { Val = "20" },
                new Color { Val = "444444" }));
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            para.AppendChild(run);
            return para;
        }

        // ---- Markdown 圖片 → Word 內嵌圖片 ----

        private static Paragraph? MakeImageParagraph(MainDocumentPart mainPart, string imagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                    return null;

                byte[] bytes = File.ReadAllBytes(imagePath);

                var imagePart = mainPart.AddImagePart(ImagePartType.Png);
                using (var ms = new MemoryStream(bytes))
                    imagePart.FeedData(ms);
                string relId = mainPart.GetIdOfPart(imagePart);

                var (pw, ph) = ReadPngSize(bytes);
                const long maxWidthEmu = 4572000; // 12 cm，留邊距
                long cx = maxWidthEmu;
                long cy = (long)(maxWidthEmu * (double)ph / Math.Max(1, pw));

                return new Paragraph(
                    new ParagraphProperties(
                        new Justification { Val = JustificationValues.Center },
                        new SpacingBetweenLines { Before = "120", After = "160" }),
                    new Run(BuildImageDrawing(relId, cx, cy)));
            }
            catch
            {
                return null;
            }
        }

        // PNG IHDR 寬高（big-endian，位移 16..23）；讀不到時退回 1024×1024。
        private static (int w, int h) ReadPngSize(byte[] png)
        {
            if (png != null && png.Length >= 24)
            {
                int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
                int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
                if (w > 0 && h > 0) return (w, h);
            }
            return (1024, 1024);
        }

        private static Drawing BuildImageDrawing(string relId, long cx, long cy)
        {
            return new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = cx, Cy = cy },
                    new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                    new DW.DocProperties { Id = 1U, Name = "ReportImage" },
                    new DW.NonVisualGraphicFrameDrawingProperties(
                        new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = "image.png" },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = relId },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = cx, Cy = cy }),
                                    new A.PresetGeometry(new A.AdjustValueList())
                                    { Preset = A.ShapeTypeValues.Rectangle })))
                        { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
                {
                    DistanceFromTop = 0U,
                    DistanceFromBottom = 0U,
                    DistanceFromLeft = 0U,
                    DistanceFromRight = 0U
                });
        }

        // ---- Markdown 表格 → Word 表格 ----

        /// <summary>從 index 起的第一個非空行是不是一個 Markdown 表格的開頭。</summary>
        private static bool NextNonEmptyStartsTable(string[] lines, int index)
        {
            for (int i = index; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                return IsTableRow(lines[i]) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]);
            }
            return false;
        }

        private static bool IsTableRow(string line)
        {
            string t = (line ?? "").Trim();
            // 至少要有一個內部分隔（| a | b |），單純 "|" 或空字串不算。
            return t.Length >= 3 && t.StartsWith("|", StringComparison.Ordinal) && t.Contains('|', StringComparison.Ordinal)
                   && t.IndexOf('|', 1) >= 1;
        }

        private static bool IsTableSeparator(string line)
        {
            string t = (line ?? "").Trim();
            if (!t.StartsWith("|", StringComparison.Ordinal))
                return false;

            // 每個 cell 只由 - : 空白組成，且至少含一個 '-'。
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

            return t.Split('|').Select(c => c.Trim()).ToList();
        }

        private static Table MakeTable(List<string> tableLines, DeckArtDirection art)
        {
            // 年報式表格：沒有直線、沒有網格底色，只有表頭下的實線與列間髮絲線。
            var props = new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.None },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = art.Muted },
                    new LeftBorder { Val = BorderValues.None },
                    new RightBorder { Val = BorderValues.None },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 2, Color = art.Muted },
                    new InsideVerticalBorder { Val = BorderValues.None }),
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });

            var table = new Table(props);

            for (int r = 0; r < tableLines.Count; r++)
            {
                bool isHeader = r == 0;
                var cells = SplitRowCells(tableLines[r]);

                // 標題列跨頁重複，資料列不被頁面切成兩半——長表格跨頁時最基本的可讀性。
                var rowProps = new TableRowProperties(new CantSplit());
                if (isHeader) rowProps.AppendChild(new TableHeader());
                var row = new TableRow(rowProps);

                foreach (var cellText in cells)
                {
                    var cellProps = new TableCellProperties(
                        new TableCellWidth { Type = TableWidthUnitValues.Auto });

                    if (isHeader)
                        cellProps.Append(new TableCellBorders(new BottomBorder
                        {
                            Val = BorderValues.Single, Size = 10, Color = art.Ink
                        }));

                    var para = new Paragraph(new ParagraphProperties(
                        new SpacingBetweenLines { Before = "60", After = "60" }));

                    foreach (var run in ParseInlineRuns(cellText, art, forceBold: isHeader))
                        para.AppendChild(run);

                    row.Append(new TableCell(cellProps, para));
                }

                table.Append(row);
            }

            return table;
        }

        private static IEnumerable<Run> ParseInlineRuns(string text, DeckArtDirection art)
            => ParseInlineRuns(text, art, forceBold: false);

        private static IEnumerable<Run> ParseInlineRuns(string text, DeckArtDirection art, bool forceBold)
        {
            // Regex.Split with a capturing group returns alternating: normal, bold, normal, bold...
            // odd-indexed parts are the captured (bold) text.
            var parts = Regex.Split(text, @"\*\*(.+?)\*\*");

            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                    continue;

                var run = new Run();
                var runProps = new RunProperties(
                    Fonts(art.BodyLatinFont, art.BodyEaFont),
                    new Color { Val = art.Ink },
                    new FontSize { Val = "21" });
                if (i % 2 == 1 || forceBold) runProps.AppendChild(new Bold());
                run.AppendChild(runProps);

                run.AppendChild(new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
                yield return run;
            }
        }
    }
}
