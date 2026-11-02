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

        /// <summary>簡報與它的對照 PDF 一起切版的結果。StaleCompanions＝沒跟上、仍停在原本那一版的檔案。</summary>
        public sealed record StepResult(bool Ok, IReadOnlyList<string> RestoredCompanions, IReadOnlyList<string> StaleCompanions);

        /// <summary>
        /// 簡報切到上一版／下一版，對照 PDF 跟著切；PDF 切不動時解除配對，而不是假裝兩者一致。
        ///
        /// 2026-11-02 第三方審查（GPT）以暫存檔重現：簡報有上一版、PDF 沒有。
        /// 簡報還原成功、PDF 還原回 false，但呼叫端不看結果，照樣把兩者重新登記成配對——
        /// 雜湊對得上，預覽便顯示 PDF：「簡報是 v1、預覽是 v2」。
        /// 這裡只把真的切過去的 PDF 登記回去；一份都沒有就登記成「無對照 PDF」，預覽改用簡報本身。
        /// </summary>
        /// <param name="companions">切版「之前」先問出來的對照 PDF（切完 pptx 雜湊就變了，問不出來）。</param>
        public static StepResult StepWithCompanions(string path, IReadOnlyList<string> companions, bool forward)
        {
            bool ok = forward ? RestoreNext(path) : RestorePrevious(path);
            if (!ok)
                return new StepResult(false, Array.Empty<string>(), Array.Empty<string>());

            var restored = new List<string>();
            var stale = new List<string>();
            foreach (var companion in companions ?? Array.Empty<string>())
            {
                bool moved = forward ? RestoreNext(companion) : RestorePrevious(companion);
                (moved ? restored : stale).Add(companion);
            }

            // null＝解除配對：FindCompanionPdf 之後回 null，預覽不會再拿那份沒跟上的 PDF 出來。
            GeneratedFileCompanion.Register(path, restored.FirstOrDefault());

            if (stale.Count > 0)
                AppLog.Warn("FileHistory",
                    $"{Path.GetFileName(path)} 已切版，但 {stale.Count} 份對照 PDF 沒有對應的版本，已解除配對");

            return new StepResult(true, restored, stale);
        }

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
