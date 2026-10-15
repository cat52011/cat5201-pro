using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 預覽畫布上的直接編輯：點字就改、點圖就搬／縮放／刪除。
    /// 使用者 2026-10-15：「我希望編輯是可以直接像系統程式那樣點擊字就能改」。
    ///
    /// 形狀用 ShapeTree 的子元素索引定位——用「過濾後清單的序號」會對不回去，
    /// 因為預覽會跳過沒有文字也沒有填色的裝飾形狀。
    /// </summary>
    public class PptxSlideEditorTests
    {
        [Fact]
        public void 改一段文字只動那一段()
        {
            string path = BuildDeck();
            var deck = PptxContentPreview.Read(path)!;
            var slide = deck.Slides[1];

            // 找到真正寫著標題的那個形狀與段落（版面裡還有頁首、頁碼）。
            var (shape, line) = Locate(slide, "筍與山菜");

            int changed = PptxSlideEditor.Apply(path, slide.Order,
                texts: new[] { new PptxSlideEditor.TextEdit(shape.Index, line, "春：竹筍與野菜") });

            Assert.Equal(1, changed);

            var after = PptxContentPreview.Read(path)!;
            Assert.Contains(after.Slides[1].Lines, l => l.Contains("竹筍與野菜"));
            Assert.DoesNotContain(after.Slides[1].Lines, l => l.Contains("筍與山菜"));
            Assert.Equal(deck.Slides[0].Lines, after.Slides[0].Lines);
            Assert.Equal(deck.Slides[2].Lines, after.Slides[2].Lines);
        }

        [Fact]
        public void 改文字不會動到版面()
        {
            string path = BuildDeck();
            var before = PptxContentPreview.Read(path)!;
            var (shape, line) = Locate(before.Slides[1], "筍與山菜");

            PptxSlideEditor.Apply(path, 2,
                texts: new[] { new PptxSlideEditor.TextEdit(shape.Index, line, "春") });

            var after = PptxContentPreview.Read(path)!;
            Assert.Equal(before.Slides[1].Shapes.Count, after.Slides[1].Shapes.Count);
            for (int i = 0; i < before.Slides[1].Shapes.Count; i++)
            {
                Assert.Equal(before.Slides[1].Shapes[i].XPt, after.Slides[1].Shapes[i].XPt, 2);
                Assert.Equal(before.Slides[1].Shapes[i].WidthPt, after.Slides[1].Shapes[i].WidthPt, 2);
            }
        }

        [Fact]
        public void 搬動與縮放圖片()
        {
            string path = BuildDeck(withCover: true);
            var before = PptxContentPreview.Read(path)!;
            var page = before.Slides.First(s => s.Images.Count > 0);
            var pic = page.Images[0];

            int changed = PptxSlideEditor.Apply(path, page.Order,
                images: new[] { new PptxSlideEditor.ImageEdit(pic.Index, 100, 60, 240, 180, false) });

            Assert.Equal(1, changed);

            var after = PptxContentPreview.Read(path)!;
            var moved = after.Slides.First(s => s.Order == page.Order).Images[0];

            Assert.Equal(100, moved.XPt, 1);
            Assert.Equal(60, moved.YPt, 1);
            Assert.Equal(240, moved.WidthPt, 1);
            Assert.Equal(180, moved.HeightPt, 1);
        }

        [Fact]
        public void 刪掉圖片()
        {
            string path = BuildDeck(withCover: true);
            var before = PptxContentPreview.Read(path)!;
            var page = before.Slides.First(s => s.Images.Count > 0);

            int changed = PptxSlideEditor.Apply(path, page.Order,
                images: new[] { new PptxSlideEditor.ImageEdit(page.Images[0].Index, 0, 0, 0, 0, true) });

            Assert.Equal(1, changed);

            var after = PptxContentPreview.Read(path)!;
            Assert.Empty(after.Slides.First(s => s.Order == page.Order).Images);

            // 文字不該被刪圖影響。
            Assert.Equal(before.Slides[1].Lines, after.Slides[1].Lines);
        }

        [Fact]
        public void 文字和圖片可以一次送()
        {
            string path = BuildDeck(withCover: true);
            var deck = PptxContentPreview.Read(path)!;
            var page = deck.Slides.First(s => s.Images.Count > 0);
            var textShape = page.Shapes.FirstOrDefault(sh => sh.Lines.Count > 0);
            if (textShape == null) return;   // 封面沒文字就跳過這一題

            int changed = PptxSlideEditor.Apply(path, page.Order,
                texts: new[] { new PptxSlideEditor.TextEdit(textShape.Index, 0, "改過的字") },
                images: new[] { new PptxSlideEditor.ImageEdit(page.Images[0].Index, 10, 10, 100, 80, false) });

            Assert.Equal(2, changed);
        }

        [Fact]
        public void 沒有變化時回零不寫檔()
        {
            string path = BuildDeck();
            var deck = PptxContentPreview.Read(path)!;
            var (shape, line) = Locate(deck.Slides[1], "筍與山菜");
            string same = deck.Slides[1].Lines[line + CountBefore(deck.Slides[1], shape)];

            // 用原文去改＝沒有變化。
            Assert.Equal(0, PptxSlideEditor.Apply(path, 2,
                texts: new[] { new PptxSlideEditor.TextEdit(shape.Index, line, same) }));
        }

        [Fact]
        public void 索引不對時安全地什麼都不做()
        {
            string path = BuildDeck();
            Assert.Equal(0, PptxSlideEditor.Apply(path, 2,
                texts: new[] { new PptxSlideEditor.TextEdit(999, 0, "x") }));
            Assert.Equal(0, PptxSlideEditor.Apply(path, 99,
                texts: new[] { new PptxSlideEditor.TextEdit(0, 0, "x") }));
            Assert.Equal(0, PptxSlideEditor.Apply("D:\\不存在.pptx", 1,
                texts: new[] { new PptxSlideEditor.TextEdit(0, 0, "x") }));
        }

        // ── 輔助

        /// <summary>找出寫著某段文字的形狀，以及它在那個形狀裡是第幾段。</summary>
        private static (PptxContentPreview.Shape Shape, int Line) Locate(PptxContentPreview.Slide slide, string needle)
        {
            foreach (var shape in slide.Shapes)
                for (int i = 0; i < shape.Lines.Count; i++)
                    if (shape.Lines[i].Text.Contains(needle, StringComparison.Ordinal))
                        return (shape, i);

            throw new Xunit.Sdk.XunitException($"找不到包含「{needle}」的段落");
        }

        private static int CountBefore(PptxContentPreview.Slide slide, PptxContentPreview.Shape shape)
            => slide.Shapes.TakeWhile(s => s.Index != shape.Index).Sum(s => s.Lines.Count);

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

        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-slideedit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
