using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    // AgentRuntime 的一部分：獨立審查：報告做完後由沒有參與撰寫的一方核對數字與文字。
    // （2026-11-03 依職責拆檔：方法原樣搬出，沒有改任何邏輯；主流程在 AgentRuntime.cs。）
    public sealed partial class AgentRuntime
    {
        /// <summary>
        /// 獨立審查（ReportReview）：這一趟產出了報告（.docx）就跑一次；沒有 Word 報告但有簡報（.pptx）時審簡報。
        /// 只列問題，不改報告：清單另存成「審查清單」檔，答案最後加一句結果。
        /// 審查失敗不影響報告本身。
        /// 同一趟產出的程式檔（任何語言，見 ReportReview.CodeExtensions）也一起給審查者，
        /// 核對報告寫的做法和程式是否一致（2026-11-04）。
        /// </summary>
        private async Task<AiFallbackExecutionResult> TryReviewDeliverablesAsync(
            INodeContext node, AgentWorkspace workspace, AiFallbackExecutionResult execution, CancellationToken ct)
        {
            var files = workspace.GetByType("generated_file")
                .Select(x => (Item: x, File: x.Payload as GeneratedFilePayload))
                .Where(x => x.File is { Success: true } && System.IO.File.Exists(x.File.FilePath))
                .ToList();

            bool HasExt(GeneratedFilePayload f, string ext) =>
                string.Equals(System.IO.Path.GetExtension(f.FilePath), ext, StringComparison.OrdinalIgnoreCase);

            var report = files.LastOrDefault(x => HasExt(x.File!, ".docx"));
            if (report.File == null)
                report = files.LastOrDefault(x => HasExt(x.File!, ".pptx"));
            if (report.File == null)
                return execution;

            try
            {
                node.SetLoadingHint("獨立審查：對照報告的數字與文字…");

                string reportName = report.File.FileName;
                string markdown = HasExt(report.File, ".pptx")
                    ? ArtifactTextExtractor.ExtractPptx(report.File.FilePath)
                    : ArtifactTextExtractor.ExtractDocx(report.File.FilePath);

                var tables = new List<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>();
                var comparedNames = new List<string>();
                foreach (var x in files.Where(x => HasExt(x.File!, ".xlsx")))
                {
                    comparedNames.Add(x.File!.FileName);
                    foreach (var (sheet, rows) in ArtifactTextExtractor.ExtractXlsxSheets(x.File.FilePath))
                        tables.Add(($"{x.File.FileName}／{sheet}", rows));
                }

                // 程式碼只給審查模型核對做法描述，不做程式檢查（nan 等字樣在程式裡是正常的）。
                var codeFiles = new List<(string Name, string Code)>();
                foreach (var x in files.Where(x => ReportReview.IsCodeFile(x.File!.FilePath)))
                {
                    string code;
                    try { code = ReportReview.ReadCode(x.File!.FilePath); }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Review", $"讀不到程式檔，審查不含這個檔：{x.File!.FileName}", ex);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    codeFiles.Add((x.File!.FileName, code));
                    comparedNames.Add(x.File.FileName);
                }

                var programIssues = ReportReview.ProgramChecks(markdown, tables);

                var (reviewer, note) = ReportReview.PickReviewer(_scope, report.Item.ModelId, ReportReview.HasKey);

                IReadOnlyList<ReportReview.Issue> modelIssues = Array.Empty<ReportReview.Issue>();
                string checkedText = "", raw = "";
                if (reviewer != null)
                {
                    using var reservation = SpendReservations.TryReserveWithinBudget("獨立審查", 0.5);
                    if (reservation == null)
                    {
                        reviewer = null;
                        note = "會超過今日預算上限，只做程式檢查";
                    }
                    else
                    {
                        var decision = new NodeExecutionDecision
                        {
                            RequestedAgentId = "general-agent",
                            ActualAgentId = "general-agent",
                            RequestedModelId = AiModelHelper.NormalizeNodeModel(reviewer),
                            ModelId = AiModelHelper.NormalizeNodeModel(reviewer),
                            ActualModelId = "",
                            TaskMode = NodeTaskMode.Chat,
                            ResolverLabel = "Independent Review",
                            ResolverReason = "沒有參與撰寫的模型對照報告的數字與文字、找矛盾，只回報修改清單。",
                            StatusLabel = "Auto",
                            ForceSingleModel = true,
                        };

                        using var purpose = UsageMeter.Purpose("獨立審查");
                        var r = await _executeWithFallbackAsync(
                            node, ReportReview.BuildPrompt(reportName, markdown, tables, programIssues, codeFiles), decision, null, false, ct);
                        raw = r.IsSuccess ? (r.Text ?? "") : "";
                        var parsed = ReportReview.ParseReply(raw);
                        if (parsed != null)
                        {
                            modelIssues = parsed.Value.Issues;
                            checkedText = parsed.Value.Checked;
                        }
                        else if (!r.IsSuccess)
                        {
                            AppLog.Warn("Review", $"審查模型呼叫失敗：{r.ErrorMessage}");
                            note += $"（審查模型這次沒有回應：{r.ErrorMessage}）";
                        }
                    }
                }

                var result = new ReportReview.Result(modelIssues, programIssues, reviewer ?? "", note, checkedText, raw);
                string title = System.IO.Path.GetFileNameWithoutExtension(reportName);
                var list = GeneratedFileWriter.WriteMarkdown(
                    _main.GetGeneratedFilesDir(), title + "（審查清單）",
                    ReportReview.ToMarkdown(reportName, comparedNames, result),
                    $"獨立審查 / {reviewer ?? "程式檢查"}");
                if (list.Success)
                    workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                        workspace, node, "review-agent", "generated_file", list, modelId: reviewer ?? ""));

                AppLog.Info("Review",
                    $"獨立審查：審查模型 {modelIssues.Count} 項、程式檢查 {programIssues.Count} 項（{reviewer ?? "未呼叫模型"}）");
                return ReplaceExecutionText(execution, (execution.Text ?? "").TrimEnd() + "\n\n" + ReportReview.Summary(result));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("Review", "獨立審查失敗（報告不受影響）", ex);
                return execution;
            }
            finally
            {
                node.SetLoadingHint(null);
            }
        }
    }
}
