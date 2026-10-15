using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Cat5201
{
    /// <summary>
    /// 任何一份 .pptx 的版面預覽。
    ///
    /// 程式內預覽本來只支援自己產的簡報（檔案裡附了版面資料才重建得出來），其餘一律
    /// 顯示「無法在程式內預覽」——包括使用者在 PowerPoint 改過的，以及最佳品質模式
    /// 由 Claude 在沙箱裡排版的那些（2026-10-15：「電動機車簡報能預覽，只有京都不能」）。
    ///
    /// 第一版只把文字按順序列出來，但那不是預覽，是目錄。這一版直接讀 pptx 的實際幾何：
    /// 每個形狀的位置、大小、字級、粗細、顏色、對齊，以及圖片的擺放位置，
    /// 照原樣縮放畫出來。不是像素級還原（陰影、漸層、特效沒有做），但「這一頁長什麼樣」看得出來。
    /// </summary>
    public static class PptxContentPreview
    {
        /// <summary>EMU：Office 的內部長度單位，1 pt = 12700 EMU。</summary>
        private const double EmuPerPoint = 12700.0;

        public sealed record Deck(double WidthPt, double HeightPt, IReadOnlyList<Slide> Slides);

        public sealed record Slide(int Order, IReadOnlyList<Shape> Shapes)
        {
            /// <summary>這一頁的文字（給摘要與測試用）。</summary>
            public IReadOnlyList<string> Lines =>
                Shapes.SelectMany(s => s.Lines).Select(l => l.Text).Where(t => t.Length > 0).ToList();

            public IReadOnlyList<Shape> Images => Shapes.Where(s => s.Image != null).ToList();
        }

        /// <param name="Index">
        /// 這個形狀在 ShapeTree 子元素裡的位置。
        /// 畫面上改完要寫回同一個形狀，靠的就是這個索引——用「過濾後清單的序號」會對不回去，
        /// 因為預覽會跳過沒有文字也沒有填色的裝飾形狀。
        /// </param>
        /// <param name="XPt">左上角位置與大小，單位 pt（＝1/72 吋），直接對應投影片座標。</param>
        public sealed record Shape(
            int Index,
            double XPt, double YPt, double WidthPt, double HeightPt,
            IReadOnlyList<Line> Lines,
            Image? Image,
            string? FillColor);

        /// <param name="SizePt">字級（pt）。<param name="Align">l / ctr / r。</param>
        public sealed record Line(string Text, double SizePt, bool Bold, string Color, string Align);

        public sealed record Image(string ContentType, byte[] Bytes);

        /// <summary>讀出整份簡報的版面。讀不開（壞檔、不是 pptx）回 null。</summary>
        public static Deck? Read(string path)
        {
            try
            {
                using var doc = PresentationDocument.Open(path, false);
                var part = doc.PresentationPart;
                if (part?.Presentation?.SlideIdList == null)
                    return null;

                var size = part.Presentation.SlideSize;
                double widthPt = (size?.Cx?.Value ?? 12192000) / EmuPerPoint;
                double heightPt = (size?.Cy?.Value ?? 6858000) / EmuPerPoint;

                var slides = new List<Slide>();
                int order = 0;

                foreach (var slideId in part.Presentation.SlideIdList.Elements<P.SlideId>())
                {
                    order++;
                    if (slideId.RelationshipId?.Value is not string relId) continue;
                    if (part.GetPartById(relId) is not SlidePart slidePart) continue;

                    slides.Add(new Slide(order, ReadShapes(slidePart, widthPt, heightPt)));
                }

                return slides.Count == 0 ? null : new Deck(widthPt, heightPt, slides);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Preview", $"讀取簡報版面失敗：{Path.GetFileName(path)}", ex);
                return null;
            }
        }

        private static IReadOnlyList<Shape> ReadShapes(SlidePart slidePart, double slideW, double slideH)
        {
            var shapes = new List<Shape>();
            var tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
            if (tree == null) return shapes;

            int index = -1;
            foreach (var element in tree.ChildElements)
            {
                index++;

                switch (element)
                {
                    case P.Shape sp:
                    {
                        var (x, y, w, h) = Frame(sp.ShapeProperties?.Transform2D, slideW, slideH);
                        var lines = ReadLines(sp.TextBody);
                        string? fill = SolidFill(sp.ShapeProperties);

                        // 沒有文字、也沒有填色的形狀（接點、裝飾框）畫出來只是雜訊。
                        if (lines.Count == 0 && fill == null)
                            break;

                        shapes.Add(new Shape(index, x, y, w, h, lines, null, fill));
                        break;
                    }

                    case P.Picture pic:
                    {
                        var (x, y, w, h) = Frame(pic.ShapeProperties?.Transform2D, slideW, slideH);
                        var image = ReadImage(slidePart, pic);
                        if (image != null)
                            shapes.Add(new Shape(index, x, y, w, h, Array.Empty<Line>(), image, null));
                        break;
                    }
                }
            }

            return shapes;
        }

        private static (double X, double Y, double W, double H) Frame(A.Transform2D? xfrm, double slideW, double slideH)
        {
            long ox = xfrm?.Offset?.X?.Value ?? 0;
            long oy = xfrm?.Offset?.Y?.Value ?? 0;
            long cx = xfrm?.Extents?.Cx?.Value ?? 0;
            long cy = xfrm?.Extents?.Cy?.Value ?? 0;

            // 沒有 xfrm（繼承版面配置）時給一個保守的全幅框，至少內容不會疊在左上角。
            if (cx <= 0 || cy <= 0)
                return (slideW * 0.06, slideH * 0.08, slideW * 0.88, slideH * 0.2);

            return (ox / EmuPerPoint, oy / EmuPerPoint, cx / EmuPerPoint, cy / EmuPerPoint);
        }

        private static IReadOnlyList<Line> ReadLines(P.TextBody? body)
        {
            var lines = new List<Line>();
            if (body == null) return lines;

            foreach (var paragraph in body.Elements<A.Paragraph>())
            {
                string text = string.Concat(paragraph.Descendants<A.Text>().Select(t => t.Text ?? ""))
                                    .Replace('\v', ' ')
                                    .Trim();
                if (text.Length == 0) continue;

                var run = paragraph.Elements<A.Run>().FirstOrDefault();
                var props = run?.RunProperties;

                double sizePt = props?.FontSize?.Value is int sz && sz > 0 ? sz / 100.0 : 18;
                bool bold = props?.Bold?.Value == true;
                string color = props?.Descendants<A.RgbColorModelHex>().FirstOrDefault()?.Val?.Value is string hex
                    ? "#" + hex
                    : "#1b1b1f";
                string align = paragraph.ParagraphProperties?.Alignment?.Value.ToString()?.ToLowerInvariant() switch
                {
                    "center" => "center",
                    "right" => "right",
                    _ => "left",
                };

                lines.Add(new Line(text, sizePt, bold, color, align));
            }

            return lines;
        }

        private static string? SolidFill(P.ShapeProperties? props)
            => props?.GetFirstChild<A.SolidFill>()?.RgbColorModelHex?.Val?.Value is string hex ? "#" + hex : null;

        private static Image? ReadImage(SlidePart slidePart, P.Picture pic)
        {
            try
            {
                string? embed = pic.BlipFill?.Blip?.Embed?.Value;
                if (string.IsNullOrWhiteSpace(embed)) return null;
                if (slidePart.GetPartById(embed!) is not ImagePart imagePart) return null;

                using var stream = imagePart.GetStream();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);

                // 預覽不需要原尺寸；太大的跳過，免得整頁 HTML 變成幾十 MB。
                if (buffer.Length is 0 or > 8_000_000) return null;

                return new Image(imagePart.ContentType, buffer.ToArray());
            }
            catch { return null; }
        }
    }
}
