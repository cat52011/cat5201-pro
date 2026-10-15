using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 由 Claude 排版的簡報沒有可重建的大綱，所以版面不能單頁重做。
    /// 但「改文字」「換圖」不需要重建版面——把內容換掉就好，位置、字級、顏色全部不動。
    /// 使用者 2026-10-15：「我不能像之前有每一頁各自的重新生成重新編輯按鍵嗎」。
    /// </summary>
    public class PptxTextEditorTests
    {
        [Fact]
        public void 改掉一頁的文字而且其他頁不動()
        {
            string path = BuildDeck();

            var before = PptxContentPreview.Read(path)!;
            var target = before.Slides[1];

            // 版面裡除了標題與條列，還有頁首、頁碼、條列序號——索引 0 不一定是標題。
            // 所以要照「值」找到那一段再換，這也正是使用者在編輯框裡會看到的樣子。
            var replacement = target.Lines
                .Select(l => l.Contains("筍與山菜", StringComparison.Ordinal) ? "春：竹筍與野菜" : l)
                .ToList();

            int changed = PptxTextEditor.ReplaceSlideText(path, 2, replacement);
            Assert.True(changed >= 1);

            var after = PptxContentPreview.Read(path)!;
            Assert.Contains(after.Slides[1].Lines, l => l.Contains("竹筍與野菜"));
            Assert.DoesNotContain(after.Slides[1].Lines, l => l.Contains("筍與山菜"));

            // 其他頁完全不動。
            Assert.Equal(before.Slides[0].Lines, after.Slides[0].Lines);
            Assert.Equal(before.Slides[2].Lines, after.Slides[2].Lines);
        }

        [Fact]
        public void 版面完全不動()
        {
            string path = BuildDeck();
            var before = PptxContentPreview.Read(path)!;

            var lines = before.Slides[1].Lines.Select(l => l + "（改）").ToList();
            PptxTextEditor.ReplaceSlideText(path, 2, lines);

            var after = PptxContentPreview.Read(path)!;

            // 形狀數、座標、字級都要一樣——改字不該動到版面。
            Assert.Equal(before.Slides[1].Shapes.Count, after.Slides[1].Shapes.Count);
            for (int i = 0; i < before.Slides[1].Shapes.Count; i++)
            {
                Assert.Equal(before.Slides[1].Shapes[i].XPt, after.Slides[1].Shapes[i].XPt, 2);
                Assert.Equal(before.Slides[1].Shapes[i].YPt, after.Slides[1].Shapes[i].YPt, 2);
                Assert.Equal(before.Slides[1].Shapes[i].WidthPt, after.Slides[1].Shapes[i].WidthPt, 2);
            }

            Assert.Equal(
                before.Slides[1].Shapes.SelectMany(sh => sh.Lines).Select(l => l.SizePt),
                after.Slides[1].Shapes.SelectMany(sh => sh.Lines).Select(l => l.SizePt));
        }

        [Fact]
        public void 頁碼不對時什麼都不做()
        {
            string path = BuildDeck();
            Assert.Equal(0, PptxTextEditor.ReplaceSlideText(path, 0, new[] { "x" }));
            Assert.Equal(0, PptxTextEditor.ReplaceSlideText(path, 99, new[] { "x" }));
        }

        [Fact]
        public void 空清單不會清空整頁()
        {
            string path = BuildDeck();
            var before = PptxContentPreview.Read(path)!;

            Assert.Equal(0, PptxTextEditor.ReplaceSlideText(path, 2, Array.Empty<string>()));

            var after = PptxContentPreview.Read(path)!;
            Assert.Equal(before.Slides[1].Lines, after.Slides[1].Lines);
        }

        [Fact]
        public void 換圖之後位置與大小不變()
        {
            string path = BuildDeck(withCover: true);
            var before = PptxContentPreview.Read(path)!;
            var page = before.Slides.First(s => s.Images.Count > 0);
            var frameBefore = page.Images[0];

            Assert.True(PptxTextEditor.ReplaceSlideImage(path, page.Order, OtherPng()));

            var after = PptxContentPreview.Read(path)!;
            var frameAfter = after.Slides.First(s => s.Order == page.Order).Images[0];

            Assert.Equal(frameBefore.XPt, frameAfter.XPt, 2);
            Assert.Equal(frameBefore.YPt, frameAfter.YPt, 2);
            Assert.Equal(frameBefore.WidthPt, frameAfter.WidthPt, 2);
            Assert.Equal(frameBefore.HeightPt, frameAfter.HeightPt, 2);

            // 內容真的換了。
            Assert.NotEqual(frameBefore.Image!.Bytes, frameAfter.Image!.Bytes);
        }

        [Fact]
        public void 沒有圖的那一頁換圖會回false()
        {
            string path = BuildDeck();   // 沒有封面圖
            Assert.False(PptxTextEditor.ReplaceSlideImage(path, 2, OtherPng()));
        }

        // ── 素材

        private static string BuildDeck(bool withCover = false)
        {
            var outline = new PresentationOutlinePayload
            {
                Title = "京都的季節性飲食文化",
                Topic = "四季與食材",
                Slides = new List<PresentationSlidePayload>
                {
                    new() { Order = 1, Kind = "cover", Heading = "京都的季節性飲食文化" },
                    new() { Order = 2, Kind = "content", Heading = "春：筍與山菜",
                            Bullets = new[] { "白子筍的產季只有兩週", "山菜的苦味是季節訊號" } },
                    new() { Order = 3, Kind = "content", Heading = "夏：鱧與冷素麵",
                            Bullets = new[] { "祇園祭與鱧料理" } },
                },
            };

            string path = Path.Combine(NewDir(), "deck.pptx");
            File.WriteAllBytes(path, PptxBuilder.Build(outline, withCover ? TinyPng() : null));
            return path;
        }

        private static byte[] TinyPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        /// <summary>另一張 1×1 PNG（顏色不同），用來確認內容真的被換掉。</summary>
        private static byte[] OtherPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-pptxedit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
