using System;
using System.IO;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 預覽要顯示「實際版面」：.pptx 沒辦法在程式內渲染，改顯示同一次產出的對照 PDF。
    /// 舊版是抽文字自己排 HTML，使用者看到的跟成品完全不同。
    /// </summary>
    public class GeneratedFileCompanionTests
    {
        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-companion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void FindsPdfFromSameRun_EvenWhenTimestampsDifferBySeconds()
        {
            string dir = NewDir();
            string pptx = Path.Combine(dir, "台積電分析_20260924_164649.pptx");
            string pdf = Path.Combine(dir, "台積電分析_20260924_164651.pdf");
            File.WriteAllText(pptx, "x");
            File.WriteAllText(pdf, "x");

            Assert.Equal(pdf, GeneratedFileCompanion.FindCompanionPdf(pptx));
        }

        [Fact]
        public void DoesNotMatchDifferentDocumentOrOldRun()
        {
            string dir = NewDir();
            string pptx = Path.Combine(dir, "台積電分析_20260924_164649.pptx");
            File.WriteAllText(pptx, "x");
            File.WriteAllText(Path.Combine(dir, "台積電分析（報告）_20260924_164700.pdf"), "x"); // 另一份文件
            File.WriteAllText(Path.Combine(dir, "台積電分析_20260924_100000.pdf"), "x");        // 幾小時前的舊版

            Assert.Null(GeneratedFileCompanion.FindCompanionPdf(pptx));
        }

        [Fact]
        public void ExplicitPairingRejectsStaleAndUnrelatedPreviews()
        {
            string dir = NewDir();
            string deck = Path.Combine(dir, "測試_20260924_210000.pptx");
            string pdf = Path.Combine(dir, "測試_20260924_210001.pdf");
            File.WriteAllText(deck, "deck");
            File.WriteAllText(pdf, "pdf");
            GeneratedFileCompanion.Register(deck, null);
            Assert.Null(GeneratedFileCompanion.FindCompanionPdf(deck));
            GeneratedFileCompanion.Register(deck, pdf);
            Assert.Equal(pdf, GeneratedFileCompanion.FindCompanionPdf(deck));
            File.WriteAllText(deck, "new deck");
            Assert.Null(GeneratedFileCompanion.FindCompanionPdf(deck));
            GeneratedFileCompanion.Register(deck, pdf);
            File.WriteAllText(pdf, "new pdf");
            Assert.Null(GeneratedFileCompanion.FindCompanionPdf(deck));
        }

        [Fact]
        public void IgnoresUnsupportedExtensions()
        {
            string dir = NewDir();
            string png = Path.Combine(dir, "圖_20260924_164649.png");
            File.WriteAllText(png, "x");
            File.WriteAllText(Path.Combine(dir, "圖_20260924_164650.pdf"), "x");

            Assert.Null(GeneratedFileCompanion.FindCompanionPdf(png));
            Assert.Null(GeneratedFileCompanion.FindCompanionPdf(null));
        }
    }
}
