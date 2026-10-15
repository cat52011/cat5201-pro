using System;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Cat5201
{
    /// <summary>
    /// 就地改掉某一頁的文字，版面原封不動。
    ///
    /// 單頁編輯本來只有自己產的簡報能用（要有內嵌的大綱才重建得出來）。
    /// 由 Claude 排版的簡報沒有那份資料，於是只剩「整份重做」——改一個錯字要花好幾分鐘跟好幾毛錢。
    ///
    /// 但「改文字」其實不需要重建版面：pptx 的每個段落都在原處，把文字換掉就好。
    /// 位置、字級、顏色、圖片、特效全部不動——這反而比重建版面更忠實。
    /// 真正需要改版面的情況仍然只能整份重做，那是誠實的限制。
    /// </summary>
    public static class PptxTextEditor
    {
        /// <summary>
        /// 把第 order 頁（1 起算）的文字段落依序換成 newLines。
        /// 段落數與行數不一致時以較少的那個為準，多出來的段落保持原樣。
        /// </summary>
        /// <returns>真的改了幾段；0＝沒改到（頁碼不對、讀不開）。</returns>
        public static int ReplaceSlideText(string path, int order, IReadOnlyList<string> newLines)
        {
            if (string.IsNullOrWhiteSpace(path) || newLines == null || newLines.Count == 0)
                return 0;

            try
            {
                using var doc = PresentationDocument.Open(path, true);
                var part = doc.PresentationPart;
                var slideIds = part?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList();
                if (slideIds == null || order < 1 || order > slideIds.Count)
                    return 0;

                if (slideIds[order - 1].RelationshipId?.Value is not string relId) return 0;
                if (part!.GetPartById(relId) is not SlidePart slidePart) return 0;

                // 必須跟 PptxContentPreview 走**完全一樣**的順序，否則文字會寫到別的段落去。
                // 它是沿著 ShapeTree 的子元素（只看 p:sp）往下走，不是 Descendants——
                // Descendants 會多撈到群組、表格裡的段落，兩邊的索引就對不起來了。
                var paragraphs = CollectParagraphs(slidePart);

                int changed = 0;
                for (int i = 0; i < paragraphs.Count && i < newLines.Count; i++)
                {
                    if (ReplaceParagraphText(paragraphs[i], newLines[i] ?? ""))
                        changed++;
                }

                if (changed > 0)
                    slidePart.Slide.Save();

                return changed;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Preview", $"就地改文字失敗：{System.IO.Path.GetFileName(path)}", ex);
                return 0;
            }
        }

        /// <summary>
        /// 這一頁有文字的段落，順序與 PptxContentPreview 一致（沿 ShapeTree，只看 p:sp）。
        /// 兩邊共用同一個走訪方式，是「改字不會寫錯段落」的前提。
        /// </summary>
        private static List<A.Paragraph> CollectParagraphs(SlidePart slidePart)
        {
            var result = new List<A.Paragraph>();
            var tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
            if (tree == null) return result;

            foreach (var element in tree.ChildElements)
            {
                if (element is not P.Shape sp || sp.TextBody == null) continue;

                foreach (var paragraph in sp.TextBody.Elements<A.Paragraph>())
                    if (paragraph.Descendants<A.Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text)))
                        result.Add(paragraph);
            }

            return result;
        }

        /// <summary>
        /// 換掉第 order 頁的第一張圖，位置與大小不動（只換內容）。
        /// 那一頁沒有圖就回 false——這條路不負責「新增一張圖」，因為新增就得決定擺哪裡，
        /// 那等於重做版面。
        /// </summary>
        public static bool ReplaceSlideImage(string path, int order, byte[] png)
        {
            if (string.IsNullOrWhiteSpace(path) || png == null || png.Length == 0)
                return false;

            try
            {
                using var doc = PresentationDocument.Open(path, true);
                var part = doc.PresentationPart;
                var slideIds = part?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList();
                if (slideIds == null || order < 1 || order > slideIds.Count) return false;

                if (slideIds[order - 1].RelationshipId?.Value is not string relId) return false;
                if (part!.GetPartById(relId) is not SlidePart slidePart) return false;

                var picture = slidePart.Slide?.Descendants<P.Picture>().FirstOrDefault();
                if (picture?.BlipFill?.Blip?.Embed?.Value is not string embed) return false;
                if (slidePart.GetPartById(embed) is not ImagePart imagePart) return false;

                using (var stream = imagePart.GetStream(System.IO.FileMode.Create, System.IO.FileAccess.Write))
                    stream.Write(png, 0, png.Length);

                slidePart.Slide!.Save();
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Preview", $"就地換圖失敗：{System.IO.Path.GetFileName(path)}", ex);
                return false;
            }
        }

        /// <summary>
        /// 一個段落的文字換成 text。字型、字級、顏色是掛在第一個 run 上的，
        /// 所以把文字全部塞進第一個 run，其餘 run 清空——這樣格式完全保留。
        /// </summary>
        private static bool ReplaceParagraphText(A.Paragraph paragraph, string text)
        {
            var runs = paragraph.Elements<A.Run>().ToList();
            if (runs.Count == 0) return false;

            var first = runs[0].GetFirstChild<A.Text>();
            if (first == null) return false;

            if (string.Equals(first.Text, text, StringComparison.Ordinal) && runs.Count == 1)
                return false;

            first.Text = text;

            // 其餘 run 的文字清掉（整段保留一個 run 的格式）。
            for (int i = 1; i < runs.Count; i++)
            {
                var t = runs[i].GetFirstChild<A.Text>();
                if (t != null) t.Text = "";
            }

            return true;
        }
    }
}
