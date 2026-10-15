using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cat5201
{
    /// <summary>
    /// 最近刪除（存檔的回收桶）。放在主專案而非 Core：它要處理 MemoryItem，而記憶模型屬於主專案。
    ///
    /// 刪除一個存檔時，該存檔的東西要一起走：專案檔、上一版備份、附件資料夾，
    /// 以及「只屬於這個存檔」的記憶（全域記憶不動——那是跨專案的個人化，刪了等於把使用者的設定也毀了）。
    /// 但不是立刻消失：整包搬進 _trash，7 天內都可以完整復原；使用者自己按「永久刪除」或過了保留期才真的清掉。
    ///
    /// 磁碟佈局：
    ///   &lt;savesDir&gt;\_trash\&lt;id&gt;\manifest.json   刪除時間、原本路徑、顯示名稱、記憶筆數
    ///   &lt;savesDir&gt;\_trash\&lt;id&gt;\project.json    專案檔（含 .bak）
    ///   &lt;savesDir&gt;\_trash\&lt;id&gt;\attachments\    附件資料夾原樣搬進來
    ///   &lt;savesDir&gt;\_trash\&lt;id&gt;\memories.json   被移除的記憶（復原時放回記憶庫）
    /// </summary>
    public sealed class RecycleBin
    {
        /// <summary>保留天數。過了就在下次啟動時自動清掉。</summary>
        public const int RetentionDays = 7;

        private const string ManifestName = "manifest.json";
        private const string ProjectName = "project.json";
        private const string MemoriesName = "memories.json";
        private const string AttachmentsName = "attachments";

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public string TrashDir { get; }

        public RecycleBin(string savesDir)
        {
            TrashDir = Path.Combine(savesDir ?? throw new ArgumentNullException(nameof(savesDir)), "_trash");
        }

        public sealed record Entry(
            string Id,
            string DisplayName,
            string OriginalPath,
            DateTime DeletedAtUtc,
            int MemoryCount)
        {
            /// <summary>還剩幾天會被自動清掉（0＝今天之內）。</summary>
            public int DaysLeft(DateTime nowUtc)
                => Math.Max(0, RetentionDays - (int)(nowUtc - DeletedAtUtc).TotalDays);
        }

        // ===== 丟進最近刪除 =====

        /// <summary>
        /// 把一個存檔整包搬進最近刪除。回傳這筆的 id；任何一步失敗都會拋出，呼叫端顯示給使用者。
        /// 搬移（不是複製）才不會在硬碟上留兩份；記憶由呼叫端先從記憶庫取出並移除後傳進來。
        /// </summary>
        public string MoveToTrash(
            string projectFilePath,
            string? attachmentsFolder,
            IReadOnlyList<MemoryItem>? memories)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath))
                throw new ArgumentException("專案檔路徑不可為空", nameof(projectFilePath));

            string id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N")[..6];
            string dir = Path.Combine(TrashDir, id);
            Directory.CreateDirectory(dir);

            string target = Path.Combine(dir, ProjectName);
            File.Move(projectFilePath, target);

            string bak = projectFilePath + ".bak";
            if (File.Exists(bak))
            {
                try { File.Move(bak, target + ".bak"); }
                catch (Exception ex) { AppLog.Warn("Trash", "備份檔搬移失敗（不影響復原主檔）", ex); }
            }

            if (!string.IsNullOrWhiteSpace(attachmentsFolder) && Directory.Exists(attachmentsFolder))
            {
                try { Directory.Move(attachmentsFolder, Path.Combine(dir, AttachmentsName)); }
                catch (Exception ex)
                {
                    // 附件被其他程式佔用時，複製一份進來，至少復原得回來。
                    AppLog.Warn("Trash", "附件資料夾搬移失敗，改用複製", ex);
                    CopyDirectory(attachmentsFolder, Path.Combine(dir, AttachmentsName));
                }
            }

            var items = memories ?? Array.Empty<MemoryItem>();
            if (items.Count > 0)
                File.WriteAllText(Path.Combine(dir, MemoriesName), JsonSerializer.Serialize(items, Json));

            var entry = new Entry(
                id,
                Path.GetFileNameWithoutExtension(projectFilePath),
                projectFilePath,
                DateTime.UtcNow,
                items.Count);

            File.WriteAllText(Path.Combine(dir, ManifestName), JsonSerializer.Serialize(entry, Json));
            AppLog.Info("Trash", $"已移到最近刪除：{entry.DisplayName}（記憶 {items.Count} 筆，{RetentionDays} 天內可復原）");
            return id;
        }

        // ===== 列出 =====

        /// <summary>最近刪除的內容，新→舊。讀不到 manifest 的資料夾直接略過（不讓一筆壞資料擋住整個清單）。</summary>
        public IReadOnlyList<Entry> List()
        {
            var result = new List<Entry>();
            try
            {
                if (!Directory.Exists(TrashDir)) return result;

                foreach (var dir in Directory.GetDirectories(TrashDir))
                {
                    var entry = ReadManifest(dir);
                    if (entry != null) result.Add(entry);
                }
            }
            catch (Exception ex) { AppLog.Warn("Trash", "列出最近刪除失敗", ex); }

            return result.OrderByDescending(e => e.DeletedAtUtc).ToList();
        }

        private Entry? ReadManifest(string dir)
        {
            try
            {
                string manifest = Path.Combine(dir, ManifestName);
                if (!File.Exists(manifest)) return null;
                return JsonSerializer.Deserialize<Entry>(File.ReadAllText(manifest));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Trash", $"最近刪除項目讀取失敗：{dir}", ex);
                return null;
            }
        }

        // ===== 復原 =====

        public sealed record RestoreResult(bool Ok, string RestoredPath, IReadOnlyList<MemoryItem> Memories, string Error);

        /// <summary>
        /// 把一筆搬回原本的位置。原路徑已經有同名檔案時，自動加上「（復原）」而不是覆蓋——
        /// 使用者刪掉之後又建了同名存檔，不能讓復原把新的蓋掉。
        /// </summary>
        public RestoreResult Restore(string id, string savesDir, string attachmentsRootDir)
        {
            string dir = Path.Combine(TrashDir, id ?? "");
            var entry = ReadManifest(dir);
            if (entry == null)
                return new RestoreResult(false, "", Array.Empty<MemoryItem>(), "找不到這筆最近刪除的資料。");

            try
            {
                string target = entry.OriginalPath;
                if (string.IsNullOrWhiteSpace(target))
                    target = Path.Combine(savesDir, entry.DisplayName + ".json");

                if (File.Exists(target))
                {
                    string stem = Path.GetFileNameWithoutExtension(target);
                    string folder = Path.GetDirectoryName(target) ?? savesDir;
                    int n = 1;
                    do { target = Path.Combine(folder, $"{stem}（復原{(n == 1 ? "" : n.ToString())}）.json"); n++; }
                    while (File.Exists(target));
                }

                File.Move(Path.Combine(dir, ProjectName), target);

                string bak = Path.Combine(dir, ProjectName + ".bak");
                if (File.Exists(bak))
                {
                    try { File.Move(bak, target + ".bak"); }
                    catch (Exception ex) { AppLog.Warn("Trash", "備份檔復原失敗（主檔已復原）", ex); }
                }

                string attachments = Path.Combine(dir, AttachmentsName);
                if (Directory.Exists(attachments))
                {
                    Directory.CreateDirectory(attachmentsRootDir);
                    string attachmentTarget = Path.Combine(attachmentsRootDir, Path.GetFileNameWithoutExtension(target));
                    if (!Directory.Exists(attachmentTarget))
                        Directory.Move(attachments, attachmentTarget);
                    else
                        CopyDirectory(attachments, attachmentTarget);
                }

                var memories = ReadMemories(dir);

                TryDeleteDirectory(dir);
                AppLog.Info("Trash", $"已復原：{Path.GetFileNameWithoutExtension(target)}（記憶 {memories.Count} 筆）");
                return new RestoreResult(true, target, memories, "");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Trash", "復原失敗", ex);
                return new RestoreResult(false, "", Array.Empty<MemoryItem>(), ex.Message);
            }
        }

        private static IReadOnlyList<MemoryItem> ReadMemories(string dir)
        {
            try
            {
                string path = Path.Combine(dir, MemoriesName);
                if (!File.Exists(path)) return Array.Empty<MemoryItem>();
                return JsonSerializer.Deserialize<List<MemoryItem>>(File.ReadAllText(path)) ?? new List<MemoryItem>();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Trash", "記憶復原失敗（專案檔仍會復原）", ex);
                return Array.Empty<MemoryItem>();
            }
        }

        // ===== 永久刪除 =====

        /// <summary>從最近刪除永久移除一筆。</summary>
        public bool Purge(string id)
        {
            string dir = Path.Combine(TrashDir, id ?? "");
            if (!Directory.Exists(dir)) return false;

            var entry = ReadManifest(dir);
            bool ok = TryDeleteDirectory(dir);
            if (ok) AppLog.Info("Trash", $"已永久刪除：{entry?.DisplayName ?? id}");
            return ok;
        }

        /// <summary>清掉超過保留期的項目（啟動時呼叫）。回傳清掉幾筆。</summary>
        public int PurgeExpired(DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            int removed = 0;

            foreach (var entry in List())
            {
                if ((now - entry.DeletedAtUtc).TotalDays < RetentionDays) continue;
                if (Purge(entry.Id)) removed++;
            }

            if (removed > 0) AppLog.Info("Trash", $"已自動清除超過 {RetentionDays} 天的最近刪除項目：{removed} 筆");
            return removed;
        }

        // ===== 工具 =====

        private static bool TryDeleteDirectory(string dir)
        {
            try { Directory.Delete(dir, recursive: true); return true; }
            catch (Exception ex) { AppLog.Warn("Trash", $"刪除資料夾失敗：{dir}", ex); return false; }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            foreach (var sub in Directory.GetDirectories(source))
                CopyDirectory(sub, Path.Combine(target, Path.GetFileName(sub)));
        }
    }
}
