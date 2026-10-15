using System;
using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 寫檔與檔名。這一塊沒有測試保護過，但它出過事：
    /// 檔名時間戳差幾秒就會生出第二份 PDF，使用者點到的 chip 永遠停在舊版。
    /// 檔名還會進到路徑裡，使用者輸入的標題必須擋掉路徑分隔符與非法字元。
    /// </summary>
    public class GeneratedFileWriterTests
    {
        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void TitleCannotEscapeTheOutputFolder()
        {
            string dir = NewDir();

            // 使用者的標題會被拿去當檔名，不能讓它跳出輸出資料夾。
            var result = GeneratedFileWriter.WriteMarkdown(dir, @"..\..\系統檔案", "內容", "測試");

            Assert.True(result.Success);
            Assert.Equal(
                Path.GetFullPath(dir),
                Path.GetFullPath(Path.GetDirectoryName(result.FilePath)!));
            // 路徑分隔符被換成底線後，".." 只是檔名裡的普通字元，不會跳出資料夾。
            Assert.DoesNotContain(Path.DirectorySeparatorChar, Path.GetFileName(result.FilePath));
        }

        [Theory]
        [InlineData("報告:第一版")]
        [InlineData(@"a/b\c")]
        [InlineData("問號?與星號*")]
        public void IllegalFileNameCharactersAreReplaced(string title)
        {
            string dir = NewDir();
            var result = GeneratedFileWriter.WriteMarkdown(dir, title, "內容", "測試");

            Assert.True(result.Success);
            string name = Path.GetFileName(result.FilePath);
            Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c));
            Assert.True(File.Exists(result.FilePath));
        }

        [Fact]
        public void AnEmptyTitleStillProducesAUsableFile()
        {
            string dir = NewDir();
            var result = GeneratedFileWriter.WriteMarkdown(dir, "   ", "內容", "測試");

            Assert.True(result.Success);
            Assert.True(File.Exists(result.FilePath));
            Assert.EndsWith(".md", result.FilePath);
        }

        [Fact]
        public void TwoWritesOfTheSameTitleDoNotOverwriteEachOther()
        {
            // 同一秒內連寫兩次也不能互相覆蓋——覆蓋掉的是使用者已經拿到的產出。
            string dir = NewDir();

            var a = GeneratedFileWriter.WriteMarkdown(dir, "同名報告", "第一份", "測試");
            var b = GeneratedFileWriter.WriteMarkdown(dir, "同名報告", "第二份", "測試");

            Assert.True(a.Success && b.Success);
            Assert.NotEqual(a.FilePath, b.FilePath);
            Assert.Equal("第一份", File.ReadAllText(a.FilePath));
            Assert.Equal("第二份", File.ReadAllText(b.FilePath));
        }

        [Fact]
        public void VeryLongTitlesDoNotBlowUpThePath()
        {
            string dir = NewDir();
            var result = GeneratedFileWriter.WriteMarkdown(dir, new string('長', 400), "內容", "測試");

            Assert.True(result.Success);
            Assert.True(result.FilePath.Length < 260);   // Windows 傳統路徑上限
            Assert.True(File.Exists(result.FilePath));
        }

        [Fact]
        public void EveryWriterReportsTheNameItActuallyWrote()
        {
            // chip 上顯示的檔名與硬碟上的必須一致，否則「開啟檔案」會找不到。
            string dir = NewDir();
            var result = GeneratedFileWriter.WritePdf(dir, "對帳", new byte[] { 0x25, 0x50, 0x44, 0x46 }, "測試");

            Assert.True(result.Success);
            Assert.Equal(result.FileName, Path.GetFileName(result.FilePath));
            Assert.True(File.Exists(result.FilePath));
        }
    }
}
