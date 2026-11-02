using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 報告與表格要跟簡報同一個水準：同一套藝術方向（色盤 / 字體 / 家族），
    /// 而不是 Word 與 Excel 的預設樣子。
    /// </summary>
    public class DocumentDesignTests
    {
        private const string Report = """
            # 法國高檔餐點的當代版圖

            > 一份給選品團隊的市場觀察

            ## 市場結構

            內文段落。

            - 第一個重點
            1. 第一個步驟

            | 分類 | 客單價 |
            | --- | --- |
            | 星級餐廳 | 285 |
            """;

        [Fact]
        public void ReportPdfIsNotEmptyAndPaginates()
        {
            byte[] pdf = PdfReportBuilder.Build(Report);
            Assert.True(pdf.Length > 2000);
            Assert.Equal(0x25, pdf[0]);   // %PDF
        }

        [Fact]
        public void DocxCarriesTheArtDirectionColoursAndFonts()
        {
            var art = DeckArtDirection.ById("editorial");
            byte[] docx = DocxReportBuilder.Build(Report, art);

            // 標題的字體與顏色住在 styles.xml（那才是「真正的樣式」的意思），
            // 內文與引述條仍是直接格式，所以兩個 part 要一起看。
            string xml = ReadPart(docx, "word/document.xml") + ReadPart(docx, "word/styles.xml");
            Assert.Contains(art.Ink, xml);                 // 內文色
            Assert.Contains(art.Accent, xml);              // 強調色（標題線、引述條、編號）
            Assert.Contains(art.DisplayEaFont, xml);       // 雜誌家族的中文大標字體

            // 頁首與頁尾是獨立 part：沒掛上去的話頁碼與報告名都不會出現。
            Assert.Contains(Names(docx), n => n.StartsWith("word/header", System.StringComparison.Ordinal));
            Assert.Contains(Names(docx), n => n.StartsWith("word/footer", System.StringComparison.Ordinal));
        }

        [Fact]
        public void XlsxUsesAccentHeaderAndFreezesIt()
        {
            var art = DeckArtDirection.ById("finance");
            byte[] xlsx = XlsxReportBuilder.Build("| 欄 | 值 |\n| --- | --- |\n| 甲 | 12.5% |", "表", art);

            string styles = ReadPart(xlsx, "xl/styles.xml");
            Assert.Contains("FF" + art.Accent, styles);    // 標題列底色
            Assert.Contains("FFFFFFFF", styles);           // 標題列白字

            string sheet = ReadPart(xlsx, "xl/worksheets/sheet1.xml");
            Assert.Contains("frozen", sheet);              // 凍結標題列
            Assert.Contains("fitToPage", sheet);           // 列印縮成一頁寬
        }

        private static string[] Names(byte[] office)
        {
            using var ms = new MemoryStream(office);
            using var zip = new System.IO.Compression.ZipArchive(ms);
            return zip.Entries.Select(e => e.FullName).ToArray();
        }

        private static string ReadPart(byte[] office, string prefix)
        {
            using var ms = new MemoryStream(office);
            using var zip = new System.IO.Compression.ZipArchive(ms);
            var entry = zip.Entries.First(e => e.FullName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase));
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
    
        /// <summary>
        /// 拍得到的主體就該拍它。2026-10-27：一份冰島極光簡報生出三張棚拍玻璃雕塑，
        /// 畫質沒問題，但沒有一張是極光——主題一個關鍵字都沒命中，落到預設的「材質靜物」風格。
        /// </summary>
        [Theory]
        [InlineData("給我一個介紹冰島極光的5頁簡報")]
        [InlineData("冰川健行安全指南")]
        [InlineData("Iceland aurora viewing guide")]
        [InlineData("富士山四季景觀")]
        public void NaturalSubjects_GetTheLandscapeArtDirection(string topic)
            => Assert.Equal("nature", DeckArtDirection.ForTopic(topic).Id);

        [Fact]
        public void DefaultIllustrationStyle_PhotographsRealSubjects()
        {
            string style = IllustrationStyle.StyleSuffix;

            // 真實主體 → 拍它
            Assert.Contains("photograph", style, StringComparison.OrdinalIgnoreCase);
            // 抽象概念才退回材質靜物
            Assert.Contains("still life", style, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("abstract idea", style, StringComparison.OrdinalIgnoreCase);
            // 廉價畫風的排除不能弄丟
            Assert.Contains("not cartoon", style, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No text", style, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 使用者說五頁就是整份五頁。2026-10-27 實測交出 8 頁：
        /// 頁數只算內容頁（封面、來源頁另計）＝7，排版時又有一頁放不下自動拆成續頁＝8。
        /// </summary>
        [Theory]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(8)]
        public void RequestedPageCount_CountsTheWholeFile(int requested)
        {
            var slides = new List<PresentationSlidePayload>
            {
                new() { Order = 1, Kind = "cover", Heading = "冰島極光觀賞指南", Bullets = new List<string>() },
            };

            // 內容頁故意給得又多又長，逼出「排不下自動拆續頁」那條路。
            for (int i = 0; i < 9; i++)
            {
                slides.Add(new PresentationSlidePayload
                {
                    Order = slides.Count + 1,
                    Kind = "content",
                    Heading = $"第 {i + 1} 節：觀測條件與實務建議",
                    // 重點本身就要夠長。2026-10-27 第三方審查指出：原本的樣本每點只有三十幾字，
                    // 排版根本不會拆頁，等於這個測試從來沒有真的驗到「排版後幾頁」。
                    Bullets = Enumerable.Range(0, 6)
                        .Select(j => $"重點 {j + 1}：" + string.Concat(Enumerable.Repeat(
                            "帶電粒子沿磁力線進入極區大氣，與氧原子碰撞後放出綠色光，高度約一百至二百五十公里之間，" +
                            "實際亮度受太陽風速度、行星際磁場方向與當地天候共同影響。", 4)))
                        .ToList()
                });
            }

            slides.Add(new PresentationSlidePayload
            {
                Order = slides.Count + 1,
                Kind = "sources",
                Heading = "資料來源",
                Bullets = new List<string> { "Visit North Iceland", "Iceland.org" }
            });

            var outline = new PresentationOutlinePayload
            {
                Title = "冰島極光觀賞指南",
                Slides = slides,
                SlideCount = slides.Count,
            };

            var trimmed = PresentationOutlineBuilder.EnforceTotalPageCount(outline, requested);

            Assert.True(trimmed.Slides.Count <= requested,
                $"大綱張數 {trimmed.Slides.Count} 超過使用者要的 {requested}");

            // 真正要對的是「檔案會排出幾頁」——合併投影片不會減少頁數，減量才會。
            Assert.True(DeckScene.CountPages(trimmed.Slides, null) <= requested,
                $"排版後 {DeckScene.CountPages(trimmed.Slides, null)} 頁，超過使用者要的 {requested}");

            // 頁數夠的時候，封面與來源頁不該被犧牲掉。
            Assert.Contains(trimmed.Slides, x => string.Equals(x.Kind, "cover", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(trimmed.Slides, x => string.Equals(x.Kind, "sources", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 頁數少到連封面與來源頁都放不下時，要犧牲的是附屬頁，不是把頁數撐破。
        /// 2026-10-28 AI 輔助審查：要求 1 頁仍輸出 3 頁（封面＋內容＋來源）。
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void VeryFewPages_DropsExtrasNotTheLimit(int requested)
        {
            string longBullet = string.Concat(Enumerable.Repeat(
                "帶電粒子沿磁力線進入極區大氣，與氧原子碰撞後放出綠色光，高度約一百至二百五十公里之間。", 6));

            var slides = new List<PresentationSlidePayload>
            {
                new() { Order = 1, Kind = "cover", Heading = "冰島極光", Bullets = new List<string>() }
            };
            for (int i = 0; i < 9; i++)
                slides.Add(new PresentationSlidePayload
                {
                    Order = slides.Count + 1, Kind = "content", Heading = $"第 {i + 1} 節",
                    Bullets = Enumerable.Range(0, 6).Select(_ => longBullet).ToList()
                });
            slides.Add(new PresentationSlidePayload
            {
                Order = slides.Count + 1, Kind = "sources", Heading = "資料來源",
                Bullets = new List<string> { "A", "B" }
            });

            var outline = new PresentationOutlinePayload
            { Title = "冰島極光", Slides = slides, SlideCount = slides.Count };

            var trimmed = PresentationOutlineBuilder.EnforceTotalPageCount(outline, requested);

            Assert.True(DeckScene.CountPages(trimmed.Slides, null) <= requested,
                $"要求 {requested} 頁，實際排出 {DeckScene.CountPages(trimmed.Slides, null)} 頁");

            // 內容頁永遠留得住——被犧牲的是封面與來源頁。
            Assert.Contains(trimmed.Slides, x => string.Equals(x.Kind, "content", StringComparison.OrdinalIgnoreCase));
        }
}
}
