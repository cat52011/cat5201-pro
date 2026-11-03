using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Cat5201
{
    /// <summary>
    /// 把附件（PDF / HTML / Office / 純文字）在本機抽成文字並快取，避免每次執行都把原檔
    /// （尤其大型 PDF：OpenAI 會把每頁也當圖片計 token，極貴）重新上傳給模型。
    /// 圖片回傳 null＝維持以圖片形式傳送（需要視覺）。
    /// 快取鍵＝絕對路徑 + 檔長 + 修改時間，同一 app session 內每個附件只抽一次；附件本身不變即命中。
    /// </summary>
    public static class AttachmentTextCache
    {
        private static readonly ConcurrentDictionary<string, string> _cache = new();

        // 防單一超大檔（如數百頁 PDF）失控；超過截斷並標註。仍遠比把原檔逐頁當圖片送便宜。
        private const int MaxCharsPerAttachment = 60000;

        /// <summary>
        /// 資料檔（CSV、Excel）只附欄位列與前幾行，並註明總行數。
        ///
        /// 2026-11-03 機器學習作業：1.2 MB 的 CSV 截到 6 萬字送進每一次呼叫，一次約 11 萬 token，
        /// 而模型在對話裡本來就算不了整份資料——那 6 萬字只是前一百多行，拿來算統計反而會錯。
        /// 認識欄位與資料長相，幾十行就夠；要真的算，走沙箱執行（DataAnalysisJob），那裡是整份檔案。
        /// </summary>
        internal const int MaxCharsForDataFile = 8000;

        internal static bool IsDataFile(string ext) => ext is ".csv" or ".tsv" or ".xlsx";

        internal static string TrimDataFile(string text)
        {
            if (text.Length <= MaxCharsForDataFile)
                return text;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var kept = new StringBuilder();
            int count = 0;
            foreach (var line in lines)
            {
                if (kept.Length + line.Length + 1 > MaxCharsForDataFile && count > 0)
                    break;
                kept.Append(line).Append('\n');
                count++;
            }

            return kept.ToString().TrimEnd() +
                   $"\n…（資料檔只附前 {count} 行（含欄位列），整份共 {lines.Length} 行。" +
                   "這裡的內容只夠認識欄位與資料長相，不能拿來計算整份資料的統計數字。）";
        }

        /// <summary>
        /// 回傳附件抽出的純文字；圖片或無法抽取的型別回傳 null（呼叫端維持原樣以檔案形式傳送）。
        /// </summary>
        public static string? TryGetText(string? absolutePath, string? fileName, string? mimeType)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
                return null;

            string ext = Path.GetExtension(fileName ?? absolutePath).ToLowerInvariant();
            if (IsImage(ext, mimeType))
                return null;

            string key;
            try
            {
                var fi = new FileInfo(absolutePath);
                key = $"{absolutePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            }
            catch
            {
                key = absolutePath;
            }

            if (_cache.TryGetValue(key, out var cached))
                return cached;

            string? text = Extract(absolutePath, ext);
            if (text == null)
                return null;

            text = text.Trim();
            if (IsDataFile(ext))
                text = TrimDataFile(text);
            else if (text.Length > MaxCharsPerAttachment)
                text = text.Substring(0, MaxCharsPerAttachment) + "\n…（內容過長，已截斷）";

            _cache[key] = text;
            return text;
        }

        private static bool IsImage(string ext, string? mime)
        {
            if (!string.IsNullOrEmpty(mime) && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return true;
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";
        }

        private static string? Extract(string path, string ext)
        {
            try
            {
                switch (ext)
                {
                    case ".pdf": return ExtractPdf(path);
                    case ".html":
                    case ".htm": return StripHtml(File.ReadAllText(path));
                    case ".docx": return ArtifactTextExtractor.ExtractDocx(path);
                    case ".pptx": return ArtifactTextExtractor.ExtractPptx(path);
                    case ".xlsx":
                        return string.Join("\n",
                            ArtifactTextExtractor.ExtractXlsxRows(path).Select(r => string.Join("\t", r)));
                    case ".txt":
                    case ".md":
                    case ".csv":
                    case ".json":
                    case ".css":
                    case ".sh":
                    case ".bat":
                    case ".log":
                    case ".xml":
                        return File.ReadAllText(path);
                    default:
                        return null; // 未知型別→交回呼叫端（通常略過或維持原樣）
                }
            }
            catch
            {
                return null; // 抽取失敗→回 null，呼叫端退回原樣傳檔，永不中斷主流程
            }
        }

        private static string ExtractPdf(string path)
        {
            var sb = new StringBuilder();
            using var pdf = PdfDocument.Open(path);
            foreach (var page in pdf.GetPages())
                sb.AppendLine(page.Text);
            return sb.ToString();
        }

        // HTML → 可見文字：移除 script/style、把區塊標籤換成換行、剝掉其餘標籤、解碼實體、收斂空白。
        // 一個十幾萬字的聊天匯出 HTML 大半是標籤，剝完通常只剩幾分之一。
        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return "";

            html = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(br|/p|/div|/li|/tr|/h[1-6])\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", " ");
            html = System.Net.WebUtility.HtmlDecode(html);
            html = Regex.Replace(html, @"[ \t]+", " ");
            html = Regex.Replace(html, @"\n\s*\n\s*\n+", "\n\n");
            return html.Trim();
        }
    }
}
