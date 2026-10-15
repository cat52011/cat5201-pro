using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cat5201;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 真正的「賺錢路徑」整合測試：意圖 → 內容作者 → 產檔 → 檔名 → 配對 PDF。
    ///
    /// 既有的 AgentRuntimeIntegrationTests 兩個案例都設 SkipCapabilities = true，
    /// 驗的是執行骨幹，沒有跑過產出這一段。但使用者真正拿到手的就是這一段——
    /// 而且作者步驟也走注入的 provider，所以整條鏈可以完全離線驗。
    /// </summary>
    // 全域靜態狀態（帳本、預留、DeliveryOptions）是所有測試共用的：
    // 碰到它們的測試必須在同一個集合裡循序執行，否則別的類別平行跑時會改掉這裡的前提。
    [Collection("spend-ledger")]
    public class DeliverableChainIntegrationTests
    {
        private sealed class FakeNode : INodeContext
        {
            public Guid Id { get; } = Guid.NewGuid();
            public void SetLoadingHint(string? hint) { }
        }

        private sealed class FakeHost : IAgentHost
        {
            public string Dir { get; } = Path.Combine(
                Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            public int ConfirmCalls { get; private set; }

            public FakeHost() => Directory.CreateDirectory(Dir);

            public bool IsAutoModelSelectionEnabled() => false;
            public bool IsAdvancedAutoResolverEnabled() => false;
            public PresentationEngine GetPresentationEngine() => PresentationEngine.Claude;
            public DocumentEngine GetDocumentEngine() => DocumentEngine.Builtin;   // 不走 Claude 技能
            public VeoModelTier GetVeoModelTier() => VeoModelTier.Lite;
            public string GetEffectiveVeoModel() => "";
            public string GetEffectiveVideoStylePrompt() => "";
            public System.Collections.Generic.IReadOnlyList<AttachmentInfo> GetAttachmentsForNode(INodeContext n)
                => Array.Empty<AttachmentInfo>();
            public System.Collections.Generic.IReadOnlyList<AttachmentInfo> GetEffectiveAttachmentsForNode(INodeContext n)
                => Array.Empty<AttachmentInfo>();
            public string GetAttachmentsRootDir() => Dir;
            public string GetGeneratedFilesDir() => Dir;
            public void SetLiveDecisionResolving(INodeContext node, NodeExecutionDecision decision) { }
            public Task<bool> ConfirmGenerationAsync(OrchestrationTaskType t, OutputIntent? i)
            {
                ConfirmCalls++;
                return Task.FromResult(true);
            }
        }

        private sealed class FakeResolver : IExecutionDecisionResolver
        {
            public Task<NodeExecutionDecision> ResolveAsync(INodeContext n, string topText, CancellationToken ct)
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
            public NodeExecutionDecision FinalizeDecision(NodeExecutionDecision d, AiFallbackExecutionResult e)
            {
                d.ActualModelId = string.IsNullOrWhiteSpace(e.ActualModelId) ? d.ModelId : e.ActualModelId;
                return d;
            }
        }

        // 一份含真表格的回答：報告作者與表格作者都從這裡取材。
        private const string Answer = @"## 摘要

兩個分類的營收都成長，甜點的毛利率最高。

表：各分類的營收與毛利率（來源：https://example.com/report.pdf）

| 分類 | 營收 | 毛利率 |
| --- | --- | --- |
| 主菜 | 1,200 | 28.5% |
| 甜點 | 800 | 44.2% |

## 結論與建議

優先擴張甜點線。前提是通路可獨立於本店。";

        private static AgentRuntime Build(FakeHost host)
            => new AgentRuntime(
                host,
                new FakeResolver(),
                (node, prompt, decision, onDelta, streaming, ct) =>
                    Task.FromResult(new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = Answer,
                        ActualModelId = decision.ModelId,
                    }),
                new FakeFinalizer());

        private static async Task<FakeHost> RunAsync(bool companionPdf)
        {
            var host = new FakeHost();
            DeliveryOptions.CompanionPdf = companionPdf;
            try
            {
                await Build(host).ExecuteAsync(new AgentExecutionRequest
                {
                    Node = new FakeNode(),
                    Agent = AgentRegistry.Get(null),
                    TopText = "研究餐飲市場，寫一份報告並附表格",
                    SkipCapabilities = false,          // ← 真的跑產出這一段
                    OutputIntent = new OutputIntent { WantsReport = true, WantsTable = true },
                    Workspace = new AgentWorkspace(),
                    CancellationToken = CancellationToken.None,
                });
            }
            finally { DeliveryOptions.ResetForTests(); }
            return host;
        }

        private static string[] Files(FakeHost host, string ext)
            => Directory.GetFiles(host.Dir, "*" + ext);

        [Fact]
        public async Task TheWholeChainProducesTheFilesTheUserAskedFor()
        {
            var host = await RunAsync(companionPdf: true);

            Assert.Single(Files(host, ".docx"));
            Assert.Single(Files(host, ".xlsx"));
            Assert.NotEmpty(Files(host, ".pdf"));        // 報告與表格各一份對照 PDF
            Assert.Equal(1, host.ConfirmCalls);          // 產檔前問過使用者
        }

        [Fact]
        public async Task TheGeneratedSpreadsheetIsActuallyCalculable()
        {
            // 走完整條鏈之後，表格裡的數字仍然必須是真的數值——
            // 只驗「有產出檔案」等於沒驗到使用者真正在意的事。
            var host = await RunAsync(companionPdf: false);

            using var doc = SpreadsheetDocument.Open(Files(host, ".xlsx").Single(), false);
            var cells = doc.WorkbookPart!.WorksheetParts.First().Worksheet
                .Descendants<Cell>().ToList();

            Assert.Contains(cells, c => c.DataType?.Value == CellValues.Number);
            // 百分比存成 0.285 這種數值，不是字串。
            Assert.Contains(cells, c =>
                c.DataType?.Value == CellValues.Number &&
                c.CellValue != null &&
                Math.Abs(double.Parse(c.CellValue.Text) - 0.285) < 0.0001);
        }

        [Fact]
        public async Task TurningOffTheCompanionPdfReallySkipsIt()
        {
            var host = await RunAsync(companionPdf: false);

            Assert.Single(Files(host, ".docx"));
            Assert.Single(Files(host, ".xlsx"));
            Assert.Empty(Files(host, ".pdf"));
        }
    }
}
