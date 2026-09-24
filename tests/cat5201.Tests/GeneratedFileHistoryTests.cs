using System;
using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 單頁重生會覆蓋同一個檔案，新版不一定比舊版好 → 覆蓋前留版本、可連續退回。
    /// </summary>
    public class GeneratedFileHistoryTests
    {
        private static string NewFile(string content, string ext = ".pptx")
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "簡報_20260924_164649" + ext);
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void SaveThenRestore_BringsBackPreviousContent()
        {
            string path = NewFile("第一版");
            Assert.False(GeneratedFileHistory.HasPrevious(path));

            GeneratedFileHistory.SaveVersion(path);
            File.WriteAllText(path, "第二版");

            Assert.True(GeneratedFileHistory.HasPrevious(path));
            Assert.True(GeneratedFileHistory.RestorePrevious(path));
            Assert.Equal("第一版", File.ReadAllText(path));
        }

        [Fact]
        public void RestoreTwice_StepsBackTwoVersions()
        {
            string path = NewFile("v1");
            GeneratedFileHistory.SaveVersion(path);
            File.WriteAllText(path, "v2");
            GeneratedFileHistory.SaveVersion(path);
            File.WriteAllText(path, "v3");

            Assert.True(GeneratedFileHistory.RestorePrevious(path));
            Assert.Equal("v2", File.ReadAllText(path));

            Assert.True(GeneratedFileHistory.RestorePrevious(path));
            Assert.Equal("v1", File.ReadAllText(path));

            // 兩版都用完了，沒有更早的版本可退。
            Assert.False(GeneratedFileHistory.HasPrevious(path));
        }

        [Fact]
        public void KeepsAtMostFiveVersions()
        {
            string path = NewFile("v0");
            for (int i = 1; i <= 8; i++)
            {
                GeneratedFileHistory.SaveVersion(path);
                File.WriteAllText(path, "v" + i);
            }

            string versionDir = Path.Combine(Path.GetDirectoryName(path)!, "_versions");
            Assert.True(Directory.GetFiles(versionDir).Length <= 5);
        }

        [Fact]
        public void MissingFileOrNoHistory_IsSafe()
        {
            Assert.False(GeneratedFileHistory.HasPrevious(null));
            Assert.False(GeneratedFileHistory.RestorePrevious(@"C:\不存在\檔案.pptx"));
            GeneratedFileHistory.SaveVersion(null); // 不可丟例外
        }
    }
}
