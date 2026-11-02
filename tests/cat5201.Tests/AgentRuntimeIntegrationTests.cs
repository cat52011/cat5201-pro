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
    /// Slice B2 的成果驗收：AgentRuntime（執行腦）已完全脫離 WPF——
    /// 用假宿主(IAgentHost)/假節點(INodeContext)/假決策(IExecutionDecisionResolver)/假 Provider(fallback 委派)
    /// 就能在單元測試裡跑完 ExecuteAsync 端到端。這是「賺錢路徑」的第一個自動化整合測試。
    /// </summary>
    public class AgentRuntimeIntegrationTests
    {
        // ===== 假件 =====

        private sealed class FakeNode : INodeContext
        {
            public Guid Id { get; } = Guid.NewGuid();
            public List<string> Hints { get; } = new();
            public void SetLoadingHint(string? hint) { if (hint != null) Hints.Add(hint); }
        }

        private sealed class FakeHost : IAgentHost
        {
            public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            public bool AutoMode { get; set; }
            public int ConfirmCalls { get; private set; }

            public bool IsAutoModelSelectionEnabled() => AutoMode;
            public bool IsAdvancedAutoResolverEnabled() => false;
            public PresentationEngine GetPresentationEngine() => PresentationEngine.Claude;
            public DocumentEngine GetDocumentEngine() => DocumentEngine.Builtin;
            public VeoModelTier GetVeoModelTier() => VeoModelTier.Lite;
            public string GetEffectiveVeoModel() => "";
            public string GetEffectiveVideoStylePrompt() => "";
            public IReadOnlyList<AttachmentInfo> GetAttachmentsForNode(INodeContext node)
                => Array.Empty<AttachmentInfo>();
            public IReadOnlyList<AttachmentInfo> GetEffectiveAttachmentsForNode(INodeContext node)
                => Array.Empty<AttachmentInfo>();
            public string GetAttachmentsRootDir() => Dir;
            public string GetGeneratedFilesDir() => Dir;
            public void SetLiveDecisionResolving(INodeContext node, NodeExecutionDecision decision) { }
            public GenerationChoices? LastChoices { get; private set; }

            public Task<GenerationApproval> ConfirmGenerationAsync(
                OrchestrationTaskType taskType, OutputIntent? outputIntent, GenerationChoices choices)
            {
                ConfirmCalls++;
                LastChoices = choices;
                return Task.FromResult(GenerationApproval.Accepted);
            }

            public string GetNodeSelectedModel(INodeContext node) => "";
        }

        private sealed class FakeResolver : IExecutionDecisionResolver
        {
            public Task<NodeExecutionDecision> ResolveAsync(INodeContext nodeContext, string topText, CancellationToken ct)
                => Task.FromResult(new NodeExecutionDecision
                {
                    ModelId = AiModelRegistry.Default.Id,
                    RequestedModelId = AiModelRegistry.Default.Id,
                    TaskMode = NodeTaskMode.Chat,
                    Confidence = 1.0,
                    UseStreaming = false,
                });
        }

        private sealed class FakeFinalizer : IDecisionFinalizer
        {
            public NodeExecutionDecision FinalizeDecision(NodeExecutionDecision decision, AiFallbackExecutionResult execution)
            {
                decision.ActualModelId = string.IsNullOrWhiteSpace(execution.ActualModelId)
                    ? decision.ModelId
                    : execution.ActualModelId;
                return decision;
            }
        }

        private static AgentRuntime BuildRuntime(FakeHost host, string cannedReply = "（假 Provider 回覆）測試成功。")
            => new AgentRuntime(
                host,
                new FakeResolver(),
                (node, prompt, decision, onDelta, streaming, ct) =>
                    Task.FromResult(new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = cannedReply,
                        ActualModelId = decision.ModelId,
                    }),
                new FakeFinalizer());

        // ===== 測試 =====

        /// <summary>
        /// 假 Provider：所選模型失敗、由下一個模型完成（跟真正的 fallback 執行器回報的形狀一樣）。
        /// </summary>
        private static AgentRuntime BuildSwitchingRuntime(FakeHost host, string replacement)
            => new AgentRuntime(
                host,
                new FakeResolver(),
                (node, prompt, decision, onDelta, streaming, ct) =>
                    Task.FromResult(new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = "（替補模型的回覆）",
                        ActualModelId = replacement,
                        UsedFallback = true,
                        Attempts = new List<AiFallbackAttempt>
                        {
                            new() { AttemptIndex = 1, ModelId = decision.ModelId, Success = false, ErrorMessage = "400 invalid_request_error" },
                            new() { AttemptIndex = 2, ModelId = replacement, Success = true },
                        },
                    }),
                new FakeFinalizer());

        /// <summary>
        /// 手動模式下所選模型確定失敗、改由其他模型完成時，答案最上方一定要講。
        /// 2026-10-29 使用者指示：「當選定模型確定失敗時，系統會自動切換到其他可用模型但通知使用者」。
        /// 這條測試走完整個 ExecuteAsync——這個專案已經兩次栽在「方法寫對、呼叫端沒接上」。
        /// </summary>
        [Fact]
        public async Task ManualMode_ModelSwitch_IsAnnouncedAtTheTopOfTheAnswer()
        {
            var host = new FakeHost { AutoMode = false };
            string replacement = AiModelRegistry.Available.First(m => m.Id != AiModelRegistry.Default.Id).Id;
            var runtime = BuildSwitchingRuntime(host, replacement);

            var result = await runtime.ExecuteAsync(new AgentExecutionRequest
            {
                Node = new FakeNode(),
                Agent = AgentRegistry.Get(null),
                TopText = "用一句話介紹你自己",
                SkipCapabilities = true,
                Workspace = new AgentWorkspace(),
                CancellationToken = CancellationToken.None,
            });

            Assert.True(result.IsSuccess);
            Assert.StartsWith("⚠️【已改用其他模型】", result.FinalText);
            Assert.Contains(AiModelRegistry.Default.DisplayName, result.FinalText);   // 所選的
            Assert.Contains(AiModelRegistry.Find(replacement)!.DisplayName, result.FinalText); // 替補的
            Assert.Contains("（替補模型的回覆）", result.FinalText);                    // 答案本身還在
        }

        /// <summary>Auto 模式本來就由系統挑模型，換模型不需要每次都講。</summary>
        [Fact]
        public async Task AutoMode_ModelSwitch_IsNotAnnounced()
        {
            var host = new FakeHost { AutoMode = true };
            string replacement = AiModelRegistry.Available.First(m => m.Id != AiModelRegistry.Default.Id).Id;
            var runtime = BuildSwitchingRuntime(host, replacement);

            var result = await runtime.ExecuteAsync(new AgentExecutionRequest
            {
                Node = new FakeNode(),
                Agent = AgentRegistry.Get(null),
                TopText = "用一句話介紹你自己",
                SkipCapabilities = true,
                Workspace = new AgentWorkspace(),
                CancellationToken = CancellationToken.None,
            });

            Assert.True(result.IsSuccess);
            Assert.DoesNotContain("已改用其他模型", result.FinalText);
        }

        /// <summary>
        /// 答案把查到的數字寫錯單位時，答案最上方要出現提醒——走完整個 ExecuteAsync。
        /// 2026-10-29 第一階段九份裡有三份這樣錯，系統一聲不吭。
        /// </summary>
        [Fact]
        public async Task UnitErrorAgainstFoundFacts_IsFlaggedAtTheTop()
        {
            var host = new FakeHost { AutoMode = true };
            var workspace = new AgentWorkspace();
            workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                workspace, null!, "research-agent", "verified_facts",
                new VerifiedFactPayload
                {
                    Facts = new List<VerifiedFactItem>
                    {
                        new() { FactType = "revenue", Value = "NT$1,494.24 billion", UsageRole = FactOwnership.UsageNumericFactSource },
                    },
                }));

            var result = await BuildRuntime(host, "台積電第三季營收為新台幣 1,494.24 億元。").ExecuteAsync(new AgentExecutionRequest
            {
                Node = new FakeNode(),
                Agent = AgentRegistry.Get(null),
                TopText = "研究台積電最近一季財報",
                SkipCapabilities = true,
                Workspace = workspace,
                CancellationToken = CancellationToken.None,
            });

            Assert.StartsWith("⚠️【數字可能有單位錯誤】", result.FinalText);
            Assert.Contains("新台幣 1,494.24 億元", result.FinalText);   // 答案本身還在，沒有被改寫
        }

        /// <summary>
        /// 每一步收到的內容都有記錄，而且能判斷有沒有完整包含原始要求。
        /// 模擬面試 Q11：「日誌會顯示各步驟實際收到的內容」——這條測試讓那句話成真。
        /// </summary>
        [Fact]
        public async Task EveryStepsInputIsRecordedAgainstTheOriginalRequest()
        {
            var host = new FakeHost { AutoMode = false };
            const string request = "用一句話介紹你自己，語氣要正式";

            var result = await BuildRuntime(host).ExecuteAsync(new AgentExecutionRequest
            {
                Node = new FakeNode(),
                Agent = AgentRegistry.Get(null),
                TopText = request,
                SkipCapabilities = true,
                Workspace = new AgentWorkspace(),
                CancellationToken = CancellationToken.None,
            });

            var steps = result.Decision.StepInputs;
            Assert.NotEmpty(steps);
            Assert.All(steps, s => Assert.True(s.ContainsOriginalRequest, s.Step + " 沒有完整收到原始要求"));

            // 完整內容檔寫出來了，而且裡面的雜湊與紀錄一致。
            Assert.False(string.IsNullOrWhiteSpace(result.Decision.StepInputLogPath));
            string log = System.IO.File.ReadAllText(result.Decision.StepInputLogPath);
            Assert.Contains(steps[0].Sha256, log);
            Assert.Contains(request, log);
        }

        /// <summary>
        /// 協作模式走完整個 ExecuteAsync：只有使用者指定的模型被呼叫，每次都鎖定單一模型，
        /// 輸出固定有各模型回答、交叉檢視、共識與分歧。
        /// </summary>
        [Fact]
        public async Task Collaboration_OnlyTheChosenModelsAreCalled()
        {
            var host = new FakeHost { AutoMode = true };
            var calls = new System.Collections.Concurrent.ConcurrentBag<NodeExecutionDecision>();
            var runtime = new AgentRuntime(
                host,
                new FakeResolver(),
                (node, prompt, decision, onDelta, streaming, ct) =>
                {
                    calls.Add(decision);
                    return Task.FromResult(new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = decision.ModelId + " 的回答",
                        ActualModelId = decision.ModelId,
                    });
                },
                new FakeFinalizer());

            var chosen = new[] { AiModels.Claude_Opus5, AiModels.Perplexity_Sonar };
            var result = await runtime.ExecuteAsync(new AgentExecutionRequest
            {
                Node = new FakeNode(),
                Agent = AgentRegistry.Get(null),
                TopText = "比較兩種投資方式",
                SkipCapabilities = true,
                Workspace = new AgentWorkspace(),
                CollaborationModels = chosen,
                CancellationToken = CancellationToken.None,
            });

            Assert.True(result.IsSuccess);
            Assert.NotEmpty(calls);
            Assert.All(calls, d => Assert.Contains(d.ModelId, chosen));      // 名單外的模型一次都沒被叫
            Assert.All(calls, d => Assert.True(d.ForceSingleModel));          // 而且不准 fallback
            Assert.Equal(4, calls.Count);                                     // 兩個模型 × 兩輪

            Assert.Contains("## 各模型的回答", result.FinalText);
            Assert.Contains("## 共識與分歧", result.FinalText);
        }

        /// <summary>沒有換模型就什麼都不加——答案開頭不能憑空多一行警告。</summary>
        [Fact]
        public async Task ManualMode_NoSwitch_NoNotice()
        {
            var host = new FakeHost { AutoMode = false };
            var result = await BuildRuntime(host).ExecuteAsync(new AgentExecutionRequest
            {
                Node = new FakeNode(),
                Agent = AgentRegistry.Get(null),
                TopText = "用一句話介紹你自己",
                SkipCapabilities = true,
                Workspace = new AgentWorkspace(),
                CancellationToken = CancellationToken.None,
            });

            Assert.DoesNotContain("已改用其他模型", result.FinalText);
        }


        [Fact]
        public async Task PlainChat_EndToEnd_WithFakeProvider()
        {
            var host = new FakeHost();
            var node = new FakeNode();
            var workspace = new AgentWorkspace();
            var runtime = BuildRuntime(host);

            var result = await runtime.ExecuteAsync(new AgentExecutionRequest
            {
                Node = node,
                Agent = AgentRegistry.Get(null),   // 預設代理
                TopText = "用一句話介紹你自己",
                SkipCapabilities = true,           // 跳過 research 等能力層，聚焦執行骨幹
                Workspace = workspace,
                CancellationToken = CancellationToken.None,
            });

            Assert.True(result.IsSuccess);
            Assert.Contains("測試成功", result.FinalText);
            Assert.NotEmpty(workspace.GetAll());               // 至少寫入了 orchestration 產物
            Assert.Equal(0, host.ConfirmCalls);                // 純文字不該觸發產檔確認
        }

        [Fact]
        public async Task FailedProvider_SurfacesFailure()
        {
            var host = new FakeHost();
            var runtime = new AgentRuntime(
                host,
                new FakeResolver(),
                (node, prompt, decision, onDelta, streaming, ct) =>
                    Task.FromResult(new AiFallbackExecutionResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "假故障",
                    }),
                new FakeFinalizer());

            var result = await runtime.ExecuteAsync(new AgentExecutionRequest
            {
                Node = new FakeNode(),
                Agent = AgentRegistry.Get(null),
                TopText = "隨便問一句",
                SkipCapabilities = true,
                Workspace = new AgentWorkspace(),
                CancellationToken = CancellationToken.None,
            });

            Assert.False(result.IsSuccess);
        }
    }
}
