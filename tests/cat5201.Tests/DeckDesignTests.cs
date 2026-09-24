using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cat5201;
using DocumentFormat.OpenXml.Packaging;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 簡報設計（2026-09-24）：主題決定視覺語言、內容形狀決定版面。
    /// 舊版所有主題都是「深藍標題列＋條列」，這些測試釘住不要走回去。
    /// </summary>
    public class DeckDesignTests
    {
        [Theory]
        [InlineData("2026 雙年展策展論述", "gallery")]
        [InlineData("品牌視覺識別設計提案", "editorial")]
        [InlineData("塔斯馬尼亞旅遊與自然生態", "nature")]
        [InlineData("台積電財報與營收分析", "finance")]
        [InlineData("多代理 AI 系統架構", "tech")]
        [InlineData("量子計算的論文研究方法", "academic")]
        [InlineData("週會進度更新", "modern")]
        [InlineData("AI 系統設計", "tech")]
        [InlineData("上海金融市場", "finance")]
        [InlineData("Taiwan overview", "modern")]
        public void ArtDirection_FollowsTopic(string topic, string expectedId)
            => Assert.Equal(expectedId, DeckArtDirection.ForTopic(topic).Id);

        [Fact]
        public void EveryArtDirection_HasModelBriefAndContrastingInverse()
        {
            foreach (var id in DeckArtDirection.AllIds)
            {
                var art = DeckArtDirection.ById(id);
                Assert.False(string.IsNullOrWhiteSpace(art.DirectionForModel), id);
                Assert.NotEqual(art.Background, art.InverseBackground);
                Assert.NotEqual(art.Ink, art.Background);
                Assert.Contains("【藝術方向", art.BuildModelBrief("測試主題"));
            }
        }

        [Theory]
        // 年份不是成績：舊版把「2025 年」放大當主角，整頁只剩一個沒有資訊的數字。
        [InlineData("2025 年人口約 57.6 萬人", "57.6萬人")]
        [InlineData("觀光收入 426 億元，年增 8.4%", "426億元")]
        [InlineData("2026 年的展望", "")]
        [InlineData("失業率降至 3.9%", "3.9%")]
        public void KeyFigure_SkipsYears_RequiresUnit(string text, string expected)
            => Assert.Equal(expected, PptxBuilder.FindKeyFigure(text).Replace(" ", ""));

        [Fact]
        public void SplitStat_SeparatesFigureFromCaption()
        {
            var (value, caption) = PptxBuilder.SplitStat("觀光收入 426 億元，年增 8.4%");
            Assert.Equal("426億元", value.Replace(" ", ""));
            Assert.DoesNotContain("426", caption);
            Assert.Contains("觀光收入", caption);
        }

        [Fact]
        public void Build_ProducesOnePptxSlidePerOutlineSlide()
        {
            var outline = SampleOutline();
            byte[] bytes = PptxBuilder.Build(outline, null);

            Assert.True(bytes.Length > 5000);

            using var ms = new MemoryStream(bytes);
            using var doc = PresentationDocument.Open(ms, false);
            var slides = doc.PresentationPart!.SlideParts.ToList();
            Assert.Equal(outline.Slides.Count, slides.Count);

            // 封面必須有大標題文字（舊版空白封面的回歸防線）。
            string firstSlideText = string.Join(" ", slides[0].Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text));
            Assert.Contains("塔斯馬尼亞", firstSlideText);
        }

        [Fact]
        public void DeckPdf_HasOnePagePerSlide()
        {
            var outline = SampleOutline();
            byte[] pdf = DeckPdfBuilder.Build(outline, null);

            Assert.True(pdf.Length > 5000);
            // PDF 頁數＝投影片數（曾因版面撐破變成每張兩頁）。
            string raw = System.Text.Encoding.Latin1.GetString(pdf);
            int pageObjects = System.Text.RegularExpressions.Regex.Matches(raw, @"/Type\s*/Page[^s]").Count;
            Assert.Equal(outline.Slides.Count, pageObjects);
        }


        [Fact]
        public void Illustration_FollowsArtDirection()
        {
            string gallery = IllustrationStyle.Compose("一座孤立的雕塑", DeckArtDirection.ById("gallery"));
            string editorial = IllustrationStyle.Compose("一座孤立的雕塑", DeckArtDirection.ById("editorial"));

            Assert.Contains("0E0E0E", gallery);                    // 展覽風的近黑底
            Assert.Contains("F4F1EA", editorial);                  // 雜誌風的紙感底
            Assert.NotEqual(gallery, editorial);
            Assert.All(new[] { gallery, editorial }, p => Assert.Contains("No text", p));

            // 封面圖要留左側空間給大標題壓字。
            string cover = IllustrationStyle.Compose("海岸線", DeckArtDirection.ById("nature"), isCover: true);
            Assert.Contains("left third", cover);
        }

        private static PresentationOutlinePayload SampleOutline() => new()
        {
            Title = "塔斯馬尼亞：自然之島的經濟新篇章",
            Topic = "從純淨環境、產業結構到觀光成長",
            Slides = new List<PresentationSlidePayload>
            {
                new() { Order = 1, Kind = "cover", Heading = "塔斯馬尼亞：自然之島的經濟新篇章" },
                new() { Order = 2, Kind = "content", Heading = "澳洲最南端的島州",
                        Bullets = new[] { "人口 57.6 萬人", "成長率 0.4%", "面積 6.8 萬平方公里" } },
                new() { Order = 3, Kind = "content", Heading = "觀光成為成長引擎",
                        Bullets = new[] { "重點一", "重點二", "重點三", "重點四", "重點五" } },
                new() { Order = 4, Kind = "sources", Heading = "資料來源",
                        Bullets = new[] { "Tourism Tasmania", "ABS Labour Force" } },
            },
            SlideCount = 4
        };
    }
}
