using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 短標題的命名規則：存檔（左側列表）和產出檔用同一套。
    ///
    /// 2026-11-03 使用者：產出檔的檔名要跟存檔的標題用同一個方式命名，有些命名太爛了。
    /// 存檔一直是請模型取一個「像 ChatGPT 自動命名筆記那樣」的短標題；產出檔以前卻是從指令裡刪字，
    /// 再接上時間戳記（「BMI（快速版、不）_20261103_231928.pptx」）。
    /// 現在兩邊都由模型取標題、用同一套整理，檔名不再帶時間戳記（同名時加 _2、_3）。
    /// </summary>
    public static class ShortTitle
    {
        /// <summary>給模型的命名規則（存檔自動命名與第一層意圖判斷的 topic 欄位共用）。</summary>
        public const string Rules =
            "像 ChatGPT 自動命名筆記那樣的「短標題」：使用繁體中文，盡量 6～16 字，只寫主題本身，" +
            "不要放指令與格式詞（做一份、簡報、報告、快速版、不配圖、頁數…），不要加引號、編號或副檔名。";

        public const int MaxLength = 28;

        /// <summary>模型回的標題 → 可以當檔名的字串（去引號、合併空白、換掉檔名不允許的字元、限長）。</summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            var s = raw.Trim();
            s = s.Trim('"', '“', '”', '\'', '『', '』', '「', '」');
            s = Regex.Replace(s, @"\s+", " ");

            foreach (var ch in Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');

            s = s.Trim().TrimEnd('.');
            s = s.Trim('_');

            if (s.Length > MaxLength)
                s = s.Substring(0, MaxLength).Trim();

            return s;
        }
    }
}
