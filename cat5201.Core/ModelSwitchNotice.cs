using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 「你選的模型失敗了，系統改用別的模型完成」——這句話要怎麼講。
    ///
    /// 2026-10-29 使用者指示：手動選擇模型時，不該出現選擇之外的模型參與；
    /// 但「當選定模型確定失敗時，系統會自動切換到其他可用模型，並通知使用者」。
    ///
    /// 在這之前，手動模式的 fallback 是無聲的：所選模型一失敗（例如收到不接受的設定回 400），
    /// 就換同一家、再換別家的模型重跑，使用者只看到一份答案，不知道是誰寫的。
    /// 換模型本身不是問題，問題在沒講。所以這裡只負責把「換了」變成使用者一定看得到的一句話。
    /// </summary>
    public static class ModelSwitchNotice
    {
        /// <summary>
        /// 一個步驟實際由其他模型完成時的說明；沒有換（或整個失敗）回 null。
        /// 整個失敗另有錯誤訊息，不在這裡講。
        /// </summary>
        public static string? Build(string? requestedModelId, AiFallbackExecutionResult? result, string? step)
        {
            if (result == null || !result.IsSuccess || string.IsNullOrWhiteSpace(result.ActualModelId))
                return null;

            string requested = Canonical(requestedModelId);
            string actual = Canonical(result.ActualModelId);
            if (requested.Length == 0 || string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase))
                return null;

            string reason = Shorten(result.Attempts?.FirstOrDefault(a => a != null && !a.Success)?.ErrorMessage);
            return Compose(requested, actual, step, reason.Length > 0 ? "執行失敗（" + reason + "）" : "執行失敗");
        }

        /// <summary>搜尋研究換了模型時的說明（不經過一般執行器，原因由搜尋端給）。</summary>
        public static string ForSearch(string? requestedModelId, string? actualModelId, string why)
            => Compose(Canonical(requestedModelId), Canonical(ModelScope.FromAgentApiModel(actualModelId)), "搜尋研究", why);

        /// <summary>把同一次執行裡的多句說明合成一段放在答案最上方；沒有就回空字串。</summary>
        public static string Combine(IEnumerable<string>? notices)
        {
            var lines = (notices ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return lines.Count == 0 ? "" : string.Join("\n\n", lines) + "\n\n";
        }

        private static string Compose(string requested, string actual, string? step, string what)
        {
            string where = string.IsNullOrWhiteSpace(step) ? "" : $"在「{step!.Trim()}」這一步";
            // 不用 **粗體**：串流時節點顯示的是原文，記號會原樣露出來好幾分鐘（2026-10-29 使用者回報）。
            return $"⚠️【已改用其他模型】你選的 {DisplayName(requested)} {where}{what}，" +
                   $"系統改由 {DisplayName(actual)} 完成。";
        }

        // 比對前不能用 NormalizeNodeModel：查不到的 id 會被它換成預設模型，兩邊就「剛好相同」了。
        private static string Canonical(string? modelId)
        {
            string id = (modelId ?? "").Trim();
            return id.Length == 0 ? "" : AiModelRegistry.ResolveAlias(id) ?? id;
        }

        private static string DisplayName(string modelId)
            => AiModelRegistry.TryGetLegacyDisplayName(modelId)
               ?? AiModelRegistry.Find(modelId)?.DisplayName
               ?? modelId;

        private static string Shorten(string? text)
        {
            string s = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length <= 80 ? s : s.Substring(0, 80) + "…";
        }
    }
}
