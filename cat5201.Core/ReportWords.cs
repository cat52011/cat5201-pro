using System;

namespace Cat5201
{
    /// <summary>
    /// 報告與表格的「文件用語」：目錄、表 N、圖 N、資料來源、合計。
    ///
    /// 這些字不是內容，是文件的骨架用語，由程式產生而不是模型寫的，所以得自己翻。
    /// 語言由第一層語意判斷（<see cref="OutputIntent.RequestedLanguage"/>）解析後傳進來；
    /// 沒對應的語言就用英文——讀者看得懂的字，總比一個他不認得的字好。
    /// 集中在這裡是為了讓 docx / pdf / xlsx 三條路徑用同一套字，不會一份寫「表 1」一份寫「Table 1」。
    /// </summary>
    public static class ReportWords
    {
        private enum Lang { Chinese, Japanese, Korean, Other }

        private static Lang Detect(string? language)
        {
            string lang = (language ?? "").Trim();
            if (lang.Length == 0 || lang.Contains("中文") || lang.Contains("Chinese", StringComparison.OrdinalIgnoreCase))
                return Lang.Chinese;
            if (lang.Contains("日本") || lang.Contains("Japanese", StringComparison.OrdinalIgnoreCase))
                return Lang.Japanese;
            if (lang.Contains("한국") || lang.Contains("Korean", StringComparison.OrdinalIgnoreCase))
                return Lang.Korean;
            return Lang.Other;
        }

        private static string Pick(string? language, string chinese, string japanese, string korean, string other)
            => Detect(language) switch
            {
                Lang.Chinese => chinese,
                Lang.Japanese => japanese,
                Lang.Korean => korean,
                _ => other
            };

        /// <summary>目錄的標題，以及欄位還沒更新時先顯示的提示。</summary>
        public static (string Label, string Hint) Contents(string? language) => Detect(language) switch
        {
            Lang.Chinese => ("目錄", "（開啟後自動更新頁碼）"),
            Lang.Japanese => ("目次", "（開いたときに更新されます）"),
            Lang.Korean => ("목차", "(문서를 열면 갱신됩니다)"),
            _ => ("Contents", "(updates when the document opens)")
        };

        public static string Table(string? language) => Pick(language, "表", "表", "표", "Table");

        public static string Figure(string? language) => Pick(language, "圖", "図", "그림", "Figure");

        public static string Source(string? language) => Pick(language, "資料來源", "出典", "출처", "Source");

        public static string Total(string? language) => Pick(language, "合計", "合計", "합계", "Total");

        /// <summary>
        /// AI 生成的示意圖一定要標明。那不是照片、也不是從資料畫出來的圖，
        /// 讀者若當成實景或實測結果，報告就在誤導人。
        /// </summary>
        public static string AiIllustration(string? language)
            => Pick(language, "AI 生成示意圖", "AI 生成のイメージ図", "AI 생성 이미지", "AI-generated illustration");

        /// <summary>「表 3｜各分類毛利率（2025）」這種編號標題。說明為空時只留編號。</summary>
        public static string Caption(string? language, string word, int number, string? text)
        {
            string label = $"{word} {number}";
            string body = (text ?? "").Trim();
            if (body.Length == 0)
                return label;

            // 全形直線在中日韓排版裡是標準分隔，拉丁文裡看起來像亂碼。
            string separator = Detect(language) == Lang.Other ? " — " : "｜";
            return label + separator + body;
        }

        /// <summary>
        /// 「表：各分類毛利率（來源：協會年報）」這種說明行 → 取出說明文字。
        /// 不是說明行就回 null。呼叫端只有在下一行真的是表格時才吃掉它，
        /// 免得把內文裡剛好以「表：」開頭的句子當成標題吞掉。
        /// </summary>
        public static string? TryCaptionText(string? line)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                (line ?? "").Trim(),
                @"^(表|図|표|圖|그림|Table|Figure)\s*[:：]\s*(.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[2].Value.Trim() : null;
        }
    }
}
