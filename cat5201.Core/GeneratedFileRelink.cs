using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 產出檔案不在記錄的位置時，決定要不要改連到某個同名檔。
    ///
    /// 原本是「在整個產出根目錄用檔名找，取第一個」。甲、乙兩個專案都有「分析報告.docx」、
    /// 甲的原檔被移走時，重新開啟甲就會連到乙的報告；而那個路徑會加進甲的節點，
    /// 甲一重跑就把乙的檔案刪掉。
    ///
    /// 現在只在能確定歸屬時才改連，按順序：
    /// 1. 相對位置完全相同（例如使用者把整個產出資料夾換了地方）——只有一個符合才算。
    /// 2. 在這個專案自己的資料夾裡——只有一個符合才算。
    /// 3. 在無主的 other 裡（被刪的存檔從最近刪除復原時，它的檔案在這裡），而且整個根目錄只有這一個同名檔。
    /// 其他情況一律不連，寧可讓使用者看到「找不到」，也不要拿到別人的檔案。
    /// </summary>
    public static class GeneratedFileRelink
    {
        public static string? Pick(string recordedPath, string root, string? projectDir, IReadOnlyList<string> candidates)
        {
            if (candidates == null || candidates.Count == 0 || string.IsNullOrWhiteSpace(root))
                return null;

            string fullRoot = Normalize(root);
            var inRoot = candidates
                .Select(Normalize)
                .Where(c => IsUnder(c, fullRoot))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (inRoot.Count == 0)
                return null;

            // 1. 相對路徑一致
            string recorded = Normalize(recordedPath);
            var sameShape = inRoot
                .Where(c => recorded.EndsWith(Path.DirectorySeparatorChar + Relative(c, fullRoot), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sameShape.Count == 1)
                return sameShape[0];

            // 2. 在這個專案自己的資料夾裡
            if (!string.IsNullOrWhiteSpace(projectDir))
            {
                string fullProject = Normalize(projectDir!);
                if (!string.Equals(fullProject, fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var mine = inRoot.Where(c => IsUnder(c, fullProject)).ToList();
                    if (mine.Count == 1)
                        return mine[0];
                    if (mine.Count > 1)
                        return null;
                }
            }

            // 3. 無主的 other，而且全根目錄只有這一個
            string other = Path.Combine(fullRoot, GeneratedFileOrganizer.OtherFolder);
            if (inRoot.Count == 1 && IsUnder(inRoot[0], other))
                return inRoot[0];

            return null;
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        private static bool IsUnder(string path, string dir)
            => path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static string Relative(string path, string root)
            => path.Substring(root.Length + 1);
    }
}
