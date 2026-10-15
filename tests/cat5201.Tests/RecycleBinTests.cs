using System;
using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 刪除存檔＝整包（畫布 + 附件 + 該存檔的記憶）進最近刪除，7 天內可完整復原；
    /// 全域記憶不能跟著被刪——那是跨專案的個人化。
    /// </summary>
    public class RecycleBinTests
    {
        private static string NewSaves()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-trash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static (string saves, ProjectStore store, RecycleBin bin, string project) Seed()
        {
            string saves = NewSaves();
            var store = new ProjectStore(saves);
            var bin = new RecycleBin(saves);

            string project = Path.Combine(saves, "我的存檔.json");
            File.WriteAllText(project, "{\"Nodes\":[]}");
            File.WriteAllText(project + ".bak", "{\"old\":true}");

            string attachments = store.GetAttachmentFolder(project);
            Directory.CreateDirectory(attachments);
            File.WriteAllText(Path.Combine(attachments, "a.txt"), "附件");

            return (saves, store, bin, project);
        }

        [Fact]
        public void DeleteMovesEverythingToTrashAndRestorePutsItBack()
        {
            var (saves, store, bin, project) = Seed();
            var memories = new[]
            {
                new MemoryItem { Scope = MemoryScope.File, FileKey = "我的存檔", Content = "這個存檔的記憶" },
            };

            string id = bin.MoveToTrash(project, store.GetAttachmentFolder(project), memories);

            // 原地不留任何東西
            Assert.False(File.Exists(project));
            Assert.False(File.Exists(project + ".bak"));
            Assert.False(Directory.Exists(store.GetAttachmentFolder(project)));

            var entry = Assert.Single(bin.List());
            Assert.Equal("我的存檔", entry.DisplayName);
            Assert.Equal(1, entry.MemoryCount);
            Assert.Equal(RecycleBin.RetentionDays, entry.DaysLeft(DateTime.UtcNow));

            var restored = bin.Restore(id, saves, store.AttachmentsRootDir);

            Assert.True(restored.Ok);
            Assert.True(File.Exists(project));
            Assert.True(File.Exists(project + ".bak"));
            Assert.True(File.Exists(Path.Combine(store.GetAttachmentFolder(project), "a.txt")));
            Assert.Equal("這個存檔的記憶", Assert.Single(restored.Memories).Content);
            Assert.Empty(bin.List());
        }

        [Fact]
        public void RestoreNeverOverwritesASaveWithTheSameName()
        {
            var (saves, store, bin, project) = Seed();
            string id = bin.MoveToTrash(project, store.GetAttachmentFolder(project), null);

            // 刪掉之後又建了同名存檔：復原不能把新的蓋掉。
            File.WriteAllText(project, "{\"new\":true}");

            var restored = bin.Restore(id, saves, store.AttachmentsRootDir);

            Assert.True(restored.Ok);
            Assert.NotEqual(project, restored.RestoredPath);
            Assert.Contains("復原", Path.GetFileNameWithoutExtension(restored.RestoredPath));
            Assert.Equal("{\"new\":true}", File.ReadAllText(project));
        }

        [Fact]
        public void PurgeRemovesItForGood()
        {
            var (saves, store, bin, project) = Seed();
            string id = bin.MoveToTrash(project, store.GetAttachmentFolder(project), null);

            Assert.True(bin.Purge(id));
            Assert.Empty(bin.List());
            Assert.False(bin.Restore(id, saves, store.AttachmentsRootDir).Ok);
        }

        [Fact]
        public void ExpiredItemsArePurgedButFreshOnesSurvive()
        {
            var (saves, store, bin, project) = Seed();
            bin.MoveToTrash(project, store.GetAttachmentFolder(project), null);

            // 還沒到期：不能清
            Assert.Equal(0, bin.PurgeExpired(DateTime.UtcNow.AddDays(RecycleBin.RetentionDays - 1)));
            Assert.Single(bin.List());

            // 滿 7 天：自動清除
            Assert.Equal(1, bin.PurgeExpired(DateTime.UtcNow.AddDays(RecycleBin.RetentionDays)));
            Assert.Empty(bin.List());
        }

        [Fact]
        public void GlobalMemoriesAreNeverTakenWithTheSave()
        {
            string dir = NewSaves();
            var store = new MemoryStore(dir);

            store.Add(new MemoryItem { Scope = MemoryScope.File, FileKey = "我的存檔", Content = "存檔記憶" });
            store.Add(new MemoryItem { Scope = MemoryScope.Project, FileKey = "我的存檔", Content = "專案記憶" });
            store.Add(new MemoryItem { Scope = MemoryScope.Global, FileKey = "我的存檔", Content = "全域偏好" });
            store.Add(new MemoryItem { Scope = MemoryScope.File, FileKey = "另一個存檔", Content = "別人的記憶" });

            var taken = store.TakeByFileKey("我的存檔");

            Assert.Equal(2, taken.Count);
            Assert.DoesNotContain(taken, m => m.Scope == MemoryScope.Global);

            var left = store.GetAll();
            Assert.Contains(left, m => m.Content == "全域偏好");
            Assert.Contains(left, m => m.Content == "別人的記憶");
            Assert.DoesNotContain(left, m => m.Content == "存檔記憶");
        }
    }
}
