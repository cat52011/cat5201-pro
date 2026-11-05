using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 判斷「要產出什麼檔案」時，附件也要算進去。
    ///
    /// 2026-11-03 機器學習作業：使用者只打「完成此作業並說明」，要交的東西（「HW1作業檔案」與「程式碼檔案」）
    /// 寫在附件的作業說明截圖與 Word 範本裡。輸出判斷只看使用者打的那一句，於是判成純文字，
    /// 報告沒有產出；同一份作業早一點跑的那次又有產出——判斷結果還不穩定。
    ///
    /// 兩層：
    /// 1. 把附件的檔名與文件類附件的開頭一段交給輸出判斷（語意判斷）。
    /// 2. 固定規則：附件裡有「範本／模板」，而且使用者要你完成／照著做時，產出範本那一種檔案。
    ///    只在語意判斷拿不到結果時（沒模型、API 失敗）才用；語意判斷有結果時以它為準（2026-11-04）。
    /// </summary>
    public static class AttachmentIntentHints
    {
        public sealed record Hint(string FileName, string? Excerpt);

        /// <summary>每個文件附件給判斷器看的最多字數（判斷要什麼格式，不需要整份）。</summary>
        internal const int MaxExcerptChars = 600;

        /// <summary>這些是資料，不是說明：只給檔名，不給內容（大型 CSV 給內容只是浪費 token）。</summary>
        private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csv", ".tsv", ".json", ".xlsx", ".xls", ".parquet", ".sqlite", ".db",
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
        };

        private static readonly string[] TemplateTerms = { "範本", "模板", "樣板", "template" };

        private static readonly string[] CompleteTerms =
        {
            "完成", "填寫", "填好", "填入", "照著", "依照", "按照", "根據範本", "照範本", "寫作業", "做作業", "幫我做",
            "complete", "fill", "finish",
        };

        public static IReadOnlyList<Hint> Build(IEnumerable<(string FileName, string? FullPath)> attachments,
                                                Func<string, string, string?>? extract = null)
        {
            extract ??= (path, name) => AttachmentTextCache.TryGetText(path, name, null);
            var hints = new List<Hint>();

            foreach (var (fileName, fullPath) in attachments ?? Array.Empty<(string, string?)>())
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                string ext = Path.GetExtension(fileName);
                string? excerpt = null;
                if (!DataExtensions.Contains(ext) && !ImageExtensions.Contains(ext) && !string.IsNullOrWhiteSpace(fullPath))
                {
                    try
                    {
                        string? text = extract(fullPath!, fileName);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            string oneLine = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                            excerpt = oneLine.Length > MaxExcerptChars ? oneLine[..MaxExcerptChars] + "…" : oneLine;
                        }
                    }
                    catch { excerpt = null; }
                }

                hints.Add(new Hint(fileName, excerpt));
            }

            return hints;
        }

        /// <summary>給輸出判斷器的附件說明段；沒有附件回空字串。</summary>
        public static string ToPromptBlock(IReadOnlyList<Hint>? hints)
        {
            if (hints == null || hints.Count == 0)
                return "";

            var sb = new StringBuilder("\n\n使用者附上的檔案：\n");
            foreach (var h in hints)
            {
                string ext = Path.GetExtension(h.FileName);
                string kind = ImageExtensions.Contains(ext) ? "（圖片，內容未讀）"
                            : DataExtensions.Contains(ext) ? "（資料檔）"
                            : "";
                sb.Append("- ").Append(h.FileName).Append(kind).Append('\n');
                if (!string.IsNullOrWhiteSpace(h.Excerpt))
                    sb.Append("  開頭內容：").Append(h.Excerpt).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 固定規則：附件有範本，而且使用者要你完成它 → 產出範本那一種檔案。
        /// Word 範本→報告、PowerPoint 範本→簡報、Excel 範本→表格。
        /// </summary>
        public static OutputIntent ApplyTemplateRule(OutputIntent intent, string? request, IReadOnlyList<Hint>? hints)
        {
            intent ??= OutputIntent.None;
            if (hints == null || hints.Count == 0)
                return intent;

            string text = request ?? "";
            if (!CompleteTerms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return intent;

            bool report = false, deck = false, table = false;
            foreach (var h in hints)
            {
                string name = Path.GetFileNameWithoutExtension(h.FileName);
                if (!TemplateTerms.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    continue;

                switch (Path.GetExtension(h.FileName).ToLowerInvariant())
                {
                    case ".docx": case ".doc": report = true; break;
                    case ".pptx": case ".ppt": deck = true; break;
                    case ".xlsx": case ".xls": table = true; break;
                }
            }

            if (!(report && !intent.WantsReport) && !(deck && !intent.WantsPresentation) && !(table && !intent.WantsTable))
                return intent;

            return new OutputIntent
            {
                WantsPresentation = intent.WantsPresentation || deck,
                WantsReport = intent.WantsReport || report,
                WantsTable = intent.WantsTable || table,
                WantsImage = intent.WantsImage,
                WantsVideo = intent.WantsVideo,
                WantsIllustrations = intent.WantsIllustrations,
                DeclinesIllustrations = intent.DeclinesIllustrations,
                Quality = intent.Quality,
                Topic = intent.Topic,
                RequestedSlides = intent.RequestedSlides,
                RequestedLanguage = intent.RequestedLanguage,
                Capabilities = intent.Capabilities,
                ArtDirection = intent.ArtDirection,
                Source = intent.Source + " +template",
            };
        }
    }
}
