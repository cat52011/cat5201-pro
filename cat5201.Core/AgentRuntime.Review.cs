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
        /// 個人化開了「審查後自動修正」時，接著修正一輪（見 ReviewAutoFix、AgentRuntime.AutoFix）；原本的報告仍然不改。
        /// </summary>
        private async Task<AiFallbackExecutionResult> TryReviewDeliverablesAsync(
            INodeContext node, AgentWorkspace workspace, AiFallbackExecutionResult execution, CancellationToken ct)
        {
            var files = workspace.GetByType("generated_file")
                .Select(x => (Item: x, File: x.Payload as GeneratedFilePayload))
                .Where(x => x.File is { Success: true } && System.IO.File.Exists(x.File.FilePath))
                .ToList();

            var report = files.LastOrDefault(x => HasExtension(x.File!, ".docx"));
            if (report.File == null)
                report = files.LastOrDefault(x => HasExtension(x.File!, ".pptx"));
            if (report.File == null)
                return execution;

            var tableFiles = files.Where(x => HasExtension(x.File!, ".xlsx")).Select(x => x.File!).ToList();
            // 這一趟只審一份報告或簡報：其他報告、簡報列在清單的「檢查範圍」，使用者才不會以為整批都審過。
            var unreviewed = files
                .Where(x => (HasExtension(x.File!, ".docx") || HasExtension(x.File!, ".pptx")) &&
                            !string.Equals(x.File!.FilePath, report.File.FilePath, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.File!.FileName).Distinct().ToList();
            ReviewOutcome first;
            try
            {
                first = await ReviewOnceAsync(
                    node, workspace, report.File, report.Item.ModelId, tableFiles,
                    files.Where(x => ReportReview.IsCodeFile(x.File!.FilePath)).Select(x => x.File!).ToList(),
                    saveChecklist: true, ct, unreviewedFiles: unreviewed);
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

            string fixText = "";
            bool applied = false;
            if (DeliveryOptions.AutoFixAfterReview)
            {
                try
                {
                    (fixText, applied) = await TryAutoFixAsync(node, workspace, report.File, tableFiles, first, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warn("AutoFix", "審查後自動修正失敗（原本的檔案不受影響）", ex);
                    fixText = ReviewAutoFix.FailedLine(ex.Message);
                }
                finally
                {
                    node.SetLoadingHint(null);
                }
            }

            // 修正成功時，外面的報告已經換成修正後的版本，第一行不能再說「報告本身沒有被修改」。
            string text = (execution.Text ?? "").TrimEnd() + "\n\n" + ReportReview.Summary(first.Result, reportUnchanged: !applied);
            if (!string.IsNullOrWhiteSpace(fixText))
                text += "\n\n" + fixText;

            return ReplaceExecutionText(execution, text);
        }

        private static bool HasExtension(GeneratedFilePayload f, string ext) =>
            string.Equals(System.IO.Path.GetExtension(f.FilePath), ext, StringComparison.OrdinalIgnoreCase);

        /// <summary>一次審查的結果：審查內容、清單的 Markdown，以及存好的清單檔（沒存時為 null）。</summary>
        private sealed record ReviewOutcome(
            ReportReview.Result Result, string Markdown, GeneratedFilePayload? Checklist,
            string ReportName = "", IReadOnlyList<string>? ComparedNames = null);

        /// <summary>
        /// 審查一份報告：程式檢查＋審查模型。saveChecklist 時清單另存成「（報告名）（審查清單）」檔並放進 workspace。
        /// tableFiles、codeFiles 是要一起對照的結果表與程式檔。
        /// 第一次審查和自動修正後的再審查都用這裡（再審查不另存，併進合併的審查清單）。
        /// </summary>
        private async Task<ReviewOutcome> ReviewOnceAsync(
            INodeContext node, AgentWorkspace workspace, GeneratedFilePayload report, string? writerModelId,
            IReadOnlyList<GeneratedFilePayload> tableFiles, IReadOnlyList<GeneratedFilePayload> codeFiles,
            bool saveChecklist, CancellationToken ct,
            IReadOnlyList<ReportReview.Issue>? previousIssues = null, IReadOnlyList<string>? unreviewedFiles = null,
            string? writerNote = null)
        {
            node.SetLoadingHint("獨立審查：對照報告的數字與文字…");

            string reportName = report.FileName;
            string markdown = HasExtension(report, ".pptx")
                ? ArtifactTextExtractor.ExtractPptx(report.FilePath)
                : ArtifactTextExtractor.ExtractDocx(report.FilePath);

            var tables = new List<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>();
            // 每張表的實際列數（讀檔只讀前 200 列）與沒讀到的工作表：送審說明和「檢查範圍」要用真的數字。
            // 2026-11-04 第 4 次實測：只讀到 199 列卻告訴審查「共 199 列」（實際 581 列），兩輪都報了不存在的問題。
            var totalRows = new Dictionary<string, int>(StringComparer.Ordinal);
            var skippedSheets = new List<string>();
            var comparedNames = new List<string>();
            foreach (var t in tableFiles)
            {
                comparedNames.Add(t.FileName);
                var book = ArtifactTextExtractor.ExtractXlsxSheetsWithCounts(t.FilePath);
                foreach (var (sheet, rows) in book.Sheets)
                {
                    string key = $"{t.FileName}／{sheet}";
                    tables.Add((key, rows));
                    if (book.TotalRows.TryGetValue(sheet, out int n)) totalRows[key] = n;
                }
                skippedSheets.AddRange(book.SkippedSheets.Select(s => $"{t.FileName}／{s}"));
            }

            // 程式碼只給審查模型核對做法描述，不做程式檢查（nan 等字樣在程式裡是正常的）。
            var code = new List<(string Name, string Code)>();
            foreach (var c in codeFiles)
            {
                string text;
                try { text = ReportReview.ReadCode(c.FilePath); }
                catch (Exception ex)
                {
                    AppLog.Warn("Review", $"讀不到程式檔，審查不含這個檔：{c.FileName}", ex);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(text)) continue;
                code.Add((c.FileName, text));
                comparedNames.Add(c.FileName);
            }

            // 程式檢查（不經 AI）：報告與結果表的無效值、沒填完的模板字，加上程式裡新版套件已移除的寫法
            var programIssues = ReportReview.ProgramChecks(markdown, tables)
                .Concat(ReportReview.CodeChecks(code))
                .ToList();

            var (reviewer, note) = ReportReview.PickReviewer(_scope, writerModelId, ReportReview.HasKey);

            IReadOnlyList<ReportReview.Issue> modelIssues = Array.Empty<ReportReview.Issue>();
            IReadOnlyList<ReportReview.PreviousStatus>? previous = null;
            string checkedText = "", raw = "";
            // 只有審查模型回了可解析的 JSON 才是 Completed；其他情況照實標，摘要不會說「沒有找到問題」。
            var status = ReportReview.ReviewStatus.NotRun;
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
                        node, ReportReview.BuildPrompt(reportName, markdown, tables, programIssues, code, previousIssues,
                            writerNote, totalRows),
                        decision, null, false, ct);
                    raw = r.IsSuccess ? (r.Text ?? "") : "";
                    var parsed = ReportReview.ParseReply(raw);
                    if (!r.IsSuccess || string.IsNullOrWhiteSpace(raw))
                    {
                        status = ReportReview.ReviewStatus.Failed;
                        AppLog.Warn("Review", $"審查模型呼叫失敗：{r.ErrorMessage}");
                        note += string.IsNullOrWhiteSpace(r.ErrorMessage) ? "（回覆是空的）" : $"（{r.ErrorMessage}）";
                    }
                    else if (parsed == null)
                    {
                        status = ReportReview.ReviewStatus.Unparseable;
                        AppLog.Warn("Review", "審查回覆不是含 issues 陣列的 JSON");
                        note += "（回覆不是規定的 JSON 格式）";
                    }
                    else
                    {
                        // issues 裡有讀不出內容的項目：照列讀得出的，但不算「完成」（摘要不會說零問題）
                        status = parsed.Value.Unparsed > 0
                            ? ReportReview.ReviewStatus.PartiallyParsed
                            : ReportReview.ReviewStatus.Completed;
                        if (parsed.Value.Unparsed > 0)
                            AppLog.Warn("Review", $"審查回覆有 {parsed.Value.Unparsed} 項格式無法解析，已標成部分無法解析");
                        modelIssues = parsed.Value.Issues;
                        checkedText = parsed.Value.Checked;
                        if (previousIssues is { Count: > 0 })
                            previous = ReportReview.ParsePrevious(raw, previousIssues);
                    }
                }
            }

            var coverage = ReportReview.Coverage(markdown, tables, code, unreviewedFiles, totalRows, skippedSheets);
            var result = new ReportReview.Result(modelIssues, programIssues, reviewer ?? "", note, checkedText, raw,
                status, coverage, previous);
            string markdown2 = ReportReview.ToMarkdown(reportName, comparedNames, result);
            GeneratedFilePayload? list = null;
            if (saveChecklist)
            {
                string title = System.IO.Path.GetFileNameWithoutExtension(reportName);
                list = GeneratedFileWriter.WriteMarkdown(
                    _main.GetGeneratedFilesDir(), title + "（審查清單）", markdown2, $"獨立審查 / {reviewer ?? "程式檢查"}");
                if (list.Success)
                    workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                        workspace, node, "review-agent", "generated_file", list, modelId: reviewer ?? ""));
                else
                    list = null;
            }

            AppLog.Info("Review",
                $"獨立審查（{reportName}）：審查模型 {modelIssues.Count} 項、程式檢查 {programIssues.Count} 項（{reviewer ?? "未呼叫模型"}）");
            return new ReviewOutcome(result, markdown2, list, reportName, comparedNames);
        }
    }
}
