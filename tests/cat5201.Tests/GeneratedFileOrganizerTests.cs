using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 產出檔案按「哪個聊天做的」分資料夾（使用者 2026-10-15 要求）。
    /// 認領不到主人的集中到 other，不丟掉——那些是花錢跑出來的成品。
    /// </summary>
    public class GeneratedFileOrganizerTests
    {
        [Fact]
        public void 檔案搬進各自的存檔資料夾()
        {
            string root = NewRoot("甲簡報.pptx", "乙報告.docx", "孤兒.xlsx");

            int moved = GeneratedFileOrganizer.Organize(root, new Dictionary<string, string>
            {
                ["甲簡報.pptx"] = "京都專案",
                ["乙報告.docx"] = "電動機車",
            });

            Assert.Equal(3, moved);
            Assert.True(File.Exists(Path.Combine(root, "京都專案", "甲簡報.pptx")));
            Assert.True(File.Exists(Path.Combine(root, "電動機車", "乙報告.docx")));
            Assert.True(File.Exists(Path.Combine(root, "other", "孤兒.xlsx")));
            Assert.Empty(Directory.GetFiles(root));
        }

        [Fact]
        public void 側車檔跟著本尊走()
        {
            // .preview.json 不會自己出現在節點的產出清單裡，但它必須跟 pptx 放在一起。
            string root = NewRoot("甲簡報.pptx", "甲簡報.pptx.preview.json");

            GeneratedFileOrganizer.Organize(root, new Dictionary<string, string> { ["甲簡報.pptx"] = "京都專案" });

            Assert.True(File.Exists(Path.Combine(root, "京都專案", "甲簡報.pptx")));
            Assert.True(File.Exists(Path.Combine(root, "京都專案", "甲簡報.pptx.preview.json")));
        }

        [Fact]
        public void 刪掉存檔之後它的產出集中到other()
        {
            string root = NewRoot();
            string folder = Path.Combine(root, "京都專案");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.pptx"), "x");
            File.WriteAllText(Path.Combine(folder, "b.pdf"), "x");

            int moved = GeneratedFileOrganizer.MoveToOther(root, "京都專案");

            Assert.Equal(2, moved);
            Assert.True(File.Exists(Path.Combine(root, "other", "a.pptx")));
            Assert.True(File.Exists(Path.Combine(root, "other", "b.pdf")));
            Assert.False(Directory.Exists(folder));   // 空了就收掉
        }

        [Fact]
        public void 同名不覆蓋()
        {
            // other 裡已經有一個同名檔（另一個被刪的存檔也產過同名檔案）。
            string root = NewRoot();
            Directory.CreateDirectory(Path.Combine(root, "other"));
            File.WriteAllText(Path.Combine(root, "other", "a.pptx"), "舊的");

            string folder = Path.Combine(root, "乙專案");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.pptx"), "新的");

            GeneratedFileOrganizer.MoveToOther(root, "乙專案");

            Assert.Equal("舊的", File.ReadAllText(Path.Combine(root, "other", "a.pptx")));
            Assert.Equal("新的", File.ReadAllText(Path.Combine(root, "other", "a-2.pptx")));
        }

        [Theory]
        [InlineData("京都/專案", "京都_專案")]
        [InlineData("a:b*c?", "a_b_c_")]
        [InlineData("  前後空白  ", "前後空白")]
        public void 存檔名轉成安全的資料夾名(string input, string expected)
            => Assert.Equal(expected, GeneratedFileOrganizer.SafeFolderName(input));

        [Fact]
        public void 太長的存檔名會截短()
            => Assert.Equal(60, GeneratedFileOrganizer.SafeFolderName(new string('長', 200)).Length);

        [Fact]
        public void 沒有東西要搬時不會爆掉()
        {
            Assert.Equal(0, GeneratedFileOrganizer.Organize(NewRoot(), new Dictionary<string, string>()));
            Assert.Equal(0, GeneratedFileOrganizer.Organize("D:\\\\不存在的路徑", new Dictionary<string, string>()));
            Assert.Equal(0, GeneratedFileOrganizer.MoveToOther(NewRoot(), "沒有這個存檔"));
            Assert.Equal(0, GeneratedFileOrganizer.MoveToOther(NewRoot(), ""));
        }

        [Fact]
        public void other自己不會被搬進other()
        {
            string root = NewRoot();
            Directory.CreateDirectory(Path.Combine(root, "other"));
            File.WriteAllText(Path.Combine(root, "other", "a.pptx"), "x");

            Assert.Equal(0, GeneratedFileOrganizer.MoveToOther(root, "other"));
            Assert.True(File.Exists(Path.Combine(root, "other", "a.pptx")));
        }

        private static string NewRoot(params string[] files)
        {
            string root = Path.Combine(Path.GetTempPath(), "cat5201-organize-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (string f in files)
                File.WriteAllText(Path.Combine(root, f), "x");
            return root;
        }
    }
}
