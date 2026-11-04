using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    // AgentRuntime 的一部分：審查後自動修正一輪（個人化，預設關；規則見 ReviewAutoFix）。
    public sealed partial class AgentRuntime
    {
        /// <summary>
        /// 審查找到「高」「中」的問題時，把上一趟的程式與問題清單交回沙箱修正、重跑一次。
        /// 成功：修正後的檔案用原本的檔名放在原位，修正前的檔案移進程式資料夾裡的「修正前」，
        /// 修正版再審查一次，兩次審查與修正說明合併成一份審查清單。
        /// 回傳接在答案最後的說明，以及外面的檔案是否已換成修正版（Applied）。沒完成時原本的檔案一律不動。
        /// </summary>
        private async Task<(string Text, bool Applied)> TryAutoFixAsync(
            INodeContext node, AgentWorkspace workspace, GeneratedFilePayload report,
            IReadOnlyList<GeneratedFilePayload> tableFiles, ReviewOutcome first, CancellationToken ct)
        {
            var issues = ReviewAutoFix.FixableIssues(first.Result);
            if (issues.Count == 0)
                return (first.Result.EffectiveStatus == ReportReview.ReviewStatus.Completed
                    ? ReviewAutoFix.NothingToFixLine
                    : ReviewAutoFix.ReviewIncompleteLine, false);

            var ctx = workspace.GetByType(ReviewAutoFix.ContextItemType)
                .Select(x => x.Payload).OfType<ReviewAutoFix.SandboxContext>().LastOrDefault();
            if (ctx == null)
                return (ReviewAutoFix.NotSupportedLine, false);

            if (string.IsNullOrWhiteSpace(ctx.CodeFilePath) || !System.IO.File.Exists(ctx.CodeFilePath))
                return (ReviewAutoFix.FailedLine("上一趟沒有交出程式檔，無法重跑"), false);

            var service = new ClaudeDocumentSkillsService(ctx.Model);
            if (!service.IsConfigured)
                return (ReviewAutoFix.FailedLine("未設定 ANTHROPIC_API_KEY"), false);

            using var reservation = SpendReservations.TryReserveWithinBudget("審查後自動修正", ReviewAutoFix.BudgetUsd);
            if (reservation == null)
                return (ReviewAutoFix.FailedLine($"會超過今日預算上限（{SpendLedger.CommittedDisplay()}）"), false);

            using var usagePurpose = UsageMeter.Purpose("審查後自動修正");

            // 1) 上一趟的程式放進沙箱；資料檔、範本、圖片沿用上一趟已上傳的檔案（file_id 仍有效）。
            node.SetLoadingHint("審查後自動修正：把程式放進沙箱…");
            string originalCodeName = System.IO.Path.GetFileName(ctx.CodeFilePath);
            byte[] codeBytes = await System.IO.File.ReadAllBytesAsync(ctx.CodeFilePath, ct);
            string codeSandboxName = ClaudeFilesApi.SafeUploadName(originalCodeName, 90);
            string? codeId = await ClaudeFilesApi.UploadAsync(codeBytes, codeSandboxName, "text/plain", ct);
            if (string.IsNullOrWhiteSpace(codeId))
                return (ReviewAutoFix.FailedLine("程式檔上傳失敗"), false);

            var uploads = ctx.Uploads.ToList();
            uploads.Add(new ClaudeDocumentSkillsService.ContainerFile(codeId!, codeSandboxName));

            // 交付檔名和上一趟相同：修正版要取代原本的檔案，報告裡也不會出現「修正版」。
            var job = ctx.Job;
            string prompt = ReviewAutoFix.BuildFixPrompt(job, codeSandboxName, originalCodeName, issues);

            // 2) 重跑一次。只跑一趟：沒交出檔案就算這次修正沒完成，原本的檔案照樣在。
            var started = DateTime.UtcNow;
            int steps = 0;
            void ShowProgress() => node.SetLoadingHint(
                $"審查後自動修正：在沙箱修改程式並重跑（{(DateTime.UtcNow - started).TotalMinutes:0.0} / {ReviewAutoFix.TimeBudget.TotalMinutes:0} 分鐘" +
                (steps > 0 ? $"，已完成 {steps} 個步驟" : "") + "）");
            ShowProgress();

            ClaudeDocumentSkillsService.Result result;
            try
            {
                result = await service.GenerateAsync(
                    DataAnalysisJob.SystemPrompt,
                    prompt,
                    Array.Empty<string>(),
                    DataAnalysisJob.WantedExtensions(job.WantsReport, job.WantsTable),
                    (round, s) => { steps = s; ShowProgress(); },
                    ct,
                    ReviewAutoFix.TimeBudget,
                    uploads,
                    ReviewAutoFix.BudgetUsd);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("AutoFix", "沙箱修正拋出例外", ex);
                return (ReviewAutoFix.FailedLine(ex.Message), false);
            }
            finally
            {
                AppLog.Info("AutoFix", $"沙箱修正結束：{(DateTime.UtcNow - started).TotalMinutes:0.0} 分鐘");
            }

            if (!result.Success)
                return (ReviewAutoFix.FailedLine(result.TimedOut
                    ? $"超過 {ReviewAutoFix.TimeBudget.TotalMinutes:0} 分鐘還沒完成"
                    : result.ErrorMessage), false);

            // 報告和程式都要有才換：只有報告沒有程式，報告就對不上可以重現它的程式。
            bool HasExt(string ext) => result.Files.Any(f => string.Equals(f.Extension, ext, StringComparison.OrdinalIgnoreCase));
            if (!HasExt(".docx") || !HasExt(".py"))
                return (ReviewAutoFix.FailedLine("沙箱沒有交回修正後的報告與程式"), false);
            bool newTable = HasExt(".xlsx");

            // 3) 修正前的檔案移進「修正前」（沒交回新結果表時，原本的結果表留在外面），workspace 也不再列它們。
            string genDir = _main.GetGeneratedFilesDir();
            string archiveDir = ReviewAutoFix.ArchiveDirFor(ctx.CodeFilePath);
            var toArchive = new List<string> { report.FilePath, ctx.CodeFilePath };
            if (newTable) toArchive.AddRange(tableFiles.Select(t => t.FilePath));
            if (first.Checklist != null) toArchive.Add(first.Checklist.FilePath);

            IReadOnlyList<(string From, string To)> moved;
            try
            {
                moved = ReviewAutoFix.MoveToArchive(toArchive, archiveDir);
            }
            catch (Exception ex)
            {
                AppLog.Warn("AutoFix", "修正前的檔案移不進子資料夾，保留原本的檔案", ex);
                return (ReviewAutoFix.FailedLine("修正前的檔案無法移進子資料夾"), false);
            }

            var movedFrom = new HashSet<string>(moved.Select(m => m.From), StringComparer.OrdinalIgnoreCase);
            var removedItems = workspace.RemoveWhere(x => x.Payload is GeneratedFilePayload g && movedFrom.Contains(g.FilePath));

            // 4) 修正版用原本的標題與程式路徑寫回原位。
            var collected = CollectSandboxFiles(node, workspace, result, job, genDir, ctx.Title,
                DataAnalysisJob.TableTitle(ctx.Title, job.WantsTable), ctx.SourceSummary + " / 審查後自動修正", ctx.Model,
                codeTargetPath: ctx.CodeFilePath);

            if (collected.Report == null || collected.CodeFiles.Count == 0)
            {
                // 寫到一半失敗：拿掉寫進去的修正版項目，原本的檔案搬回原位、項目放回 workspace。
                var written = new HashSet<string>(
                    (collected.Report == null ? Array.Empty<GeneratedFilePayload>() : new[] { collected.Report })
                        .Concat(collected.Tables).Concat(collected.CodeFiles).Select(f => f.FilePath),
                    StringComparer.OrdinalIgnoreCase);
                workspace.RemoveWhere(x => x.Payload is GeneratedFilePayload g && written.Contains(g.FilePath));
                ReviewAutoFix.Restore(moved);
                foreach (var item in removedItems) workspace.Add(item);
                return (ReviewAutoFix.FailedLine("修正後的檔案寫不進產出資料夾"), false);
            }

            // 5) 再審查修正版一次（不另存清單）；原問題沿用同樣的編號逐項回報處理狀態。這一次不再自動修正。
            ReviewOutcome? recheck = null;
            try
            {
                var outsideTables = newTable
                    ? collected.Tables
                    : tableFiles.Where(t => System.IO.File.Exists(t.FilePath)).ToList();
                recheck = await ReviewOnceAsync(node, workspace, collected.Report, ctx.Model,
                    outsideTables, collected.CodeFiles, saveChecklist: false, ct, previousIssues: issues,
                    writerNote: result.Summary);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("AutoFix", "修正版再審查失敗（修正版仍然保留）", ex);
            }

            // 新發現的問題：比對修正前後的原文，標出它引用的內容是修正前就有、還是修正後才有（只說文字新舊，不推論問題何時形成）。
            if (recheck != null && recheck.Result.ModelIssues.Count > 0)
            {
                try
                {
                    string before = ArchivedText(moved);
                    string after = ArtifactTextExtractor.ExtractDocx(collected.Report.FilePath) + "\n" +
                                   string.Join("\n", collected.CodeFiles.Select(c => ReportReview.ReadCode(c.FilePath)));
                    var classified = recheck.Result with
                    {
                        Origins = ReviewAutoFix.ClassifyOrigins(recheck.Result.ModelIssues, before, after),
                    };
                    recheck = recheck with
                    {
                        Result = classified,
                        Markdown = ReportReview.ToMarkdown(recheck.ReportName, recheck.ComparedNames ?? Array.Empty<string>(), classified),
                    };
                }
                catch (Exception ex)
                {
                    AppLog.Warn("AutoFix", "比對新問題的來源失敗（清單照樣列出，只是沒有來源）", ex);
                }
            }

            // 6) 合併的審查清單：同一個檔名，外面的檔案數和沒修正時一樣。
            string archiveDisplay = ReviewAutoFix.DisplayPath(archiveDir, genDir);
            string reportTitle = System.IO.Path.GetFileNameWithoutExtension(collected.Report.FileName);
            var combined = GeneratedFileWriter.WriteMarkdown(
                genDir, reportTitle + "（審查清單）",
                ReviewAutoFix.CombinedRecord(collected.Report.FileName, archiveDisplay, first.Markdown, result.Summary, recheck?.Markdown),
                "獨立審查＋審查後自動修正");
            if (combined.Success)
                workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                    workspace, node, "review-agent", "generated_file", combined, modelId: recheck?.Result.ReviewerModel ?? ""));

            AppLog.Info("AutoFix",
                $"修正完成：{string.Join("、", collected.Produced)}；修正前移到 {archiveDisplay}；" +
                $"再審查 {(recheck == null ? "未完成" : recheck.Result.Status.ToString())}");
            return (ReviewAutoFix.SuccessText(issues.Count, archiveDisplay, result.UsdCost, result.Summary, recheck?.Result), true);
        }

        /// <summary>移進「修正前」的報告與程式的文字（比對新問題的來源用）。結果表與審查清單不算。</summary>
        private static string ArchivedText(IReadOnlyList<(string From, string To)> moved)
        {
            var parts = new List<string>();
            foreach (var (_, to) in moved)
            {
                if (!System.IO.File.Exists(to)) continue;
                if (to.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                    parts.Add(ArtifactTextExtractor.ExtractDocx(to));
                else if (ReportReview.IsCodeFile(to))
                    parts.Add(ReportReview.ReadCode(to));
            }
            return string.Join("\n", parts);
        }
    }
}
