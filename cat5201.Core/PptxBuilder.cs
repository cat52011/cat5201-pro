using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Cat5201
{
    /// <summary>
    /// Presentation 二進位匯出：把 PresentationOutlinePayload 轉成真正的 .pptx（Open XML）。
    ///
    /// 2026-09-24 改版：舊版每頁都是「深藍標題列＋小字條列」，四分之三頁面空白，任何主題長得一模一樣。
    /// 現在改成：① 依主題選 <see cref="DeckArtDirection"/>（色盤／字體／個性）；
    /// ② 依每頁內容挑版面（滿版封面、反白章節句、大數字、圖文半版、編號重點、來源頁）；
    /// ③ 版面填滿整頁、字級階層明確、中文字型明確指定，PowerPoint 開啟不掉字。
    /// 仍然手動定位（不依賴 layout placeholder），確保跨 PowerPoint / Keynote / LibreOffice 一致。
    /// </summary>
    public static class PptxBuilder
    {
        // 16:9，EMU（1 inch = 914400 EMU）：13.333in x 7.5in。
        private const long SlideWidth = 12192000;
        private const long SlideHeight = 6858000;

        public static byte[] Build(PresentationOutlinePayload outline)
            => Build(outline, null);

        public static byte[] Build(PresentationOutlinePayload outline, byte[]? coverImagePng)
        {
            var art = DeckArtDirection.ForTopic(outline.Title + " " + outline.Topic);
            using var stream = new MemoryStream();

            using (var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
            {
                var presentationPart = doc.AddPresentationPart();
                presentationPart.Presentation = new P.Presentation();

                var (slideMasterPart, slideLayoutPart) = CreateMasterAndLayout(presentationPart);
                CreateThemePart(slideMasterPart, art);

                var slideIdList = new P.SlideIdList();
                uint slideId = 256;

                var models = DeckScene.Build(outline, coverImagePng);
                for (int i = 0; i < models.Count; i++)
                {
                    var slidePart = CreateSceneSlide(presentationPart, slideLayoutPart, models[i]);
                    slideIdList.Append(new P.SlideId
                    {
                        Id = slideId++,
                        RelationshipId = presentationPart.GetIdOfPart(slidePart)
                    });
                }

                presentationPart.Presentation.Append(
                    new P.SlideMasterIdList(
                        new P.SlideMasterId
                        {
                            Id = 2147483648U,
                            RelationshipId = presentationPart.GetIdOfPart(slideMasterPart)
                        }),
                    slideIdList,
                    new P.SlideSize { Cx = (int)SlideWidth, Cy = (int)SlideHeight, Type = P.SlideSizeValues.Screen16x9 },
                    new P.NotesSize { Cx = 6858000, Cy = (int)SlideWidth });

                presentationPart.Presentation.Save();
                NativeDeckStorage.Write(presentationPart, outline, coverImagePng);
            }

            return stream.ToArray();
        }

        private static SlidePart CreateSceneSlide(PresentationPart presentation, SlideLayoutPart layout, DeckPage page)
        {
            var part = presentation.AddNewPart<SlidePart>();
            var tree = new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()));
            uint id = 2;
            foreach (var e in page.Elements)
            {
                long x = (long)(e.X * 12700), y = (long)(e.Y * 12700), w = (long)(e.W * 12700), h = (long)(e.H * 12700);
                if (e.Kind == "rect") tree.Append(Rect(id++, "Color", x, y, w, h, e.Color));
                else if (e.Kind == "image") tree.Append(FillPicture(part, id++, "Image", e.Image!, x, y, w, h));
                else
                {
                    var paragraph = new A.Paragraph(new A.ParagraphProperties(new A.NoBullet()),
                        Run(e.Text, (int)(e.Size * 100), DeckScene.Font, DeckScene.Font, e.Color, e.Bold));
                    tree.Append(TextBox(id++, "Text", x, y, w, h, new[] { paragraph }));
                }
            }
            part.Slide = new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
            part.AddPart(layout);
            return part;
        }

        // 「帶單位的數字」才是簡報值得放大的主角：426 億元、8.4%、50,800 個。
        private static readonly Regex KeyFigureRegex = new(
            @"(?<value>[+−-]?[0-9]+(?:,[0-9]{3})*(?:\.[0-9]+)?)\s*(?<unit>%|％|倍|億美元|億澳元|億元|億|萬平方公里|萬人次|萬人|萬夜|萬|兆元|兆|美元|澳元|元|人次|人|個|家|天|夜|小時|分鐘|平方公里|公里)",
            RegexOptions.Compiled);

        /// <summary>找出該句的關鍵數字（含單位）；年份與沒有單位的數字不算，找不到回空字串。</summary>
        internal static string FindKeyFigure(string? text)
            => FindKeyFigureMatch(text)?.Value.Replace(" ", "") ?? "";

        // 回傳原字串中的整段比對結果（含位置），SplitStat 才能精準把它從說明文字裡拿掉——
        // 原句常寫成「426 億元」（中間有空格），用組合後的字串去 IndexOf 會找不到。
        private static Match? FindKeyFigureMatch(string? text)
        {
            string t = (text ?? "").Trim();
            foreach (Match m in KeyFigureRegex.Matches(t))
            {
                // 2025 年 / 2026 年度：是時間座標不是成績，放大毫無資訊。
                // Years have no matched unit; a legitimate count such as 2025 人 must remain.

                return m;
            }
            return null;
        }

        /// <summary>把「2025 年觀光收入 426 億元，年增 8.4%」拆成大數字（426 億元）與說明。</summary>
        internal static (string Value, string Caption) SplitStat(string text)
        {
            string t = (text ?? "").Trim();
            var match = FindKeyFigureMatch(t);
            if (match == null)
                return ("", t);

            string figure = match.Value.Replace(" ", "");
            string caption = t.Remove(match.Index, match.Length).Trim(' ', '，', ',', '：', ':', '、', '。', '-', '—');
            caption = Regex.Replace(caption, @"[達為約至]\s*(?=[，,。；;]|$)", "");
            return (figure, caption);
        }

        private static A.Run Run(
            string text, int fontSize, string latinFont, string eaFont, string colorHex,
            bool bold = false, int letterSpacing = 0)
        {
            var props = new A.RunProperties(
                new A.SolidFill(new A.RgbColorModelHex { Val = colorHex }),
                new A.LatinFont { Typeface = latinFont },
                new A.EastAsianFont { Typeface = eaFont })
            {
                Language = "zh-TW",
                FontSize = fontSize,
                Bold = bold,
                Dirty = false
            };

            if (letterSpacing > 0)
                props.Spacing = letterSpacing;

            return new A.Run(props, new A.Text(text ?? ""));
        }

        private static P.Shape TextBox(
            uint shapeId, string name,
            long x, long y, long cx, long cy,
            A.Paragraph[] paragraphs,
            int lineSpacingPercent = 0,
            A.TextAnchoringTypeValues? anchor = null,
            bool autoFit = false)
        {
            var bodyProps = new A.BodyProperties
            {
                Wrap = A.TextWrappingValues.Square,
                LeftInset = 0,
                RightInset = 0,
                TopInset = 0,
                BottomInset = 0
            };
            if (anchor.HasValue)
                bodyProps.Anchor = anchor.Value;
            if (autoFit)
                bodyProps.Append(new A.NormalAutoFit()); // 內容偏長時讓 PowerPoint 自動縮字，不溢出版面

            var textBody = new P.TextBody(bodyProps, new A.ListStyle());

            foreach (var para in paragraphs)
            {
                if (lineSpacingPercent > 0)
                {
                    var props = para.GetFirstChild<A.ParagraphProperties>() ?? new A.ParagraphProperties();
                    props.Append(new A.LineSpacing(new A.SpacingPercent { Val = lineSpacingPercent }));
                    if (para.GetFirstChild<A.ParagraphProperties>() == null)
                        para.InsertAt(props, 0);
                }
                textBody.Append(para);
            }

            return new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = shapeId, Name = name },
                    new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = x, Y = y },
                        new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
                textBody);
        }

        private static P.Shape Rect(
            uint shapeId, string name, long x, long y, long cx, long cy, string fillHex, int alphaPercent = 100)
        {
            var fillColor = new A.RgbColorModelHex { Val = fillHex };
            if (alphaPercent < 100)
                fillColor.Append(new A.Alpha { Val = alphaPercent * 1000 });

            return new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = shapeId, Name = name },
                    new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = x, Y = y },
                        new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                    new A.SolidFill(fillColor),
                    new A.Outline(new A.NoFill())),
                new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.EndParagraphRunProperties { Language = "zh-TW" })));
        }

        /// <summary>滿版配圖：依實際長寬裁切成目標比例（不變形、不留白邊）。</summary>
        private static P.Picture FillPicture(
            SlidePart part, uint shapeId, string name, byte[] imageBytes,
            long x, long y, long cx, long cy)
        {
            var imagePart = part.AddImagePart(ImagePartType.Png);
            using (var ms = new MemoryStream(imageBytes))
                imagePart.FeedData(ms);
            string relId = part.GetIdOfPart(imagePart);

            var blipFill = new P.BlipFill(new A.Blip { Embed = relId });

            var size = TryReadPngSize(imageBytes);
            if (size.HasValue && size.Value.Width > 0 && size.Value.Height > 0)
            {
                double target = (double)cx / cy;
                double actual = (double)size.Value.Width / size.Value.Height;
                var srcRect = new A.SourceRectangle();

                if (actual > target)
                {
                    // 圖比框寬 → 左右各裁掉一半多餘
                    int cut = (int)Math.Round((1 - target / actual) / 2 * 100000);
                    srcRect.Left = cut;
                    srcRect.Right = cut;
                }
                else if (actual < target)
                {
                    int cut = (int)Math.Round((1 - actual / target) / 2 * 100000);
                    srcRect.Top = cut;
                    srcRect.Bottom = cut;
                }

                blipFill.Append(srcRect);
            }

            blipFill.Append(new A.Stretch(new A.FillRectangle()));

            return new P.Picture(
                new P.NonVisualPictureProperties(
                    new P.NonVisualDrawingProperties { Id = shapeId, Name = name },
                    new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = false }),
                    new P.ApplicationNonVisualDrawingProperties()),
                blipFill,
                new P.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = x, Y = y },
                        new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
        }

        /// <summary>PNG 標頭讀寬高（IHDR）；不是 PNG 或讀不到回 null，呼叫端退回不裁切。</summary>
        internal static (int Width, int Height)? TryReadPngSize(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 24)
                return null;
            if (!(bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47))
                return null;

            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return width > 0 && height > 0 ? (width, height) : null;
        }

        // ===== 樣板 / 主題（結構最小化，實際樣式都在每頁手動定位）=====

        private static (SlideMasterPart, SlideLayoutPart) CreateMasterAndLayout(PresentationPart presentationPart)
        {
            var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
            var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();

            slideLayoutPart.SlideLayout = new P.SlideLayout(
                new P.CommonSlideData(new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(new A.TransformGroup()))),
                new P.ColorMapOverride(new A.MasterColorMapping()))
            { Type = P.SlideLayoutValues.Blank };

            slideMasterPart.SlideMaster = new P.SlideMaster(
                new P.CommonSlideData(new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(new A.TransformGroup()))),
                new P.ColorMap
                {
                    Background1 = A.ColorSchemeIndexValues.Light1,
                    Text1 = A.ColorSchemeIndexValues.Dark1,
                    Background2 = A.ColorSchemeIndexValues.Light2,
                    Text2 = A.ColorSchemeIndexValues.Dark2,
                    Accent1 = A.ColorSchemeIndexValues.Accent1,
                    Accent2 = A.ColorSchemeIndexValues.Accent2,
                    Accent3 = A.ColorSchemeIndexValues.Accent3,
                    Accent4 = A.ColorSchemeIndexValues.Accent4,
                    Accent5 = A.ColorSchemeIndexValues.Accent5,
                    Accent6 = A.ColorSchemeIndexValues.Accent6,
                    Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                    FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
                },
                new P.SlideLayoutIdList(
                    new P.SlideLayoutId
                    {
                        Id = 2147483649U,
                        RelationshipId = slideMasterPart.GetIdOfPart(slideLayoutPart)
                    }));

            return (slideMasterPart, slideLayoutPart);
        }

        private static void CreateThemePart(SlideMasterPart slideMasterPart, DeckArtDirection art)
        {
            var themePart = slideMasterPart.AddNewPart<ThemePart>();
            themePart.Theme = new A.Theme(
                new A.ThemeElements(
                    new A.ColorScheme(
                        new A.Dark1Color(new A.RgbColorModelHex { Val = art.Ink }),
                        new A.Light1Color(new A.RgbColorModelHex { Val = art.Background }),
                        new A.Dark2Color(new A.RgbColorModelHex { Val = art.InverseBackground }),
                        new A.Light2Color(new A.RgbColorModelHex { Val = art.Background }),
                        new A.Accent1Color(new A.RgbColorModelHex { Val = art.Accent }),
                        new A.Accent2Color(new A.RgbColorModelHex { Val = art.Muted }),
                        new A.Accent3Color(new A.RgbColorModelHex { Val = art.Ink }),
                        new A.Accent4Color(new A.RgbColorModelHex { Val = art.Accent }),
                        new A.Accent5Color(new A.RgbColorModelHex { Val = art.Muted }),
                        new A.Accent6Color(new A.RgbColorModelHex { Val = art.Ink }),
                        new A.Hyperlink(new A.RgbColorModelHex { Val = art.Accent }),
                        new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = art.Muted }))
                    { Name = "cat5201" },
                    new A.FontScheme(
                        new A.MajorFont(
                            new A.LatinFont { Typeface = art.HeadingLatinFont },
                            new A.EastAsianFont { Typeface = art.EastAsianFont },
                            new A.ComplexScriptFont { Typeface = "" }),
                        new A.MinorFont(
                            new A.LatinFont { Typeface = art.BodyLatinFont },
                            new A.EastAsianFont { Typeface = art.EastAsianFont },
                            new A.ComplexScriptFont { Typeface = "" }))
                    { Name = "cat5201" },
                    new A.FormatScheme(
                        new A.FillStyleList(
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
                        new A.LineStyleList(
                            new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 6350 },
                            new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 12700 },
                            new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 19050 }),
                        new A.EffectStyleList(
                            new A.EffectStyle(new A.EffectList()),
                            new A.EffectStyle(new A.EffectList()),
                            new A.EffectStyle(new A.EffectList())),
                        new A.BackgroundFillStyleList(
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })))
                    { Name = "cat5201" }))
            { Name = "cat5201 Deck" };
        }
    }
}
