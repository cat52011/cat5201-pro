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
    /// 直接把「在預覽畫面上改的東西」寫回 pptx。
    ///
    /// 使用者 2026-10-15：「我希望編輯是可以直接像系統程式那樣點擊字就能改，
    /// 點擊圖片就能放大縮小甚至刪除」。
    ///
    /// 所以這裡接受的是畫面座標系的編輯：某個形狀的第幾段文字改成什麼、
    /// 某張圖搬到哪裡／多大／要不要刪掉。形狀用 ShapeTree 的子元素索引定位
    /// （跟 PptxContentPreview 給畫面的 Index 同一個），其餘一律不動。
    /// </summary>
    public static class PptxSlideEditor
    {
        private const double EmuPerPoint = 12700.0;

        /// <param name="ShapeIndex">形狀在 ShapeTree 子元素裡的位置（PptxContentPreview.Shape.Index）。</param>
        /// <param name="LineIndex">這個形狀裡的第幾個有文字的段落。</param>
        public sealed record TextEdit(int ShapeIndex, int LineIndex, string Text);

        /// <param name="Delete">true＝刪掉這張圖；此時位置與大小忽略。</param>
        public sealed record ImageEdit(
            int ShapeIndex, double XPt, double YPt, double WidthPt, double HeightPt, bool Delete);

        /// <summary>一次把這一頁的文字與圖片編輯寫回去。回傳實際改動的項目數。</summary>
        public static int Apply(
            string path, int order,
            IReadOnlyList<TextEdit>? texts = null,
            IReadOnlyList<ImageEdit>? images = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return 0;

            try
            {
                using var doc = PresentationDocument.Open(path, true);
                var part = doc.PresentationPart;
                var slideIds = part?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList();
                if (slideIds == null || order < 1 || order > slideIds.Count) return 0;

                if (slideIds[order - 1].RelationshipId?.Value is not string relId) return 0;
                if (part!.GetPartById(relId) is not SlidePart slidePart) return 0;

                var tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
                if (tree == null) return 0;

                var children = tree.ChildElements.ToList();
                int changed = 0;

                foreach (var edit in texts ?? Array.Empty<TextEdit>())
                {
                    if (edit.ShapeIndex < 0 || edit.ShapeIndex >= children.Count) continue;
                    if (children[edit.ShapeIndex] is not P.Shape sp || sp.TextBody == null) continue;

                    var paragraphs = sp.TextBody.Elements<A.Paragraph>()
                        .Where(x => x.Descendants<A.Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text)))
                        .ToList();

                    if (edit.LineIndex < 0 || edit.LineIndex >= paragraphs.Count) continue;
                    if (SetParagraphText(paragraphs[edit.LineIndex], edit.Text ?? "")) changed++;
                }

                // 刪除要由後往前，否則前面的索引會跟著位移。
                foreach (var edit in (images ?? Array.Empty<ImageEdit>()).OrderByDescending(x => x.ShapeIndex))
                {
                    if (edit.ShapeIndex < 0 || edit.ShapeIndex >= children.Count) continue;
                    if (children[edit.ShapeIndex] is not P.Picture pic) continue;

                    if (edit.Delete)
                    {
                        pic.Remove();
                        changed++;
                        continue;
                    }

                    if (SetFrame(pic.ShapeProperties?.Transform2D, edit)) changed++;
                }

                if (changed > 0)
                    slidePart.Slide!.Save();

                return changed;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Preview", $"寫回這一頁的編輯失敗：{Path.GetFileName(path)}", ex);
                return 0;
            }
        }

        /// <summary>
        /// 段落的文字換成 text。字型、字級、顏色掛在第一個 run 上，
        /// 所以文字全塞進第一個 run、其餘清空——格式完全保留。
        /// </summary>
        private static bool SetParagraphText(A.Paragraph paragraph, string text)
        {
            var runs = paragraph.Elements<A.Run>().ToList();
            if (runs.Count == 0) return false;

            var first = runs[0].GetFirstChild<A.Text>();
            if (first == null) return false;

            string current = string.Concat(paragraph.Descendants<A.Text>().Select(t => t.Text ?? ""));
            if (string.Equals(current, text, StringComparison.Ordinal))
                return false;

            first.Text = text;
            for (int i = 1; i < runs.Count; i++)
                if (runs[i].GetFirstChild<A.Text>() is { } t) t.Text = "";

            return true;
        }

        private static bool SetFrame(A.Transform2D? xfrm, ImageEdit edit)
        {
            if (xfrm?.Offset == null || xfrm.Extents == null)
                return false;

            long x = (long)Math.Round(edit.XPt * EmuPerPoint);
            long y = (long)Math.Round(edit.YPt * EmuPerPoint);
            long cx = (long)Math.Round(Math.Max(1, edit.WidthPt) * EmuPerPoint);
            long cy = (long)Math.Round(Math.Max(1, edit.HeightPt) * EmuPerPoint);

            if (xfrm.Offset.X?.Value == x && xfrm.Offset.Y?.Value == y &&
                xfrm.Extents.Cx?.Value == cx && xfrm.Extents.Cy?.Value == cy)
            {
                return false;
            }

            xfrm.Offset.X = x;
            xfrm.Offset.Y = y;
            xfrm.Extents.Cx = cx;
            xfrm.Extents.Cy = cy;
            return true;
        }
    }
}
