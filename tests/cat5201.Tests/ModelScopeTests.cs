using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 手動選擇模型時，不該出現選擇之外的模型參與（2026-10-29 使用者指示）。
    ///
    /// 在這之前，手動模式只決定「誰寫最後的答案」，背後有四個幫手寫死 GPT：
    /// 輸出意圖判斷、配圖意圖判斷、搜尋研究、記憶重點萃取。
    /// 九次評測裡選 Claude 的那一組，九成以上的花費其實是 OpenAI。
    /// </summary>
    public class ModelScopeTests
    {
        private const string AutoHelper = AiModels.OpenAi_Gpt56;
        private const string AutoSearch = "openai/gpt-5.6-sol";

        [Fact]
        public void AutoModeKeepsEachHelpersOwnDefault()
        {
            Assert.Equal(AutoHelper, ModelScope.Auto.HelperModel(AutoHelper));
            Assert.Equal(AutoSearch, ModelScope.Auto.SearchAgentModel(AutoSearch));
            Assert.False(ModelScope.Auto.IsManual);
        }

        [Theory]
        [InlineData(AiModels.OpenAi_Gpt56, "openai/gpt-5.6-sol")]
        [InlineData(AiModels.OpenAi_Gpt6, "openai/gpt-6-astra")]
        [InlineData(AiModels.Claude_Sonnet5, "anthropic/claude-sonnet-5-5")]
        [InlineData(AiModels.Claude_Opus5, "anthropic/claude-opus-5-5")]
        [InlineData(AiModels.Perplexity_Sonar, "perplexity/sonar")]
        public void ManualModeUsesTheSelectedModelForEverything(string selected, string expectedAgentModel)
        {
            var scope = ModelScope.Manual(selected);

            Assert.Equal(selected, scope.HelperModel(AutoHelper));          // 意圖判斷、記憶萃取
            Assert.Equal(expectedAgentModel, scope.SearchAgentModel(AutoSearch)); // 搜尋研究
        }

        /// <summary>
        /// 選的模型在搜尋服務上沒有時，改用預設模型完成，但一定附上告知。
        /// 2026-10-29 使用者指示：「當選定模型確定失敗時，系統會自動切換到其他可用模型但通知使用者」。
        /// 換本身不是問題，沒講才是——以前的版本就是默默換成 GPT。
        /// </summary>
        [Theory]
        [InlineData("gemini-3.5-flash", "Gemini 3.5 Flash")]
        [InlineData("gemini-3.1-pro", "Gemini 3.1 Pro")]
        public void ManualModeSearchSwitchesToTheDefaultAndSaysSo(string selected, string selectedName)
        {
            var scope = ModelScope.Manual(selected);
            Assert.Null(scope.SearchAgentModel(AutoSearch));     // 所選模型做不到

            var (model, notice) = SearchCapability.ResolveSearchModel(scope, AutoSearch);

            Assert.Equal(AutoSearch, model);                     // 改用預設完成
            Assert.NotNull(notice);                              // 而且一定講
            Assert.Contains("已改用其他模型", notice);
            Assert.Contains(selectedName, notice);               // 講的是使用者認得的名字
            Assert.Contains("GPT-5.6 Sol", notice);              // 也講清楚換成誰
        }

        /// <summary>所選模型做得到時不換，也不需要任何說明。</summary>
        [Fact]
        public void ManualModeSearchWithASupportedModelNeedsNoNotice()
        {
            var (model, notice) = SearchCapability.ResolveSearchModel(
                ModelScope.Manual(AiModels.Claude_Sonnet5), AutoSearch);

            Assert.Equal("anthropic/claude-sonnet-5-5", model);
            Assert.Null(notice);
        }

        [Fact]
        public void DeepResearchIsNotUsedForTinyHelperCalls()
        {
            // 用一次以美元計價的深度研究去判斷「要不要產出簡報」是荒謬的——改走不需要模型的後備。
            Assert.Null(ModelScope.Manual(AiModels.Perplexity_SonarDeepResearch).HelperModel(AutoHelper));
        }

        [Fact]
        public void ManualWithNoModelCallsNothing()
            => Assert.Null(ModelScope.Manual("").HelperModel(AutoHelper));

        [Fact]
        public void LegacyIdsInOldProjectFilesMapForward()
        {
            // 舊專案檔存的是 claude-sonnet-5；手動模式下應該用它的後繼者，而不是對不上就放棄搜尋。
            Assert.Equal("anthropic/claude-sonnet-5-5", ModelScope.ToAgentApiModel("claude-sonnet-5"));
        }

        /// <summary>
        /// 核心不變式：選單上的每一個模型，手動模式下的搜尋要嘛不做，
        /// 要嘛用同一家、同一個模型——絕不會出現另一家的模型。
        /// 選單換型號時這條會先爆。
        /// </summary>
        [Fact]
        public void NoMenuModelEverPullsInAnotherVendor()
        {
            foreach (var def in AiModelRegistry.All)
            {
                string? agent = ModelScope.Manual(def.Id).SearchAgentModel(AutoSearch);
                if (agent == null) continue;

                string expectedPrefix = def.Provider switch
                {
                    AiProviderType.OpenAI => "openai/",
                    AiProviderType.Claude => "anthropic/",
                    AiProviderType.Perplexity => "perplexity/",
                    AiProviderType.Gemini => "google/",
                    _ => "?",
                };

                Assert.True(agent.StartsWith(expectedPrefix, StringComparison.Ordinal),
                    $"{def.Id}（{def.Provider}）手動模式下的搜尋竟然用 {agent}");
            }
        }

        /// <summary>對照表裡的每一個 Agent API 型號都要有價目，否則成本會悄悄退回預設價。</summary>
        [Fact]
        public void EveryMappedAgentModelIsPriced()
        {
            foreach (var def in AiModelRegistry.All)
            {
                string? agent = ModelScope.ToAgentApiModel(def.Id);
                if (agent == null) continue;

                Assert.Equal(agent, ModelCostEstimator.ResolvePricingModelId(agent));
                Assert.True(ModelCostEstimator.FromUsage(agent, 1_000_000, 0).UsdCost > 0, agent + " 沒有價目");
            }
        }

        /// <summary>2026-10-29 Perplexity 頁面上的價格；舊值 $5/$30 讓九次評測的搜尋成本高估約兩成五。</summary>
        [Theory]
        [InlineData("openai/gpt-5.6-sol", 4.00, 20.00)]
        [InlineData("anthropic/claude-sonnet-5-5", 2.00, 10.00)]
        [InlineData("anthropic/claude-opus-5-5", 4.00, 20.00)]
        [InlineData("perplexity/sonar", 0.25, 2.50)]
        public void AgentApiPricesMatchThePublishedPage(string id, double input, double output)
        {
            Assert.Equal(input, ModelCostEstimator.FromUsage(id, 1_000_000, 0).UsdCost, 2);
            Assert.Equal(output, ModelCostEstimator.FromUsage(id, 0, 1_000_000).UsdCost, 2);
        }
    }

    /// <summary>服務商回報的金額要真的被記下來，而不是只在備註寫「取自 API 回報」。</summary>
    [Collection("spend-ledger")]
    public class ReportedCostTests
    {
        [Fact]
        public void ReportedCostWinsOverThePriceTable()
        {
            using var _ = UsageMeter.Purpose("搜尋研究");

            var record = UsageMeter.RecordLlm("openai/gpt-5.6-sol", 11_841, 2_119,
                note: "費用取自 API 回報", reportedUsd: 0.0931);

            Assert.NotNull(record);
            Assert.Equal(0.0931, record!.UsdCost, 6);

            // 對照：同樣的 token 用價目表算出來的是另一個數字——兩者不一樣，備註才有意義。
            double table = ModelCostEstimator.FromUsage("openai/gpt-5.6-sol", 11_841, 2_119).UsdCost;
            Assert.NotEqual(table, record.UsdCost, 4);
        }

        [Fact]
        public void WithoutAReportTheTableIsUsed()
        {
            using var _ = UsageMeter.Purpose("搜尋研究");

            var record = UsageMeter.RecordLlm("openai/gpt-5.6-sol", 10_000, 1_000);
            double table = ModelCostEstimator.FromUsage("openai/gpt-5.6-sol", 10_000, 1_000).UsdCost;

            Assert.Equal(table, record!.UsdCost, 6);
        }
    }
    /// <summary>
    /// 「所選模型確定失敗、系統改用其他模型」要怎麼講（2026-10-29 使用者指示）。
    /// 以前手動模式的 fallback 是無聲的：使用者只看到一份答案，不知道是誰寫的。
    /// </summary>
    public class ModelSwitchNoticeTests
    {
        private static AiFallbackExecutionResult Ran(string actual, params (string Model, bool Ok, string Error)[] attempts)
            => new()
            {
                IsSuccess = true,
                Text = "答案",
                ActualModelId = actual,
                Attempts = attempts.Select((a, i) => new AiFallbackAttempt
                {
                    AttemptIndex = i + 1, ModelId = a.Model, Success = a.Ok, ErrorMessage = a.Error,
                }).ToList(),
            };

        [Fact]
        public void NoSwitchNoNotice()
            => Assert.Null(ModelSwitchNotice.Build(AiModels.Claude_Opus5,
                Ran(AiModels.Claude_Opus5, (AiModels.Claude_Opus5, true, "")), "主回覆"));

        [Fact]
        public void ASwitchNamesBothModelsTheStepAndTheReason()
        {
            string? notice = ModelSwitchNotice.Build(
                AiModels.Claude_Opus5,
                Ran(AiModels.OpenAi_Gpt56,
                    (AiModels.Claude_Opus5, false, "400 invalid_request_error"),
                    (AiModels.OpenAi_Gpt56, true, "")),
                "主回覆");

            Assert.NotNull(notice);
            Assert.Contains("已改用其他模型", notice);
            Assert.Contains("Claude Opus 5.5", notice);      // 使用者選的
            Assert.Contains("GPT-5.6 Sol", notice);          // 實際完成的
            Assert.Contains("主回覆", notice);                // 哪一步
            Assert.Contains("400", notice);                   // 為什麼
        }

        [Fact]
        public void AWholeFailureIsNotDescribedAsASwitch()
        {
            // 全部都失敗時另有錯誤訊息；這裡講「改由某某完成」就是在說謊。
            var failed = new AiFallbackExecutionResult { IsSuccess = false, ErrorMessage = "全部失敗" };
            Assert.Null(ModelSwitchNotice.Build(AiModels.Claude_Opus5, failed, "主回覆"));
        }

        [Fact]
        public void UnknownIdsAreNotCollapsedIntoTheDefaultModel()
        {
            // NormalizeNodeModel 會把查不到的 id 換成預設模型——用它比對的話，
            // 「所選：某個未知 id、實際：GPT-5.6 Sol」會被誤判成「沒有換」。
            string? notice = ModelSwitchNotice.Build("some-unknown-model",
                Ran(AiModels.OpenAi_Gpt56, ("some-unknown-model", false, "x")), "主回覆");
            Assert.NotNull(notice);
        }

        [Fact]
        public void LegacyIdThatRoutesToItsSuccessorIsNotASwitch()
        {
            // 舊專案檔存的是 claude-sonnet-5，實際跑的是 5.5——那是升級路由，不是失敗替補。
            Assert.Null(ModelSwitchNotice.Build("claude-sonnet-5",
                Ran(AiModels.Claude_Sonnet5, (AiModels.Claude_Sonnet5, true, "")), "主回覆"));
        }

        [Fact]
        public void CombineDeduplicatesAndEndsWithABlankLine()
        {
            string a = "⚠️ A", b = "⚠️ B";
            Assert.Equal("⚠️ A\n\n⚠️ B\n\n", ModelSwitchNotice.Combine(new[] { a, b, a, "" }));
            Assert.Equal("", ModelSwitchNotice.Combine(Array.Empty<string>()));
        }
    }

    /// <summary>
    /// 文件的撰寫者與配圖（使用者選了 (a)：節點選的模型一律優先），
    /// 加上「說不定使用者想看其他 AI 寫的簡報」——在確認框裡親手改選的撰寫者也算選擇之內。
    /// </summary>
    public class DocumentScopeTests
    {
        private const string EngineAuthor = AiModels.Claude_Sonnet5;   // 個人化設定的引擎
        private const string SkillsDefault = AiModels.Claude_Opus5;

        [Fact]
        public void AutoModeKeepsThePersonalisationSettings()
        {
            Assert.Equal(EngineAuthor, ModelScope.Auto.DocumentAuthorModel(EngineAuthor));
            Assert.Equal(SkillsDefault, ModelScope.Auto.SkillsModel(SkillsDefault));
            Assert.True(ModelScope.Auto.IllustrationsAllowed());
        }

        [Fact]
        public void ManualModeTheNodeModelWritesTheDocument()
        {
            var scope = ModelScope.Manual(AiModels.OpenAi_Gpt56);

            Assert.Equal(AiModels.OpenAi_Gpt56, scope.DocumentAuthorModel(EngineAuthor));
            // 最佳品質只有 Claude 會；撰寫者不是 Claude 就不走技能路徑，而不是偷偷換成 Claude。
            Assert.Null(scope.SkillsModel(SkillsDefault));
            // 圖片模型不是使用者選的：預設不配圖。
            Assert.False(scope.IllustrationsAllowed());
        }

        [Fact]
        public void ManualClaudeUsesItsOwnModelForBestQuality()
            => Assert.Equal(AiModels.Claude_Sonnet5,
                ModelScope.Manual(AiModels.Claude_Sonnet5).SkillsModel(SkillsDefault));   // 不是被換成 Opus

        [Fact]
        public void AWriterChosenInTheDialogIsHonoured()
        {
            // 節點用 GPT 聊天，但使用者在確認框選了 Claude Opus 寫簡報。
            var scope = ModelScope.Manual(AiModels.OpenAi_Gpt56)
                .WithApproval(new GenerationApproval(true, AiModels.Claude_Opus5));

            Assert.Equal(AiModels.Claude_Opus5, scope.DocumentAuthorModel(EngineAuthor));
            Assert.Equal(AiModels.Claude_Opus5, scope.SkillsModel(SkillsDefault));
            // 撰寫者改選不影響其他幫手：意圖判斷、搜尋仍是節點選的模型。
            Assert.Equal(AiModels.OpenAi_Gpt56, scope.HelperModel(AiModels.OpenAi_Gpt56));
        }

        [Fact]
        public void IllustrationsTickedInTheDialogAreAllowedInManualMode()
            => Assert.True(ModelScope.Manual(AiModels.OpenAi_Gpt56)
                .WithApproval(new GenerationApproval(true, Illustrations: true))
                .IllustrationsAllowed());

        [Fact]
        public void IllustrationsUntickedInTheDialogAreRefusedInAutoMode()
            => Assert.False(ModelScope.Auto
                .WithApproval(new GenerationApproval(true, Illustrations: false))
                .IllustrationsAllowed());

        [Fact]
        public void AnUnchangedDialogLeavesAutoModeExactlyAsBefore()
        {
            var scope = ModelScope.Auto.WithApproval(GenerationApproval.Accepted);
            Assert.Equal(EngineAuthor, scope.DocumentAuthorModel(EngineAuthor));
            Assert.Equal(SkillsDefault, scope.SkillsModel(SkillsDefault));
            Assert.True(scope.IllustrationsAllowed());
        }
    }
}
