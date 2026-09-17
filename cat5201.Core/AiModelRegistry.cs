using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    public static class AiModelRegistry
    {
        private static readonly IReadOnlyList<AiModelDefinition> _all = new[]
        {
            new AiModelDefinition
            {
                Id = AiModels.OpenAi_Gpt56,
                DisplayName = "GPT-5.6 Sol",
                IconPath = "pack://application:,,,/Assets/OpenAI_logo.png",
                Provider = AiProviderType.OpenAI,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files
                    | AiModelCapability.LongContext | AiModelCapability.ImageGeneration | AiModelCapability.Code,
                CostTier = AiCostTier.Standard,
                IsDefaultNodeModel = true,
                ServiceModel = AiModels.OpenAi_Gpt56,
                IsDeepResearch = false
            },
            // GPT-6 Astra：OpenAI 旗艦（$10/$50，約 Opus 的兩倍價）。只供手動選擇——
            // 刻意不放進 API Auto 的推薦清單，Auto 模式永遠不會自動升級到最貴的模型。
            new AiModelDefinition
            {
                Id = AiModels.OpenAi_Gpt6,
                DisplayName = "GPT-6 Astra",
                IconPath = "pack://application:,,,/Assets/OpenAI_logo.png",
                Provider = AiProviderType.OpenAI,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files
                    | AiModelCapability.LongContext | AiModelCapability.ImageGeneration | AiModelCapability.Code,
                CostTier = AiCostTier.Premium,
                IsDefaultNodeModel = false,
                ServiceModel = AiModels.OpenAi_Gpt6,
                IsDeepResearch = false
            },
            new AiModelDefinition
            {
                Id = AiModels.Claude_Sonnet5,
                DisplayName = "Claude Sonnet 5",
                IconPath = "pack://application:,,,/Assets/Claude_logo.png",
                Provider = AiProviderType.Claude,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files
                    | AiModelCapability.LongContext | AiModelCapability.Code,
                CostTier = AiCostTier.Standard,
                IsDefaultNodeModel = false,
                ServiceModel = AiModels.Claude_Sonnet5,
                IsDeepResearch = false
            },
            new AiModelDefinition
            {
                Id = AiModels.Claude_Opus5,
                DisplayName = "Claude Opus 5",
                IconPath = "pack://application:,,,/Assets/Claude_logo.png",
                Provider = AiProviderType.Claude,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files
                    | AiModelCapability.LongContext | AiModelCapability.Code,
                CostTier = AiCostTier.Premium,
                IsDefaultNodeModel = false,
                ServiceModel = AiModels.Claude_Opus5,
                IsDeepResearch = false
            },
            new AiModelDefinition
            {
                Id = "pplx-sonar",
                DisplayName = "Perplexity Sonar",
                IconPath = "pack://application:,,,/Assets/Perplexity_logo.png",
                Provider = AiProviderType.Perplexity,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Search,
                CostTier = AiCostTier.Economy,
                IsDefaultNodeModel = false,
                ServiceModel = "sonar",
                IsDeepResearch = false
            },
            new AiModelDefinition
            {
                Id = "pplx-sonar-deep-research",
                DisplayName = "Perplexity Deep Research",
                IconPath = "pack://application:,,,/Assets/Perplexity_logo.png",
                Provider = AiProviderType.Perplexity,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Search | AiModelCapability.LongContext,
                CostTier = AiCostTier.Standard,
                IsDefaultNodeModel = false,
                ServiceModel = "sonar-deep-research",
                IsDeepResearch = true
            },

            // Multi-Model v1：Gemini 已啟用（GeminiProvider + GeminiChatService + router case 已接線）。
            // v1 能力宣告為文字 / 長文 / 程式碼（未宣告 Images/Search，避免把圖片/搜尋任務導到尚未實作的路徑）。
            new AiModelDefinition
            {
                Id = "gemini-3.5-flash",
                DisplayName = "Gemini 3.5 Flash",
                IconPath = "pack://application:,,,/Assets/Gemini_logo.png",
                Provider = AiProviderType.Gemini,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.LongContext | AiModelCapability.Code,
                CostTier = AiCostTier.Economy,
                IsAvailable = true,
                IsDefaultNodeModel = false,
                ServiceModel = "gemini-3.5-flash",
                IsDeepResearch = false
            },
            // Gemini 3.5 Pro 尚未公開——Google I/O 2026 只發 3.5 Flash；Pro 層目前最新為 gemini-3.1-pro。
            // 注意：API model ID 帶 -preview 後綴（v1beta 端點要求），節點 Id 維持乾淨顯示。
            new AiModelDefinition
            {
                Id = "gemini-3.1-pro",
                DisplayName = "Gemini 3.1 Pro",
                IconPath = "pack://application:,,,/Assets/Gemini_logo.png",
                Provider = AiProviderType.Gemini,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.LongContext | AiModelCapability.Code,
                CostTier = AiCostTier.Standard,
                IsAvailable = true,
                IsDefaultNodeModel = false,
                ServiceModel = "gemini-3.1-pro-preview",
                IsDeepResearch = false
            }
        };

        // ===== 舊世代模型 ID（2026-09-17 升級前）=====
        // 使用者的舊專案檔（節點模型）、個人化偏好（任務→模型路由）、執行紀錄都存著這些 ID。
        //  - 路由用 LegacyAliases：舊選擇自動改走同廠牌的後繼模型（選 Claude 的人不會被默默換成 GPT）；
        //  - 顯示用 LegacyDisplayNames：歷史執行紀錄照實顯示「當時真正跑的模型」，
        //    絕不把一筆 Sonnet 4.6 跑出來的紀錄改寫成「Claude Sonnet 5」——可稽查的底線。
        private static readonly Dictionary<string, string> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.5"] = AiModels.OpenAi_Gpt56,
            ["claude-sonnet-4-6"] = AiModels.Claude_Sonnet5,
            ["claude-opus-4-8"] = AiModels.Claude_Opus5,
        };

        private static readonly Dictionary<string, string> LegacyDisplayNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.5"] = "GPT-5.5",
            ["claude-sonnet-4-6"] = "Claude Sonnet 4.6",
            ["claude-opus-4-8"] = "Claude Opus 4.8",
        };

        /// <summary>舊世代 ID → 後繼模型 ID；非舊 ID 原樣返回。只用於「要實際執行/路由」的場合。</summary>
        public static string? ResolveAlias(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return modelId;

            return LegacyAliases.TryGetValue(modelId.Trim(), out var successor) ? successor : modelId;
        }

        /// <summary>舊世代 ID 的原始顯示名稱（歷史紀錄用）；非舊 ID 回 null。</summary>
        public static string? TryGetLegacyDisplayName(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return null;

            return LegacyDisplayNames.TryGetValue(modelId.Trim(), out var name) ? name : null;
        }

        public static IReadOnlyList<AiModelDefinition> All => _all;

        /// <summary>只含已啟用的模型（休眠擴充點不列入）。UI 選單、fallback、capability 重導都用這個。</summary>
        public static IReadOnlyList<AiModelDefinition> Available =>
            _all.Where(x => x.IsAvailable).ToList();

        public static AiModelDefinition Default =>
            _all.First(x => x.IsDefaultNodeModel);

        public static AiModelDefinition? Find(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return null;

            return _all.FirstOrDefault(x =>
                string.Equals(x.Id, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsKnown(string? modelId)
            => Find(modelId) != null;

        public static bool IsAvailable(string? modelId)
            => Find(modelId)?.IsAvailable == true;

        /// <summary>列出具備指定能力、已啟用的模型，依成本層級由便宜到貴排序。</summary>
        public static IReadOnlyList<AiModelDefinition> WithCapability(AiModelCapability required)
        {
            return _all
                .Where(x => x.IsAvailable && (x.Capabilities & required) == required)
                .OrderBy(x => (int)x.CostTier)
                .ToList();
        }

        /// <summary>在具備指定能力的已啟用模型中，挑成本最低者；找不到回 null。</summary>
        public static AiModelDefinition? CheapestWithCapability(AiModelCapability required)
            => WithCapability(required).FirstOrDefault();
    }
}
