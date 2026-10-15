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
