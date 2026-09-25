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
        /// 重寫這份簡報時，應該一起更新哪些 PDF。
        ///
        /// 為什麼不能直接用 FindCompanionPdf：那個是「讀取用」的嚴格比對，雜湊對不上就回 null
        /// （預覽寧可不顯示，也不能顯示舊版）。但拿它來決定「要覆蓋哪個檔」時，一旦回 null 就會
        /// 退回「同名 .pdf」，於是生出第二份 PDF——使用者的 chip 還指著舊的那份，看起來就是
        /// 「單頁重生後 PDF 永遠不更新」（2026-09-25 實測到的狀況）。
        ///
        /// 所以這裡不看雜湊，把「配對記錄的那份」＋「同標題、時間相近的那些」全部列出來一起覆蓋，
        /// 不管使用者點的是哪一個檔案，看到的都會是最新版。
        /// </summary>
        public static IReadOnlyList<string> ResolveRewriteTargets(string pptxPath)
        {
            var targets = new List<string>();
            string? dir = Path.GetDirectoryName(pptxPath);
            if (string.IsNullOrWhiteSpace(dir))
                return new[] { Path.ChangeExtension(pptxPath, ".pdf") };

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!targets.Contains(path, StringComparer.OrdinalIgnoreCase))
                    targets.Add(path!);
            }

            // ① 配對記錄指定的那份（不驗雜湊——我們正要覆蓋它）。
            try
            {
                string pairing = pptxPath + ".preview.json";
                if (File.Exists(pairing))
                {
                    var pair = JsonSerializer.Deserialize<Pair>(File.ReadAllText(pairing));
                    if (!string.IsNullOrWhiteSpace(pair?.Pdf))
                        Add(Path.Combine(dir, pair!.Pdf!));
                }
            }
            catch (Exception ex) { AppLog.Warn("Preview", "讀取對照 PDF 配對失敗", ex); }

            // ② 同標題、時間相近的 PDF（產出時 pptx 與 pdf 的時間戳常差幾秒，檔名因此不同）。
            var (title, stamp) = SplitName(Path.GetFileNameWithoutExtension(pptxPath));
            if (!string.IsNullOrWhiteSpace(title))
            {
                try
                {
                    foreach (var pdf in Directory.GetFiles(dir, "*.pdf"))
                    {
                        var (pdfTitle, pdfStamp) = SplitName(Path.GetFileNameWithoutExtension(pdf));
                        if (!string.Equals(pdfTitle, title, StringComparison.OrdinalIgnoreCase)) continue;

                        TimeSpan gap = stamp.HasValue && pdfStamp.HasValue
                            ? (stamp.Value - pdfStamp.Value).Duration()
                            : TimeSpan.Zero;
                        if (gap <= MaxGap) Add(pdf);
                    }
                }
                catch (Exception ex) { AppLog.Warn("Preview", "掃描對照 PDF 失敗", ex); }
            }

            if (targets.Count == 0)
                Add(Path.ChangeExtension(pptxPath, ".pdf"));

            return targets;
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
