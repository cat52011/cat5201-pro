using System;
using System.Collections.Concurrent;
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
    /// 獨立審查（2026-11-03 使用者要求）：不參與寫作，只對照數字與文字、找矛盾，回報修改清單。
    /// 起因：機器學習作業的沙箱報告裡有 nan、有「影響為 改善」這種拼接句、也有前後矛盾。
    /// </summary>
    public class ReportReviewTests
    {
        private static IReadOnlyList<IReadOnlyList<string>> Rows(params string[][] rows) => rows;

        [Fact]
        public void ProgramChecks_FindInvalidValuesAndLeftoverPlaceholders_WithoutAi()
        {
            string report =
                "# 報告\n\n" +
                "M1 的測試 R² 為 0.856，log 尺度為 nan。\n\n" +
                "| 模型 | R²(log) | RMSE |\n|---|---|---|\n| M1 | nan | 7,812,384 |\n| M2 | 0.914 | 6,040,000 |\n\n" +
                "最佳模型是 {best_model}。\n\n" +
                "![圖](data:image/png;base64,AAAA)\n\n" +
                "None of the models overfit.";      // 正常英文句子裡的 None 不算

            var tables = new List<(string, IReadOnlyList<IReadOnlyList<string>>)>
            {
                ("results.xlsx／model_comparison", Rows(new[] { "model", "r2_log" }, new[] { "M1", "NaN" })),
            };

            var issues = ReportReview.ProgramChecks(report, tables);

            Assert.Contains(issues, i => i.Where == "報告第 1 個表格第 2 列" && i.Problem.Contains("「nan」"));
            Assert.Contains(issues, i => i.Where.StartsWith("內文") && i.Problem.Contains("「nan」"));
            Assert.Contains(issues, i => i.Problem.Contains("{best_model}"));
            Assert.Contains(issues, i => i.Where.Contains("model_comparison") && i.Problem.Contains("NaN"));
            Assert.DoesNotContain(issues, i => i.Evidence.Contains("None of the models"));
            Assert.DoesNotContain(issues, i => i.Evidence.Contains("base64"));
        }

        [Fact]
        public void ProgramChecks_CleanReport_FindsNothing()
        {
            Assert.Empty(ReportReview.ProgramChecks(
                "M2 的測試 R² 為 0.914。\n\n| 模型 | R² |\n|---|---|\n| M2 | 0.914 |\n\n訓練集 2,213 筆、測試集 554 筆。"));
        }

        [Fact]
        public void ReviewerReply_IsParsed_SortedBySeverity_AndBadSeverityIsNormalized()
        {
            string reply = "以下是結果：\n{\"issues\":[" +
                "{\"severity\":\"低\",\"where\":\"第 9 節\",\"problem\":\"殘留字句\",\"evidence\":\"影響為 改善\",\"suggestion\":\"改成「降低了 8.5 個百分點」\"}," +
                "{\"severity\":\"高\",\"where\":\"第 5 節\",\"problem\":\"R² 與表格不一致\",\"evidence\":\"文字寫 0.86，表格是 0.81\",\"suggestion\":\"改成 0.81\"}," +
                "{\"severity\":\"嚴重\",\"where\":\"第 7 節\",\"problem\":\"前後矛盾\",\"evidence\":\"…\",\"suggestion\":\"…\"}," +
                "{\"severity\":\"高\",\"where\":\"x\",\"problem\":\"\"}" +       // 沒寫問題的項目略過
                "],\"checked\":\"核對了第 5、7、9 節的數字\"}";

            var parsed = ReportReview.ParseReply(reply);

            Assert.NotNull(parsed);
            var issues = parsed!.Value.Issues;
            Assert.Equal(3, issues.Count);
            Assert.Equal("高", issues[0].Severity);
            Assert.Equal("中", issues[1].Severity);       // 「嚴重」不是三個等級之一 → 中
            Assert.Equal("低", issues[2].Severity);
            Assert.Equal("核對了第 5、7、9 節的數字", parsed.Value.Checked);
        }

        [Fact]
        public void UnparseableReply_ReturnsNull_AndTheListSaysSo()
        {
            Assert.Null(ReportReview.ParseReply("我覺得報告寫得不錯。"));

            var result = new ReportReview.Result(
                Array.Empty<ReportReview.Issue>(), Array.Empty<ReportReview.Issue>(),
                AiModels.OpenAi_Gpt56, "測試", "", "我覺得報告寫得不錯。");
            string md = ReportReview.ToMarkdown("報告.docx", Array.Empty<string>(), result);

            Assert.Contains("審查回覆無法解析", md);
            Assert.Contains("我覺得報告寫得不錯。", md);
            Assert.DoesNotContain("沒有找到數字不符", md);       // 不能把「解析失敗」說成「沒問題」
        }

        [Fact]
        public void Reviewer_InAuto_IsFromTheOtherProvider()
        {
            static bool Both(AiProviderKind _) => true;
            Assert.Equal(AiModels.OpenAi_Gpt56,
                ReportReview.PickReviewer(ModelScope.Auto, AiModels.Claude_Sonnet5, Both).Model);
            Assert.Equal(AiModels.Claude_Sonnet5,
                ReportReview.PickReviewer(ModelScope.Auto, AiModels.OpenAi_Gpt56, Both).Model);

            // 只有一家的金鑰：同一家，但說明清楚
            var onlyClaude = ReportReview.PickReviewer(ModelScope.Auto, AiModels.Claude_Sonnet5, k => k == AiProviderKind.Claude);
            Assert.Equal(AiModels.Claude_Sonnet5, onlyClaude.Model);
            Assert.Contains("同一家", onlyClaude.Note);

            Assert.Null(ReportReview.PickReviewer(ModelScope.Auto, AiModels.Claude_Sonnet5, _ => false).Model);
        }

        [Fact]
        public void Reviewer_InManualMode_IsOnlyTheSelectedModel()
        {
            // 手動模式不能有選擇之外的模型參與（2026-10-29 使用者指示），即使另一家有金鑰。
            var pick = ReportReview.PickReviewer(ModelScope.Manual(AiModels.Claude_Sonnet5), AiModels.Claude_Sonnet5, _ => true);
            Assert.Equal(AiModels.Claude_Sonnet5, pick.Model);
            Assert.Contains("獨立的呼叫", pick.Note);

            var deep = ReportReview.PickReviewer(ModelScope.Manual(AiModels.Perplexity_SonarDeepResearch), null, _ => true);
            Assert.Null(deep.Model);
        }

        [Fact]
        public void Prompt_HasTheReportAndResults_ButNoImagesAndNoRepeatedProgramFindings()
        {
            string prompt = ReportReview.BuildPrompt(
                "HW1.docx",
                "M1 的 R² 為 0.86。\n\n![圖](data:image/png;base64," + new string('A', 5000) + ")",
                new List<(string, IReadOnlyList<IReadOnlyList<string>>)> { ("results.xlsx／cmp", Rows(new[] { "M1", "0.81" })) },
                new[] { new ReportReview.Issue("中", "報告第 1 個表格第 2 列", "儲存格是「nan」", "", "") });

            Assert.Contains("M1 的 R² 為 0.86", prompt);
            Assert.Contains("M1 | 0.81", prompt);
            Assert.DoesNotContain("base64", prompt);
            Assert.Contains("［圖］", prompt);
            Assert.Contains("程式已找到的問題（不要重複回報）", prompt);
            Assert.Contains("不會修改它", prompt);
        }

        [Fact]
        public void Summary_SaysWhoReviewed_AndThatTheReportWasNotChanged()
        {
            var r = new ReportReview.Result(
                new[] { new ReportReview.Issue("高", "第 5 節", "R² 不一致", "0.86 / 0.81", "改成 0.81") },
                new[] { new ReportReview.Issue("中", "表格", "nan", "", "") },
                AiModels.OpenAi_Gpt56, "和撰寫者不同家的模型，沒有參與撰寫", "", "");

            string summary = ReportReview.Summary(r);
            Assert.Contains("沒有參與撰寫", summary);
            Assert.Contains("2 項", summary);
            Assert.Contains("1 項較嚴重", summary);
            Assert.Contains("沒有被修改", summary);

            string md = ReportReview.ToMarkdown("HW1.docx", new[] { "results.xlsx" }, r);
            Assert.Contains("| 1 | 高 | 第 5 節 | R² 不一致 | 0.86 / 0.81 | 改成 0.81 |", md);
            Assert.Contains("對照 results.xlsx", md);
            Assert.Contains("沒有修改報告", md);
        }

        [Fact]
        public void ReviewAndDisclosureFiles_HaveTheirOwnCategories()
        {
            Assert.Equal("審查", OutputFileCategory.Of("HW1（審查清單）_20261103_210000.md").Label);
            Assert.Equal("紀錄", OutputFileCategory.Of("完成此作業（AI使用紀錄）_20261103_210000.md").Label);
            Assert.Equal("文字", OutputFileCategory.Of("筆記.md").Label);
        }

        // ── 走完整個 ExecuteAsync：產出報告 → 審查 → 清單另存、報告不動 ──

        private const string ReportText =
            "# BMI 分析報告\n\n" +
            "M1 的測試 R² 為 0.86，是最好的模型。\n\n" +
            "| 模型 | 測試 R² |\n|---|---|\n| M1 | 0.81 |\n| M2 | nan |\n";

        private const string ReviewerReply =
            "{\"issues\":[{\"severity\":\"高\",\"where\":\"第 1 段\",\"problem\":\"R² 與表格不一致\"," +
            "\"evidence\":\"文字寫 0.86，表格寫 0.81\",\"suggestion\":\"改成 0.81\"}],\"checked\":\"核對了 R²\"}";

        [Fact]
        public async Task ProducedReport_IsReviewed_ListIsSavedSeparately_AndTheReportIsUntouched()
        {
            var previous = ReportReview.HasKey;
            ReportReview.HasKey = _ => true;
            try
            {
                var prompts = new ConcurrentBag<string>();
                var host = new Host();
                var runtime = new AgentRuntime(host, new Resolver(),
                    (node, prompt, decision, onDelta, streaming, ct) =>
                    {
                        prompts.Add(prompt);
                        bool review = prompt.Contains("你是獨立的審查者", StringComparison.Ordinal);
                        return Task.FromResult(new AiFallbackExecutionResult
                        {
                            IsSuccess = true,
                            Text = review ? ReviewerReply : ReportText,
                            ActualModelId = decision.ModelId,
                        });
                    },
                    new Finalizer());

                var workspace = new AgentWorkspace();
                var result = await runtime.ExecuteAsync(new AgentExecutionRequest
                {
                    Node = new Node(),
                    Agent = AgentRegistry.Get(null),
                    TopText = "寫一份書面報告比較 BMI 模型",
                    SkipCapabilities = true,
                    Workspace = workspace,
                    CancellationToken = CancellationToken.None,
                });

                var files = workspace.GetByType("generated_file").Select(x => x.Payload)
                    .OfType<GeneratedFilePayload>().Where(f => f.Success).ToList();
                var docx = Assert.Single(files, f => f.FilePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
                var list = Assert.Single(files, f => f.FileName.Contains("審查清單"));

                // 審查模型看到的是做好的報告
                var reviewPrompt = Assert.Single(prompts, p => p.Contains("你是獨立的審查者"));
                Assert.Contains("0.86", reviewPrompt);
                Assert.Contains("M2 | nan", reviewPrompt);

                // 清單：模型找到的矛盾＋程式找到的 nan
                string md = File.ReadAllText(list.FilePath);
                Assert.Contains("R² 與表格不一致", md);
                Assert.Contains("「nan」", md);

                // 報告本身沒有被改
                Assert.Contains("nan", ArtifactTextExtractor.ExtractDocx(docx.FilePath));

                Assert.Contains("獨立審查", result.FinalText);
                Assert.Contains("沒有參與撰寫", result.FinalText);
            }
            finally
            {
                ReportReview.HasKey = previous;
            }
        }

        [Fact]
        public async Task ReviewSwitchedOff_NoReviewCall_NoChecklist()
        {
            // 個人化關掉獨立審查（2026-11-04）：不呼叫審查模型、不產審查清單、答案不加那一行。
            // 放在這個類別裡：同一類別的測試依序執行，不會跟上面那個要審查的測試互相干擾。
            var previousKey = ReportReview.HasKey;
            bool previousSwitch = DeliveryOptions.IndependentReview;
            ReportReview.HasKey = _ => true;
            DeliveryOptions.IndependentReview = false;
            try
            {
                var prompts = new ConcurrentBag<string>();
                var runtime = new AgentRuntime(new Host(), new Resolver(),
                    (node, prompt, decision, onDelta, streaming, ct) =>
                    {
                        prompts.Add(prompt);
                        return Task.FromResult(new AiFallbackExecutionResult
                        {
                            IsSuccess = true, Text = ReportText, ActualModelId = decision.ModelId,
                        });
                    },
                    new Finalizer());

                var workspace = new AgentWorkspace();
                var result = await runtime.ExecuteAsync(new AgentExecutionRequest
                {
                    Node = new Node(),
                    Agent = AgentRegistry.Get(null),
                    TopText = "寫一份書面報告比較 BMI 模型",
                    SkipCapabilities = true,
                    Workspace = workspace,
                    CancellationToken = CancellationToken.None,
                });

                var files = workspace.GetByType("generated_file").Select(x => x.Payload)
                    .OfType<GeneratedFilePayload>().Where(f => f.Success).ToList();
                Assert.Contains(files, f => f.FilePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(files, f => f.FileName.Contains("審查清單"));
                Assert.DoesNotContain(prompts, p => p.Contains("你是獨立的審查者"));
                Assert.DoesNotContain("獨立審查", result.FinalText);
            }
            finally
            {
                ReportReview.HasKey = previousKey;
                DeliveryOptions.IndependentReview = previousSwitch;
            }
        }

        private sealed class Host : IAgentHost
        {
            public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            public bool IsAutoModelSelectionEnabled() => true;
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
            public Task<GenerationApproval> ConfirmGenerationAsync(OrchestrationTaskType t, OutputIntent? i, GenerationChoices c)
                => Task.FromResult(GenerationApproval.Accepted);
            public string GetNodeSelectedModel(INodeContext node) => "";
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
