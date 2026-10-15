using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 單頁重生後「PDF 永遠是舊的」的真實情境：產出時 pptx 與 pdf 的檔名時間戳差了幾秒，
    /// 重寫時若用讀取用的嚴格比對找不到對照檔，就會退回「同名 .pdf」生出第二份，
    /// 使用者點的那個 chip 於是永遠停在舊版（2026-09-25 現場檔案證實）。
    /// </summary>
    public class CompanionRewriteRepro
    {
        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-pair-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void RewriteTargetsIncludeTheChipsPdfEvenWithoutAValidPairing()
        {
            string dir = NewDir();
            string pptx = Path.Combine(dir, "法式簡報_20260925_210213.pptx");
            string chipPdf = Path.Combine(dir, "法式簡報_20260925_210218.pdf");
            File.WriteAllBytes(pptx, new byte[] { 1 });
            File.WriteAllBytes(chipPdf, new byte[] { 2 });

            // 配對記錄遺失或失效（實際就是這樣）——仍然必須找到那份 PDF。
            var targets = GeneratedFileCompanion.ResolveRewriteTargets(pptx);

            Assert.Contains(chipPdf, targets);
            Assert.DoesNotContain(Path.ChangeExtension(pptx, ".pdf"), targets);
        }

        [Fact]
        public void RewriteFollowsThePairingRecordAndNothingElse()
        {
            string dir = NewDir();
            string pptx = Path.Combine(dir, "法式簡報_20260925_210213.pptx");
            string chipPdf = Path.Combine(dir, "法式簡報_20260925_210218.pdf");
            string otherPdf = Path.Combine(dir, "法式簡報_20260925_210213.pdf");
            File.WriteAllBytes(pptx, new byte[] { 1 });
            File.WriteAllBytes(chipPdf, new byte[] { 2 });
            File.WriteAllBytes(otherPdf, new byte[] { 3 });
            GeneratedFileCompanion.Register(pptx, chipPdf);

            // 有配對記錄就只動那一份。之前的版本會把「同標題、五分鐘內」的 PDF 全部覆蓋，
            // 連續產兩份同名簡報時會互相污染（Codex 指出的資料安全問題）。
            Assert.Equal(new[] { chipPdf }, GeneratedFileCompanion.ResolveRewriteTargets(pptx).ToArray());
        }

        [Fact]
        public void DeletedPairedPdfFallsBackInsteadOfRecreatingAnOrphan()
        {
            string dir = NewDir();
            string pptx = Path.Combine(dir, "法式簡報_20260925_210213.pptx");
            string strayPdf = Path.Combine(dir, "法式簡報_20260925_210213.pdf");
            string chipPdf = Path.Combine(dir, "法式簡報_20260925_210218.pdf");
            File.WriteAllBytes(pptx, new byte[] { 1 });
            File.WriteAllBytes(strayPdf, new byte[] { 3 });
            File.WriteAllBytes(chipPdf, new byte[] { 2 });
            GeneratedFileCompanion.Register(pptx, strayPdf);

            // 使用者把舊 bug 留下的孤兒檔刪掉之後，不該再生一個出來。
            File.Delete(strayPdf);

            Assert.Equal(new[] { chipPdf }, GeneratedFileCompanion.ResolveRewriteTargets(pptx).ToArray());
        }

        [Fact]
        public void NoCompanionAtAllFallsBackToTheSameName()
        {
            string dir = NewDir();
            string pptx = Path.Combine(dir, "孤兒簡報_20260925_210213.pptx");
            File.WriteAllBytes(pptx, new byte[] { 1 });

            Assert.Equal(new[] { Path.ChangeExtension(pptx, ".pdf") },
                GeneratedFileCompanion.ResolveRewriteTargets(pptx).ToArray());
        }
    }
}
