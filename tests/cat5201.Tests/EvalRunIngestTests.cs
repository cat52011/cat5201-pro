using System;
using System.Collections.Generic;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 從 App 存檔取數的判斷規則。
    ///
    /// 這些規則是方法論的一部分，不是搬資料的細節：
    /// 「哪一筆屬於哪一題、哪一組」決定了實驗的資料正確性。
    /// 操作者填錯一格，整份結論就錯；所以組別一律由存檔推定，並且在這裡釘死。
    /// </summary>
    public class EvalRunIngestTests
    {
        // ───────────────────────── 組別只能由存檔決定 ─────────────────────────

        [Theory]
        [InlineData("API Auto", "gpt-5.6-sol", "D")]          // 產品組：自動選模型 + 多代理
        [InlineData("Auto", "claude-sonnet-4-6", "D")]        // 規則式 Auto 也算自動
        [InlineData("Manual", "gpt-5.6-sol", "A")]
        [InlineData("Manual", "gpt-5.5", "A")]
        [InlineData("Manual", "claude-sonnet-4-6", "B")]
        [InlineData("Manual", "claude-opus-5", "B")]
        [InlineData("Collaboration", "gpt-5.6-sol", "?")]    // 協作不是單一模型組，不能依第一個模型分進 A
        [InlineData("Manual", "gemini-3-pro", "?")]           // 不在實驗設計裡的模型要出聲，不要硬塞
        [InlineData("Manual", "", "?")]
        [InlineData("", "", "?")]
        public void GroupComesFromTheSaveFileNotTheOperator(string mode, string model, string expected)
            => Assert.Equal(expected, EvalRunIngest.ResolveGroup(mode, model));

        // ───────────────────────── 題目比對要嚴格 ─────────────────────────

        private static readonly EvalRubricSpec SpecA = EvalRubricSpec.Parse("""
            {"task_id":"T01","prompt":"研究台積電最近一季財報與近期股價表現，列出關鍵數字與可信來源",
             "required":[{"id":"R1","kind":"any_of","critical":true,"patterns":["營收"]}]}
            """);

        private static readonly EvalRubricSpec SpecB = EvalRubricSpec.Parse("""
            {"task_id":"T09","prompt":"摘要以下文章的五個重點",
             "required":[{"id":"R1","kind":"any_of","critical":true,"patterns":["重點"]}]}
            """);

        private static List<EvalRubricSpec> Specs => new() { SpecA, SpecB };

        [Fact]
        public void PromptMatchesItsTaskIgnoringWhitespaceAndWidth()
        {
            Assert.Equal("T01", EvalRunIngest.MatchSpec(Specs,
                "研究台積電最近一季財報與近期股價表現，列出關鍵數字與可信來源")?.TaskId);

            // 貼進畫布時常多出換行與空白，那跟題目是不是同一題無關。
            Assert.Equal("T01", EvalRunIngest.MatchSpec(Specs,
                "研究台積電最近一季財報與近期股價表現，\n列出關鍵數字與 可信來源")?.TaskId);
        }

        [Fact]
        public void PromptFollowedByFixedMaterialStillMatches()
        {
            // T06／T09 必須把固定素材貼在題目後面的同一個節點裡。
            Assert.Equal("T09", EvalRunIngest.MatchSpec(Specs,
                "摘要以下文章的五個重點\n\n為什麼加了機台，隊伍還是沒變短……（以下省略兩千字）")?.TaskId);
        }

        [Fact]
        public void AnUnrelatedPromptMatchesNothing()
        {
            // 這很重要：平常的工作節點不能被算進實驗。
            Assert.Null(EvalRunIngest.MatchSpec(Specs, "給我一個介紹冰島極光的5頁簡報"));
            Assert.Null(EvalRunIngest.MatchSpec(Specs, ""));
            Assert.Null(EvalRunIngest.MatchSpec(Specs, null));
        }

        // ───────────────────────── 整份存檔的攤平 ─────────────────────────

        private static ExecutionLogState Log(
            string nodeId, string mode, string model, DateTime startUtc,
            long ms = 60000, int inTok = 1000, int outTok = 200,
            double[]? usd = null, bool success = true, string error = "")
        {
            var usage = (usd ?? new[] { 0.01 })
                .Select(u => new UsageRecord
                {
                    AtUtc = startUtc, Kind = "llm", ModelId = model, Purpose = "test",
                    InputTokens = inTok, OutputTokens = outTok, IsActual = true, UsdCost = u,
                })
                .ToList();

            return new ExecutionLogState(
                NodeId: nodeId, StartedAtUtc: startUtc, EndedAtUtc: startUtc.AddMilliseconds(ms),
                DurationMs: ms, SelectionMode: mode, Resolver: "", WorkspaceSummary: "",
                WorkspaceArtifactDetails: new List<string>(), WorkspaceArtifacts: null,
                RequestedModelId: model, PlannedModelId: model, ActualModelId: model,
                TaskMode: "Chat", Confidence: 1, ResolverReason: "",
                ResolverKeywords: new List<string>(), CapabilityAdjusted: false, CapabilityReason: "",
                CapabilityRequestedModelId: "", CapabilityResolvedModelId: "", CapabilityRequired: "",
                CapabilityMissing: "", CapabilityStreamingAdjusted: false,
                CapabilityTrace: new List<AgentCapabilityTraceItem>(),
                RequestedAgentId: "", ActualAgentId: "", RuntimeFallbackUsed: false,
                RuntimeFallbackSummary: "", Success: success, ErrorMessage: error,
                FallbackAttempts: new List<AiFallbackAttempt>(),
                InputTokens: inTok, OutputTokens: outTok, CostDisplay: "", UsageRecords: usage);
        }

        private static NodeState Node(string id, string prompt, string output)
            => new(id, 0, 0, 100, 100, prompt, output, false, 14);

        private static AppState State(List<NodeState> nodes, List<ExecutionLogState> logs)
            => new(DateTime.UtcNow, null, nodes, new List<ConnState>(),
                   new List<AttachmentState>(), logs);

        [Fact]
        public void RepsAreNumberedByActualExecutionTime()
        {
            var t0 = new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);

            // 節點在畫布上的順序刻意與執行順序相反——第幾次要看時間，不看版面。
            var state = State(
                new List<NodeState>
                {
                    Node("n2", SpecA.Prompt, "第二次跑的營收答案"),
                    Node("n1", SpecA.Prompt, "第一次跑的營收答案"),
                },
                new List<ExecutionLogState>
                {
                    Log("n2", "Manual", "gpt-5.6-sol", t0.AddMinutes(10)),
                    Log("n1", "Manual", "gpt-5.6-sol", t0),
                });

            var rows = EvalRunIngest.Build(state, Specs).Rows;

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { 1, 2 }, rows.Select(x => x.Rep).ToArray());
            Assert.Contains("第一次", rows.Single(x => x.Rep == 1).Output);
            Assert.Contains("第二次", rows.Single(x => x.Rep == 2).Output);
        }

        [Fact]
        public void BuildIsIdempotent()
        {
            var t0 = new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);
            var state = State(
                new List<NodeState> { Node("n1", SpecA.Prompt, "營收答案") },
                new List<ExecutionLogState> { Log("n1", "API Auto", "gpt-5.6-sol", t0) });

            // 同一個存檔重跑幾次都要得到同一份結果，否則「可重算」只是說說。
            string First() => string.Join(";", EvalRunIngest.Build(state, Specs).Rows
                .Select(r => $"{r.TaskId}-{r.Group}-{r.Rep}:{r.CostUsd:0.0000}:{r.Seconds:0.0}"));

            Assert.Equal(First(), First());
            Assert.Equal("T01-D-1:0.0100:60.0", First());
        }

        [Fact]
        public void CostIsSummedFromPerCallRecordsNotTheDisplayString()
        {
            var t0 = new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);
            var state = State(
                new List<NodeState> { Node("n1", SpecA.Prompt, "營收答案") },
                new List<ExecutionLogState>
                {
                    Log("n1", "API Auto", "gpt-5.6-sol", t0,
                        usd: new[] { 0.0125, 0.0310, 0.0007 }),
                });

            var row = Assert.Single(EvalRunIngest.Build(state, Specs).Rows);
            Assert.Equal(0.0442, row.CostUsd, 6);
        }

        [Fact]
        public void FailedRunsAreSkippedWithAReason()
        {
            var t0 = new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);
            var state = State(
                new List<NodeState> { Node("n1", SpecA.Prompt, "半截的答案") },
                new List<ExecutionLogState>
                {
                    Log("n1", "API Auto", "gpt-5.6-sol", t0, success: false, error: "使用者中止"),
                });

            var result = EvalRunIngest.Build(state, Specs);

            // 中途失敗或被中止的那一次算不出完整產出，留著只會汙染平均。
            Assert.Empty(result.Rows);
            Assert.Contains(result.Warnings, w => w.Contains("執行失敗"));
        }

        [Fact]
        public void NodesWithoutExecutionLogsAreSkipped()
        {
            var state = State(
                new List<NodeState> { Node("n1", SpecA.Prompt, "看起來像答案，但沒有執行紀錄") },
                new List<ExecutionLogState>());

            var result = EvalRunIngest.Build(state, Specs);

            // 沒有執行紀錄就沒有成本與耗時可查證——不可採用。
            Assert.Empty(result.Rows);
            Assert.Contains(result.Warnings, w => w.Contains("沒有執行紀錄"));
        }

        [Fact]
        public void ARerunNodeWarnsBecauseEarlierOutputsAreGone()
        {
            var t0 = new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);
            var state = State(
                new List<NodeState> { Node("n1", SpecA.Prompt, "只剩最後一次的營收答案") },
                new List<ExecutionLogState>
                {
                    Log("n1", "Manual", "gpt-5.6-sol", t0, usd: new[] { 0.01 }),
                    Log("n1", "Manual", "gpt-5.6-sol", t0.AddMinutes(5), usd: new[] { 0.02 }),
                });

            var result = EvalRunIngest.Build(state, Specs);

            var row = Assert.Single(result.Rows);
            Assert.Equal(0.02, row.CostUsd, 6);                 // 採用最後一次
            Assert.Contains(result.Warnings, w => w.Contains("被重跑過"));
        }

        [Fact]
        public void UnrelatedWorkInTheSameProjectIsNotCountedAsAnExperimentRun()
        {
            var t0 = new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);
            var state = State(
                new List<NodeState>
                {
                    Node("n1", SpecA.Prompt, "營收答案"),
                    Node("n9", "幫我把這段程式改成非同步", "改好的程式"),
                },
                new List<ExecutionLogState>
                {
                    Log("n1", "API Auto", "gpt-5.6-sol", t0),
                    Log("n9", "API Auto", "gpt-5.6-sol", t0.AddMinutes(1)),
                });

            var result = EvalRunIngest.Build(state, Specs);

            Assert.Single(result.Rows);
            Assert.Equal("T01", result.Rows[0].TaskId);
            Assert.Contains(result.Warnings, w => w.Contains("對不上任何題目"));
        }

        [Fact]
        public void ModelsColumnListsEveryModelActuallyUsed()
        {
            var t0 = new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);

            var usage = new List<UsageRecord>
            {
                new() { AtUtc = t0, Kind = "llm", ModelId = "gpt-5.6-sol", UsdCost = 0.01, IsActual = true },
                new() { AtUtc = t0, Kind = "llm", ModelId = "claude-sonnet-4-6", UsdCost = 0.02, IsActual = true },
                new() { AtUtc = t0, Kind = "llm", ModelId = "gpt-5.6-sol", UsdCost = 0.01, IsActual = true },
            };

            var log = Log("n1", "API Auto", "gpt-5.6-sol", t0) with { UsageRecords = usage };
            var state = State(new List<NodeState> { Node("n1", SpecA.Prompt, "營收答案") },
                              new List<ExecutionLogState> { log });

            var row = Assert.Single(EvalRunIngest.Build(state, Specs).Rows);

            // D 組的模型鏈是結論裡最有說服力的一手材料，不能只留一個代表值。
            Assert.Equal("gpt-5.6-sol|claude-sonnet-4-6", row.Models);
            Assert.Equal(0.04, row.CostUsd, 6);
        }

        // ───────────────────────── 手動組是不是真的單一模型 ─────────────────────────

        private static UsageRecord Llm(string model, string purpose)
            => new() { Kind = UsageKinds.Llm, ModelId = model, Purpose = purpose, UsdCost = 0.01, IsActual = true };

        [Fact]
        public void SameModelThroughTheSearchServiceStillCountsAsSingleModel()
        {
            // 搜尋研究經 Perplexity Agent API 跑同一個模型，帳目上的 id 長得不一樣，但它是同一個模型。
            var others = EvalRunIngest.OtherLlmModels(AiModels.Claude_Sonnet5, new[]
            {
                Llm(AiModels.Claude_Sonnet5, "輸出意圖判斷"),
                Llm("anthropic/claude-sonnet-5-5", "搜尋研究"),
                Llm(AiModels.Claude_Sonnet5, "主回覆"),
                new UsageRecord { Kind = UsageKinds.Image, ModelId = "gpt-image-2", Purpose = "簡報封面圖" }, // 不是語言模型
            });

            Assert.Empty(others);
        }

        [Fact]
        public void TheFirstPilotsClaudeGroupWasNotSingleModel()
        {
            // 2026-10-29 第一階段 B 組的帳目形狀：選了 Claude，意圖判斷與搜尋研究卻是 GPT。
            var others = EvalRunIngest.OtherLlmModels("claude-sonnet-5", new[]
            {
                Llm(AiModels.OpenAi_Gpt56, "輸出意圖判斷"),
                Llm("openai/gpt-5.6-sol", "搜尋研究"),
                Llm("claude-sonnet-5", "主回覆"),
            });

            Assert.Equal(new[] { AiModels.OpenAi_Gpt56 }, others);
        }

        [Fact]
        public void AManualRunThatSwitchedModelsIsFlagged()
        {
            var t0 = new DateTime(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);
            var log = Log("n1", "Manual", AiModels.Claude_Opus5, t0) with
            {
                RequestedModelId = AiModels.Claude_Opus5,
                ActualModelId = AiModels.OpenAi_Gpt56,
                UsageRecords = new List<UsageRecord> { Llm(AiModels.OpenAi_Gpt56, "主回覆") },
            };
            var state = State(new List<NodeState> { Node("n1", SpecA.Prompt, "營收答案") },
                              new List<ExecutionLogState> { log });

            var result = EvalRunIngest.Build(state, Specs);

            var row = Assert.Single(result.Rows);
            Assert.False(row.SingleModel);
            Assert.Contains(result.Warnings, w => w.Contains("不是單一模型執行"));
        }

        [Fact]
        public void EmptyStateProducesNothingInsteadOfThrowing()
        {
            Assert.Empty(EvalRunIngest.Build(null, Specs).Rows);
            Assert.Empty(EvalRunIngest.Build(
                State(new List<NodeState>(), new List<ExecutionLogState>()), Specs).Rows);
        }
    }
}
