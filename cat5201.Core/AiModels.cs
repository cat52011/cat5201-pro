using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    public static class AiModels
    {
        // 2026-09-17 模型世代更新（官方 ID 已查證）：
        //  OpenAI：GPT-5.6 是家族（sol/terra/luna），取旗艦 Sol 接替 gpt-5.5；GPT-6 目前唯一變體為 Astra。
        //  Anthropic：Claude Sonnet 5 / Opus 5（無日期後綴的 ID 本身就是固定快照）。
        public const string OpenAi_Gpt56 = "gpt-5.6-sol";
        public const string OpenAi_Gpt6 = "gpt-6-astra";
        public const string Claude_Sonnet5 = "claude-sonnet-5";
        public const string Claude_Opus5 = "claude-opus-5";
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
