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

        // ── 存檔改名：產出資料夾跟著改名（2026-11-04 使用者回報：改名後新名稱的資料夾不會出現） ──

        [Fact]
        public void 改名時整個產出資料夾跟著改名_並回傳新舊路徑對照()
        {
            string root = NewRoot();
            string oldDir = Path.Combine(root, "作業完成事項");
            Directory.CreateDirectory(Path.Combine(oldDir, "機器學習迴歸分析作業", "修正前"));
            File.WriteAllText(Path.Combine(oldDir, "報告.docx"), "r");
            File.WriteAllText(Path.Combine(oldDir, "機器學習迴歸分析作業", "hw1.py"), "c");
            File.WriteAllText(Path.Combine(oldDir, "機器學習迴歸分析作業", "修正前", "報告.docx"), "old");

            var map = GeneratedFileOrganizer.RenameSaveFolder(root, "作業完成事項", "Review Agent test 3");

            string newDir = Path.Combine(root, "Review Agent test 3");
            Assert.False(Directory.Exists(oldDir));
            Assert.Equal("r", File.ReadAllText(Path.Combine(newDir, "報告.docx")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(newDir, "機器學習迴歸分析作業", "修正前", "報告.docx")));
            Assert.Equal(3, map.Count);
            Assert.Equal(Path.Combine(newDir, "機器學習迴歸分析作業", "hw1.py"),
                GeneratedFileOrganizer.Remap(Path.Combine(oldDir, "機器學習迴歸分析作業", "hw1.py"), map));
            Assert.Equal(@"D:\別處\x.docx", GeneratedFileOrganizer.Remap(@"D:\別處\x.docx", map));   // 不在對照裡的照原樣
        }

        [Fact]
        public void 新名稱的資料夾已存在時逐檔合併_同名加序號不覆蓋()
        {
            string root = NewRoot();
            Directory.CreateDirectory(Path.Combine(root, "舊名"));
            Directory.CreateDirectory(Path.Combine(root, "新名"));
            File.WriteAllText(Path.Combine(root, "舊名", "報告.docx"), "舊的");
            File.WriteAllText(Path.Combine(root, "新名", "報告.docx"), "原本就在");

            var map = GeneratedFileOrganizer.RenameSaveFolder(root, "舊名", "新名");

            Assert.Equal("原本就在", File.ReadAllText(Path.Combine(root, "新名", "報告.docx")));
            Assert.Equal("舊的", File.ReadAllText(Path.Combine(root, "新名", "報告-2.docx")));
            Assert.False(Directory.Exists(Path.Combine(root, "舊名")));      // 搬空的舊資料夾收掉
            Assert.Equal(Path.Combine(root, "新名", "報告-2.docx"), map.Values.Single());
        }

        [Theory]
        [InlineData("other")]
        [InlineData("_versions")]
        public void 改成系統資料夾的名稱時不搬(string reserved)
        {
            string root = NewRoot();
            Directory.CreateDirectory(Path.Combine(root, "聊天"));
            File.WriteAllText(Path.Combine(root, "聊天", "a.docx"), "x");

            Assert.Empty(GeneratedFileOrganizer.RenameSaveFolder(root, "聊天", reserved));
            Assert.True(File.Exists(Path.Combine(root, "聊天", "a.docx")));
        }

        [Fact]
        public void 沒有舊資料夾或名稱沒變時什麼都不做()
        {
            string root = NewRoot();
            Assert.Empty(GeneratedFileOrganizer.RenameSaveFolder(root, "不存在", "新名"));
            Directory.CreateDirectory(Path.Combine(root, "同名"));
            Assert.Empty(GeneratedFileOrganizer.RenameSaveFolder(root, "同名", "同名"));
            Assert.True(Directory.Exists(Path.Combine(root, "同名")));
        }

        [Fact]
        public void 清空資料夾往上收到根目錄為止_不碰根目錄與其他資料夾()
        {
            // 重新執行刪掉上一輪產出後：聊天資料夾、程式資料夾都空了就收掉（2026-11-04 留下好幾個空殼資料夾）
            string root = NewRoot();
            string code = Path.Combine(root, "舊聊天名", "報告名", "空的子資料夾");
            Directory.CreateDirectory(code);
            Directory.CreateDirectory(Path.Combine(root, "別的聊天"));
            File.WriteAllText(Path.Combine(root, "別的聊天", "a.docx"), "x");

            GeneratedFileOrganizer.RemoveEmptyDirs(Path.Combine(root, "舊聊天名", "報告名"), root);

            Assert.False(Directory.Exists(Path.Combine(root, "舊聊天名")));
            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(Path.Combine(root, "別的聊天", "a.docx")));

            // 還有檔案的資料夾不收
            Directory.CreateDirectory(Path.Combine(root, "有檔案"));
            File.WriteAllText(Path.Combine(root, "有檔案", "b.docx"), "x");
            GeneratedFileOrganizer.RemoveEmptyDirs(Path.Combine(root, "有檔案"), root);
            Assert.True(File.Exists(Path.Combine(root, "有檔案", "b.docx")));

            // 根目錄外面的一律不碰
            string outside = Path.Combine(Path.GetTempPath(), "cat5201-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            GeneratedFileOrganizer.RemoveEmptyDirs(outside, root);
            Assert.True(Directory.Exists(outside));
            Directory.Delete(outside);
        }

        // ── 啟動時對齊：改名修好之前就改過名的存檔 ──

        private static GeneratedFileOrganizer.SaveOutputs Save(string name, params string[] paths) => new(name, paths);

        [Fact]
        public void 啟動對齊_改過名的存檔資料夾換成目前名稱_並收掉空資料夾()
        {
            // 重現 2026-11-04：「Review Agent test 3」的檔案在「作業完成事項」，另有點進去是空的資料夾
            string root = NewRoot();
            string old = Path.Combine(root, "作業完成事項");
            Directory.CreateDirectory(Path.Combine(old, "機器學習迴歸分析作業"));
            File.WriteAllText(Path.Combine(old, "報告.docx"), "r");
            File.WriteAllText(Path.Combine(old, "機器學習迴歸分析作業", "hw1.py"), "c");
            Directory.CreateDirectory(Path.Combine(root, "作業完成進度", "子資料夾"));       // 空殼
            Directory.CreateDirectory(Path.Combine(root, "other"));                          // 系統資料夾，空的也留著

            var results = GeneratedFileOrganizer.AlignSaveFolders(root, new[]
            {
                Save("Review Agent test 3", Path.Combine(old, "報告.docx"), Path.Combine(old, "機器學習迴歸分析作業", "hw1.py")),
            });

            var r = Assert.Single(results);
            Assert.Equal(("Review Agent test 3", "作業完成事項", "Review Agent test 3"), (r.SaveName, r.FromFolder, r.ToFolder));
            Assert.True(File.Exists(Path.Combine(root, "Review Agent test 3", "機器學習迴歸分析作業", "hw1.py")));
            Assert.Equal(Path.Combine(root, "Review Agent test 3", "報告.docx"),
                GeneratedFileOrganizer.Remap(Path.Combine(old, "報告.docx"), r.Map));
            Assert.False(Directory.Exists(old));
            Assert.False(Directory.Exists(Path.Combine(root, "作業完成進度")));
            Assert.True(Directory.Exists(Path.Combine(root, "other")));

            // 再跑一次：已經對齊，什麼都不做
            Assert.Empty(GeneratedFileOrganizer.AlignSaveFolders(root, new[]
            {
                Save("Review Agent test 3", Path.Combine(root, "Review Agent test 3", "報告.docx")),
            }));
        }

        [Fact]
        public void 啟動對齊_共用的資料夾與別人名下的資料夾都不搬()
        {
            string root = NewRoot();
            foreach (var d in new[] { "共用", "乙" })
            {
                Directory.CreateDirectory(Path.Combine(root, d));
                File.WriteAllText(Path.Combine(root, d, "a.docx"), "x");
            }

            var results = GeneratedFileOrganizer.AlignSaveFolders(root, new[]
            {
                Save("甲", Path.Combine(root, "共用", "a.docx"), Path.Combine(root, "乙", "a.docx")),  // 「乙」是另一個存檔的名稱
                Save("丙", Path.Combine(root, "共用", "a.docx")),                                      // 「共用」被兩個存檔引用
                Save("乙"),
            });

            Assert.Empty(results);
            Assert.True(File.Exists(Path.Combine(root, "共用", "a.docx")));
            Assert.True(File.Exists(Path.Combine(root, "乙", "a.docx")));
            Assert.False(Directory.Exists(Path.Combine(root, "甲")));
        }

        [Fact]
        public void 啟動對齊_不在根目錄底下或直接放在根目錄的路徑不處理()
        {
            string root = NewRoot("散檔.docx");
            var results = GeneratedFileOrganizer.AlignSaveFolders(root, new[]
            {
                Save("甲", Path.Combine(root, "散檔.docx"), @"C:\別的地方\x.docx"),
            });
            Assert.Empty(results);
            Assert.True(File.Exists(Path.Combine(root, "散檔.docx")));
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
