using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 把一次對話濃縮成「值得長期記住的事實」。
    ///
    /// 以前是直接把使用者那句話截 240 字、產出截 420 字存進去。那不是摘要是截斷：
    /// 長對話存進去大半是跟記憶無關的字，召回時還會把它們塞進提示詞裡佔位置。
    ///
    /// 這個類別只負責「規則版」與提示詞／解析，不呼叫模型——語意版在
    /// <c>MemoryDigestResolver</c>，模型不通時退回這裡，流程永不中斷。
    /// </summary>
    public static class MemoryDigest
    {
        /// <summary>一條記憶最多幾句。超過的話召回時會吃掉太多提示詞空間。</summary>
        public const int MaxFacts = 3;

        /// <summary>單句上限，避免模型把整段論述塞成「一句」。</summary>
        public const int MaxFactLength = 80;

        public static string BuildSystemPrompt() =>
@"你把一段對話濃縮成「值得長期記住的事實」。

只保留下次換個主題、甚至換個專案時仍然成立的事：
- 使用者是誰、在哪、用什麼（身分、所在地、職業、慣用工具與語言）
- 使用者的偏好與習慣（格式、語氣、長度、風格）
- 使用者講過的長期限制或目標

不要保留：
- 這一次任務才成立的細節（這次要查的題目、這次的資料、這次的檔名）
- AI 產出的內容本身、推理過程、客套話
- 問句，以及任何你不確定是不是長期成立的推測

寫法：每條一句話、陳述句、主詞用「使用者」，不要加編號或符號。
使用者用哪種語言，就用哪種語言寫。

最多 3 條。真的沒有值得長期記住的事就回空陣列。
只輸出 JSON，不要任何多餘文字：

{""facts"": [""使用者是美國人""]}";

        public static string BuildUserPrompt(string topText, string bottomText) =>
$@"使用者輸入：
{Clip(topText, 1500)}

這次的產出：
{Clip(bottomText, 1500)}

請輸出 JSON。";

        /// <summary>解析模型回的 JSON。解析不出來回 null，呼叫端據此退回規則版。</summary>
        public static IReadOnlyList<string>? TryParse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            try
            {
                var match = Regex.Match(raw, @"\{[\s\S]*\}");
                using var doc = JsonDocument.Parse(match.Success ? match.Value : raw);

                if (!doc.RootElement.TryGetProperty("facts", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return null;

                return Tidy(arr.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? ""));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 規則版：模型不通時用。挑出看起來像「長期事實」的句子——
        /// 第一人稱的自我陳述、以及明講是長期偏好的句子。
        ///
        /// 這套只對中英文有效，所以它是後備而不是主力：語意版才處理得了任意語言。
        /// 一條都挑不出來時回傳截短的原文，寧可粗糙也不要讓使用者標記了卻什麼都沒記住。
        /// </summary>
        public static IReadOnlyList<string> FromText(string? topText, string? bottomText)
        {
            var picked = Sentences(topText).Where(LooksDurable).ToList();

            if (picked.Count == 0)
                picked = Sentences(topText).Take(1).ToList();

            if (picked.Count == 0)
                picked = Sentences(bottomText).Take(1).ToList();

            return Tidy(picked);
        }

        /// <summary>幾條事實 → 存進記憶的內容字串。</summary>
        public static string ToContent(IReadOnlyList<string> facts)
            => string.Join(Environment.NewLine, facts.Select(f => "- " + f));

        private static readonly string[] DurableMarkers =
        {
            "我是", "我叫", "我住", "我在", "我來自", "我的", "我有", "我會", "我用",
            "我喜歡", "我討厭", "我不要", "我希望", "我習慣", "我目前", "我正在",
            "一律", "以後都", "每次都", "預設", "長期", "通常",
            "i am ", "i'm ", "my ", "i live", "i work", "i prefer", "i always", "i usually", "i never",
        };

        private static bool LooksDurable(string sentence)
        {
            string s = sentence.Trim();
            if (s.Length == 0) return false;

            // 問句通常是這一次的需求，不是長期事實。
            if (s.EndsWith("?", StringComparison.Ordinal) || s.EndsWith("？", StringComparison.Ordinal))
                return false;

            string lower = s.ToLowerInvariant();
            return DurableMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
        }

        private static IEnumerable<string> Sentences(string? text)
            => Regex.Split((text ?? "").Replace("\r", ""), @"[\n。！!？?；;]+")
                .Select(x => x.Trim())
                .Where(x => x.Length > 0);

        private static IReadOnlyList<string> Tidy(IEnumerable<string> facts)
            => facts
                .Select(f => Clip(Regex.Replace(f.Trim(), @"^[-•*\d.、\s]+", ""), MaxFactLength))
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxFacts)
                .ToList();

        private static string Clip(string? text, int max)
        {
            string t = (text ?? "").Trim();
            return t.Length <= max ? t : t[..max].TrimEnd() + "…";
        }
    }
}
