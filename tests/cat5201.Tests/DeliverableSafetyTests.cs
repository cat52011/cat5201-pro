using System;
using System.IO;
using System.Linq;
using Cat5201;
using DocumentFormat.OpenXml.Packaging;
using Xunit;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace Cat5201.Tests
{
    /// <summary>
    /// 產出（pptx + 對照 PDF）必須以「一組」為單位更新與回復，
    /// 而且不能動到別份產出的檔案；使用者在 PowerPoint 手動改過的設計也不能被靜靜覆蓋。
    /// </summary>
    public class DeliverableSafetyTests
    {
        private static readonly byte[] Pixel = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=");

        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-deliv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void RewriteTouchesOnlyItsOwnPdfNotAnotherDeckWithTheSameTitle()
        {
            string dir = NewDir();

            // 同一分鐘內產了兩份同標題的簡報（連按兩次就會這樣）。
            string deckA = Path.Combine(dir, "市場分析_20260930_120000.pptx");
            string pdfA = Path.Combine(dir, "市場分析_20260930_120003.pdf");
            string deckB = Path.Combine(dir, "市場分析_20260930_120100.pptx");
            string pdfB = Path.Combine(dir, "市場分析_20260930_120104.pdf");

            foreach (var f in new[] { deckA, deckB }) File.WriteAllBytes(f, new byte[] { 1 });
            File.WriteAllText(pdfA, "A");
            File.WriteAllText(pdfB, "B");

            GeneratedFileCompanion.Register(deckA, pdfA);
            GeneratedFileCompanion.Register(deckB, pdfB);

            var targets = GeneratedFileCompanion.ResolveRewriteTargets(deckA);

            Assert.Equal(new[] { pdfA }, targets.ToArray());
            Assert.Equal("B", File.ReadAllText(pdfB));   // 另一份完全沒被列入
        }

        [Fact]
        public void EachDeliverableKeepsAStableIdAcrossRewrites()
        {
            string dir = NewDir();
            string deck = Path.Combine(dir, "簡報_20260930_120000.pptx");
            string pdf = Path.Combine(dir, "簡報_20260930_120004.pdf");
            File.WriteAllBytes(deck, new byte[] { 1 });
            File.WriteAllText(pdf, "v1");

            GeneratedFileCompanion.Register(deck, pdf);
            string id = GeneratedFileCompanion.DeliverableIdOf(deck);
            Assert.NotEqual("", id);

            // 重寫（單頁重生）後仍是同一份產出。
            File.WriteAllBytes(deck, new byte[] { 2 });
            File.WriteAllText(pdf, "v2");
            GeneratedFileCompanion.Register(deck, pdf);

            Assert.Equal(id, GeneratedFileCompanion.DeliverableIdOf(deck));
        }

        [Fact]
        public void CompanionIsResolvableBeforeRestoreEvenThoughHashesChanged()
        {
            string dir = NewDir();
            string deck = Path.Combine(dir, "簡報_20260930_120000.pptx");
            string pdf = Path.Combine(dir, "簡報_20260930_120004.pdf");
            File.WriteAllBytes(deck, new byte[] { 1 });
            File.WriteAllText(pdf, "v1");
            GeneratedFileCompanion.Register(deck, pdf);

            // 模擬「回到上一版」：pptx 已被還原成舊版，雜湊與配對記錄對不上。
            File.WriteAllBytes(deck, new byte[] { 9, 9 });

            // 嚴格查詢（預覽用）會拒絕：內容可能不同步，寧可不顯示。
            Assert.Null(GeneratedFileCompanion.FindCompanionPdf(deck));

            // 但「要回復哪些檔案」必須仍然答得出來，否則 PDF 會被留在新版。
            Assert.Equal(new[] { pdf }, GeneratedFileCompanion.ResolveRewriteTargets(deck).ToArray());
        }

        [Fact]
        public void ManualDesignChangeInPowerPointIsDetectedEvenWhenTextIsUnchanged()
        {
            var outline = new PresentationOutlinePayload
            {
                Title = "設計保護",
                Topic = "測試",
                SlideCount = 2,
                Slides = new[]
                {
                    new PresentationSlidePayload { Order = 1, Kind = "cover", Heading = "封面" },
                    new PresentationSlidePayload { Order = 2, Heading = "內容", Bullets = new[] { "一句重點" } },
                },
            };

            string path = Path.Combine(NewDir(), "deck.pptx");
            File.WriteAllBytes(path, PptxBuilder.Build(outline, Pixel));

            Assert.NotNull(NativeDeckStorage.Read(path));   // 剛產出：可安全重建

            // 使用者在 PowerPoint 裡把一個形狀搬走（文字一個字都沒改）。
            using (var doc = PresentationDocument.Open(path, true))
            {
                var slide = doc.PresentationPart!.SlideParts.First().Slide;
                var shape = slide.Descendants<P.Shape>().First(s => s.ShapeProperties?.Transform2D?.Offset != null);
                shape.ShapeProperties!.Transform2D!.Offset!.X = new DocumentFormat.OpenXml.Int64Value(999999L);
                slide.Save();
            }

            // 這是使用者自己的設計，不能再當成「可安全重建」。
            Assert.Null(NativeDeckStorage.Read(path));
        }

        [Fact]
        public void DecksStoredWithTheOlderFingerprintAreStillEditable()
        {
            // 指紋演算法從「只比對文字」換成「文字＋圖形幾何」之後，換演算法之前產出的簡報
            // 存的是舊雜湊。只認新版的話，那些檔案會全部被誤判成「使用者手動改過」，
            // 於是整批失去單頁編輯——它們其實一個字都沒被動過。
            var outline = new PresentationOutlinePayload
            {
                Title = "舊格式相容",
                Topic = "測試",
                SlideCount = 2,
                Slides = new[]
                {
                    new PresentationSlidePayload { Order = 1, Kind = "cover", Heading = "封面" },
                    new PresentationSlidePayload { Order = 2, Heading = "內容", Bullets = new[] { "一句重點" } },
                },
            };

            string path = Path.Combine(NewDir(), "deck.pptx");
            File.WriteAllBytes(path, PptxBuilder.Build(outline, Pixel));
            Assert.NotNull(NativeDeckStorage.Read(path));            // 新版雜湊

            StoreHash(path, LegacyTextFingerprint(path));
            Assert.NotNull(NativeDeckStorage.Read(path));            // 舊版雜湊也要認得

            StoreHash(path, new string('A', 64));
            Assert.Null(NativeDeckStorage.Read(path));               // 兩種都對不上＝真的被改過
        }

        /// <summary>把簡報裡存的內容雜湊換成指定值（模擬「用另一套演算法寫入的舊檔」）。</summary>
        private static void StoreHash(string path, string hash)
        {
            using var doc = PresentationDocument.Open(path, true);
            var part = doc.PresentationPart!.CustomXmlParts.First();

            string xml;
            using (var read = part.GetStream(FileMode.Open, FileAccess.Read))
            using (var reader = new StreamReader(read))
                xml = reader.ReadToEnd();

            int at = xml.IndexOf("ContentHash", StringComparison.Ordinal);
            int open = xml.IndexOf('"', xml.IndexOf(':', at) + 1);
            int close = xml.IndexOf('"', open + 1);
            xml = xml[..(open + 1)] + hash + xml[close..];

            using var write = part.GetStream(FileMode.Create, FileAccess.Write);
            using var writer = new StreamWriter(write);
            writer.Write(xml);
        }

        /// <summary>舊版指紋：只比對文字（這份複製品把舊格式釘住，改壞了這個測試就會紅）。</summary>
        private static string LegacyTextFingerprint(string path)
        {
            using var doc = PresentationDocument.Open(path, false);
            var part = doc.PresentationPart!;
            var text = new System.Text.StringBuilder();
            foreach (var id in part.Presentation.SlideIdList!.Elements<P.SlideId>())
            {
                text.Append('');
                foreach (var run in ((SlidePart)part.GetPartById(id.RelationshipId!)).Slide.Descendants<A.Text>())
                    text.Append(run.Text).Append('');
            }
            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(text.ToString())));
        }
    }
}
