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
    /// 並行安全（Codex #15）：藍虛線自動流讓「同層多節點並行」成為日常路徑，
    /// 這裡釘住兩件事：① AsyncLocal 的「每條 async 流各持己值」語義（P0-5 串線修法的根據）
    /// ② AgentRuntime 十節點同時執行不互染（各自的回覆/workspace 歸各自）。
    /// </summary>
    public class ConcurrencyTests
    {
        // ===== ① AsyncLocal 語義：模擬 NodeService._currentExecutionNode 的使用模式 =====
        // 普通欄位在 await 交錯時會互蓋（A 讀到 B 的值）；AsyncLocal 不會。這個測試是 P0-5 修法的活文件。

        private static readonly AsyncLocal<string?> _ctx = new();

        [Fact]
        public async Task AsyncLocal_ParallelFlows_DoNotCrossContaminate()
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
            {
                _ctx.Value = $"node-{i}";          // 「本次執行」環境值（同 NodeService 設定點）
                await Task.Yield();                 // 強迫交錯：讓其他流有機會插進來設它們的值
                await Task.Delay(10 * (i % 3));
                return (_ctx.Value, Expected: $"node-{i}");  // 讀回（同 BuildExecutionRequestAsync 讀取點）
            }));

            Assert.All(results, r => Assert.Equal(r.Expected, r.Value));
        }

        // ===== ② AgentRuntime 十節點並行整合測試（沿用 AgentRuntimeIntegrationTests 的假件模式）=====

        private sealed class FakeNode : INodeContext
        {
            public Guid Id { get; } = Guid.NewGuid();
            public void SetLoadingHint(string? hint) { }
        }

        private sealed class FakeHost : IAgentHost
        {
            public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            public bool IsAutoModelSelectionEnabled() => false;
            public bool IsAdvancedAutoResolverEnabled() => false;
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
            public Task<bool> ConfirmGenerationAsync(OrchestrationTaskType taskType, OutputIntent? outputIntent) => Task.FromResult(true);
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
                decision.ActualModelId = decision.ModelId;
                return decision;
            }
        }

        [Fact]
        public async Task AgentRuntime_TenParallelNodes_ResultsDoNotCrossContaminate()
        {
            var host = new FakeHost();

            // 假 Provider：回覆帶「自己的 prompt 內容」——若執行流互染，A 會拿到 B 的回覆。
            var runtime = new AgentRuntime(
                host,
                new FakeResolver(),
                async (node, prompt, decision, onDelta, streaming, ct) =>
                {
                    await Task.Delay(Random.Shared.Next(5, 40), ct); // 打亂完成順序，逼出交錯
                    return new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = $"REPLY::{prompt}",
                        ActualModelId = decision.ModelId,
                    };
                },
                new FakeFinalizer());

            var tasks = Enumerable.Range(0, 10).Select(async i =>
            {
                var workspace = new AgentWorkspace();
                var result = await runtime.ExecuteAsync(new AgentExecutionRequest
                {
                    Node = new FakeNode(),
                    Agent = AgentRegistry.Get(null),
                    TopText = $"任務-{i}",
                    SkipCapabilities = true,
                    Workspace = workspace,
                    CancellationToken = CancellationToken.None,
                });
                return (Index: i, Result: result, Workspace: workspace);
            }).ToArray();

            var all = await Task.WhenAll(tasks);

            Assert.All(all, x =>
            {
                Assert.True(x.Result.IsSuccess);
                // 各自的回覆必須對應「自己的」任務文字，不能拿到別人的。
                Assert.Contains($"任務-{x.Index}", x.Result.FinalText);
                Assert.NotEmpty(x.Workspace.GetAll()); // 各自的 workspace 都有自己的產物
            });
        }
    }
}
