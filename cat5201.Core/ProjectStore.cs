using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cat5201
{
    /// <summary>
    /// 專案檔持久化(第 1 刀,拆上帝物件):存/讀/列舉/刪除/改名/附件資料夾的**純 I/O**,
    /// 自 MainWindow 抽出。畫布狀態的收集與重建仍屬 UI(MainWindow),這裡不碰任何 WPF。
    ///
    /// 磁碟佈局(沿用既有,零遷移):
    ///   &lt;savesDir&gt;\*.json                  專案檔(AtomicFile 原子寫入,舊檔留 .bak)
    ///   &lt;savesDir&gt;\_attachments\&lt;專案名&gt;\  該專案的附件資料夾
    /// </summary>
    public sealed class ProjectStore
    {
        // 目前寫出的專案檔格式版本。演進規則:只加欄位不改義=不用升版;欄位改義/移除=升版並在載入處分流。
        public const int CurrentSchemaVersion = 1;

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public string SavesDir { get; }
        public string AttachmentsRootDir { get; }

        public ProjectStore(string savesDir)
        {
            SavesDir = savesDir ?? throw new ArgumentNullException(nameof(savesDir));
            AttachmentsRootDir = Path.Combine(savesDir, "_attachments");
        }

        // ===== 路徑 =====

        public static string DisplayNameFromPath(string path)
            => Path.GetFileNameWithoutExtension(path);

        public string NewProjectPath()
            => Path.Combine(SavesDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");

        public string GetAttachmentFolder(string projectFilePath)
            => Path.Combine(AttachmentsRootDir, DisplayNameFromPath(projectFilePath));

        /// <summary>SavesDir 根目錄的專案檔(非遞迴;_config/_memory 等子資料夾自然不入列)。新→舊。</summary>
        public IReadOnlyList<string> ListProjectFiles()
        {
            try
            {
                if (!Directory.Exists(SavesDir))
                    return Array.Empty<string>();

                return Directory.GetFiles(SavesDir, "*.json")
                    .OrderByDescending(File.GetLastWriteTime)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Project", "列舉專案檔失敗", ex);
                return Array.Empty<string>();
            }
        }

        // ===== 存 / 讀 =====

        public void Save(AppState state, string path)
        {
            var json = JsonSerializer.Serialize(state, WriteOptions);
            AtomicFile.WriteAllText(path, json); // 原子寫入:專案檔(使用者的畫布)絕不因崩潰毀損
        }

        /// <summary>
        /// 讀專案檔;主檔「讀不到或 JSON 解析失敗」都退 .bak(AtomicFile 每次存檔留的上一版)。
        /// 注意不能只用 AtomicFile.ReadAllTextWithFallback——它只救 IO 失敗,救不了
        /// 「檔案讀得到但內容毀損(半寫入/亂碼)」這種最常見的毀損型態(ProjectStoreTests 釘著)。
        /// </summary>
        public AppState? Load(string path)
        {
            var fromMain = TryDeserialize(path);
            if (fromMain != null)
                return fromMain;

            var fromBak = TryDeserialize(path + ".bak");
            if (fromBak != null)
            {
                AppLog.Warn("Project", $"專案主檔毀損,已改用 .bak 上一版備份:{path}");
                return fromBak;
            }

            return null;
        }

        private static AppState? TryDeserialize(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Project", $"專案檔解析失敗:{path}", ex);
                return null;
            }
        }

        // ===== 刪除 =====

        /// <summary>刪專案檔+其 .bak+附件資料夾。檔案刪除失敗會拋出(呼叫端給使用者看);附件資料夾佔用只記 log。</summary>
        public void Delete(string projectFilePath)
        {
            File.Delete(projectFilePath);
            TryDeleteSidecarBak(projectFilePath);

            var folder = GetAttachmentFolder(projectFilePath);
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception ex) { AppLog.Warn("Project", "刪除專案附件資料夾失敗(可能有檔案被占用)", ex); }
            }
        }

        // ===== 改名 =====

        public sealed record RenameResult(bool Ok, string NewPath, string Error);

        /// <summary>
        /// 專案改名的完整磁碟事務:主檔 Move + .bak 跟著搬 + 附件資料夾安全搬移(失敗回滾主檔)
        /// + 專案 JSON 內附件相對路徑改寫 + 檔名鎖定旗標。newName 需已正規化且不含副檔名。
        /// </summary>
        public RenameResult Rename(string oldPath, string newName)
        {
            var newPath = Path.Combine(SavesDir, newName + ".json");

            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                return new RenameResult(false, oldPath, ""); // 同名視為無事(呼叫端不顯示錯誤)

            if (File.Exists(newPath))
                return new RenameResult(false, oldPath, "已存在同名檔案,請換一個名稱。");

            var oldBaseName = DisplayNameFromPath(oldPath);
            var newBaseName = DisplayNameFromPath(newPath);

            var moved = MoveProject(oldPath, newPath);
            if (!moved.Ok)
                return new RenameResult(false, oldPath, $"附件資料夾無法同步搬移。\n{moved.Error}");

            RewriteAttachmentRelativePathsOnDisk(newPath, oldBaseName, newBaseName);
            MarkFileNameLockedOnDisk(newPath);

            return new RenameResult(true, newPath, "");
        }

        /// <summary>
        /// 低階搬移:主檔 Move + .bak 跟著搬 + 附件資料夾安全搬移(失敗回滾主檔)。
        /// 不改寫 JSON、不鎖檔名——自動關鍵字命名流程用(鎖了自動命名就再也不會跑)。
        /// </summary>
        public RenameResult MoveProject(string oldPath, string newPath)
        {
            File.Move(oldPath, newPath);
            TryMoveSidecarBak(oldPath, newPath); // 舊名的 .json.bak 跟著搬,否則留在原名下變孤兒

            if (!MoveAttachmentFolderSafely(GetAttachmentFolder(oldPath), GetAttachmentFolder(newPath), out var folderMoveError))
            {
                try
                {
                    if (File.Exists(newPath) && !File.Exists(oldPath))
                    {
                        File.Move(newPath, oldPath);
                        TryMoveSidecarBak(newPath, oldPath);
                    }
                }
                catch (Exception ex) { AppLog.Warn("Project", "搬移失敗後回滾檔名也失敗(檔名可能不一致)", ex); }

                return new RenameResult(false, oldPath, folderMoveError);
            }

            return new RenameResult(true, newPath, "");
        }

        /// <summary>把「使用者鎖定檔名」旗標寫進專案 JSON(改名後自動關鍵字命名不再覆蓋)。</summary>
        public void MarkFileNameLockedOnDisk(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(filePath));
                if (state == null || state.FileNameLocked) return;

                Save(state with { FileNameLocked = true }, filePath);
            }
            catch (Exception ex) { AppLog.Warn("Project", "寫入檔名鎖定旗標失敗", ex); }
        }

        // ===== 附件相對路徑 =====

        /// <summary>附件相對路徑的基底資料夾更名(專案改名時同步):「舊名\檔案」→「新名\檔案」。</summary>
        public static string ReplaceAttachmentRelativeBase(string relativePath, string oldBaseName, string newBaseName)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return relativePath ?? "";

            var normalized = relativePath.Replace('/', '\\');
            var oldPrefix = oldBaseName + "\\";

            if (normalized.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return newBaseName + normalized.Substring(oldBaseName.Length);
            }

            var fileName = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(fileName))
                return Path.Combine(newBaseName, normalized);

            return Path.Combine(newBaseName, fileName);
        }

        private void RewriteAttachmentRelativePathsOnDisk(string filePath, string oldBaseName, string newBaseName)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(filePath));
                if (state == null) return;

                var newAttachments = (state.Attachments ?? new List<AttachmentState>())
                    .Select(a => new AttachmentState(
                        a.NodeId,
                        a.FileName,
                        ReplaceAttachmentRelativeBase(a.RelativePath, oldBaseName, newBaseName),
                        a.MimeType,
                        a.Kind))
                    .ToList();

                Save(state with { Attachments = newAttachments }, filePath);
            }
            catch (Exception ex) { AppLog.Warn("Project", "改名後改寫專案附件路徑失敗", ex); }
        }

        // ===== 內部:附件資料夾搬移 / .bak sidecar =====

        private static bool MoveAttachmentFolderSafely(string oldFolder, string newFolder, out string errorMessage)
        {
            errorMessage = "";

            try
            {
                if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!Directory.Exists(oldFolder))
                    return true;

                if (!Directory.Exists(newFolder))
                {
                    Directory.Move(oldFolder, newFolder);
                    return true;
                }

                Directory.CreateDirectory(newFolder);

                foreach (var srcFile in Directory.GetFiles(oldFolder))
                {
                    var fileName = Path.GetFileName(srcFile);
                    var destFile = Path.Combine(newFolder, fileName);

                    if (File.Exists(destFile))
                    {
                        var uniqueName = $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid():N}{Path.GetExtension(fileName)}";
                        destFile = Path.Combine(newFolder, uniqueName);
                    }

                    File.Move(srcFile, destFile);
                }

                foreach (var srcDir in Directory.GetDirectories(oldFolder))
                {
                    var dirName = Path.GetFileName(srcDir);
                    var destDir = Path.Combine(newFolder, dirName);

                    if (Directory.Exists(destDir))
                    {
                        destDir = Path.Combine(newFolder, $"{dirName}_{Guid.NewGuid():N}");
                    }

                    Directory.Move(srcDir, destDir);
                }

                if (!Directory.EnumerateFileSystemEntries(oldFolder).Any())
                {
                    Directory.Delete(oldFolder, recursive: false);
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        // AtomicFile 原子寫入會把舊內容留成同名 .bak(斷電保護)。專案主檔刪除/改名時,
        // 這個 .bak 不處理就成了不出現在清單、只在檔案總管現形的孤兒。
        private static void TryDeleteSidecarBak(string jsonPath)
        {
            try
            {
                var bak = jsonPath + ".bak";
                if (File.Exists(bak))
                    File.Delete(bak);
            }
            catch (Exception ex) { AppLog.Warn("Project", "清除 .bak 備份失敗", ex); }
        }

        private static void TryMoveSidecarBak(string oldJsonPath, string newJsonPath)
        {
            try
            {
                var oldBak = oldJsonPath + ".bak";
                if (!File.Exists(oldBak))
                    return;

                var newBak = newJsonPath + ".bak";
                if (File.Exists(newBak))
                    File.Delete(newBak);

                File.Move(oldBak, newBak);
            }
            catch (Exception ex) { AppLog.Warn("Project", "搬移 .bak 備份失敗", ex); }
        }
    }
}
