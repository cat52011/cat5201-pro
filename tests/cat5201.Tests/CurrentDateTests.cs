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
    /// 模型要知道今天是哪一天。2026-11-06 實測：「東京這週末會下雨嗎？」搜尋拿回 10 月舊文和 11/5 新預報，
    /// 模型不知道今天幾號，只回「資料不足，請提供日期」；ChatGPT 網頁版會給日期，所以答得出來。
    /// </summary>
    public class CurrentDateTests
    {
        private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

        [Theory]
        [InlineData("2026-11-05", "2026-11-07", "2026-11-08", "星期四")]   // 平日 → 接下來的週末
        [InlineData("2026-11-07", "2026-11-07", "2026-11-08", "星期六")]   // 週六 → 今天、明天
        [InlineData("2026-11-08", "2026-11-07", "2026-11-08", "星期日")]   // 週日 → 昨天、今天
        [InlineData("2026-11-09", "2026-11-14", "2026-11-15", "星期一")]   // 週一 → 下一個週末
        public void Block_GivesTodayAndThisWeekend(string today, string saturday, string sunday, string weekday)
        {
            var now = new DateTimeOffset(DateTime.Parse(today).AddHours(0.5), Taipei);   // 半夜也算同一天
            string block = CurrentDateBlock.Build(now);

            Assert.Contains($"今天是 {today}（{weekday}）", block);
            Assert.Contains($"{saturday}（星期六）", block);
            Assert.Contains($"{sunday}（星期日）", block);
            Assert.Contains("UTC+08:00", block);
        }

        [Fact]
        public void Block_HandlesNegativeTimeZones()
            => Assert.Contains("UTC-05:00", CurrentDateBlock.Build(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(-5))));

        // ===== 走完整個 ExecuteAsync：模型真的收到日期 =====

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
            public Task<GenerationApproval> ConfirmGenerationAsync(
                OrchestrationTaskType taskType, OutputIntent? outputIntent, GenerationChoices choices)
                => Task.FromResult(GenerationApproval.Accepted);
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
                    StatusLabel = "Manual",
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

        // ===== 搜尋查詢也帶上換算好的日期 =====
        // 2026-11-06 實測：只送「東京這週末會下雨嗎？」去搜尋，拿回 10 月的舊文章和只到週六的預報。

        [Fact]
        public void SearchQuery_IsUnchanged_WhenTheDateIsNotNeeded()
            => Assert.Equal("Python 跟 Go 寫爬蟲各有什麼好壞",
                SearchCapability.BuildSearchQuery("Python 跟 Go 寫爬蟲各有什麼好壞", currentDate: null));

        [Theory]
        [InlineData("東京這週末會下雨嗎？")]
        [InlineData("Will it rain in Tokyo this weekend?")]   // 任何語言都一樣附 ISO 日期
        public void SearchQuery_CarriesTodayAndThisWeekend(string question)
        {
            string query = SearchCapability.BuildSearchQuery(question,
                new DateTimeOffset(2026, 11, 6, 0, 49, 0, Taipei));   // 星期五

            Assert.StartsWith(question, query);                      // 原句完整保留
            Assert.Contains("as of 2026-11-06", query);
            Assert.Contains("this weekend 2026-11-07 to 2026-11-08", query);
        }

        [Fact]
        public void SearchQuery_OnSunday_ThisWeekendIsYesterdayAndToday()
            => Assert.Contains("this weekend 2026-11-07 to 2026-11-08",
                SearchCapability.BuildSearchQuery("東京這週末會下雨嗎？", new DateTimeOffset(2026, 11, 8, 9, 0, 0, Taipei)));

        // ===== 文件、資料分析、簡報撰寫也拿得到日期 =====
        // 主回覆有日期，但交給 Claude 做的簡報與報告、資料分析、簡報撰寫的提示原本沒有。

        [Fact]
        public void DocumentPrompts_GetTheSameDateBlock_WhenNeeded()
        {
            var now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, Taipei);
            string prefix = AgentRuntime.DatePrefix(now);

            Assert.StartsWith(CurrentDateBlock.Build(now), prefix);    // 跟主回覆同一段文字
            Assert.EndsWith("\n\n", prefix);                           // 跟後面的提示分開
        }

        [Fact]
        public void DocumentPrompts_GetNothing_WhenTheDateIsNotNeeded()
            => Assert.Equal("", AgentRuntime.DatePrefix(null));

        // ===== 需要時才給日期（2026-11-06 使用者指示：需要的時候去讀，不需要就不管）=====

        private static async Task<string> PromptFor(string text, CapabilityNeeds? needs)
        {
            var prompts = new List<string>();
            var runtime = new AgentRuntime(
                new FakeHost(),
                new FakeResolver(),
                (node, prompt, decision, onDelta, streaming, ct) =>
                {
                    prompts.Add(prompt);
                    return Task.FromResult(new AiFallbackExecutionResult { IsSuccess = true, Text = "好的。", ActualModelId = decision.ModelId });
                },
                new FakeFinalizer());

            await runtime.ExecuteAsync(new AgentExecutionRequest
            {
                Node = new FakeNode(),
                Agent = AgentRegistry.Get(null),
                TopText = text,
                SkipCapabilities = true,
                Workspace = new AgentWorkspace(),
                OutputIntentTask = needs == null
                    ? null
                    : Task.FromResult<OutputIntent?>(new OutputIntent { Source = "api:test", Capabilities = needs }),
            });
            return Assert.Single(prompts);
        }

        [Fact]
        public async Task NoDate_WhenTheIntentSaysTimeDoesNotMatter()
            => Assert.DoesNotContain("【目前日期】",
                await PromptFor("幫我比較 Python 和 Go 的優缺點", new CapabilityNeeds(false, true, false, NeedsCurrentDate: false)));

        [Fact]
        public async Task Date_WhenTheIntentSaysItIsNeeded()
            => Assert.Contains("【目前日期】",
                await PromptFor("我 2003 年 5 月出生，現在幾歲？", new CapabilityNeeds(false, false, false, NeedsCurrentDate: true)));

        [Fact]
        public async Task Date_WhenSearching_EvenIfTheIntentForgotIt()
            // 有搜尋就一定給：要靠日期分辨搜尋結果的新舊。
            => Assert.Contains("【目前日期】",
                await PromptFor("東京這週末會下雨嗎？", new CapabilityNeeds(true, false, false, NeedsCurrentDate: false)));

        [Fact]
        public async Task Date_WhenTheIntentJudgementIsMissing()
            // 不知道需不需要時給：多給只多幾十個 token，少給可能答錯。
            => Assert.Contains("【目前日期】", await PromptFor("東京這週末會下雨嗎？", needs: null));

        [Theory]
        [InlineData(",\"needs_date\":false", false)]
        [InlineData(",\"needs_date\":true", true)]
        [InlineData("", true)]   // 模型沒給這欄（舊格式）→ 當成需要
        public void Parser_ReadsNeedsDate(string field, bool expected)
        {
            var intent = OutputIntentResolver.TryParse(
                "{\"presentation\":false,\"needs_search\":false,\"needs_reasoning\":false,\"needs_code\":false" + field + "}");

            Assert.NotNull(intent?.Capabilities);
            Assert.Equal(expected, intent!.Capabilities!.NeedsCurrentDate);
        }

        [Fact]
        public async Task TheModelReceivesTodaysDate()
        {
            var prompts = new List<string>();
            var runtime = new AgentRuntime(
                new FakeHost(),
                new FakeResolver(),
                (node, prompt, decision, onDelta, streaming, ct) =>
                {
                    prompts.Add(prompt);
                    return Task.FromResult(new AiFallbackExecutionResult { IsSuccess = true, Text = "好的。", ActualModelId = decision.ModelId });
                },
                new FakeFinalizer());

            // 日期來自平台的使用者時間（UserClock），跟每日額度用的「今天」同一個來源。
            AgentExecutionResult result;
            using (UserClock.Override(() => new DateTimeOffset(2026, 11, 6, 0, 17, 0, Taipei)))
            {
                result = await runtime.ExecuteAsync(new AgentExecutionRequest
                {
                    Node = new FakeNode(),
                    Agent = AgentRegistry.Get(null),
                    TopText = "東京這週末會下雨嗎？",
                    SkipCapabilities = true,
                    Workspace = new AgentWorkspace(),
                    PreferenceBlock = "【使用者偏好】回答簡短。",
                });
            }

            Assert.True(result.IsSuccess);
            string main = Assert.Single(prompts);
            Assert.Contains("【目前日期】今天是 2026-11-06（星期五）", main);
            Assert.Contains("2026-11-07（星期六）與 2026-11-08（星期日）", main);
            Assert.Contains("東京這週末會下雨嗎？", main);
            // 使用者偏好仍在最前面（最高優先），日期緊接在後。
            Assert.StartsWith("【使用者偏好】", main);
            Assert.True(main.IndexOf("【目前日期】", StringComparison.Ordinal) < main.IndexOf("東京這週末", StringComparison.Ordinal));
        }
    }
}
