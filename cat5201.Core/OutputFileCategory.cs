using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 節點下方產出檔案的分類：顯示成「程式碼：app.py」「報告：x.docx」，並依類別排在一起。
    ///
    /// 2026-11-03 使用者要求：同時產生程式碼與書面報告時要分類好。
    /// 以前每一個都標「檔案：」，程式碼、報告、PDF 混在一起，看不出哪個是哪個。
    /// 類別只看副檔名，所以重開存檔時（只有路徑）也分得出來。
    /// </summary>
    public static class OutputFileCategory
    {
        public sealed record Category(string Label, int Order);

        private static readonly Category Deck = new("簡報", 0);
        private static readonly Category Report = new("報告", 1);
        private static readonly Category Table = new("表格", 2);
        private static readonly Category Pdf = new("PDF", 3);
        private static readonly Category Code = new("程式碼", 4);
        private static readonly Category Web = new("網頁", 4);   // .html 也是程式碼產出，排在一起
        private static readonly Category Image = new("圖片", 5);
        private static readonly Category Video = new("影片", 6);
        private static readonly Category Text = new("文字", 7);
        private static readonly Category Other = new("檔案", 8);

        public static Category Of(string? fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext switch
            {
                ".pptx" => Deck,
                ".docx" => Report,
                ".xlsx" or ".csv" => Table,
                ".pdf" => Pdf,
                ".html" or ".htm" => Web,
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => Image,
                ".mp4" or ".mov" or ".webm" or ".m4v" => Video,
                ".md" or ".txt" => Text,
                _ when CodeFileExtractor.CodeExtensions.Contains(ext) => Code,
                _ => Other,
            };
        }

        /// <summary>依類別排序（同類保持原本順序）。</summary>
        public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, string?> fileName)
            => (items ?? Array.Empty<T>())
                .Select((item, index) => (item, index))
                .OrderBy(x => Of(fileName(x.item)).Order)
                .ThenBy(x => x.index)
                .Select(x => x.item)
                .ToList();
    }
}
