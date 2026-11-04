using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 產出檔案按「哪個聊天做的」分資料夾。
    ///
    /// 所有存檔共用一個 _generated 時，幾十份簡報報告混在一起，光看檔名分不出哪一份是哪個專案的。
    /// 分開之後「這個聊天產了什麼」一眼就看得到。
    ///
    /// 認領不到主人的（存檔已經刪了、或是舊版留下來的）集中到 other，不要丟掉——
    /// 那些是使用者花錢跑出來的東西，刪聊天不該連帶清掉成品。
    /// </summary>
    public static class GeneratedFileOrganizer
    {
        /// <summary>沒有主人的產出檔案放這裡。</summary>
        public const string OtherFolder = "other";

        /// <summary>存檔名拿來當資料夾名：去掉檔名不能用的字元，太長就截短。</summary>
        public static string SafeFolderName(string? name)
        {
            string t = (name ?? "").Trim();
            if (t.Length == 0) return "";

            foreach (char c in Path.GetInvalidFileNameChars())
                t = t.Replace(c, '_');

            t = t.Trim('.', ' ');
            return t.Length <= 60 ? t : t.Substring(0, 60);
        }

        /// <summary>
        /// 把 root 底下散落的檔案搬進各自的存檔資料夾。
        /// </summary>
        /// <param name="root">_generated 的路徑。</param>
        /// <param name="ownerByFileName">檔名（不含路徑）→ 存檔顯示名稱。</param>
        /// <returns>搬動了幾個檔案。</returns>
        public static int Organize(string root, IReadOnlyDictionary<string, string> ownerByFileName)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return 0;

            int moved = 0;

            foreach (string file in Directory.GetFiles(root))
            {
                string name = Path.GetFileName(file);

                // 側車檔（.preview.json）跟著它的本尊走，不要自己找主人。
                string ownerKey = name.EndsWith(".preview.json", StringComparison.OrdinalIgnoreCase)
                    ? name[..^".preview.json".Length]
                    : name;

                string folder = ownerByFileName.TryGetValue(ownerKey, out var save) && SafeFolderName(save).Length > 0
                    ? SafeFolderName(save)
                    : OtherFolder;

                if (MoveInto(file, Path.Combine(root, folder)))
                    moved++;
            }

            return moved;
        }

        /// <summary>
        /// 某個存檔被刪了：把它的產出檔案集中到 other，資料夾留空就移除。
        /// 檔案本身不刪——使用者刪的是聊天，不是成品。
        /// </summary>
        public static int MoveToOther(string root, string? saveName)
        {
            string folder = SafeFolderName(saveName);
            if (string.IsNullOrWhiteSpace(root) || folder.Length == 0)
                return 0;

            string source = Path.Combine(root, folder);
            if (!Directory.Exists(source) || string.Equals(folder, OtherFolder, StringComparison.OrdinalIgnoreCase))
                return 0;

            int moved = 0;
            foreach (string file in Directory.GetFiles(source))
                if (MoveInto(file, Path.Combine(root, OtherFolder)))
                    moved++;

            try
            {
                if (!Directory.EnumerateFileSystemEntries(source).Any())
                    Directory.Delete(source);
            }
            catch { /* 刪不掉空資料夾不是問題 */ }

            if (moved > 0)
                AppLog.Info("Output", $"「{saveName}」已刪除，{moved} 個產出檔案移到 {OtherFolder}");

            return moved;
        }

        /// <summary>
        /// 存檔改名（手動或自動命名）：產出資料夾跟著改名，回傳每個搬到的檔案「舊完整路徑 → 新完整路徑」，
        /// 呼叫端用它把節點與存檔裡記的路徑改到新位置。
        ///
        /// 2026-11-04：原本改名只搬存檔與附件資料夾，產出資料夾留在舊名稱——
        /// 同一個聊天的檔案分散在兩個資料夾，新名稱的資料夾要等下一次產出才會出現。
        ///
        /// 新名稱的資料夾不存在就整個搬；已存在（例如改回以前用過的名稱）就逐檔合併，同名加序號，絕不覆蓋。
        /// 搬不動的檔案（例如正在 Word 裡開著）留在原處並記錄——預覽仍可用檔名在根目錄底下找到它（ResolveGeneratedFile）。
        /// 新名稱是 other 或底線開頭的系統資料夾時不搬，免得把聊天的檔案混進去。
        /// </summary>
        public static IReadOnlyDictionary<string, string> RenameSaveFolder(string root, string? oldSaveName, string? newSaveName)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string oldFolder = SafeFolderName(oldSaveName), newFolder = SafeFolderName(newSaveName);
            if (string.IsNullOrWhiteSpace(root) || oldFolder.Length == 0 || newFolder.Length == 0 ||
                string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
                return map;
            if (IsSystemFolder(oldFolder) || IsSystemFolder(newFolder))
            {
                AppLog.Info("Output", $"產出資料夾沒有跟著改名：「{newFolder}」是系統資料夾的名稱");
                return map;
            }

            string source = Path.GetFullPath(Path.Combine(root, oldFolder));
            string target = Path.GetFullPath(Path.Combine(root, newFolder));
            if (!Directory.Exists(source))
                return map;

            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);

            if (!Directory.Exists(target) && !File.Exists(target))
            {
                try
                {
                    Directory.Move(source, target);
                    foreach (var f in files)
                        map[f] = Path.Combine(target, Path.GetRelativePath(source, f));
                    AppLog.Info("Output", $"產出資料夾跟著改名：{oldFolder} → {newFolder}（{files.Length} 個檔案）");
                    return map;
                }
                catch (Exception ex)
                {
                    // 整個資料夾搬不動（多半是裡面有檔案開著）→ 改成逐檔搬，能搬的先搬
                    AppLog.Warn("Output", $"產出資料夾無法整個改名，改成逐檔搬：{oldFolder} → {newFolder}", ex);
                }
            }

            foreach (var f in files)
            {
                string relDir = Path.GetDirectoryName(Path.GetRelativePath(source, f)) ?? "";
                string? moved = MoveIntoReturningPath(f, Path.Combine(target, relDir));
                if (moved != null) map[f] = moved;
            }
            RemoveEmptyDirs(source, stopAt: Path.GetFullPath(root));
            AppLog.Info("Output", $"產出資料夾合併到新名稱：{oldFolder} → {newFolder}（{map.Count}/{files.Length} 個檔案）");
            return map;
        }

        /// <summary>一個存檔與它記的產出路徑（OutputFilePaths、OutputImagePath）。</summary>
        public sealed record SaveOutputs(string SaveName, IReadOnlyList<string> Paths);

        /// <summary>對齊的結果：哪個存檔的哪個資料夾搬到哪裡，以及新舊路徑對照（呼叫端用來改寫那個存檔的 JSON）。</summary>
        public sealed record AlignResult(string SaveName, string FromFolder, string ToFolder, IReadOnlyDictionary<string, string> Map);

        /// <summary>
        /// 啟動時的一次性整理：每個存檔的產出資料夾名稱對齊目前的存檔名稱，並收掉完全空的頂層資料夾。
        ///
        /// 2026-11-04：改名不搬產出資料夾的問題修好之前，已經改過名的聊天，檔案還在舊名稱的資料夾
        /// （例如「Review Agent test 3」的檔案在「作業完成事項」），另外留下好幾個點進去是空的資料夾。
        ///
        /// 只搬「確定是這個存檔自己的」資料夾：
        ///   - 被兩個以上的存檔引用的資料夾不搬（共用的，搬給誰都不對）；
        ///   - 名稱就是另一個現存存檔的名稱的資料夾不搬（那是別人的）；
        ///   - other 與底線開頭的系統資料夾不搬。
        /// 搬移規則同 RenameSaveFolder：已存在就逐檔合併、同名加序號，絕不覆蓋。
        /// 已經對齊的存檔什麼都不做，所以每次啟動都跑也沒關係。
        /// </summary>
        public static IReadOnlyList<AlignResult> AlignSaveFolders(string root, IReadOnlyList<SaveOutputs> saves)
        {
            var results = new List<AlignResult>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || saves == null)
                return results;

            string rootFull = Path.GetFullPath(root);
            var ownName = saves.Select(s => SafeFolderName(s.SaveName)).Where(n => n.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 每個頂層資料夾被哪些存檔引用
            var referencedBy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in saves)
                foreach (var folder in s.Paths.Select(p => TopFolder(rootFull, p)).Where(f => f != null).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!referencedBy.TryGetValue(folder!, out var set))
                        referencedBy[folder!] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(s.SaveName);
                }

            foreach (var s in saves)
            {
                string expected = SafeFolderName(s.SaveName);
                if (expected.Length == 0 || IsSystemFolder(expected)) continue;

                foreach (var folder in s.Paths.Select(p => TopFolder(rootFull, p)).Where(f => f != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                {
                    if (string.Equals(folder, expected, StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsSystemFolder(folder!)) continue;
                    if (referencedBy[folder!].Count > 1) continue;
                    if (ownName.Contains(folder!)) continue;
                    if (!Directory.Exists(Path.Combine(rootFull, folder!))) continue;

                    var map = RenameSaveFolder(rootFull, folder, s.SaveName);
                    if (map.Count > 0)
                        results.Add(new AlignResult(s.SaveName, folder!, expected, map));
                }
            }

            // 完全空的頂層資料夾（裡面一個檔案都沒有）收掉
            foreach (var dir in Directory.GetDirectories(rootFull))
            {
                try
                {
                    string name = Path.GetFileName(dir);
                    if (IsSystemFolder(name)) continue;
                    if (!Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
                    {
                        Directory.Delete(dir, recursive: true);
                        AppLog.Info("Output", $"收掉空的產出資料夾：{name}");
                    }
                }
                catch (Exception ex) { AppLog.Warn("Output", $"收掉空的產出資料夾失敗：{dir}", ex); }
            }

            return results;
        }

        /// <summary>路徑在 root 底下的第一層資料夾名稱；直接放在 root 或不在 root 底下回 null。</summary>
        private static string? TopFolder(string rootFull, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string rel = Path.GetRelativePath(rootFull, Path.GetFullPath(path));
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
                int sep = rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                return sep <= 0 ? null : rel[..sep];
            }
            catch { return null; }
        }

        /// <summary>依 RenameSaveFolder 的對照改路徑；不在對照裡的照原樣。</summary>
        public static string Remap(string? path, IReadOnlyDictionary<string, string> map)
        {
            if (string.IsNullOrWhiteSpace(path) || map.Count == 0) return path ?? "";
            try { return map.TryGetValue(Path.GetFullPath(path), out var to) ? to : path; }
            catch { return path; }
        }

        /// <summary>
        /// 從 dir 往上收掉空資料夾，收到 stopAt（不含）為止。
        /// 重新執行刪掉上一輪的產出、或改名搬走檔案之後用，不留下空殼資料夾。
        /// stopAt 外面的資料夾一律不碰。
        /// </summary>
        public static void RemoveEmptyDirs(string? dir, string stopAt)
        {
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stopAt)) return;
            try
            {
                string root = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
                var current = new DirectoryInfo(Path.GetFullPath(dir));

                // 先收子資料夾（例如搬完檔案後留下的空子資料夾）
                if (current.Exists && current.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    foreach (var sub in current.GetDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length))
                        if (!sub.EnumerateFileSystemInfos().Any()) sub.Delete();

                while (current != null &&
                       current.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                       current.Exists && !current.EnumerateFileSystemInfos().Any())
                {
                    var parent = current.Parent;
                    current.Delete();
                    current = parent;
                }
            }
            catch (Exception ex) { AppLog.Warn("Output", $"清理空資料夾失敗：{dir}", ex); }
        }

        private static bool IsSystemFolder(string folder) =>
            string.Equals(folder, OtherFolder, StringComparison.OrdinalIgnoreCase) || folder.StartsWith("_", StringComparison.Ordinal);

        /// <summary>搬一個檔案進目標資料夾；同名時加序號，絕不覆蓋。成功回傳新路徑，失敗回 null。</summary>
        private static string? MoveIntoReturningPath(string file, string targetDir)
        {
            try
            {
                Directory.CreateDirectory(targetDir);

                string name = Path.GetFileName(file);
                string target = Path.Combine(targetDir, name);

                for (int i = 2; File.Exists(target) && i < 100; i++)
                    target = Path.Combine(targetDir, $"{Path.GetFileNameWithoutExtension(name)}-{i}{Path.GetExtension(name)}");

                if (File.Exists(target))
                    return null;

                File.Move(file, target);
                return Path.GetFullPath(target);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Output", $"搬動 {Path.GetFileName(file)} 失敗（留在原處）", ex);
                return null;
            }
        }

        /// <summary>搬一個檔案進目標資料夾；同名時加序號，絕不覆蓋。</summary>
        private static bool MoveInto(string file, string targetDir)
        {
            try
            {
                Directory.CreateDirectory(targetDir);

                string name = Path.GetFileName(file);
                string target = Path.Combine(targetDir, name);

                for (int i = 2; File.Exists(target) && i < 100; i++)
                {
                    string stem = Path.GetFileNameWithoutExtension(name);
                    string ext = Path.GetExtension(name);
                    target = Path.Combine(targetDir, $"{stem}-{i}{ext}");
                }

                if (File.Exists(target))
                    return false;

                File.Move(file, target);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Output", $"搬動 {Path.GetFileName(file)} 失敗", ex);
                return false;
            }
        }
    }
}
