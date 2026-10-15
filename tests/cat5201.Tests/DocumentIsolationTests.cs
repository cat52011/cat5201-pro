using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 兩個節點同時產文件時，兩次呼叫不能互相影響風格。
    /// 之前 DOCX / XLSX builder 把「這次的視覺風格」放在 static 欄位，後開始的那次會蓋掉前一次，
    /// 產出的檔案風格會隨機錯亂——這種 bug 只在並行時出現，靠人工測幾乎抓不到。
    /// </summary>
    public class DocumentIsolationTests
    {
        private const string Markdown = "# 標題\n\n內文段落。\n\n| 欄 | 值 |\n| --- | --- |\n| 甲 | 1 |\n";

        [Fact]
        public async Task ParallelBuildsKeepTheirOwnArtDirection()
        {
            var editorial = DeckArtDirection.ById("editorial");
            var finance = DeckArtDirection.ById("finance");

            // 同時跑很多次，交錯的機率才夠高。
            var jobs = Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            {
                var art = i % 2 == 0 ? editorial : finance;
                return (art, docx: DocxReportBuilder.Build(Markdown, art),
                             xlsx: XlsxReportBuilder.Build("| 欄 | 值 |\n| --- | --- |\n| 甲 | 1 |", "表", art));
            })).ToArray();

            var results = await Task.WhenAll(jobs);

            foreach (var (art, docx, xlsx) in results)
            {
                var other = ReferenceEquals(art, editorial) ? finance : editorial;

                // 標題樣式在 styles.xml、引述條與編號在 document.xml——兩個 part 都不能沾到別人的色盤。
                string document = ReadPart(docx, "word/document.xml") + ReadPart(docx, "word/styles.xml");
                Assert.Contains(art.Accent, document);
                Assert.DoesNotContain(other.Accent, document);

                string styles = ReadPart(xlsx, "xl/styles.xml");
                Assert.Contains("FF" + art.Accent, styles);
                Assert.DoesNotContain("FF" + other.Accent, styles);
            }
        }

        private static string ReadPart(byte[] office, string prefix)
        {
            using var ms = new MemoryStream(office);
            using var zip = new System.IO.Compression.ZipArchive(ms);
            var entry = zip.Entries.First(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
    }
}
