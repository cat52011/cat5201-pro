using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 匯流節點：兩條以上的上游接到同一個下游時，下游收到的輸入長什麼樣子。
    ///
    /// 2026-10-29 發現：匯流節點雖然會等所有上游跑完，主要輸入卻只放「第一個」上游的產出
    /// （FirstOrDefault）；其他上游只出現在背景脈絡裡，而且每個節點被截到 1,200 字。
    /// 使用者要讓 A、B 兩個模型各自回答、再交給下游共同檢視——結構上卻偏向先接上的那一條，
    /// 另一條的論點可能被截掉。整合時漏掉異議，不一定是模型的錯，可能是程式根本沒給它看。
    ///
    /// 這裡讓每一個上游都以同樣的格式、完整的內容出現在主要輸入裡，並明說它們地位對等。
    /// 殘留的限制：語言模型對「先讀到的」本來就有偏好，順序無法完全消除，只能明說並要求並列分歧。
    /// </summary>
    public static class UpstreamMerge
    {
        public sealed record Source(string Label, string ModelName, string Output);

        /// <summary>
        /// 上游產出交給下游之前，拿掉內部用的結束記號。
        /// 2026-10-29 實測：上游剛串流完、畫面還沒收尾時就被下游讀走，[[END_OF_RESPONSE]] 跟著進了下游的輸入。
        /// </summary>
        public static string CleanOutput(string? output)
            => (output ?? "").Replace("[[END_OF_RESPONSE]]", "", StringComparison.Ordinal).Trim();

        public const string Placeholder = "{{input}}";

        /// <summary>
        /// 使用者在這個節點「自己寫的那句話」——模板去掉 {{input}}，不含上游貼進來的內容。
        /// 這次的文字不是由這個模板套出來的（或模板只有 {{input}}）就回 null，呼叫端照舊用整段輸入。
        ///
        /// 2026-10-29 第二輪手測：匯流節點 C 只寫了「比較以下兩份回答」，
        /// 但上游的回答裡有「市場」「目前」這些字，整段輸入被判成需要即時資料，
        /// 於是跑了一次搜尋、答案頂端出現「即時資料未取得」。要不要查最新資料，是使用者的要求決定的，
        /// 不是上游的內容決定的。
        /// </summary>
        public static string? OwnInstruction(string? template, string? topText)
        {
            string tpl = template ?? "";
            string text = topText ?? "";

            int first = tpl.IndexOf(Placeholder, StringComparison.Ordinal);
            if (first < 0)
                return null;
            int last = tpl.LastIndexOf(Placeholder, StringComparison.Ordinal);

            string prefix = tpl.Substring(0, first);
            string suffix = tpl.Substring(last + Placeholder.Length);
            if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(suffix, StringComparison.Ordinal))
                return null;

            string own = tpl.Replace(Placeholder, " ", StringComparison.Ordinal).Trim();
            return own.Length == 0 ? null : own;
        }

        public static string Combine(IReadOnlyList<Source>? sources)
        {
            var list = (sources ?? Array.Empty<Source>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Output))
                .ToList();

            if (list.Count == 0) return "";
            if (list.Count == 1) return list[0].Output.Trim();

            var sb = new StringBuilder();
            sb.Append("以下 ").Append(list.Count).Append(" 份上游結果地位對等，請逐一完整考慮，")
              .Append("不得因排列先後而偏重任何一份。彼此有矛盾或不同判斷時，必須把各方的說法並列寫出，")
              .Append("不得只採用其中一方而略去另一方。\n");

            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                sb.Append("\n【上游 ").Append(i + 1).Append("／").Append(list.Count).Append('：')
                  .Append(string.IsNullOrWhiteSpace(s.Label) ? "未命名節點" : s.Label.Trim());
                if (!string.IsNullOrWhiteSpace(s.ModelName))
                    sb.Append("（").Append(s.ModelName.Trim()).Append("）");
                sb.Append("】\n").Append(s.Output.Trim()).Append('\n');
            }

            return sb.ToString();
        }
    }
}
