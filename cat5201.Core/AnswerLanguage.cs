using System;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 這次要用哪個語言回答。
    ///
    /// 2026-10-15：記憶裡存了「我是西班牙人」之後，用中文問「我是哪國人」，
    /// 回答變成 "Eres español."。問題不在那條記憶，而在**從頭到尾沒有人告訴模型要用什麼語言**——
    /// 沒有明確指示時它就從上下文猜，而一條國籍事實是比提問語言更強烈的暗示。
    ///
    /// 先前的修法是在記憶區塊加一句「這些事實不要影響語言」。那是拜託，不是規格：
    /// 模型照樣可以忽略。真正的解法是**把語言講明**——依使用者提問的文字判斷，
    /// 明確寫進指示裡。身分事實就再也沒有解釋空間。
    /// </summary>
    public static class AnswerLanguage
    {
        /// <summary>
        /// 從使用者的提問判斷要用哪個語言回答。看字形，不看內容——
        /// 「我是西班牙人」是中文句子，不是西班牙文。
        /// </summary>
        public static string Detect(string? userInput)
        {
            string text = userInput ?? "";
            if (text.Trim().Length == 0)
                return "繁體中文";

            int han = 0, kana = 0, hangul = 0, latin = 0, cyrillic = 0;

            foreach (char c in text)
            {
                if (c is >= '぀' and <= 'ヿ') kana++;
                else if (c is >= '가' and <= '힯') hangul++;
                else if (c is >= '一' and <= '鿿') han++;
                else if (c is >= 'Ѐ' and <= 'ӿ') cyrillic++;
                else if (char.IsLetter(c) && c < 128) latin++;
            }

            // 日文的漢字比例很高，所以假名只要出現就以日文為準。
            if (kana >= 2) return "日本語";
            if (hangul >= 2) return "한국어";
            if (han > 0 && han >= latin / 4) return "繁體中文";
            if (cyrillic > latin) return "Русский";
            if (latin > 0) return "English";

            return "繁體中文";
        }

        /// <summary>
        /// 要放進提示的語言指示。
        /// </summary>
        /// <param name="requestedLanguage">這次請求或偏好明確指定的語言（空＝沒指定，依提問判斷）。</param>
        public static string BuildBlock(string? userInput, string? requestedLanguage)
        {
            bool explicitly = !string.IsNullOrWhiteSpace(requestedLanguage);
            string language = explicitly ? requestedLanguage!.Trim() : Detect(userInput);

            string why = explicitly
                ? "（使用者指定）"
                : "（依使用者提問的語言判斷）";

            return
                $"【回答語言】一律使用「{language}」{why}。\n" +
                "記憶裡關於使用者的事實（國籍、居住地、母語…）是**內容**，不是語言指示：" +
                "知道使用者是西班牙人，不代表要用西班牙文回答。";
        }
    }
}
