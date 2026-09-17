using System;
using System.Collections.Generic;
using System.IO;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// ProjectStore(第 1 刀抽出的持久化 I/O)——這是「使用者的畫布」的存亡層,
    /// 抽出 MainWindow 後首次可以直接測:存讀 round-trip、.bak 退援、改名事務(含附件資料夾與 .bak 跟隨)。
    /// </summary>
    public sealed class ProjectStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
        private readonly ProjectStore _store;

        public ProjectStoreTests()
        {
            Directory.CreateDirectory(_dir);
            _store = new ProjectStore(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static AppState SampleState() => new(
            DateTime.Now, "init-id",
            new List<NodeState> { new("n1", 10, 20, 300, 400, "問題", "**答案**", true, 20) },
            new List<ConnState> { new("n1", "n2", "ThumbTL", "ThumbTR", FlowMode: true) },
            new List<AttachmentState> { new("n1", "a.pdf", @"專案\a.pdf", "application/pdf", "file") },
            SchemaVersion: ProjectStore.CurrentSchemaVersion);

        [Fact]
        public void SaveLoad_RoundTrips()
        {
            string path = Path.Combine(_dir, "專案.json");
            _store.Save(SampleState(), path);

            var loaded = _store.Load(path);

            Assert.NotNull(loaded);
            Assert.Equal("**答案**", loaded!.Nodes[0].BottomText); // 原始 Markdown 原樣保存
            Assert.True(loaded.Connections[0].FlowMode);           // 藍虛線狀態保存
            Assert.Equal(ProjectStore.CurrentSchemaVersion, loaded.SchemaVersion);
        }

        [Fact]
        public void Load_CorruptedMainFile_FallsBackToBak()
        {
            string path = Path.Combine(_dir, "p.json");
            _store.Save(SampleState(), path);   // 第一次寫
            _store.Save(SampleState(), path);   // 第二次寫 → 上一版成為 .bak

            File.WriteAllText(path, "{ 這不是合法 JSON");

            var loaded = _store.Load(path);
            Assert.NotNull(loaded); // 主檔毀損 → .bak 救回,不是資料全失
        }

        [Fact]
        public void Rename_MovesFile_Bak_AndAttachmentFolder()
        {
            string oldPath = Path.Combine(_dir, "舊名.json");
            _store.Save(SampleState(), oldPath);
            _store.Save(SampleState(), oldPath); // 產生 .bak

            var attFolder = _store.GetAttachmentFolder(oldPath);
            Directory.CreateDirectory(attFolder);
            File.WriteAllText(Path.Combine(attFolder, "a.pdf"), "x");

            var result = _store.Rename(oldPath, "新名");

            Assert.True(result.Ok);
            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(result.NewPath));
            Assert.False(File.Exists(oldPath + ".bak"));            // .bak 不留孤兒
            Assert.True(File.Exists(result.NewPath + ".bak"));      // 跟著新名字走
            Assert.True(File.Exists(Path.Combine(_store.GetAttachmentFolder(result.NewPath), "a.pdf"))); // 附件跟著搬

            var loaded = _store.Load(result.NewPath);
            Assert.True(loaded!.FileNameLocked);                     // 手動改名 → 鎖定,自動命名不再覆蓋
            Assert.StartsWith("新名", loaded.Attachments[0].RelativePath); // JSON 內附件路徑同步改寫
        }

        [Fact]
        public void Rename_ToExistingName_FailsWithoutTouchingFiles()
        {
            string a = Path.Combine(_dir, "a.json");
            string b = Path.Combine(_dir, "b.json");
            _store.Save(SampleState(), a);
            _store.Save(SampleState(), b);

            var result = _store.Rename(a, "b");

            Assert.False(result.Ok);
            Assert.NotEmpty(result.Error);
            Assert.True(File.Exists(a)); // 原檔原封不動
        }

        [Fact]
        public void Delete_RemovesFile_Bak_AndAttachments()
        {
            string path = Path.Combine(_dir, "d.json");
            _store.Save(SampleState(), path);
            _store.Save(SampleState(), path); // .bak
            var att = _store.GetAttachmentFolder(path);
            Directory.CreateDirectory(att);
            File.WriteAllText(Path.Combine(att, "x.txt"), "x");

            _store.Delete(path);

            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".bak"));
            Assert.False(Directory.Exists(att));
        }
    }
}
