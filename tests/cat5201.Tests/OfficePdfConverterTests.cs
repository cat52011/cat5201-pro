using System;
using System.IO;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// Word 預覽用 LibreOffice 轉 PDF（2026-11-03 使用者：預覽要跟實際看起來一樣）。
    /// 實際轉檔要看電腦有沒有 LibreOffice，不放進自動測試；這裡測快取規則：檔案改過就要重轉。
    /// </summary>
    public class OfficePdfConverterTests
    {
        [Fact]
        public void CacheKey_ChangesWhenTheFileChanges_AndIsStableOtherwise()
        {
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".docx");
            File.WriteAllText(path, "v1");
            string first = OfficePdfConverter.CacheKey(path);
            Assert.Equal(first, OfficePdfConverter.CacheKey(path));

            File.WriteAllText(path, "version 2");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.NotEqual(first, OfficePdfConverter.CacheKey(path));
            File.Delete(path);
        }

        [Fact]
        public async System.Threading.Tasks.Task MissingFile_ReturnsNull_SoThePreviewFallsBack()
            => Assert.Null(await OfficePdfConverter.ToPdfAsync(@"C:\does-not-exist\x.docx", Path.GetTempPath()));
    }
}
