using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;

namespace Cat5201
{
    /// <summary>
    /// 產出檔的「對照 PDF」配對。
    ///
    /// 為什麼需要：.pptx / .docx 沒辦法在程式內直接渲染，舊版預覽是把文字抽出來自己排一個簡單 HTML——
    /// 使用者看到的跟真正的版面完全不同（只有文字）。每份簡報/報告我們都會同時產一份版面一致的 PDF，
    /// 預覽時改顯示那份 PDF，看到的就是實際成品。
    ///
    /// 檔名規則：GeneratedFileWriter 寫成「{標題}_{yyyyMMdd_HHmmss}.{副檔名}」，
    /// 同一次產出的 pptx 與 pdf 時間戳可能差幾秒，所以用「同標題 + 時間相近」配對。
    /// </summary>
    public static class GeneratedFileCompanion
    {
        private static readonly Regex StampRegex = new(@"^(?<title>.*)_(?<stamp>\d{8}_\d{6})$", RegexOptions.Compiled);
        private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(5);
        private sealed record Pair(string? Pdf, string SourceHash, string? PdfHash);

        // Explicit pairing wins over the legacy filename/time heuristic, including "no PDF".
        public static void Register(string sourcePath, string? pdfPath)
        {
            try
            {
                string? pdf = pdfPath != null && File.Exists(pdfPath) ? pdfPath : null;
                var pair = new Pair(pdf == null ? null : Path.GetFileName(pdf), Hash(sourcePath), pdf == null ? null : Hash(pdf));
                File.WriteAllText(sourcePath + ".preview.json", JsonSerializer.Serialize(pair));
            }
            catch (Exception ex) { AppLog.Warn("Preview", "無法記錄對照 PDF", ex); }
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>
        /// 找同一次產出的對照 PDF；找不到回 null（呼叫端沿用原本的文字預覽）。
        /// </summary>
        public static string? FindCompanionPdf(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is not (".pptx" or ".docx" or ".xlsx"))
                return null;

            string? dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return null;

            string pairing = filePath + ".preview.json";
            if (File.Exists(pairing))
            {
                try
                {
                    var pair = JsonSerializer.Deserialize<Pair>(File.ReadAllText(pairing));
                    if (pair?.Pdf == null || pair.Pdf != Path.GetFileName(pair.Pdf) || pair.SourceHash != Hash(filePath)) return null;
                    string pdf = Path.Combine(dir, pair.Pdf);
                    return File.Exists(pdf) && pair.PdfHash == Hash(pdf) ? pdf : null;
                }
                catch { return null; }
            }

            string exact = Path.ChangeExtension(filePath, ".pdf");
            if (File.Exists(exact)) return exact;

            var (title, stamp) = SplitName(Path.GetFileNameWithoutExtension(filePath));
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string[] pdfs;
            try { pdfs = Directory.GetFiles(dir, "*.pdf"); }
            catch { return null; }

            string? best = null;
            TimeSpan bestGap = TimeSpan.MaxValue;

            foreach (var pdf in pdfs)
            {
                var (pdfTitle, pdfStamp) = SplitName(Path.GetFileNameWithoutExtension(pdf));

                // 同標題才算同一份（報告與表格會加「（報告）」「（表格）」後綴，天然分得開）。
                if (!string.Equals(pdfTitle, title, StringComparison.OrdinalIgnoreCase))
                    continue;

                TimeSpan gap = stamp.HasValue && pdfStamp.HasValue
                    ? (stamp.Value - pdfStamp.Value).Duration()
                    : TimeSpan.Zero;

                if (gap <= MaxGap && gap < bestGap)
                {
                    best = pdf;
                    bestGap = gap;
                }
            }

            return best;
        }

        private static (string Title, DateTime? Stamp) SplitName(string fileNameWithoutExtension)
        {
            var m = StampRegex.Match(fileNameWithoutExtension ?? "");
            if (!m.Success)
                return (fileNameWithoutExtension ?? "", null);

            DateTime? stamp = DateTime.TryParseExact(
                m.Groups["stamp"].Value, "yyyyMMdd_HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed)
                ? parsed
                : null;

            return (m.Groups["title"].Value, stamp);
        }
    }
}
