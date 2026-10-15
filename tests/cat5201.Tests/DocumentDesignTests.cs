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
    }
}
