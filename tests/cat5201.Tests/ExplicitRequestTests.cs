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
    /// 2026-11-03 測試：「做一份 3 頁關於BMI簡報（快速版、不配圖）」——
    /// 確認框寫「最佳品質模式」、還生了封面照片、檔名成了「BMI（快速版、不）」。
    ///
    /// 使用者的規則：要求的意思交給第一層語意判斷，不比對關鍵字——換個說法或換個語言，關鍵字就認不出來。
    /// 所以這裡測的是：模型回的欄位有沒有被讀進來，以及程式有沒有照著做。
    /// </summary>
    public class ExplicitRequestTests
    {
        private const string BmiDeck = "做一份 3 頁關於BMI簡報（快速版、不配圖）";

        [Fact]
        public void ResolverReply_CarriesQuality_NoImages_AndTopic()
        {
            var intent = OutputIntentResolver.TryParse(
                "{\"presentation\":true,\"report\":false,\"table\":false,\"image\":false,\"video\":false," +
                "\"illustrations\":false,\"no_illustrations\":true,\"quality\":\"fast\",\"topic\":\"BMI\",\"slides\":3,\"language\":\"\"}");

            Assert.NotNull(intent);
            Assert.True(intent!.WantsPresentation);
            Assert.True(intent.DeclinesIllustrations);
            Assert.False(intent.WantsIllustrations);
            Assert.Equal(RequestedQuality.Level.Fast, intent.Quality);
            Assert.Equal("BMI", intent.Topic);
            Assert.Equal(3, intent.RequestedSlides);
        }

        [Fact]
        public void ResolverReply_WithoutTheNewFields_MeansNothingWasSaid()
        {
            // 舊格式或模型漏了欄位：當作沒講，照個人化的設定，不去猜。
            var intent = OutputIntentResolver.TryParse("{\"presentation\":true,\"illustrations\":true,\"slides\":0}");
            Assert.NotNull(intent);
            Assert.Equal(RequestedQuality.Level.None, intent!.Quality);
            Assert.False(intent.DeclinesIllustrations);
            Assert.True(intent.WantsIllustrations);
            Assert.Equal("", intent.Topic);
        }

        [Fact]
        public void ResolverPrompt_AsksForTheNewFields_WithTheBmiExample()
        {
            string prompt = typeof(OutputIntentResolver)
                .GetMethod("BuildSystemPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, null) as string ?? "";
            Assert.Contains("\"no_illustrations\"", prompt);
            Assert.Contains("\"quality\"", prompt);
            Assert.Contains("\"topic\"", prompt);
            Assert.Contains(BmiDeck, prompt);
            Assert.Contains("不要依賴特定字詞", prompt);
        }

        [Theory]
        [InlineData("fast", RequestedQuality.Level.Fast)]
        [InlineData("BEST", RequestedQuality.Level.Best)]
        [InlineData("", RequestedQuality.Level.None)]
        [InlineData("very fast please", RequestedQuality.Level.None)]   // 看不懂的值當作沒講
        public void QualityField_IsParsedStrictly(string value, RequestedQuality.Level expected)
            => Assert.Equal(expected, RequestedQuality.Parse(value));

        [Fact]
        public void ExplicitQuality_OverridesThePersonalizationDefault()
        {
            Assert.Equal(DocumentEngine.Builtin, RequestedQuality.Apply(RequestedQuality.Level.Fast, DocumentEngine.ClaudeSkills));
            Assert.Equal(DocumentEngine.ClaudeSkills, RequestedQuality.Apply(RequestedQuality.Level.Best, DocumentEngine.Builtin));
            Assert.Equal(DocumentEngine.ClaudeSkills, RequestedQuality.Apply(RequestedQuality.Level.None, DocumentEngine.ClaudeSkills));
        }

        [Fact]
        public void NoImages_FromTheResolver_Wins_EvenIfTheWordsMentionImages()
        {
            // 「不配圖」裡有「配圖」兩個字——以前的關鍵字規則就是被這個騙了。現在只看語意判斷。
            Assert.False(DeckIllustrationPolicy.ShouldIllustrate(
                BmiDeck, null, explicitFromResolver: false, out var reason, declinedFromResolver: true));
            Assert.Equal(DeckIllustrationPolicy.Reason.UserDeclined, reason);

            // 模型說要，就要（任何語言的講法都由模型負責認）
            Assert.True(DeckIllustrationPolicy.ShouldIllustrate(
                "Make a deck about Kyoto, with pictures", null, explicitFromResolver: true, out _));
        }

        [Fact]
        public void RequestWords_AloneNoLongerDecideImages()
        {
            // 模型沒說要也沒說不要：看內容需不需要圖，不看請求裡有沒有「配圖」兩個字。
            Assert.False(DeckIllustrationPolicy.ShouldIllustrate(
                "做一份簡報，要配圖", null, explicitFromResolver: false, out var reason,
                contentText: "營收成長率、毛利率與 EPS 的季度比較表"));
            Assert.Equal(DeckIllustrationPolicy.Reason.TextualSubject, reason);
        }

        [Theory]
        [InlineData(BmiDeck, "BMI")]                       // 模型沒回主題時的後援：至少不留下空括號
        [InlineData("做一份 5 頁台積電簡報（）", "台積電")]
        public void FallbackTitle_LeavesNoEmptyBrackets(string request, string expected)
        {
            string title = AgentRuntime.CleanTitleFromText(request, "簡報");
            Assert.DoesNotContain("（）", title);
            Assert.StartsWith(expected, title);
        }

        // ── 走完整個 ExecuteAsync ──

        [Fact]
        public async Task ManualMode_FastAndNoImages_FromTheResolver_ShapeTheConfirmDialog()
        {
            var host = new Host(DocumentEngine.ClaudeSkills, manualModel: AiModels.Claude_Sonnet5, approve: false);
            await RunAsync(host, BmiDeck, new OutputIntent
            {
                WantsPresentation = true,
                DeclinesIllustrations = true,
                Quality = RequestedQuality.Level.Fast,
                Topic = "BMI",
                Source = "test",
            });

            var choices = Assert.Single(host.Asked);
            Assert.False(choices.BestQualityEnabled);       // 個人化是最佳品質，但這次說了快速版
            Assert.False(choices.IllustrationsDefault);     // 說了不要圖 → 預設不勾
        }

        [Fact]
        public async Task ManualMode_NothingSaid_KeepsThePersonalizationDefault()
        {
            var host = new Host(DocumentEngine.ClaudeSkills, manualModel: AiModels.Claude_Sonnet5, approve: false);
            await RunAsync(host, "做一份 3 頁關於BMI的簡報", new OutputIntent { WantsPresentation = true, Source = "test" });

            Assert.True(Assert.Single(host.Asked).BestQualityEnabled);
        }

        [Fact]
        public async Task FileName_UsesTheTopicFromTheResolver()
        {
            var host = new Host(DocumentEngine.Builtin, manualModel: null, approve: true);
            var ws = await RunAsync(host, "幫我寫一份書面報告介紹 BMI，快速版就好", new OutputIntent
            {
                WantsReport = true,
                Quality = RequestedQuality.Level.Fast,
                Topic = "BMI 介紹",
                Source = "test",
            });

            var docx = ws.GetByType("generated_file").Select(x => x.Payload).OfType<GeneratedFilePayload>()
                .Single(f => f.Success && f.FilePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("BMI 介紹.docx", docx.FileName);     // 跟存檔一樣只有標題，不帶時間戳記
        }

        [Fact]
        public void FileNames_LookLikeProjectTitles_SameNameGetsANumber()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            var first = GeneratedFileWriter.WriteMarkdown(dir, "BMI 身體質量指數", "a", "test");
            var second = GeneratedFileWriter.WriteMarkdown(dir, "BMI 身體質量指數", "b", "test");
            var code = GeneratedFileWriter.WriteCodeFiles(dir, "BMI 計算程式",
                new[] { new CodeFileExtractor.CodeFile("bmi.py", "python", "print(1)\n") }, "test");

            Assert.Equal("BMI 身體質量指數.md", first.FileName);
            Assert.Equal("BMI 身體質量指數_2.md", second.FileName);       // 同名：跟存檔一樣加 _2
            Assert.Equal("BMI 計算程式", Path.GetFileName(Path.GetDirectoryName(code.Single().FilePath)));
        }

        [Theory]
        [InlineData("「BMI 身體質量指數」", "BMI 身體質量指數")]
        [InlineData("  台積電   公司分析 ", "台積電 公司分析")]
        [InlineData("a/b:c", "a_b_c")]
        public void ShortTitle_IsCleanedTheSameWayAsProjectTitles(string raw, string expected)
            => Assert.Equal(expected, ShortTitle.Normalize(raw));

        [Fact]
        public void TopicRules_AreTheSameAsProjectNaming()
        {
            string prompt = typeof(OutputIntentResolver)
                .GetMethod("BuildSystemPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, null) as string ?? "";
            Assert.Contains(ShortTitle.Rules, prompt);
        }

        private static async Task<AgentWorkspace> RunAsync(Host host, string request, OutputIntent intent)
        {
            {
                var runtime = new AgentRuntime(host, new Resolver(),
                    (node, prompt, decision, onDelta, streaming, ct) => Task.FromResult(new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = "# BMI\n\nBMI 是體重除以身高平方，18.5 到 24 為健康體重。",
                        ActualModelId = decision.ModelId,
                    }),
                    new Finalizer());

                var workspace = new AgentWorkspace();
                await runtime.ExecuteAsync(new AgentExecutionRequest
                {
                    Node = new Node(),
                    Agent = AgentRegistry.Get(null),
                    TopText = request,
                    OutputIntent = intent,
                    SkipCapabilities = true,
                    Workspace = workspace,
                    CancellationToken = CancellationToken.None,
                });
                return workspace;
            }
        }

        private sealed class Host : IAgentHost
        {
            private readonly DocumentEngine _engine;
            private readonly string? _manualModel;
            private readonly bool _approve;

            public Host(DocumentEngine engine, string? manualModel, bool approve)
            {
                _engine = engine;
                _manualModel = manualModel;
                _approve = approve;
            }

            public List<GenerationChoices> Asked { get; } = new();
            public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            public bool IsAutoModelSelectionEnabled() => _manualModel == null;
            public bool IsAdvancedAutoResolverEnabled() => false;
            public PresentationEngine GetPresentationEngine() => PresentationEngine.Claude;
            public DocumentEngine GetDocumentEngine() => _engine;
            public VeoModelTier GetVeoModelTier() => VeoModelTier.Lite;
            public string GetEffectiveVeoModel() => "";
            public string GetEffectiveVideoStylePrompt() => "";
            public IReadOnlyList<AttachmentInfo> GetAttachmentsForNode(INodeContext node) => Array.Empty<AttachmentInfo>();
            public IReadOnlyList<AttachmentInfo> GetEffectiveAttachmentsForNode(INodeContext node) => Array.Empty<AttachmentInfo>();
            public string GetAttachmentsRootDir() => Dir;
            public string GetGeneratedFilesDir() => Dir;
            public void SetLiveDecisionResolving(INodeContext node, NodeExecutionDecision decision) { }

            // 測確認框的那兩個測試選「不產生」：只看確認框收到什麼，不讓它真的去產檔（更不能碰到真的 API）。
            public Task<GenerationApproval> ConfirmGenerationAsync(OrchestrationTaskType t, OutputIntent? i, GenerationChoices c)
            {
                lock (Asked) Asked.Add(c);
                return Task.FromResult(_approve ? GenerationApproval.Accepted : GenerationApproval.Declined);
            }

            public string GetNodeSelectedModel(INodeContext node) => _manualModel ?? "";
        }

        private sealed class Node : INodeContext
        {
            public Guid Id { get; } = Guid.NewGuid();
            public void SetLoadingHint(string? hint) { }
        }

        private sealed class Resolver : IExecutionDecisionResolver
        {
            public Task<NodeExecutionDecision> ResolveAsync(INodeContext n, string topText, CancellationToken ct)
                => Task.FromResult(new NodeExecutionDecision
                {
                    ModelId = AiModelRegistry.Default.Id,
                    RequestedModelId = AiModelRegistry.Default.Id,
                    TaskMode = NodeTaskMode.Chat,
                    Confidence = 1.0,
                });
        }

        private sealed class Finalizer : IDecisionFinalizer
        {
            public NodeExecutionDecision FinalizeDecision(NodeExecutionDecision d, AiFallbackExecutionResult e)
            {
                d.ActualModelId = string.IsNullOrWhiteSpace(e.ActualModelId) ? d.ModelId : e.ActualModelId;
                return d;
            }
        }
    }
}
