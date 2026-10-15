using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-15：「電動機車簡報能預覽，只有京都不能」——京都那份是 Claude 在沙箱裡排版的，
    /// 檔案裡沒有我們的版面資料，預覽只顯示一句「無法在程式內預覽」。
    ///
    /// 第一版改成把文字列出來，使用者的回應是「為什麼還是不能預覽」——那是目錄不是預覽。
    /// 所以這裡驗的是**版面**讀得出來：每個形狀的位置、大小、字級、圖片。
    /// </summary>
    public class PptxContentPreviewTests
    {
        [Fact]
        public void 讀得出投影片尺寸與頁數()
        {
            var deck = PptxContentPreview.Read(BuildDeck());

            Assert.NotNull(deck);
            Assert.Equal(3, deck!.Slides.Count);
            Assert.Equal(new[] { 1, 2, 3 }, deck.Slides.Select(s => s.Order));

            // 16:9；單位是 pt（960×540）。
            Assert.True(deck.WidthPt > deck.HeightPt);
            Assert.Equal(16.0 / 9.0, deck.WidthPt / deck.HeightPt, 2);
        }

        [Fact]
        public void 讀得出每一頁的文字()
        {
            var deck = PptxContentPreview.Read(BuildDeck())!;

            Assert.Contains(deck.Slides[0].Lines, l => l.Contains("京都的季節性飲食"));
            Assert.Contains(deck.Slides[1].Lines, l => l.Contains("春：筍與山菜"));
            Assert.Contains(deck.Slides[1].Lines, l => l.Contains("白子筍"));
        }

        [Fact]
        public void 每個形狀都有位置與大小()
        {
            var deck = PptxContentPreview.Read(BuildDeck())!;
            var shapes = deck.Slides.SelectMany(s => s.Shapes).ToList();

            Assert.NotEmpty(shapes);

            // 沒有座標就畫不出版面，只能退回列清單——那正是上一版被嫌的地方。
            Assert.All(shapes, sh =>
            {
                Assert.True(sh.WidthPt > 0, "形狀沒有寬度");
                Assert.True(sh.HeightPt > 0, "形狀沒有高度");
                Assert.True(sh.XPt >= 0 && sh.XPt < deck.WidthPt, "形狀跑出版面外");
            });
        }

        [Fact]
        public void 文字帶得出字級()
        {
            var deck = PptxContentPreview.Read(BuildDeck())!;
            var lines = deck.Slides.SelectMany(s => s.Shapes).SelectMany(sh => sh.Lines).ToList();

            Assert.NotEmpty(lines);
            Assert.All(lines, l => Assert.True(l.SizePt > 0));

            // 標題應該比內文大——字級沒讀出來的話整頁會變成同一個大小。
            Assert.True(lines.Max(l => l.SizePt) > lines.Min(l => l.SizePt));
        }

        [Fact]
        public void 圖片讀得出來而且有位置()
        {
            var deck = PptxContentPreview.Read(BuildDeck(withCover: true))!;
            var images = deck.Slides.SelectMany(s => s.Images).ToList();

            Assert.NotEmpty(images);
            Assert.All(images, i =>
            {
                Assert.NotNull(i.Image);
                Assert.NotEmpty(i.Image!.Bytes);
                Assert.True(i.WidthPt > 0 && i.HeightPt > 0);
            });
        }

        [Fact]
        public void 不是簡報的檔案回null而不是丟例外()
        {
            string path = Path.Combine(NewDir(), "not-a-deck.pptx");
            File.WriteAllText(path, "這不是 pptx");

            Assert.Null(PptxContentPreview.Read(path));
        }

        [Fact]
        public void 檔案不存在也不會丟例外()
            => Assert.Null(PptxContentPreview.Read(Path.Combine(NewDir(), "missing.pptx")));

        // ── 用自家的 builder 產一份真的 pptx 當素材

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
                            Bullets = new[] { "祇園祭與鱧料理", "冷物的器皿選擇" } },
                },
            };

            string path = Path.Combine(NewDir(), "deck.pptx");
            File.WriteAllBytes(path, PptxBuilder.Build(outline, withCover ? TinyPng() : null));
            return path;
        }

        /// <summary>1×1 的 PNG，只是要有一張真的圖片在檔案裡。</summary>
        private static byte[] TinyPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-pptxpreview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
