using System;
using System.Text.RegularExpressions;

namespace Cat5201
{
    public static class FinalAnswerSanitizer
    {
        private const string HeadingKeyData = "\u95dc\u9375\u8cc7\u6599";
        private const string HeadingTrend = "\u77ed\u671f\u8d70\u52e2\u5224\u65b7";
        private const string HeadingGaps = "\u8cc7\u6599\u885d\u7a81 / \u7f3a\u5931";
        private const string HeadingOneLine = "\u7e3d\u7d50\u4e00\u53e5\u8a71";

        private static readonly Regex CitationMarkerRegex =
            new(@"(?<![A-Za-z0-9])\[(?:\d+|[ivxlcdm]+)(?:\s*[,;]\s*(?:\d+|[ivxlcdm]+))*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SourceTagCitationRegex =
            new(@"(?<![A-Za-z0-9])\[(?:[A-Z][A-Z0-9.\-]{1,8})(?:\]\[(?:[A-Z][A-Z0-9.\-]{1,8}))*\]", RegexOptions.Compiled);

        private static readonly Regex EndMarkerRegex =
            new(@"\[\[END_OF_RESPONSE\]\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 內部脈絡標籤：模型有時會把 prompt 裡的脈絡區塊名（[Container:…]、[Capability Data]）當成引用寫進答案。
        // 這些是系統內部名稱、對使用者是雜訊，最終答案一律清掉（稽查資訊留在決策窗，不進人看的答案）。Codex P1-10。
        private static readonly Regex InternalContextLabelRegex =
            new(@"\s*\[(?:Container\s*:[^\]]*|Capability\s*Data(?:\s*:[^\]]*)?)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Sanitize(string text, bool enforceSynthesisFormat)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = EndMarkerRegex.Replace(text, "");

            // 程式碼不能動：引用記號的規則會把 split()[0] 的 [0]、data[[TARGET]] 的 [TARGET] 當成引用刪掉
            // （2026-11-03 機器學習作業實測），空白整理也會改掉程式碼裡的空行。只處理程式碼以外的部分。
            text = OutsideCode(text, prose =>
            {
                prose = InternalContextLabelRegex.Replace(prose, "");
                prose = CitationMarkerRegex.Replace(prose, "");
                prose = SourceTagCitationRegex.Replace(prose, "");
                if (enforceSynthesisFormat)
                    prose = NormalizeLegacyHeadingsOnly(prose);
                return CleanupWhitespaceInner(prose);
            });

            return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        // 程式碼區塊（``` 或 ~~~ 起訖）與行內程式碼（`…`）。
        private static readonly Regex CodeSpanRegex = new(
            @"(?ms)^[ \t]{0,3}(?<f>`{3,}|~{3,})[^\n]*\n.*?^[ \t]{0,3}\k<f>[ \t]*$|^[ \t]{0,3}(?:`{3,}|~{3,})[^\n]*\n.*\z|`[^`\n]+`",
            RegexOptions.Compiled);

        /// <summary>只對程式碼以外的文字套用 transform；程式碼原樣保留。</summary>
        internal static string OutsideCode(string text, Func<string, string> transform)
        {
            text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var sb = new System.Text.StringBuilder(text.Length);
            int last = 0;
            foreach (Match m in CodeSpanRegex.Matches(text))
            {
                sb.Append(transform(text[last..m.Index]));
                sb.Append(m.Value);
                last = m.Index + m.Length;
            }
            sb.Append(transform(text[last..]));
            return sb.ToString();
        }

        public static AiFallbackExecutionResult Sanitize(
            AiFallbackExecutionResult result,
            bool enforceSynthesisFormat)
        {
            if (result == null)
            {
                return new AiFallbackExecutionResult
                {
                    IsSuccess = false,
                    ErrorMessage = "Execution result was null."
                };
            }

            return new AiFallbackExecutionResult
            {
                IsSuccess = result.IsSuccess,
                Text = Sanitize(result.Text ?? "", enforceSynthesisFormat),
                ActualModelId = result.ActualModelId ?? "",
                UsedFallback = result.UsedFallback,
                Summary = result.Summary ?? "",
                ErrorMessage = result.ErrorMessage ?? "",
                Attempts = result.Attempts ?? Array.Empty<AiFallbackAttempt>()
            };
        }

        private static string NormalizeLegacyHeadingsOnly(string text)
        {
            text = ReplaceHeading(text, "\u5df2\u77e5\u8cc7\u6599", HeadingKeyData);
            text = ReplaceHeading(text, "\u5408\u7406\u63a8\u8ad6", HeadingTrend);
            text = ReplaceHeading(text, "\u98a8\u96aa / \u4e0d\u78ba\u5b9a\u6027", HeadingGaps);
            text = ReplaceHeading(text, "\u98a8\u96aa\uff0f\u4e0d\u78ba\u5b9a\u6027", HeadingGaps);
            text = ReplaceHeading(text, "\u98a8\u96aa", HeadingGaps);
            text = ReplaceHeading(text, "\u77ed\u671f\u5224\u65b7", HeadingOneLine);
            return text;
        }

        private static string ReplaceHeading(string text, string from, string to)
        {
            string escaped = Regex.Escape(from);
            return Regex.Replace(
                text,
                $@"(?m)^\s*(?:#+\s*)?(?:\*\*)?{escaped}(?:\*\*)?\s*$",
                to);
        }

        // 片段版：不做整體 Trim（片段前後接著程式碼，換行要留著）。
        private static string CleanupWhitespaceInner(string text)
        {
            text = Regex.Replace(text, @"[ \t]+\n", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text;
        }
    }
}
