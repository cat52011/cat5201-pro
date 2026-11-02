using System;
using System.Collections.Generic;

namespace Cat5201
{
    /// <summary>
    /// 一次執行裡「可以呼叫哪些模型」。
    ///
    /// 2026-10-29 使用者指示：手動選擇模型時，不該出現選擇之外的模型參與。
    ///
    /// 在這之前，手動模式只決定「誰寫最後的答案」，背後還有四個幫手寫死了 GPT：
    /// 輸出意圖判斷、配圖意圖判斷、搜尋研究、記憶重點萃取。
    /// 2026-10-29 的九次評測裡，選 Claude 的那一組有九成以上的花費其實是 OpenAI；
    /// 決策窗也因此顯示出使用者沒有選的模型。
    ///
    /// 規則：
    ///   Auto：幫手照舊用各自的預設模型（那是 Auto 的本意）。
    ///   手動：每一個步驟都先用使用者選的那個模型。
    ///         那個模型確定失敗（或根本做不到）時，可以改用其他可用的模型完成，
    ///         **但一定在答案最上方告訴使用者**（2026-10-29 使用者指示，見 ModelSwitchNotice）。
    ///         短小的判斷步驟（意圖、記憶）則直接改走不需要模型的後備，不必換模型。
    ///
    /// 界線（刻意不納入）：
    ///   · 使用者在確認框明確同意的產出（圖片、影片、檔案）。那是經過同意的，不是背後偷跑，
    ///     決策窗的「參與」欄會照逐筆帳目列出。
    ///   · 不含語言模型的工具，例如 Perplexity Search API 回傳的是連結清單，不產生任何文字判斷。
    /// </summary>
    public sealed class ModelScope
    {
        public bool IsManual { get; }

        /// <summary>手動模式下使用者選的模型（應用程式內部 id）。Auto 時為空字串。</summary>
        public string SelectedModelId { get; }

        /// <summary>使用者在確認框裡改選的撰寫者（null＝沒改，用預設）。</summary>
        public string? ApprovedAuthorModelId { get; }

        /// <summary>使用者在確認框裡對配圖的回答（null＝沒問或沒改）。</summary>
        public bool? IllustrationsApproved { get; }

        private ModelScope(bool isManual, string selectedModelId,
                           string? approvedAuthor = null, bool? illustrations = null)
        {
            IsManual = isManual;
            SelectedModelId = selectedModelId ?? "";
            ApprovedAuthorModelId = string.IsNullOrWhiteSpace(approvedAuthor) ? null : approvedAuthor.Trim();
            IllustrationsApproved = illustrations;
        }

        public static ModelScope Auto { get; } = new(false, "");

        public static ModelScope Manual(string? modelId) => new(true, (modelId ?? "").Trim());

        /// <summary>
        /// 把確認框的回答併進來。使用者在那裡親手選的撰寫者與配圖，就算是所選模型以外的，
        /// 也屬於「選擇之內」——那是在產生的那一刻、看得到的情況下做的選擇，不是背後偷換。
        /// </summary>
        public ModelScope WithApproval(GenerationApproval? approval)
            => approval == null
                ? this
                : new ModelScope(IsManual, SelectedModelId,
                    approval.AuthorModelId ?? ApprovedAuthorModelId,
                    approval.Illustrations ?? IllustrationsApproved);

        /// <summary>
        /// 寫文件的模型（簡報、報告、表格、配圖企劃、單頁改字）。
        /// 確認框改選過就用改選的；否則手動＝節點選的模型、Auto＝個人化設定的預設。
        /// </summary>
        public string DocumentAuthorModel(string autoDefault)
            => ApprovedAuthorModelId
               ?? (IsManual && SelectedModelId.Length > 0 ? SelectedModelId : autoDefault);

        /// <summary>
        /// 最佳品質（Claude 文件技能）要用哪個 Claude。
        /// 回傳 null＝這次的撰寫者不是 Claude，技能路徑用不了，改走一般版面——
        /// 不能因為「最佳品質只有 Claude 會」就偷偷把撰寫者換成 Claude。
        /// </summary>
        public string? SkillsModel(string autoDefault)
        {
            if (!IsManual && ApprovedAuthorModelId == null)
                return autoDefault;

            string author = DocumentAuthorModel(autoDefault);
            return AiModelHelper.GetProviderKind(author) == AiProviderKind.Claude ? author : null;
        }

        /// <summary>
        /// 文件裡能不能加配圖（會用到圖片模型）。這是許可，不是決定——
        /// 許可之後要不要真的配圖，仍由配圖政策依內容判斷。
        /// 手動模式預設不許可：圖片模型不是使用者選的。確認框勾了才許可。
        /// </summary>
        public bool IllustrationsAllowed(bool autoDefault = true)
            => IllustrationsApproved ?? (!IsManual && autoDefault);

        /// <summary>
        /// 幫手（意圖判斷、記憶萃取這類短呼叫）該用哪個模型。
        /// 回傳 null＝不要呼叫任何模型，改走不需要模型的後備。
        /// </summary>
        public string? HelperModel(string autoDefault)
        {
            if (!IsManual)
                return autoDefault;

            if (SelectedModelId.Length == 0)
                return null;

            // Deep Research 一次動輒數十次搜尋、單價以美元計，拿它判斷「要不要產出簡報」這種
            // 兩百 token 的小事是荒謬的。手動選了它，幫手就改走不需要模型的後備。
            if (AiModelHelper.IsPerplexityDeepResearchModel(SelectedModelId))
                return null;

            return SelectedModelId;
        }

        /// <summary>
        /// 搜尋研究要送給 Perplexity Agent API 的模型 id。
        /// 回傳 null＝所選的模型在 Agent API 上沒有；呼叫端改用預設模型，並告知使用者換過。
        /// </summary>
        public string? SearchAgentModel(string autoDefault)
            => IsManual ? ToAgentApiModel(SelectedModelId) : autoDefault;

        /// <summary>
        /// 應用程式內部的模型 id → Perplexity Agent API 的模型 id。
        ///
        /// 只列 Agent API 頁面上真的有的型號（docs.perplexity.ai/docs/agent-api/models，2026-10-29 核對）。
        /// 刻意寫成明確的對照表，而不是「前面加上 openai/」：
        /// 對照表上沒有的型號送過去只會 400——與其送出去失敗再換，不如一開始就知道要換、並且講。
        ///
        /// 頁面沒有逐型號標明是否支援 web_search 工具。若某型號實際不支援，
        /// 搜尋端會改用預設模型重做一次，並告知使用者換過。
        /// </summary>
        public static string? ToAgentApiModel(string? modelId)
        {
            string id = AiModelRegistry.ResolveAlias((modelId ?? "").Trim()) ?? "";
            return AgentApiModels.TryGetValue(id, out var agentId) ? agentId : null;
        }

        private static readonly Dictionary<string, string> AgentApiModels = new(StringComparer.OrdinalIgnoreCase)
        {
            [AiModels.OpenAi_Gpt56] = "openai/gpt-5.6-sol",
            [AiModels.OpenAi_Gpt6] = "openai/gpt-6-astra",
            [AiModels.Claude_Sonnet5] = "anthropic/claude-sonnet-5-5",
            [AiModels.Claude_Opus5] = "anthropic/claude-opus-5-5",
            [AiModels.Perplexity_Sonar] = "perplexity/sonar",
            // Gemini 3.5 Flash / 3.1 Pro 與 Sonar Deep Research 不在 Agent API 的型號清單上。
        };

        /// <summary>這是不是 Perplexity Agent API 的模型 id（例如 anthropic/claude-sonnet-5-5）。</summary>
        public static bool IsAgentApiModel(string? modelId)
            => !string.IsNullOrWhiteSpace(modelId)
               && AgentApiModels.Values.Contains(modelId.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Agent API 的模型 id → 應用程式內部 id；不是 Agent API id 就原樣回傳。
        ///
        /// 決策窗靠內部 id 查顯示名稱，而查不到的 id 會退回「預設模型」的名字（GPT-5.6 Sol）。
        /// 不先轉回來的話，手動選 Claude 的那一次會顯示成「Claude Sonnet 5.5 + GPT-5.6 Sol」——
        /// 正是使用者回報的那個問題，只是換了一條路重新出現。
        /// </summary>
        public static string FromAgentApiModel(string? modelId)
        {
            string id = (modelId ?? "").Trim();
            foreach (var kv in AgentApiModels)
                if (string.Equals(kv.Value, id, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            return id;
        }


    }
}
