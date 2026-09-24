using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 把 API 與網路的原始錯誤翻成「使用者看得懂、而且知道下一步」的說明。
    ///
    /// 為什麼需要：決策窗以前只顯示英文原文（例如 "Your credit balance is too low to access
    /// the Anthropic API"、"No such host is known"），使用者只知道失敗、不知道是自己的餘額用完
    /// 還是程式壞了——這些資訊本來只有翻日誌才找得到。
    /// </summary>
    public static class FailureExplainer
    {
        public sealed class Explanation
        {
            /// <summary>一句話結論（例：「Claude API 餘額不足」）。</summary>
            public string Title { get; init; } = "";
            /// <summary>下一步該做什麼（例：「到 Anthropic 帳單頁面加值後再試」）。</summary>
            public string Action { get; init; } = "";
            /// <summary>是不是「使用者自己可以解決」的問題（餘額、金鑰、網路）——決策窗用來決定要不要顯眼提示。</summary>
            public bool UserActionable { get; init; } = true;

            public string OneLine => string.IsNullOrWhiteSpace(Action) ? Title : $"{Title}｜{Action}";
        }

        private sealed record Rule(string[] Needles, string Title, string Action, bool UserActionable = true);

        // 順序＝優先序：越具體的放前面（餘額 / 金鑰 / 配額 都會伴隨 400/401/429，不能只看狀態碼）。
        private static readonly Rule[] Rules =
        {
            new(new[] { "credit balance is too low", "insufficient_quota", "insufficient credits", "billing_hard_limit" },
                "AI 服務的餘額用完了",
                "到該服務的帳單頁面加值或調高額度後再試；期間可改用其他模型"),

            new(new[] { "invalid x-api-key", "invalid_api_key", "authentication_error", "incorrect api key", "unauthorized", "401" },
                "金鑰無效或沒有權限",
                "到 設定 → API 重新貼上金鑰並按「測試」"),

            new(new[] { "permission", "403" },
                "這把金鑰沒有這項功能的權限",
                "確認該服務帳號已開通此模型／功能（例如 Veo 影片需要啟用帳單）"),

            new(new[] { "rate limit", "rate_limit", "429", "quota exceeded", "resource_exhausted" },
                "呼叫太頻繁或額度已達上限",
                "等一下再試；或到服務後台調高速率上限"),

            new(new[] { "no such host", "name or service not known", "network is unreachable", "connection refused",
                        "connection reset", "unable to connect", "socketexception", "nameresolution", "網路" },
                "連不上網路或服務暫時無法連線",
                "檢查網路連線（Wi-Fi／VPN／防火牆）後再試一次"),

            new(new[] { "model_not_found", "does not exist or you do not have access", "unknown model", "404" },
                "這個模型目前不可用",
                "換一個模型，或確認帳號是否已開通該模型"),

            new(new[] { "prompt is too long", "context_length_exceeded", "maximum context length", "too many tokens" },
                "內容長度超過模型上限",
                "縮短輸入或附件，或分成多個節點處理"),

            new(new[] { "overloaded", "503", "502", "temporarily unavailable", "service unavailable" },
                "AI 服務暫時過載",
                "稍等幾分鐘再試；系統會自動改用備援模型", UserActionable: false),

            new(new[] { "timeout", "timed out", "逾時", "taskcanceled" },
                "等待回應逾時",
                "重跑一次；很長的任務可到 設定 → 個人化 調高逾時上限"),

            new(new[] { "safety", "content_policy", "blocked by", "rai" },
                "內容被服務端的安全機制擋下",
                "換個說法或改用別的模型再試", UserActionable: false),

            new(new[] { "ffmpeg" },
                "本機缺少影片剪接工具 ffmpeg",
                "安裝後自動啟用：winget install Gyan.FFmpeg"),
        };

        /// <summary>翻譯原始錯誤；看不出來時回 null（呼叫端就照舊顯示原文）。</summary>
        public static Explanation? Explain(string? rawError)
        {
            string text = (rawError ?? "").Trim();
            if (text.Length == 0)
                return null;

            string lower = text.ToLowerInvariant();

            foreach (var rule in Rules)
            {
                if (rule.Needles.Any(n => lower.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    return new Explanation { Title = rule.Title, Action = rule.Action, UserActionable = rule.UserActionable };
            }

            return null;
        }

        /// <summary>給決策窗用的一行字：翻得出來就用白話，翻不出來就回原文（截短）。</summary>
        public static string Describe(string? rawError, int maxRawLength = 120)
        {
            var explained = Explain(rawError);
            if (explained != null)
                return explained.OneLine;

            string text = (rawError ?? "").Trim();
            if (text.Length == 0)
                return "";
            return text.Length <= maxRawLength ? text : text.Substring(0, maxRawLength) + "…";
        }

        /// <summary>
        /// 模型層級的失敗摘要：哪個模型不能用、為什麼、最後由誰完成。
        /// 例：「Claude Opus 5 無法使用（AI 服務的餘額用完了），已改用 GPT-5.6 Sol」。
        /// </summary>
        public static IReadOnlyList<string> DescribeAttempts(
            IEnumerable<AiFallbackAttempt>? attempts,
            Func<string, string> modelLabel)
        {
            var list = (attempts ?? Array.Empty<AiFallbackAttempt>()).Where(a => a != null).ToList();
            if (list.Count <= 1)
                return Array.Empty<string>();

            var lines = new List<string>();
            var succeeded = list.LastOrDefault(a => a.Success);

            foreach (var failed in list.Where(a => !a.Success))
            {
                string why = Describe(failed.ErrorMessage, 60);
                string tail = succeeded != null ? $"，已改用 {modelLabel(succeeded.ModelId)}" : "";
                lines.Add(string.IsNullOrWhiteSpace(why)
                    ? $"{modelLabel(failed.ModelId)} 無法使用{tail}"
                    : $"{modelLabel(failed.ModelId)} 無法使用（{why}）{tail}");
            }

            return lines;
        }
    }
}
