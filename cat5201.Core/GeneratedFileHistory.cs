using System;
using System.IO;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 產出檔的「上一版」保險。
    ///
    /// 重生單頁會直接覆蓋同一個 .pptx（路徑不變，chip 與預覽才不會斷），
    /// 但新版不一定比舊版好——覆蓋前先把舊檔收進 _versions，使用者可以一鍵退回。
    /// 每個檔案最多保留 5 個版本，退回一次就吃掉一個版本（可連續退回更早的版本）。
    /// </summary>
    public static class GeneratedFileHistory
    {
        private const string FolderName = "_versions";
        private const int MaxVersionsPerFile = 5;

        /// <summary>覆蓋前呼叫：把目前的檔案收成一個版本。失敗不影響主流程（只是沒得退回）。</summary>
        public static void SaveVersion(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                string dir = VersionDir(path!);
                Directory.CreateDirectory(dir);

                string name = Path.GetFileNameWithoutExtension(path!);
                string ext = Path.GetExtension(path!);
                string target = Path.Combine(dir, $"{name}__{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");

                File.Copy(path!, target, overwrite: true);
                Trim(dir, name, ext);
            }
            catch (Exception ex)
            {
                AppLog.Warn("FileHistory", "保存上一版失敗", ex);
            }
        }

        /// <summary>有沒有可退回的版本。</summary>
        public static bool HasPrevious(string? path) => FindVersions(path).Count > 0;

        /// <summary>
        /// 退回最近一個版本：把它蓋回原路徑並移除該版本（再按一次就退到更早的版本）。
        /// 成功回 true。
        /// </summary>
        public static bool RestorePrevious(string? path)
        {
            try
            {
                var versions = FindVersions(path);
                if (versions.Count == 0)
                    return false;

                string latest = versions[0];
                File.Copy(latest, path!, overwrite: true);
                File.Delete(latest);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("FileHistory", "退回上一版失敗", ex);
                return false;
            }
        }

        private static System.Collections.Generic.List<string> FindVersions(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return new();

                string dir = VersionDir(path!);
                if (!Directory.Exists(dir))
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

        private static string VersionDir(string path)
            => Path.Combine(Path.GetDirectoryName(path) ?? "", FolderName);
    }
}
