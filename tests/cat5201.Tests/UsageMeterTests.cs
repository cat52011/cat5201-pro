using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 成本可稽查（2026-09-17）：逐筆帳目取代「總 token × 最後模型單價」。
    /// 釘住：混用模型各按各的價、服務端模型 ID 正規化、AsyncLocal 節點歸屬不串線、用途標籤、沖銷、估算標示。
    /// 會寫全域帳本 → 與 SpendLedgerTests 同一個 collection，避免平行執行互相污染。
    /// </summary>
    [Collection("SpendLedger")]
    public class UsageMeterTests
    {
        private static void FreshLedger()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-usage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            SpendLedger.Initialize(dir);
        }

        [Fact]
        public async Task MixedModels_EachCallPricedAtItsOwnModel()
        {
            FreshLedger();
            UsageScope? scope = null;

            await Task.Run(() =>
            {
                scope = UsageMeter.BeginScope();
                UsageMeter.RecordLlm("gpt-5.6-sol", 1_000_000, 0);     // $4
                UsageMeter.RecordLlm("claude-opus-5", 0, 1_000_000);   // $25
            });

            var summary = UsageSummary.From(scope!.Snapshot());
            Assert.Equal(29.0, summary.TotalUsd, 6);
            // 舊算法（總 token 2M × 最後模型 Opus 5/25）會算成 $30——這裡確保不是那樣。
            Assert.NotEqual(30.0, summary.TotalUsd, 6);
            Assert.Equal(2, summary.Models.Count);
            Assert.Equal(29.0, SpendLedger.TodayUsd, 6); // 同時進了每日帳本
        }

        [Theory]
        [InlineData("sonar", "pplx-sonar")]
        [InlineData("sonar-deep-research", "pplx-sonar-deep-research")]
        [InlineData("gemini-3.1-pro-preview", "gemini-3.1-pro")]
        [InlineData("claude-sonnet-5", "claude-sonnet-5")]
        public void ServiceModelId_ResolvesToPricingId(string serviceModel, string pricingId)
            => Assert.Equal(pricingId, ModelCostEstimator.ResolvePricingModelId(serviceModel));

        [Fact]
        public void PerplexityAgentGpt56_UsesProviderListPrice_NotOpenAiPromo()
        {
            // Perplexity 官方：第三方模型依原廠直售價（$5/$30），不是 OpenAI 直連的促銷價 $4/$20。
            var e = ModelCostEstimator.FromUsage(ModelCostEstimator.ResolvePricingModelId("openai/gpt-5.6-sol"), 1_000_000, 1_000_000);
            Assert.Equal(35.0, e.UsdCost, 6);
        }

        [Fact]
        public async Task ParallelNodes_RecordsDoNotCrossContaminate()
        {
            FreshLedger();

            async Task<UsageScope> RunNode(string model, int calls)
            {
                var scope = UsageMeter.BeginScope();
                for (int i = 0; i < calls; i++)
                {
                    await Task.Yield();
                    UsageMeter.RecordLlm(model, 100, 100);
                }
                return scope;
            }

            var a = Task.Run(() => RunNode("gpt-5.6-sol", 5));
            var b = Task.Run(() => RunNode("claude-sonnet-5", 3));
            var scopes = await Task.WhenAll(a, b);

            Assert.All(scopes[0].Snapshot(), r => Assert.Equal("gpt-5.6-sol", r.ModelId));
            Assert.All(scopes[1].Snapshot(), r => Assert.Equal("claude-sonnet-5", r.ModelId));
            Assert.Equal(5, scopes[0].Snapshot().Count);
            Assert.Equal(3, scopes[1].Snapshot().Count);
        }

        [Fact]
        public async Task Purpose_NestsAndRestores_PurposeIfUnsetKeepsOuter()
        {
            FreshLedger();
            UsageScope? scope = null;

            await Task.Run(() =>
            {
                scope = UsageMeter.BeginScope();
                using (UsageMeter.Purpose("影片導演企劃"))
                {
                    using (UsageMeter.PurposeIfUnset("主回覆"))
                        UsageMeter.RecordLlm("claude-sonnet-5", 10, 10);   // 外層已標 → 保留「影片導演企劃」

                    using (UsageMeter.Purpose("輸出意圖判斷"))
                        UsageMeter.RecordLlm("gpt-5.6-sol", 10, 10);
                }
                UsageMeter.RecordLlm("gpt-5.6-sol", 10, 10);                // 全部還原 → 預設用途
            });

            var purposes = scope!.Snapshot().Select(r => r.Purpose).ToList();
            Assert.Equal(new[] { "影片導演企劃", "輸出意圖判斷", UsageMeter.DefaultPurpose }, purposes);
        }

        [Fact]
        public void NoUsageReported_FallsBackToEstimate_AndIsLabeledAsSuch()
        {
            FreshLedger();
            var record = UsageMeter.RecordLlm("claude-sonnet-5", 0, 0, "請幫我寫一段說明", "這是一段回覆內容");

            Assert.NotNull(record);
            Assert.False(record!.IsActual);
            Assert.True(record.UsdCost > 0);
            Assert.Contains("含估算", UsageSummary.From(new[] { record }).BuildCostDisplay());
        }

        [Fact]
        public void Refund_SubtractsFromLedger_AndSummaryShowsIt()
        {
            FreshLedger();
            var charge = UsageMeter.RecordMetered(UsageKinds.Video, "veo-3.1-lite-generate-preview", 8, "秒", 0.40);
            var refund = UsageMeter.RecordMetered(UsageKinds.Video, "veo-3.1-lite-generate-preview", 8, "秒", -0.40);

            Assert.Equal(0, SpendLedger.TodayUsd, 6);
            var summary = UsageSummary.From(new[] { charge, refund });
            Assert.Equal(0, summary.TotalUsd, 6);
            Assert.Contains("失敗沖銷", summary.BuildCostDisplay());
        }

        [Fact]
        public void Subtract_NeverGoesBelowZero()
        {
            FreshLedger();
            SpendLedger.Add(0.10, "test");
            SpendLedger.Subtract(5.0, "test");
            Assert.Equal(0, SpendLedger.TodayUsd, 6);
        }

        [Fact]
        public void Breakdown_GroupsByPurposeAndModel()
        {
            var records = new[]
            {
                new UsageRecord { Kind = UsageKinds.Llm, Purpose = "主回覆", ModelId = "claude-sonnet-5", InputTokens = 1000, OutputTokens = 500, UsdCost = 0.01 },
                new UsageRecord { Kind = UsageKinds.Llm, Purpose = "主回覆", ModelId = "claude-sonnet-5", InputTokens = 1000, OutputTokens = 500, UsdCost = 0.01 },
                new UsageRecord { Kind = UsageKinds.Image, Purpose = "I2V 英雄圖", ModelId = "gpt-image-2", Quantity = 1, Unit = "張", UsdCost = 0.25, IsActual = false },
            };

            var lines = UsageSummary.BuildBreakdownLines(records);

            Assert.Equal(2, lines.Count);
            Assert.StartsWith("主回覆 · Claude Sonnet 5 ×2 · 3.0k tokens", lines[0]);
            Assert.Contains("I2V 英雄圖", lines[1]);
            Assert.Contains("（估算）", lines[1]);
        }

        [Fact]
        public void VeoPrice_MatchesOfficialPerModel()
        {
            // 官方 pricing 頁（2026-09-17）：標準 $0.40、Fast $0.10（舊值 0.15 是錯的）、Lite $0.05；未知 ID 取標準價不漏記。
            Assert.Equal(0.40, VeoModels.UsdPerSecondForModel(VeoModels.StandardModel), 6);
            Assert.Equal(0.10, VeoModels.UsdPerSecondForModel(VeoModels.FastModel), 6);
            Assert.Equal(0.05, VeoModels.UsdPerSecondForModel(VeoModels.LiteModel), 6);
            Assert.Equal(0.40, VeoModels.UsdPerSecondForModel("veo-9-experimental"), 6);
        }

        [Fact]
        public void VideoJournal_PersistsCommittedCost()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-jobs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            VideoJobJournal.Initialize(dir);
            VideoJobJournal.Record("models/veo-3.1-lite-generate-preview/operations/abc", "prompt", 8, 0.40, costRecorded: true);

            VideoJobJournal.Initialize(dir); // 模擬重啟
            var job = VideoJobJournal.Find("models/veo-3.1-lite-generate-preview/operations/abc");

            Assert.NotNull(job);
            Assert.True(job!.CostRecorded);
            Assert.Equal(8, job.Seconds);
            Assert.Equal(0.40, job.CostUsd, 6);
            Assert.Equal("pending", job.Status); // 寫檔完成前保持可恢復
        }
    }
}
