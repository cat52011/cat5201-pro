using System.Text.Json;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 小預算呼叫的「關思考」設定，每個 Claude 型號合法的寫法不同。
    ///
    /// 2026-10-29 發現：Claude_Opus5 早在 2026-09-25 就升成 claude-opus-5-5，
    /// 但小預算呼叫一律送 thinking:{type:"disabled"}——那在 Opus 5.5 上任何 effort 都回 400。
    /// 400 之後會被 fallback 默默換模型重跑：不報錯、多花一次錢、決策窗顯示的模型也不是使用者選的。
    /// Sonnet 5.5 同樣不接受 disabled，關法是 between_tools。
    /// </summary>
    public class ClaudeThinkingConfigTests
    {
        private static readonly JsonSerializerOptions DropNulls = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>把 thinking 與 output_config 序列化成實際會送出的 JSON 片段。</summary>
        private static string Payload(string model, int maxTokens)
            => JsonSerializer.Serialize(new
            {
                thinking = ClaudeChatService.BuildThinking(model, maxTokens),
                output_config = ClaudeChatService.BuildOutputConfig(model, maxTokens),
            }, DropNulls);

        [Theory]
        [InlineData("claude-opus-5-5")]
        [InlineData("claude-fable-5-1")]
        [InlineData("claude-fable-5")]
        public void ModelsWithoutAnOffSwitchGetLowEffortAndNoThinkingField(string model)
        {
            string json = Payload(model, 300);

            Assert.DoesNotContain("disabled", json);          // 會 400
            Assert.DoesNotContain("\"thinking\"", json);      // 不帶 thinking 欄位
            Assert.Contains("\"effort\":\"low\"", json);       // 用 effort 壓住思考
        }

        [Fact]
        public void Sonnet55UsesBetweenToolsWithNothingElse()
        {
            string json = Payload("claude-sonnet-5-5", 300);

            Assert.Contains("\"type\":\"between_tools\"", json);
            Assert.DoesNotContain("disabled", json);

            // between_tools 不得與其他欄位同送，effort 也必須 ≤ high——所以 output_config 一律不送。
            Assert.DoesNotContain("output_config", json);
            Assert.DoesNotContain("effort", json);
        }

        [Theory]
        [InlineData("claude-sonnet-5")]      // 舊世代仍合法
        [InlineData("claude-opus-5")]
        [InlineData("claude-haiku-5-5")]
        public void ModelsThatAcceptDisabledStillGetIt(string model)
        {
            string json = Payload(model, 300);

            Assert.Contains("\"type\":\"disabled\"", json);
            Assert.DoesNotContain("output_config", json);     // disabled + xhigh/max 會 400，所以不送 effort
        }

        [Theory]
        [InlineData("claude-opus-5-5")]
        [InlineData("claude-sonnet-5-5")]
        [InlineData("claude-sonnet-5")]
        [InlineData("claude-fable-5-1")]
        public void LargeBudgetCallsUseAdaptiveMediumOnEveryModel(string model)
        {
            string json = Payload(model, 8000);

            Assert.Contains("\"type\":\"adaptive\"", json);
            Assert.Contains("\"effort\":\"medium\"", json);
        }

        /// <summary>
        /// 模型選單上的每一個 Claude 型號，小預算時都不能送出它不接受的設定。
        /// 選單換型號時（像這次 Sonnet 5 → 5.5）這條會先爆，而不是等使用者在執行時撞到 400。
        /// </summary>
        [Fact]
        public void EveryClaudeModelInTheMenuGetsALegalSmallBudgetConfig()
        {
            foreach (var def in AiModelRegistry.All)
            {
                if (def.Provider != AiProviderType.Claude) continue;

                string json = Payload(def.Id, 300);
                var mode = ClaudeChatService.ResolveSmallBudgetThinking(def.Id);

                if (def.Id.StartsWith("claude-opus-5-5") || def.Id.StartsWith("claude-sonnet-5-5")
                    || def.Id.StartsWith("claude-fable-5"))
                    Assert.True(mode != ClaudeChatService.SmallBudgetThinking.Disable,
                        $"{def.Id} 不接受 thinking:disabled，但小預算呼叫仍會送它");

                Assert.False(json.Contains("disabled") && json.Contains("effort"),
                    $"{def.Id}：disabled 不可與 effort 同送");
            }
        }

        // ───────────────────────── Sonnet 5 → 5.5 升級 ─────────────────────────

        [Fact]
        public void TheMenuOffersSonnet55()
        {
            Assert.Equal("claude-sonnet-5-5", AiModels.Claude_Sonnet5);
            Assert.Equal("Claude Sonnet 5.5", AiModelRegistry.Find(AiModels.Claude_Sonnet5)?.DisplayName);
        }

        [Fact]
        public void OldProjectFilesRouteForwardButHistoryStaysTruthful()
        {
            // 要實際執行時：舊專案檔裡的 claude-sonnet-5 路由到 5.5。
            Assert.Equal("claude-sonnet-5-5", AiModelRegistry.ResolveAlias("claude-sonnet-5"));

            // 顯示歷史時：照實顯示當時跑的是 Sonnet 5——
            // 2026-10-29 的九次評測就是用它跑的，紀錄不能被改寫成 5.5。
            Assert.Equal("Claude Sonnet 5", AiModelRegistry.TryGetLegacyDisplayName("claude-sonnet-5"));
        }

        [Fact]
        public void Sonnet55IsPricedAndHistoricalSonnet5StillIs()
        {
            // 兩者同價 $2/$10（Anthropic 官方，2026-10-29 查證）。
            var now = ModelCostEstimator.FromUsage("claude-sonnet-5-5", 1_000_000, 1_000_000).UsdCost;
            var old = ModelCostEstimator.FromUsage("claude-sonnet-5", 1_000_000, 1_000_000).UsdCost;

            Assert.Equal(12.00, now, 2);
            Assert.Equal(12.00, old, 2);   // 舊紀錄的歷史成本不能因為升級而變成預設價
        }
    }
}
