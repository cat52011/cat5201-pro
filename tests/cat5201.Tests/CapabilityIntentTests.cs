using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 能力規劃（要不要搜尋／推理／寫程式）改由第一層語意判斷決定，關鍵字只剩判斷失敗時的後援。
    /// 2026-11-05 模擬面試：輸出判斷早就交給模型，能力規劃卻還在看「最新」「股價」——換說法或換語言就漏。
    ///
    /// 用假模型驗證整條路：同一次意圖判斷順便回能力（不多一次呼叫）→ 規劃照它走 → 判斷失敗才退回關鍵字並留紀錄。
    ///
    /// 2026-11-05 使用者指示的模式規則：
    ///  - 手動（含協作）：只在語意判斷說要即時資料時搜尋，其他能力不加；答案由選的模型寫。
    ///  - 規則式 Auto（Auto＋關閉 API）：語意優先，拿不到才退回關鍵字——關鍵字只留在這個模式。
    ///  - API Auto：只看語意；拿不到就不加額外能力。
    /// </summary>
    public class CapabilityIntentTests
    {
        // ===== 假模型 =====

        /// <summary>照固定答案回覆，並記下被呼叫幾次、收到什麼提示。</summary>
        private sealed class FakeModel
        {
            private readonly Func<string, string> _reply;
            public int Calls { get; private set; }
            public string LastSystemPrompt { get; private set; } = "";
            public string LastUserPrompt { get; private set; } = "";

            public FakeModel(Func<string, string> reply) => _reply = reply;

            public Task<string> Generate(string model, string system, string user, int maxTokens, CancellationToken ct)
            {
                Calls++;
                LastSystemPrompt = system;
                LastUserPrompt = user;
                return Task.FromResult(_reply(user));
            }
        }

        private static string Json(bool search, bool reasoning = false, bool code = false, bool presentation = false)
            => "{\"presentation\":" + Lower(presentation) + ",\"report\":false,\"table\":false,\"image\":false,\"video\":false," +
               "\"illustrations\":false,\"no_illustrations\":false,\"quality\":\"\",\"topic\":\"\",\"slides\":0,\"language\":\"\"," +
               "\"needs_search\":" + Lower(search) + ",\"needs_reasoning\":" + Lower(reasoning) + ",\"needs_code\":" + Lower(code) + "}";

        private static string Lower(bool b) => b ? "true" : "false";

        private static async Task<(OutputIntent intent, AgentCapabilityExecutionPlan plan, FakeModel model)> Resolve(
            string text, string reply, bool hasAttachments = false)
        {
            var model = new FakeModel(_ => reply);
            var resolver = new OutputIntentResolver(model.Generate);
            var intent = await resolver.ResolveAsync(text);
            var plan = AgentCapabilityPlanner.Build(AgentRegistry.Get(null), text, NodeTaskMode.Chat, hasAttachments, intent);
            return (intent, plan, model);
        }

        // ===== 語意判斷要求搜尋 / 不要求搜尋 =====

        [Fact]
        public async Task IntentSaysSearch_SearchIsRequired_WithOneModelCall()
        {
            var (intent, plan, model) = await Resolve("台積電這禮拜收在多少？", Json(search: true));

            Assert.NotNull(intent.Capabilities);
            Assert.True(plan.RequiresFreshFacts);
            Assert.Contains("search-capability", plan.RequiredCapabilityIds);
            Assert.Equal("search-capability", plan.OrderedCapabilityIds[0]);   // 搜尋永遠第一
            Assert.Equal(AgentCapabilityPlanner.SourceSemantic, plan.Source);

            // 不多花一次呼叫：輸出判斷與能力判斷是同一次。
            Assert.Equal(1, model.Calls);
            Assert.Contains("needs_search", model.LastSystemPrompt);
        }

        [Fact]
        public async Task IntentSaysNoSearch_OverridesKeywordThatWouldHaveSearched()
        {
            // 「目前」「最新」在關鍵字規則裡會觸發搜尋，但這是在問附近這段程式，不需要上網。
            const string text = "目前這段程式為什麼會丟例外？我用的是最新版的寫法";
            Assert.True(AgentCapabilityPlanner.FromKeywords(text, NodeTaskMode.Chat, false).NeedsFreshFacts);

            var (_, plan, _) = await Resolve(text, Json(search: false, reasoning: true, code: true));

            Assert.False(plan.RequiresFreshFacts);
            Assert.DoesNotContain("search-capability", plan.OrderedCapabilityIds);
            Assert.Contains("code-capability", plan.OrderedCapabilityIds);
            Assert.Contains("reasoning-capability", plan.OrderedCapabilityIds);
        }

        // ===== 換一種說法：關鍵字清單裡沒有的講法 =====

        [Theory]
        [InlineData("東京這週末會下雨嗎？")]                 // 沒有「天氣」「今天」「最新」
        [InlineData("輝達昨天收盤後市值多少，跟上週比呢")]    // 沒有「股價」，但要即時報價
        public async Task Paraphrase_WithoutKeywords_StillSearches(string text)
        {
            var (_, plan, _) = await Resolve(text, Json(search: true));

            Assert.True(plan.RequiresFreshFacts);
            Assert.Contains("search-capability", plan.RequiredCapabilityIds);
        }

        [Fact]
        public async Task Paraphrase_CodeAndReasoning_WithoutKeywords()
        {
            const string text = "這段為什麼一直掛掉？Python 跟 Go 寫爬蟲各有什麼好壞";
            var keywords = AgentCapabilityPlanner.FromKeywords(text, NodeTaskMode.Chat, false);
            Assert.False(keywords.NeedsCode);        // 關鍵字規則漏掉這兩件事
            Assert.False(keywords.NeedsReasoning);

            var (_, plan, _) = await Resolve(text, Json(search: false, reasoning: true, code: true));

            Assert.Contains("code-capability", plan.OrderedCapabilityIds);
            Assert.Contains("reasoning-capability", plan.OrderedCapabilityIds);
            // 語意判斷選了的能力，執行時不再被各能力的關鍵字 CanHandle 否決。
            Assert.True(plan.IsIntentSelected("code-capability"));
            Assert.True(plan.IsIntentSelected("reasoning-capability"));
            // 同時要做兩件事（程式＋推理）→ 複合任務，要拆步驟；這也是語意判斷的結果，不看字面。
            Assert.True(plan.IsIntentSelected("task-planning-capability"));
        }

        [Fact]
        public async Task SingleNeed_IsNotACompoundTask_NoTaskPlanning()
        {
            // 「查一下…然後分析」在關鍵字規則裡會被當成複合任務；語意判斷只要一件事時不拆步驟。
            var (_, plan, _) = await Resolve("查一下然後分析東京這週末會不會下雨", Json(search: true));

            Assert.DoesNotContain("task-planning-capability", plan.OrderedCapabilityIds);
        }

        // ===== 英文輸入 =====

        [Fact]
        public async Task EnglishInput_UsesTheSameSemanticPath()
        {
            const string text = "How did yesterday's Fed decision move the yen?";
            Assert.False(AgentCapabilityPlanner.FromKeywords(text, NodeTaskMode.Chat, false).NeedsFreshFacts);

            var (_, plan, _) = await Resolve(text, Json(search: true, reasoning: true));

            Assert.True(plan.RequiresFreshFacts);
            Assert.Contains("reasoning-capability", plan.OrderedCapabilityIds);
            Assert.Equal(AgentCapabilityPlanner.SourceSemantic, plan.Source);
        }

        // ===== 意圖判斷失敗 → 退回關鍵字，並留紀錄 =====

        [Fact]
        public async Task ModelThrows_FallsBackToKeywords_AndSaysSo()
        {
            const string text = "台積電最新股價是多少";
            var resolver = new OutputIntentResolver((_, _, _, _, _) => throw new InvalidOperationException("API down"));

            var intent = await resolver.ResolveAsync(text);
            var plan = AgentCapabilityPlanner.Build(AgentRegistry.Get(null), text, NodeTaskMode.Chat, false, intent);

            Assert.Null(intent.Capabilities);
            Assert.Equal(AgentCapabilityPlanner.SourceKeywords, plan.Source);
            Assert.True(plan.RequiresFreshFacts);                     // 關鍵字仍然抓得到明顯的講法
            Assert.Empty(plan.IntentSelectedCapabilityIds);           // 後援時照舊由 CanHandle 決定
            Assert.Contains("keyword fallback", plan.Reason);         // 決策紀錄（orchestration_plan.Reason）
            Assert.Contains(AgentCapabilityExecutionPlan.KeywordFallbackNote, plan.ToSummary());
        }

        [Fact]
        public async Task ModelThrows_WithoutKeywordFallback_AddsNothing()
        {
            // API Auto：不准退回關鍵字。「最新股價」也不會因為字面就去搜尋——沒有判斷就不加能力。
            const string text = "台積電最新股價是多少";
            var resolver = new OutputIntentResolver((_, _, _, _, _) => throw new InvalidOperationException("API down"));

            var intent = await resolver.ResolveAsync(text);
            var plan = AgentCapabilityPlanner.Build(AgentRegistry.Get(null), text, NodeTaskMode.Chat, false, intent,
                allowKeywordFallback: false);

            Assert.Equal(AgentCapabilityPlanner.SourceNone, plan.Source);
            Assert.False(plan.RequiresFreshFacts);
            Assert.Empty(plan.OrderedCapabilityIds);
            Assert.Contains("keyword fallback disabled", plan.Reason);
            Assert.Contains(AgentCapabilityExecutionPlan.NoJudgementNote, plan.ToSummary());
        }

        [Theory]
        [InlineData("not json at all")]
        // 舊格式（沒有能力欄位）或被截斷：不把「沒講」當成「不需要」。
        [InlineData("{\"presentation\":false,\"report\":false,\"table\":false,\"image\":false,\"video\":false}")]
        [InlineData("{\"presentation\":false,\"needs_search\":false,\"needs_reasoning\":false}")]
        public async Task MissingCapabilityFields_FallBackToKeywords(string reply)
        {
            var (intent, plan, _) = await Resolve("今天美元兌日圓匯率", reply);

            Assert.Null(intent.Capabilities);
            Assert.Equal(AgentCapabilityPlanner.SourceKeywords, plan.Source);
            Assert.True(plan.RequiresFreshFacts);
        }

        [Fact]
        public async Task KeywordOutputOverride_KeepsTheModelsCapabilityAnswer()
        {
            // 模型說不要產檔、關鍵字卻看到「簡報」時，不論輸出格式最後採哪一邊，能力判斷都要用模型的。
            // （只驗能力判斷：主資料夾另有一份修改會拿掉「關鍵字推翻模型」這條，合併後這條測試照樣成立。）
            var (intent, plan, _) = await Resolve("做一份簡報介紹京都", Json(search: false, presentation: false));

            Assert.NotNull(intent.Capabilities);
            Assert.Equal(AgentCapabilityPlanner.SourceSemantic, plan.Source);
            Assert.False(plan.RequiresFreshFacts);
        }

        [Fact]
        public void ResearchTaskMode_DoesNotForceSearch_WhenIntentSaysNo()
        {
            // 2026-11-05 實測：API Auto 把「台積電企業文化簡報」判成研究模式，舊規則因此強制搜尋、
            // 還跳了即時資料提醒。Auto 模式下任務模式是系統選的，要不要搜尋只看語意判斷。
            const string text = "做一份介紹台積電企業文化的簡報";
            var intent = new OutputIntent { Capabilities = new CapabilityNeeds(false, false, false) };
            var plan = AgentCapabilityPlanner.Build(AgentRegistry.Get(null), text, NodeTaskMode.Research, false, intent);

            Assert.False(plan.RequiresFreshFacts);
            Assert.DoesNotContain("search-capability", plan.OrderedCapabilityIds);

            // API Auto 判斷失敗也一樣：不因為任務模式是研究就去搜尋。
            var none = AgentCapabilityPlanner.Build(AgentRegistry.Get(null), text, NodeTaskMode.Research, false, null,
                allowKeywordFallback: false);
            Assert.False(none.RequiresFreshFacts);

            // 規則式 Auto 的關鍵字後援保留原本的規則：研究模式一律搜尋。
            var fallback = AgentCapabilityPlanner.Build(AgentRegistry.Get(null), text, NodeTaskMode.Research, false, null);
            Assert.True(fallback.RequiresFreshFacts);
        }

        [Fact]
        public void UserPrompt_MarksTheUsersOwnInstruction_WhenUpstreamContentIsMixedIn()
        {
            const string merged = "比較以下兩份回答：\n【上游 1／2】目前市場看好…\n【上游 2／2】…";
            string prompt = OutputIntentResolver.BuildUserPrompt(merged, null, "比較以下兩份回答");

            Assert.Contains("使用者在這個節點自己寫的要求", prompt);
            Assert.Contains("比較以下兩份回答", prompt);

            // 整段都是使用者寫的：不多加這段。
            Assert.DoesNotContain("自己寫的要求", OutputIntentResolver.BuildUserPrompt("做一份簡報", null, null));
            Assert.DoesNotContain("自己寫的要求", OutputIntentResolver.BuildUserPrompt("做一份簡報", null, "做一份簡報"));
        }

        // ===== 只在需要即時資料時才跑能力層的模式（手動、一般 Auto）=====

        [Fact]
        public void SearchGate_FollowsSemanticIntent_NotFinanceKeywords()
        {
            var agent = AgentRegistry.Get(null);

            // 規則式 Auto 只在需要即時資料時才跑能力層。
            // 語意說要搜尋，但沒有任何財經關鍵字 → 以前會整個關掉，現在要跑。
            var weather = AgentCapabilityPlanner.Build(agent, "東京這週末會下雨嗎？", NodeTaskMode.Chat, false,
                new OutputIntent { Capabilities = new CapabilityNeeds(true, false, false) });
            Assert.True(weather.ShouldRunWhenSearchGated("東京這週末會下雨嗎？"));

            // 提到公司名但只是要介紹（語意說不用搜尋）→ 不跑。
            var intro = AgentCapabilityPlanner.Build(agent, "做一份介紹台積電企業文化的簡報", NodeTaskMode.Chat, false,
                new OutputIntent { Capabilities = new CapabilityNeeds(false, false, false) });
            Assert.False(intro.ShouldRunWhenSearchGated("做一份介紹台積電企業文化的簡報"));

            // 語意判斷拿不到 → 照舊用財經關鍵字。
            var fallback = AgentCapabilityPlanner.Build(agent, "做一份介紹台積電企業文化的簡報", NodeTaskMode.Chat, false, null);
            Assert.True(fallback.ShouldRunWhenSearchGated("做一份介紹台積電企業文化的簡報"));
        }

        // ===== 走完整個 ExecuteAsync（假宿主）=====

        private sealed class FakeNode : INodeContext
        {
            public Guid Id { get; } = Guid.NewGuid();
            public void SetLoadingHint(string? hint) { }
        }

        private sealed class FakeHost : IAgentHost
        {
            public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            public bool AutoMode { get; init; }
            public bool ApiAuto { get; init; }
            public bool IsAutoModelSelectionEnabled() => AutoMode;
            public bool IsAdvancedAutoResolverEnabled() => ApiAuto;
            public PresentationEngine GetPresentationEngine() => PresentationEngine.Claude;
            public DocumentEngine GetDocumentEngine() => DocumentEngine.Builtin;
            public VeoModelTier GetVeoModelTier() => VeoModelTier.Lite;
            public string GetEffectiveVeoModel() => "";
            public string GetEffectiveVideoStylePrompt() => "";
            public IReadOnlyList<AttachmentInfo> GetAttachmentsForNode(INodeContext node) => Array.Empty<AttachmentInfo>();
            public IReadOnlyList<AttachmentInfo> GetEffectiveAttachmentsForNode(INodeContext node) => Array.Empty<AttachmentInfo>();
            public string GetAttachmentsRootDir() => Dir;
            public string GetGeneratedFilesDir() => Dir;
            public void SetLiveDecisionResolving(INodeContext node, NodeExecutionDecision decision) { }
            public Task<GenerationApproval> ConfirmGenerationAsync(
                OrchestrationTaskType taskType, OutputIntent? outputIntent, GenerationChoices choices)
                => Task.FromResult(GenerationApproval.Accepted);
            public string GetNodeSelectedModel(INodeContext node) => "";
        }

        /// <summary>跟真正的 NodeExecutionDecisionResolver 一樣，在決策上標出是哪個模式做的決定。</summary>
        private sealed class FakeResolver : IExecutionDecisionResolver
        {
            private readonly Mode _mode;
            public FakeResolver(Mode mode) => _mode = mode;

            public Task<NodeExecutionDecision> ResolveAsync(INodeContext nodeContext, string topText, CancellationToken ct)
                => Task.FromResult(new NodeExecutionDecision
                {
                    ModelId = AiModelRegistry.Default.Id,
                    RequestedModelId = AiModelRegistry.Default.Id,
                    TaskMode = NodeTaskMode.Chat,
                    Confidence = 1.0,
                    UseStreaming = false,
                    StatusLabel = _mode switch { Mode.Manual => "Manual", Mode.RuleAuto => "Rule Auto", _ => "API Auto" },
                    UsedApiResolver = _mode == Mode.ApiAuto,
                });
        }

        private sealed class FakeFinalizer : IDecisionFinalizer
        {
            public NodeExecutionDecision FinalizeDecision(NodeExecutionDecision decision, AiFallbackExecutionResult execution)
            {
                decision.ActualModelId = decision.ModelId;
                return decision;
            }
        }

        private enum Mode { Manual, RuleAuto, ApiAuto }

        /// <param name="liveSwitch">
        /// 執行途中全域開關的狀態；預設跟決策同一個模式。給不同的值＝模擬「使用者在這次執行途中切了模式」。
        /// </param>
        private static AgentRuntime BuildRuntime(Mode mode, Mode? liveSwitch = null)
            => new AgentRuntime(
                new FakeHost { AutoMode = (liveSwitch ?? mode) != Mode.Manual, ApiAuto = (liveSwitch ?? mode) == Mode.ApiAuto },
                new FakeResolver(mode),
                (node, prompt, decision, onDelta, streaming, ct) =>
                    Task.FromResult(new AiFallbackExecutionResult { IsSuccess = true, Text = "好的。", ActualModelId = decision.ModelId }),
                new FakeFinalizer());

        private static OrchestrationPlanPayload PlanOf(AgentWorkspace workspace)
            => workspace.GetByType("orchestration_plan").Select(x => x.Payload).OfType<OrchestrationPlanPayload>().First();

        private static Task<AgentExecutionResult> Run(
            Mode mode, string text, AgentWorkspace workspace, CapabilityNeeds? needs, Mode? liveSwitch = null)
            => BuildRuntime(mode, liveSwitch).ExecuteAsync(new AgentExecutionRequest
            {
                Node = new FakeNode(),
                Agent = AgentRegistry.Get(null),
                TopText = text,
                SkipCapabilities = true,   // 能力本身不在這裡跑；這裡驗證計畫與決策紀錄
                Workspace = workspace,
                OutputIntentTask = needs == null
                    ? null
                    : Task.FromResult<OutputIntent?>(new OutputIntent { Source = "api:test", Capabilities = needs }),
            });

        [Fact]
        public async Task Runtime_ApiAuto_SemanticNoSearch_BeatsKeywordsInTheRecordedPlan()
        {
            var workspace = new AgentWorkspace();
            var result = await Run(Mode.ApiAuto, "目前最新的 C# 寫法哪個比較好讀", workspace, new CapabilityNeeds(false, true, true));

            Assert.True(result.IsSuccess);
            var plan = PlanOf(workspace);
            Assert.False(plan.RequiresFreshFacts);
            Assert.DoesNotContain("search-capability", plan.CapabilityOrder);
            Assert.Contains("source=semantic", plan.Reason);
            Assert.Contains("語意判斷", result.Decision.CapabilityPlanSummary);
        }

        [Fact]
        public async Task Runtime_ApiAuto_NoIntent_DoesNotUseKeywords()
        {
            var workspace = new AgentWorkspace();
            var result = await Run(Mode.ApiAuto, "台積電最新股價", workspace, needs: null);

            var plan = PlanOf(workspace);
            Assert.False(plan.RequiresFreshFacts);
            Assert.Contains("source=none", plan.Reason);
            Assert.Contains(AgentCapabilityExecutionPlan.NoJudgementNote, result.Decision.CapabilityPlanSummary);
        }

        [Fact]
        public async Task Runtime_RuleBasedAuto_NoIntent_FallsBackToKeywords_AndRecordsIt()
        {
            var workspace = new AgentWorkspace();
            var result = await Run(Mode.RuleAuto, "台積電最新股價", workspace, needs: null);

            var plan = PlanOf(workspace);
            Assert.True(plan.RequiresFreshFacts);
            Assert.Contains("keyword fallback", plan.Reason);
            Assert.Contains(AgentCapabilityExecutionPlan.KeywordFallbackNote, result.Decision.CapabilityPlanSummary);
        }

        [Fact]
        public async Task Runtime_UsesTheModeTheRunWasDecidedIn_NotTheLiveSwitch()
        {
            // 2026-11-05 實測：規則式 Auto 的節點在等意圖判斷的幾秒內，使用者切到手動去跑下一個節點，
            // 這個節點就被當成手動、沒有搜尋，回答「無法取得即時天氣」。
            var workspace = new AgentWorkspace();
            var result = await Run(Mode.RuleAuto, "東京這週末會下雨嗎？", workspace,
                new CapabilityNeeds(true, false, false), liveSwitch: Mode.Manual);

            Assert.NotEqual(AgentCapabilityExecutionPlan.ManualModeSummary, result.Decision.CapabilityPlanSummary);
            Assert.True(PlanOf(workspace).RequiresFreshFacts);
            Assert.Contains("查即時資料", result.Decision.CapabilityPlanSummary);

            // 反過來也一樣：手動跑的節點，途中切到 API Auto 也照手動的規則——不會加推理、程式。
            var manualWorkspace = new AgentWorkspace();
            var manual = await Run(Mode.Manual, "這段為什麼一直掛掉？", manualWorkspace,
                new CapabilityNeeds(false, true, true), liveSwitch: Mode.ApiAuto);
            Assert.Equal(AgentCapabilityExecutionPlan.ManualModeSummary, manual.Decision.CapabilityPlanSummary);
            Assert.Empty(PlanOf(manualWorkspace).CapabilityOrder);
        }

        [Fact]
        public async Task Runtime_Manual_SearchesWhenNeeded_AndAddsNothingElse()
        {
            // 2026-11-05 實測：手動問「東京這週末會下雨嗎？」只得到「無法取得即時天氣」，ChatGPT 網頁版卻答得出來。
            // 使用者決定：手動也要查，但只加搜尋，答案仍由選的模型寫。
            var workspace = new AgentWorkspace();
            var result = await Run(Mode.Manual, "東京這週末會下雨嗎？", workspace, new CapabilityNeeds(true, true, true));

            Assert.True(result.IsSuccess);
            var plan = PlanOf(workspace);
            Assert.True(plan.RequiresFreshFacts);
            Assert.Equal(new[] { "search-capability" }, plan.CapabilityOrder);   // 推理、程式、拆步驟都不加
            Assert.Contains("查即時資料", result.Decision.CapabilityPlanSummary);
            Assert.EndsWith(AgentCapabilityExecutionPlan.ManualSearchNote, result.Decision.CapabilityPlanSummary);
        }

        [Fact]
        public async Task Runtime_Manual_WithoutSemanticJudgement_DoesNotSearch()
        {
            // 手動不用關鍵字：語意判斷拿不到時，「台積電最新股價」也不會因為字面去搜尋。
            var workspace = new AgentWorkspace();
            var result = await Run(Mode.Manual, "台積電最新股價", workspace, needs: null);

            Assert.False(PlanOf(workspace).RequiresFreshFacts);
            Assert.Empty(PlanOf(workspace).CapabilityOrder);
            Assert.Equal(AgentCapabilityExecutionPlan.ManualModeSummary, result.Decision.CapabilityPlanSummary);
        }

        [Fact]
        public void SearchOnlyPlan_KeepsSearch_DropsEverythingElse()
        {
            var intent = new OutputIntent { Capabilities = new CapabilityNeeds(true, true, true) };
            var plan = AgentCapabilityPlanner.Build(AgentRegistry.Get(null), "x", NodeTaskMode.Chat, hasAttachments: true, intent,
                allowKeywordFallback: false, searchOnly: true);

            Assert.Equal(new[] { "search-capability" }, plan.OrderedCapabilityIds);
            Assert.True(plan.IsRequired("search-capability"));
        }

        [Fact]
        public void Disclosure_SaysWhoDecidedTheCapabilities()
        {
            static string Who(string summary, params UsageRecord[] records)
                => AiUsageDisclosure.Decisions(new AiExecutionLogEntry { SelectionMode = "Auto", CapabilityPlanSummary = summary }, records)
                    .Single(r => r.What == "要不要查資料／推理／寫程式").Who;

            var intentCall = new UsageRecord { Purpose = "輸出意圖判斷", ModelId = AiModels.OpenAi_Gpt56 };

            Assert.Equal("你（手動模式）", Who(AgentCapabilityExecutionPlan.ManualModeSummary));
            Assert.Equal("系統（關鍵字規則）", Who($"查即時資料（{AgentCapabilityExecutionPlan.KeywordFallbackNote}）", intentCall));
            Assert.Equal("系統（沒有判斷結果）", Who($"不需要搜尋、推理或程式（{AgentCapabilityExecutionPlan.NoJudgementNote}）", intentCall));
            string semantic = Who("查即時資料（語意判斷）", intentCall);
            Assert.StartsWith("系統（", semantic);
            Assert.EndsWith(" 判斷）", semantic);
        }

        // 註：「語意選了的能力不被 CanHandle 否決」與「各模式能力層跑不跑」這兩條在執行時的接線，
        // 沒有端到端測試——能力清單（AgentCapabilityRegistry）是全域的，測試裡註冊會影響其他平行跑的測試。
        // 決策本身由上面 Paraphrase_CodeAndReasoning_WithoutKeywords、SearchGate_FollowsSemanticIntent_NotFinanceKeywords
        // 與各模式的決策紀錄測試驗證。
    }
}
