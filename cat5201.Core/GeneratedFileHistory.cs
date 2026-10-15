using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 產出檔的版本保險。
    ///
    /// 重生單頁會直接覆蓋同一個 .pptx（路徑不變，chip 與預覽才不會斷），
    /// 但新版不一定比舊版好——覆蓋前先把舊檔收進 _versions，使用者可以一鍵退回。
    ///
    /// 退回之後也要能再往前：使用者退一版看了一眼，發現還是新的好，
    /// 沒有「下一版」的話那份已經生成的內容就永遠拿不回來了，只能再花一次錢重生。
    /// 所以退回時不是把舊版刪掉，而是把「當下這一版」推進 redo 堆疊。
    ///
    /// 每個方向各保留最多 5 個版本。重新產出（SaveVersion）會清掉 redo——
    /// 那時歷史已經分岔，再「往前」會跳到一條不存在的分支上。
    /// </summary>
    public static class GeneratedFileHistory
    {
        private const string FolderName = "_versions";
        private const string RedoFolderName = "_redo";
        private const int MaxVersionsPerFile = 5;

        /// <summary>覆蓋前呼叫：把目前的檔案收成一個版本。失敗不影響主流程（只是沒得退回）。</summary>
        public static void SaveVersion(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                Push(UndoDir(path!), path!);

                // 產生了新內容＝歷史分岔，原本的「下一版」已經不在這條線上了。
                ClearDir(RedoDir(path!), path!);
            }
            catch (Exception ex)
            {
                AppLog.Warn("FileHistory", "保存上一版失敗", ex);
            }
        }

        /// <summary>有沒有可退回的版本。</summary>
        public static bool HasPrevious(string? path) => FindVersions(UndoDir(path), path).Count > 0;

        /// <summary>有沒有可前進的版本（退回之後才會有）。</summary>
        public static bool HasNext(string? path) => FindVersions(RedoDir(path), path).Count > 0;

        /// <summary>
        /// 退回最近一個版本。當下這一版會被推進 redo 堆疊，所以之後還能再往前。
        /// 成功回 true。
        /// </summary>
        public static bool RestorePrevious(string? path) => Step(path, UndoDir(path), RedoDir(path));

        /// <summary>
        /// 前進到退回前的那一版。當下這一版會被推回 undo 堆疊，所以之後還能再退回。
        /// 成功回 true。
        /// </summary>
        public static bool RestoreNext(string? path) => Step(path, RedoDir(path), UndoDir(path));

        /// <summary>
        /// 把 <paramref name="fromDir"/> 最新的一版蓋回原路徑，同時把原路徑現在的內容推進 <paramref name="toDir"/>。
        /// 兩個方向共用同一套動作，差別只在堆疊的方向。
        /// </summary>
        private static bool Step(string? path, string fromDir, string toDir)
        {
            try
            {
                var versions = FindVersions(fromDir, path);
                if (versions.Count == 0)
                    return false;

                // 先把現況存到另一邊，再覆蓋——順序反過來的話現況就沒了。
                if (File.Exists(path!))
                    Push(toDir, path!);

                string latest = versions[0];
                File.Copy(latest, path!, overwrite: true);
                File.Delete(latest);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("FileHistory", "切換版本失敗", ex);
                return false;
            }
        }

        /// <summary>把目前的檔案複製成 <paramref name="dir"/> 裡的一個新版本。</summary>
        private static void Push(string dir, string path)
        {
            Directory.CreateDirectory(dir);

            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);

            // 時間戳只到毫秒，連點兩下有機會撞在一起；撞到就往後加序號，
            // 否則後一次 File.Copy 會把前一版蓋掉（看起來就像少了一版）。
            string target = Path.Combine(dir, $"{name}__{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
            for (int i = 2; File.Exists(target) && i < 100; i++)
                target = Path.Combine(dir, $"{name}__{DateTime.Now:yyyyMMdd_HHmmss_fff}-{i}{ext}");

            File.Copy(path, target, overwrite: true);
            Trim(dir, name, ext);
        }

        private static List<string> FindVersions(string dir, string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return new();

                string name = Path.GetFileNameWithoutExtension(path!);
                string ext = Path.GetExtension(path!);

                return Directory.GetFiles(dir, $"{name}__*{ext}")
                    .OrderByDescending(f => f, StringComparer.Ordinal)   // 檔名帶時間戳，字串排序＝時間排序
                    .ToList();
            }
            catch
            {
                return new();
            }
        }

        private static void ClearDir(string dir, string path)
        {
            foreach (var file in FindVersions(dir, path))
            {
                try { File.Delete(file); } catch { }
            }
        }

        private static void Trim(string dir, string name, string ext)
        {
            var all = Directory.GetFiles(dir, $"{name}__*{ext}")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(MaxVersionsPerFile)
                .ToList();

            foreach (var old in all)
            {
                try { File.Delete(old); } catch { }
            }
        }

        private static string UndoDir(string? path)
            => string.IsNullOrWhiteSpace(path)
                ? ""
                : Path.Combine(Path.GetDirectoryName(path!) ?? "", FolderName);

        private static string RedoDir(string? path)
            => string.IsNullOrWhiteSpace(path)
                ? ""
                : Path.Combine(Path.GetDirectoryName(path!) ?? "", FolderName, RedoFolderName);
    }
}
