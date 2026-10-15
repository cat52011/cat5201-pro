using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-15：使用者做了三份簡報，兩份**沒有產出檔案**，log 只寫一行
    /// 「簡報 PPTX 產出失敗（已略過，主答案不受影響）」。
    ///
    /// 追進去是 DeckScene.AddText 丟的「投影片文字超出版面」——一個為了守住可讀性而設的檢查，
    /// 結果把整份簡報一起丟掉了。一頁字稍微擠，跟整份簡報做不出來，不是同一個等級的問題。
    /// </summary>
    public class DeckOverflowTests
    {
        [Fact]
        public void 文字塞不下時仍然做得出簡報()
        {
            var outline = OverlongOutline();

            // 修之前這一行會丟 InvalidOperationException，整份簡報就沒了。
            var bytes = PptxBuilder.Build(outline, null);

            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 0, "塞不下就不給檔案，等於使用者什麼都沒拿到");
        }

        [Fact]
        public void 塞不下的那一頁還是有內容不會變空白()
        {
            var outline = OverlongOutline();
            var pages = DeckScene.Build(outline, null);

            Assert.NotEmpty(pages);

            // 每一頁都要有東西：退而求其次是「擠一點」，不是「空白頁」。
            foreach (var page in pages)
                Assert.NotEmpty(page.Elements);
        }

        [Fact]
        public void 正常長度的簡報不受影響()
        {
            var outline = new PresentationOutlinePayload
            {
                Title = "季度回顧",
                Slides = new List<PresentationSlidePayload>
                {
                    new() { Order = 1, Kind = "cover", Heading = "季度回顧" },
                    new() { Order = 2, Kind = "content", Heading = "重點", Bullets = new[] { "營收成長", "成本下降" } },
                },
            };

            var pages = DeckScene.Build(outline, null);
            Assert.NotEmpty(pages);
        }

        /// <summary>一頁塞進遠超過版面的長條列——這就是當天那兩份簡報的樣子。</summary>
        private static PresentationOutlinePayload OverlongOutline()
        {
            var bullets = Enumerable.Range(1, 12)
                .Select(i => $"{i}. " + new string('字', 220))
                .ToArray();

            return new PresentationOutlinePayload
            {
                Title = "分析台積電最近一季財報：營收、毛利率、EPS、資本支出與下季指引",
                Topic = "超長標題與超長條列",
                Slides = new List<PresentationSlidePayload>
                {
                    new() { Order = 1, Kind = "cover", Heading = new string('長', 60) },
                    new() { Order = 2, Kind = "content", Heading = new string('標', 40), Bullets = bullets },
                },
            };
        }
    }
}
