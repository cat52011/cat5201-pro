using System;
using System.Collections.Generic;

namespace Cat5201
{
    /// <summary>
    /// 介面語言（個人化 → 語言）。目前只有繁體中文與英文。
    ///
    /// 只管「介面上給人看的字」：按鈕、說明、對話框、狀態訊息。
    /// 送給模型的提示詞不走這裡——回答語言由 <see cref="AnswerLanguage"/> 依提問判斷。
    ///
    /// 用法：
    ///  - 程式裡的字串：<c>UiLanguage.P("中文", "English")</c>，兩個都寫在呼叫處，讀程式時看得到原文。
    ///  - 共用對照表（XAML 走這條）：<c>UiLanguage.T("中文")</c>，查不到就回原文。
    ///
    /// 放 Core 是因為部分給使用者看的訊息（失敗說明、金鑰檢查結果）在 Core 裡產生。
    /// </summary>
    public static class UiLanguage
    {
        public const string Chinese = "zh-TW";
        public const string English = "en";

        private static string _code = Chinese;
        private static readonly Dictionary<string, string> _en = new(StringComparer.Ordinal);

        /// <summary>切換語言後觸發（XAML 綁定、已開著的面板靠它刷新）。</summary>
        public static event Action? Changed;

        /// <summary>目前語言代碼（<see cref="Chinese"/> 或 <see cref="English"/>）。</summary>
        public static string Code
        {
            get => _code;
            set
            {
                string v = Normalize(value);
                if (string.IsNullOrEmpty(v)) v = Chinese;
                if (v == _code) return;
                _code = v;
                Changed?.Invoke();
            }
        }

        public static bool IsEnglish => _code == English;

        /// <summary>依目前語言挑一個。</summary>
        public static string P(string zh, string en) => IsEnglish ? en : zh;

        /// <summary>用共用對照表翻譯；英文模式下查不到就回原文（寧可露中文，也不要空白）。</summary>
        public static string T(string zh)
        {
            if (!IsEnglish || string.IsNullOrEmpty(zh)) return zh;
            return _en.TryGetValue(zh, out var en) ? en : zh;
        }

        /// <summary>登記中→英對照（啟動時由介面層呼叫一次）。</summary>
        public static void Register(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            foreach (var (zh, en) in pairs)
                _en[zh] = en;
        }

        /// <summary>
        /// 偏好檔裡的值 → 語言代碼；空字串＝使用者還沒選過（啟動時要問）。
        /// </summary>
        public static string Normalize(string? stored)
        {
            string s = (stored ?? "").Trim();
            if (s.Length == 0) return "";
            if (s.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return English;
            return Chinese;
        }
    }
}
