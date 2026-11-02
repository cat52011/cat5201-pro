using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    public static class AiModels
    {
        // 2026-09-17 模型世代更新（官方 ID 已查證）：
        //  OpenAI：GPT-5.6 是家族（sol/terra/luna），取旗艦 Sol 接替 gpt-5.5；GPT-6 目前唯一變體為 Astra。
        //  Anthropic：無日期後綴的 ID 本身就是固定快照。
        public const string OpenAi_Gpt56 = "gpt-5.6-sol";
        public const string OpenAi_Gpt6 = "gpt-6-astra";
        // 2026-10-29：升到 Sonnet 5.5（官方最新，與 Sonnet 5 同價 $2/$10）。
        // 比照 Opus 5.5 的升級方式：常數名沿用、舊 ID 由 AiModelRegistry 的別名表路由到新版，
        // 歷史紀錄裡的 claude-sonnet-5 仍照實顯示成「Claude Sonnet 5」。
        // 注意 Sonnet 5.5 不接受 thinking:{type:"disabled"}，小預算呼叫的關法見 ClaudeChatService。
        public const string Claude_Sonnet5 = "claude-sonnet-5-5";
        // 2026-09-25：升到 Opus 5.5（官方最新，且比 Opus 5 便宜：$4/$20 vs $5/$25）。
        // 常數名沿用 Claude_Opus5，避免 18 處呼叫端改名；歷史紀錄的 claude-opus-5 由
        // AiModelRegistry 的 LegacyDisplayNames 照實顯示成「Claude Opus 5」，不會被改寫。
        public const string Claude_Opus5 = "claude-opus-5-5";
        public const string Perplexity_Sonar = "pplx-sonar";
        public const string Perplexity_SonarDeepResearch = "pplx-sonar-deep-research";

        public const string DefaultPerplexitySonarApiModel = "sonar";

        public static string DefaultNodeModel => AiModelRegistry.Default.Id;
        public static string DefaultOpenAiModel => OpenAi_Gpt56;
        public const string DefaultClaudeModel = Claude_Sonnet5;

        public static IReadOnlyList<string> AllNodeModels =>
            AiModelRegistry.All.Select(x => x.Id).ToList();

        public static bool IsKnownNodeModel(string? model)
            => AiModelRegistry.IsKnown(model);
    }
}
