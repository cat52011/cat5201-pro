using System;
using System.Security.Cryptography;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 一次執行裡，某一步「實際收到了什麼」的紀錄。
    ///
    /// 2026-10-29 模擬面試被問：「如何證明某項要求在這次執行中確實傳到下游，而不只是設計上預期如此？」
    /// 當時的回答是「日誌會顯示各步驟實際收到的內容」——查了程式，這句不成立：
    /// 執行紀錄存的是模型、token、費用與用途，沒有任何一步收到的原文。
    ///
    /// 這裡補上三樣東西，讓「有沒有傳到」變成可以查的事實：
    ///   · 這一步收到多少字、內容的雜湊（可與完整內容檔對照，證明沒被改過）
    ///   · 這一步的輸入裡**有沒有完整包含**這個節點的原始要求（逐字，忽略空白差異）
    ///   · 開頭一小段預覽
    /// 完整內容另存成檔案（StepInputLogPath），不塞進專案檔——一次研究型執行就有十幾萬字。
    /// </summary>
    public sealed class StepInputRecord
    {
        public string Step { get; init; } = "";
        public string ModelId { get; init; } = "";
        public int Chars { get; init; }
        public string Sha256 { get; init; } = "";
        public bool ContainsOriginalRequest { get; init; }
        public string Preview { get; init; } = "";

        public static StepInputRecord From(string? step, string? modelId, string? input, string? originalRequest)
        {
            string text = input ?? "";
            return new StepInputRecord
            {
                Step = string.IsNullOrWhiteSpace(step) ? "（未標示）" : step.Trim(),
                ModelId = modelId ?? "",
                Chars = text.Length,
                Sha256 = ShortHash(text),
                ContainsOriginalRequest = ContainsVerbatim(text, originalRequest),
                Preview = Preview200(text),
            };
        }

        /// <summary>
        /// 輸入裡有沒有完整包含原始要求。只忽略空白差異（換行、縮排），其餘一個字都不能少——
        /// 被截斷、被改寫、被摘要過的要求都算「沒有完整收到」。
        /// </summary>
        public static bool ContainsVerbatim(string? input, string? originalRequest)
        {
            string req = StripWhitespace(originalRequest);
            if (req.Length == 0) return false;
            return StripWhitespace(input).Contains(req, StringComparison.Ordinal);
        }

        public static string ShortHash(string? text)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        private static string StripWhitespace(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string Preview200(string text)
        {
            string t = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return t.Length <= 200 ? t : t.Substring(0, 200) + "…";
        }
    }
}
