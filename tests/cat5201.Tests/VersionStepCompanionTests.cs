using System;
using System.IO;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-02 第三方審查（GPT）重現的反例：簡報有上一版、對照 PDF 沒有。
    /// 修正前：簡報還原成功、PDF 還原失敗，兩者照樣被重新登記成配對 → 「簡報 v1、預覽 v2」。
    /// </summary>
    public class VersionStepCompanionTests
    {
        private static (string Pptx, string Pdf) NewPair(string pptx, string pdf)
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-step-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, "簡報_20261102_210854.pptx");
            string d = Path.Combine(dir, "簡報_20261102_210854.pdf");
            File.WriteAllText(p, pptx);
            File.WriteAllText(d, pdf);
            return (p, d);
        }

        [Fact]
        public void PdfWithoutPreviousVersion_IsUnpaired_SoPreviewCannotShowIt()
        {
            var (pptx, pdf) = NewPair("簡報 v1", "預覽 v1");
            GeneratedFileHistory.SaveVersion(pptx);            // 只有簡報留了上一版
            File.WriteAllText(pptx, "簡報 v2");
            File.WriteAllText(pdf, "預覽 v2");
            GeneratedFileCompanion.Register(pptx, pdf);
            Assert.Equal(pdf, GeneratedFileCompanion.FindCompanionPdf(pptx));

            var result = GeneratedFileHistory.StepWithCompanions(pptx, new[] { pdf }, forward: false);

            Assert.True(result.Ok);
            Assert.Equal("簡報 v1", File.ReadAllText(pptx));
            Assert.Equal(new[] { pdf }, result.StaleCompanions);
            // 預覽要拿得到的 PDF 只能是「跟這一版簡報同一版」的——沒有就是沒有，不能拿 v2 出來。
            Assert.Null(GeneratedFileCompanion.FindCompanionPdf(pptx));
        }

        [Fact]
        public void PdfWithPreviousVersion_FollowsAndStaysPaired()
        {
            var (pptx, pdf) = NewPair("簡報 v1", "預覽 v1");
            GeneratedFileHistory.SaveVersion(pptx);
            GeneratedFileHistory.SaveVersion(pdf);
            File.WriteAllText(pptx, "簡報 v2");
            File.WriteAllText(pdf, "預覽 v2");
            GeneratedFileCompanion.Register(pptx, pdf);

            var result = GeneratedFileHistory.StepWithCompanions(pptx, new[] { pdf }, forward: false);

            Assert.True(result.Ok);
            Assert.Empty(result.StaleCompanions);
            Assert.Equal("預覽 v1", File.ReadAllText(GeneratedFileCompanion.FindCompanionPdf(pptx)!));

            // 再往前一版，兩者一起回到 v2。
            var forward = GeneratedFileHistory.StepWithCompanions(pptx, new[] { pdf }, forward: true);
            Assert.True(forward.Ok);
            Assert.Equal("簡報 v2", File.ReadAllText(pptx));
            Assert.Equal("預覽 v2", File.ReadAllText(GeneratedFileCompanion.FindCompanionPdf(pptx)!));
        }
    }
}
