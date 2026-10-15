using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 個人化的優先順序——**單一真相**。
    ///
    /// 為什麼要集中：一般節點的提示說「本次請求 ＞ 上游鏈 ＞ 全域偏好 ＞ 預設」，
    /// 文件生成的提示卻把全域偏好標成「最高優先，若指定語言必須照做」。
    /// 全域設繁中、這次要求英文簡報時，模型會同時收到兩條互相矛盾的規則——
    /// 不一定每次都出錯，但會變成難以重現的不一致。規則只能有一份，就是這裡。
    /// </summary>
    public static class PreferencePriority
    {
        /// <summary>所有提示共用的一句話。</summary>
        public const string Rule =
            "優先順序：本次請求明確指定 ＞ 上游鏈設定 ＞ 全域偏好 ＞ 系統預設。" +
            "全域偏好是「沒特別講時的預設」，本次請求講了什麼就以本次為準。";

        /// <summary>放在偏好區塊前面的標題。</summary>
        public const string BlockHeader = "【使用者的長期偏好（沒特別指定時的預設）】";

        /// <summary>
        /// 這次實際生效的設定摘要（決策窗顯示用）：採用了哪些偏好、哪一項被本次請求覆寫。
        /// </summary>
        public sealed record Effective(IReadOnlyList<string> Applied, IReadOnlyList<string> Overridden)
        {
            public string Summary()
            {
                var parts = new List<string>();
                if (Applied.Count > 0) parts.Add("採用偏好：" + string.Join("、", Applied));
                if (Overridden.Count > 0) parts.Add("本次請求覆寫：" + string.Join("、", Overridden));
                return parts.Count == 0 ? "" : string.Join("　·　", parts);
            }
        }

        /// <summary>
        /// 解析這次實際生效的偏好。目前程式能明確判斷的是「語言」——
        /// 那也是最常衝突的一項（全域設繁中、這次要求英文）。其餘偏好照列為採用。
        /// </summary>
        public static Effective Resolve(string? preferenceBlock, string? requestedLanguage)
        {
            var applied = new List<string>();
            var overridden = new List<string>();

            foreach (var line in (preferenceBlock ?? "").Split('\n'))
            {
                string item = line.Trim().TrimStart('-', '·', '*', ' ');
                if (item.Length == 0) continue;

                bool isLanguageRule = LanguageWords.Any(w => item.Contains(w, StringComparison.OrdinalIgnoreCase));

                if (isLanguageRule && !string.IsNullOrWhiteSpace(requestedLanguage))
                    overridden.Add($"{Shorten(item)}（改用 {requestedLanguage}）");
                else
                    applied.Add(Shorten(item));
            }

            return new Effective(applied, overridden);
        }

        private static readonly string[] LanguageWords =
        {
            "語言", "繁體", "簡體", "中文", "英文", "日文", "韓文", "language", "english", "chinese", "japanese",
        };

        private static string Shorten(string text)
            => text.Length <= 24 ? text : text[..24] + "…";
    }
}
