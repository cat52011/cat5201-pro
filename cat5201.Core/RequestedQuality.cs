using System;

namespace Cat5201
{
    /// <summary>
    /// 使用者在要求裡明講要「快速版」或「最佳品質」——照講的做，不看個人化的預設。
    ///
    /// 2026-11-03：輸入「做一份 3 頁關於BMI簡報（快速版、不配圖）」，個人化設的是最佳品質，
    /// 確認框照樣寫「PowerPoint・最佳品質模式，約需數分鐘」，實際也走了 Claude 文件技能。
    ///
    /// 判斷由第一層語意判斷（OutputIntentResolver 的 quality 欄位）負責，不用關鍵字：
    /// 換個說法（「趕時間，簡單做就好」）或換個語言（"quick version"）都要認得。
    /// 那一步失敗時就當作沒講，照個人化的設定。
    /// </summary>
    public static class RequestedQuality
    {
        public enum Level { None, Fast, Best }

        /// <summary>意圖判斷回傳的 quality 欄位 → Level。看不懂的值當作沒講。</summary>
        public static Level Parse(string? value)
            => (value ?? "").Trim().ToLowerInvariant() switch
            {
                "fast" => Level.Fast,
                "best" => Level.Best,
                _ => Level.None,
            };

        /// <summary>這一次實際用哪個文件引擎：明講的優先，沒講就用個人化的設定。</summary>
        public static DocumentEngine Apply(Level level, DocumentEngine configured)
            => level switch
            {
                Level.Fast => DocumentEngine.Builtin,
                Level.Best => DocumentEngine.ClaudeSkills,
                _ => configured,
            };
    }
}
