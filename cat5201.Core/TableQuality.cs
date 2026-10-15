using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 表格的最低標準：一張每一格都寫著「資料不足」的比較表，不是比較表。
    ///
    /// 2026-10-15 實測產出的「2026 年台灣四大便利商店比較表」：四個品牌 × 四個欄位，
    /// 除了品牌名以外**每一格**都是「資料不足」或「未取得」。使用者的原話是「在跟我開玩笑嗎」。
    ///
    /// 「查不到就寫未取得、不要編數字」是對的規則，但它只管單一格子。
    /// 整欄都查不到時，那一欄存在的唯一效果就是把空白排得很整齊；
    /// 整張表都查不到時，交出一張空表比誠實地說「查不到」更糟。
    /// </summary>
    public static class TableQuality
    {
        /// <summary>代表「這一格沒有資料」的寫法。</summary>
        private static readonly string[] MissingMarkers =
        {
            "未取得", "資料不足", "未公開", "無資料", "尚無資料", "查無", "不適用",
            "n/a", "na", "-", "—", "–", "－", "?", "／", "/",
        };

        public static bool IsMissing(string? cell)
        {
            string v = (cell ?? "").Trim().Trim('*', '`', ' ');
            if (v.Length == 0) return true;

            string lower = v.ToLowerInvariant();
            return MissingMarkers.Any(m => string.Equals(lower, m, StringComparison.Ordinal))
                   || MissingMarkers.Any(m => m.Length >= 3 && lower.Contains(m, StringComparison.Ordinal));
        }

        /// <summary>
        /// 把「整欄都沒有資料」的欄位拿掉。第一欄（項目名稱）永遠保留——
        /// 沒有它就不知道每一列在講誰。
        /// </summary>
        public static string DropAllMissingColumns(string? markdown)
        {
            var rows = Parse(markdown, out string before, out string after);
            if (rows.Count < 2)
                return markdown ?? "";

            int cols = rows.Max(r => r.Count);
            var keep = new List<int> { 0 };

            for (int c = 1; c < cols; c++)
            {
                bool anyData = rows.Skip(1).Any(r => c < r.Count && !IsMissing(r[c]));
                if (anyData) keep.Add(c);
            }

            if (keep.Count == cols)
                return markdown ?? "";

            var rebuilt = rows
                .Select(r => keep.Select(c => c < r.Count ? r[c] : "").ToList())
                .ToList();

            return Compose(before, rebuilt, after);
        }

        /// <summary>
        /// 這張表撐不撐得起來：除了名稱欄以外，有沒有任何一欄真的有資料。
        /// </summary>
        public static bool HasUsableData(string? markdown)
        {
            var rows = Parse(markdown, out _, out _);
            if (rows.Count < 2)
                return false;

            int cols = rows.Max(r => r.Count);
            for (int c = 1; c < cols; c++)
                if (rows.Skip(1).Any(r => c < r.Count && !IsMissing(r[c])))
                    return true;

            return false;
        }

        // ── Markdown 表格的最小解析：只動表格本體，前後文字原樣保留。

        private static List<List<string>> Parse(string? markdown, out string before, out string after)
        {
            before = "";
            after = "";
            var rows = new List<List<string>>();
            if (string.IsNullOrWhiteSpace(markdown))
                return rows;

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            int start = -1, end = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                bool isRow = lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal);
                if (isRow && start < 0) start = i;
                if (isRow) end = i;
            }

            if (start < 0)
                return rows;

            before = string.Join("\n", lines.Take(start));
            after = string.Join("\n", lines.Skip(end + 1));

            for (int i = start; i <= end; i++)
            {
                string line = lines[i].Trim();
                if (IsSeparator(line)) continue;
                rows.Add(SplitCells(line));
            }

            return rows;
        }

        private static bool IsSeparator(string line)
        {
            string body = line.Trim('|').Replace(" ", "");
            return body.Length > 0 && body.All(ch => ch is '-' or ':' or '|');
        }

        private static List<string> SplitCells(string line)
            => line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

        private static string Compose(string before, List<List<string>> rows, string after)
        {
            var sb = new System.Text.StringBuilder();
            if (before.Trim().Length > 0) sb.Append(before.TrimEnd()).Append("\n\n");

            for (int i = 0; i < rows.Count; i++)
            {
                sb.Append("| ").Append(string.Join(" | ", rows[i])).Append(" |\n");
                if (i == 0)
                    sb.Append("| ").Append(string.Join(" | ", rows[0].Select(_ => "---"))).Append(" |\n");
            }

            if (after.Trim().Length > 0) sb.Append('\n').Append(after.TrimStart());
            return sb.ToString();
        }
    }
}
