using System;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 「檔案產出成功」不等於「這份簡報可以拿出去」。這裡釘住可以用程式判斷的品質規則：
    /// 內文字級、內容不溢出版面、圖片不變形、頁碼數得對。
    /// </summary>
    public class DeckQualityTests
    {
        private static readonly byte[] Pixel = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=");

        private static PresentationOutlinePayload Outline(params PresentationSlidePayload[] slides) => new()
        {
            Title = "品質檢查",
            Topic = "固定案例",
            Slides = slides,
            SlideCount = slides.Length,
        };

        [Fact]
        public void CleanDeckPassesEveryRule()
        {
            var report = DeckQuality.Check(Outline(
                new PresentationSlidePayload { Order = 1, Kind = "cover", Heading = "封面標題" },
                new PresentationSlidePayload
                {
                    Order = 2, Heading = "市場現況",
                    Bullets = new[] { "旅客量創新高，帶動住宿與餐飲需求。", "國際客回流，占比回到疫情前水準。" },
                },
                new PresentationSlidePayload
                {
                    Order = 3, Heading = "關鍵數字",
                    Bullets = new[] { "營收 426 億元", "年增 8.4%", "每股盈餘 12.7 元" },
                },
                new PresentationSlidePayload
                {
                    Order = 4, Kind = "sources", Heading = "資料來源",
                    Bullets = new[] { "公開資訊觀測站，2026 年 1–6 月" },
                }), null);

            Assert.True(report.Passed, report.Summary());
            Assert.Equal(4, report.PageCount);
        }

        [Fact]
        public void BodyTextNeverShrinksBelowTheReadableFloor()
        {
            // 內容很多的一頁：應該分頁，而不是把字縮到看不清楚。
            var outline = Outline(
                new PresentationSlidePayload { Order = 1, Kind = "cover", Heading = "封面" },
                new PresentationSlidePayload
                {
                    Order = 2, Heading = "很長的一頁",
                    Bullets = Enumerable.Range(1, 8)
                        .Select(i => $"第{i}項：" + new string('內', 120))
                        .ToArray(),
                });

            var report = DeckQuality.Check(outline, null);

            Assert.True(report.Passed, report.Summary());
            Assert.True(report.PageCount > 2, "內容過多時應該分頁");

            var body = DeckScene.Build(outline, null)
                .SelectMany(p => p.Elements)
                .Where(e => e.Kind == "text" && e.Text.Contains('內'));
            Assert.All(body, e => Assert.True(e.Size >= DeckQuality.MinBodyFontSize, $"{e.Size}pt 太小"));
        }

        [Fact]
        public void FooterPageNumbersMatchTheRealPageCount()
        {
            // 內容會被拆成續頁：頁尾的「X / Y」要用實際頁數，不能用大綱張數。
            var outline = Outline(
                new PresentationSlidePayload { Order = 1, Kind = "cover", Heading = "2026 財報回顧" },
                new PresentationSlidePayload
                {
                    Order = 2, Heading = "風險與因應",
                    Bullets = Enumerable.Range(1, 9)
                        .Select(i => $"風險{i}：" + new string('說', 60))
                        .ToArray(),
                });

            var pages = DeckScene.Build(outline, null);
            Assert.True(pages.Count > outline.Slides.Count, "這組內容應該會產生續頁");

            string expected = $"/ {pages.Count:00}";
            var footers = pages.SelectMany(p => p.Elements)
                .Where(e => e.Kind == "text" && e.Text.Contains(" / "))
                .ToList();

            Assert.NotEmpty(footers);
            Assert.All(footers, f => Assert.EndsWith(expected, f.Text));
        }

        [Fact]
        public void ImagesKeepTheirAspectRatio()
        {
            var report = DeckQuality.Check(Outline(
                new PresentationSlidePayload { Order = 1, Kind = "cover", Heading = "封面" },
                new PresentationSlidePayload
                {
                    Order = 2, Heading = "有配圖的一頁", ImageBytes = Pixel,
                    Bullets = new[] { "圖片必須維持原始比例，不能為了填滿版面被壓扁。" },
                }), null);

            Assert.DoesNotContain(report.Issues, i => i.Kind == "圖片變形");
        }
    }
}
