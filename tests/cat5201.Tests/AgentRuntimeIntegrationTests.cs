using System;
using System.Collections.Generic;
using System.IO;
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
            public double MediaUsd { get; private set; }
            public void SetLoadingHint(string? hint) { if (hint != null) Hints.Add(hint); }
            public void AddMediaCostUsd(double usd, string label) => MediaUsd += usd;
        }

        private sealed class FakeHost : IAgentHost
        {
            public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            public bool AutoMode { get; set; }
            public int ConfirmCalls { get; private set; }

            public bool IsAutoModelSelectionEnabled() => AutoMode;
            public bool IsAdvancedAutoResolverEnabled() => false;
            public PresentationEngine GetPresentationEngine() => PresentationEngine.Claude;
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
            public Task<bool> ConfirmGenerationAsync(OrchestrationTaskType taskType, OutputIntent? outputIntent)
            {
                ConfirmCalls++;
                return Task.FromResult(true);
            }
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
