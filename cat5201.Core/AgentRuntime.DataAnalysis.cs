using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    // AgentRuntime 的一部分：資料分析：附資料檔要交報告時，在 Claude 沙箱實際執行程式。
    // （2026-11-03 依職責拆檔：方法原樣搬出，沒有改任何邏輯；主流程在 AgentRuntime.cs。）
    public sealed partial class AgentRuntime
    {
        private sealed record DataAnalysisOutcome(
            AiFallbackExecutionResult Execution, bool ReportDone, bool TableDone, IReadOnlyList<GeneratedFilePayload> CodeFiles,
            bool Stopped = false);

        /// <summary>
        /// 附了資料檔又要交報告／表格：在 Claude 的沙箱裡實際執行分析，一趟交出報告與程式（見 DataAnalysisJob）。
        /// 沒有資料檔、不用 Claude、沒金鑰、超出預算或沒做出來時回 null，照原本的流程走。
        /// </summary>
        private async Task<DataAnalysisOutcome?> TryRunDataAnalysisAsync(
            INodeContext node,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            OrchestrationStateMachine orchestration,
            OutputIntent intent,
            string preferenceBlock,
            CancellationToken ct)
        {
            var attachments = _main.GetEffectiveAttachmentsForNode(node) ?? Array.Empty<AttachmentInfo>();
            if (!DataAnalysisJob.Applies(attachments.Select(a => a.FileName), intent))
                return null;

            // 手動模式選了 Claude 以外的撰寫者時，不能為了執行程式改用 Claude（跟最佳品質文件同一條規則）。
            string? model = _scope.SkillsModel(DocumentEngineHelper.ResolveSkillsModel(AiAutoCostPolicy.BlockOpus));
            if (model == null)
            {
                AppLog.Info("DataAnalysis", "這次的撰寫者不是 Claude，不在沙箱執行，照原本流程產出");
                return null;
            }

            var service = new ClaudeDocumentSkillsService(model);
            if (!service.IsConfigured)
            {
                AppLog.Info("DataAnalysis", "未設定 ANTHROPIC_API_KEY，不在沙箱執行");
                return null;
            }

            using var reservation = SpendReservations.TryReserveWithinBudget("資料分析（沙箱執行）", DataAnalysisJob.JobBudgetUsd);
            if (reservation == null)
            {
                AppLog.Info("Spend", $"資料分析未啟動：會超過今日預算上限（{SpendLedger.CommittedDisplay()}）");
                return null;
            }

            using var usagePurpose = UsageMeter.Purpose("資料分析（沙箱）");
            string root = _main.GetAttachmentsRootDir();
            string FullPath(AttachmentInfo a) => System.IO.Path.Combine(root, a.RelativePath ?? "");

            // 1) 資料檔整份放進沙箱；說明圖片附在訊息裡讓 Claude 看得到；Word 範本也放進沙箱（照格式填寫用）。
            var uploads = new List<ClaudeDocumentSkillsService.ContainerFile>();
            var dataNames = new List<string>();
            var imageNames = new List<string>();
            var documents = new List<(string FileName, string Text)>();
            var templateNames = new List<string>();
            var originalNames = new Dictionary<string, string>();
            int uploadIndex = 0;

            node.SetLoadingHint("把資料檔放進沙箱…");
            foreach (var a in attachments)
            {
                ct.ThrowIfCancellationRequested();
                string path = FullPath(a);
                if (!System.IO.File.Exists(path)) continue;

                bool isData = DataAnalysisJob.IsDataFile(a.FileName);
                bool isImage = DataAnalysisJob.IsImage(a.FileName);
                bool isDoc = DataAnalysisJob.IsDocument(a.FileName);

                if (isDoc)
                {
                    string? docText = AttachmentTextCache.TryGetText(path, a.FileName, a.MimeType);
                    if (!string.IsNullOrWhiteSpace(docText)) documents.Add((a.FileName, docText!));
                }

                bool upload = isData || isImage || a.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
                if (!upload) continue;

                try
                {
                    if (new System.IO.FileInfo(path).Length > DataAnalysisJob.MaxUploadBytes)
                    {
                        AppLog.Warn("DataAnalysis", $"附件太大，沒有放進沙箱：{a.FileName}");
                        continue;
                    }

                    byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
                    // 中文檔名上傳會被拒，改用英數檔名；原檔名寫進提示，.py 才會用使用者電腦上的名稱讀檔。
                    string sandboxName = ClaudeFilesApi.SafeUploadName(a.FileName, ++uploadIndex);
                    string? id = await ClaudeFilesApi.UploadAsync(bytes, sandboxName, DataAnalysisJob.MimeFor(a.FileName), ct);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        // 資料檔上傳失敗就沒得分析；圖片與範本失敗只是少一份參考（範本文字已經在提示裡）。
                        if (isData)
                        {
                            AppLog.Warn("DataAnalysis", $"資料檔上傳失敗，改走原本流程：{a.FileName}");
                            return null;
                        }
                        continue;
                    }

                    uploads.Add(new ClaudeDocumentSkillsService.ContainerFile(id!, sandboxName, AsImage: isImage));
                    if (sandboxName != a.FileName) originalNames[sandboxName] = a.FileName;
                    if (isData) dataNames.Add(sandboxName);
                    else if (isImage) imageNames.Add(sandboxName);
                    else templateNames.Add(sandboxName);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warn("DataAnalysis", $"附件上傳失敗：{a.FileName}", ex);
                    if (isData) return null;
                }
            }

            if (dataNames.Count == 0)
                return null;

            // 交付檔名在建提示之前就定下來，告訴沙箱照用：.py 產生的檔名、報告裡引用的檔名才會和交付的一樣。
            // 2026-11-04：原本沙箱跑完才決定 title、再把 .docx／.xlsx 改名，.py 和報告裡寫的檔名都對不上。
            string genDir = _main.GetGeneratedFilesDir();
            string title = GeneratedFileWriter.UnusedTitle(genDir, DeriveCleanTitle(userInput, "分析報告"), t => new[]
            {
                GeneratedFileWriter.PlannedFileName(t, ".docx"),
                GeneratedFileWriter.PlannedFileName(DataAnalysisJob.TableTitle(t, intent.WantsTable), ".xlsx"),
            });
            string tableTitle = DataAnalysisJob.TableTitle(title, intent.WantsTable);

            var job = new DataAnalysisJob.Request
            {
                UserInput = userInput,
                PreferenceBlock = preferenceBlock ?? "",
                DataFiles = dataNames,
                Images = imageNames,
                Documents = documents,
                Templates = templateNames,
                OriginalNames = originalNames,
                WantsReport = intent.WantsReport,
                WantsTable = intent.WantsTable,
                Language = intent.RequestedLanguage ?? "",
                ReportFileName = intent.WantsReport ? GeneratedFileWriter.PlannedFileName(title, ".docx") : "",
                TableFileName = GeneratedFileWriter.PlannedFileName(tableTitle, ".xlsx"),
            };

            if (job.WantsReport) orchestration.MarkRunning("generate_file");

            // 2) 執行。跟最佳品質文件一樣：一輪可能好幾分鐘，自己每 10 秒報一次進度；沒交出檔案就帶著原因重試一次。
            var started = DateTime.UtcNow;
            var budget = DataAnalysisJob.TimeBudget;
            int steps = 0;
            void ShowProgress() => node.SetLoadingHint(
                $"在沙箱執行分析中（{(DateTime.UtcNow - started).TotalMinutes:0.0} / {budget.TotalMinutes:0} 分鐘" +
                (steps > 0 ? $"，已完成 {steps} 個步驟" : "") + "）");

            using var ticker = new System.Threading.PeriodicTimer(TimeSpan.FromSeconds(10));
            using var tickerStop = new CancellationTokenSource();
            async Task TickAsync()
            {
                try { while (await ticker.WaitForNextTickAsync(tickerStop.Token)) ShowProgress(); }
                catch (OperationCanceledException) { }
            }

            ShowProgress();
            var ticking = TickAsync();

            var result = new ClaudeDocumentSkillsService.Result { Success = false, ErrorMessage = "尚未開始" };
            double spent = 0;
            try
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    var remaining = started + budget - DateTime.UtcNow;
                    double remainingUsd = DataAnalysisJob.JobBudgetUsd - spent;
                    if (remaining < TimeSpan.FromSeconds(45) || remainingUsd <= 0.25)
                        break;

                    string retryNote = attempt == 1 ? "" :
                        "\n\n【重要】上一次沒有把最終檔案輸出。這一次請務必把報告與程式存成檔案，最後複製到 \"$OUTPUT_DIR\" 並在同一個指令裡 ls 確認。";

                    try
                    {
                        result = await service.GenerateAsync(
                            DataAnalysisJob.SystemPrompt,
                            DataAnalysisJob.BuildUserPrompt(job),
                            Array.Empty<string>(),          // 不掛技能：python-docx 等都已預裝，Word 技能光注入就上百萬 token
                            DataAnalysisJob.WantedExtensions(job.WantsReport, job.WantsTable),
                            (round, s) => { steps = s; ShowProgress(); },
                            ct,
                            remaining,
                            uploads,
                            remainingUsd,
                            retryNote);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppLog.Warn("DataAnalysis", $"沙箱執行第 {attempt} 次拋出例外", ex);
                        result = new ClaudeDocumentSkillsService.Result { Success = false, ErrorMessage = ex.Message };
                    }

                    spent += result.UsdCost;
                    // 已經在沙箱裡做過事（有步驟）就不整趟重來：沒交到檔案的情況，服務裡已經沿用同一個沙箱補交過一次。
                    if (result.Success || result.TimedOut || result.ToolSteps > 0 ||
                        !ClaudeDocumentSkillsService.IsRetryable(result))
                        break;

                    AppLog.Warn("DataAnalysis", $"第 {attempt} 次沒有交出檔案（{result.ToolSteps} 步）：{result.ErrorMessage}　→ 重試");
                    steps = 0;
                }
            }
            finally
            {
                tickerStop.Cancel();
                await ticking;
                node.SetLoadingHint(null);
                AppLog.Info("DataAnalysis",
                    $"沙箱分析結束：{(DateTime.UtcNow - started).TotalMinutes:0.0} 分鐘，US${spent:0.000}，{(result.Success ? "成功" : "未完成")}");
            }

            if (!result.Success && (result.TimedOut || result.ToolSteps > 0))
            {
                // 沙箱已經實際做過事（逾時，或做完卻沒交到檔案）：不再退回原本流程。
                // 2026-11-03 兩次實測都是這樣——退回原本流程又花了 8～12 分鐘、US$1～1.9，
                // 交出的還是沒有真實數字的報告，等於多花一倍時間換一份使用者原本就不要的東西。
                AppLog.Warn("DataAnalysis", $"沙箱分析沒有完成，停止，不退回原本流程：{result.ErrorMessage}");
                orchestration.MarkFailed("generate_file", result.TimedOut ? "沙箱執行逾時" : "沙箱沒有交出檔案");
                return new DataAnalysisOutcome(
                    new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = result.TimedOut
                            ? DataAnalysisJob.TimedOutMessage(DataAnalysisJob.TimeBudget, spent)
                            : DataAnalysisJob.NoFilesMessage(spent),
                        ActualModelId = model,
                        Summary = "沙箱執行逾時",
                        Attempts = new List<AiFallbackAttempt> { new() { AttemptIndex = 1, ModelId = model, Success = false } },
                    },
                    ReportDone: false, TableDone: false, CodeFiles: Array.Empty<GeneratedFilePayload>(), Stopped: true);
            }

            if (!result.Success)
            {
                AppLog.Warn("DataAnalysis", $"沙箱分析沒有完成，改走原本流程：{result.ErrorMessage}");
                return null;
            }

            // 3) 收檔：報告、表格照一般產出寫檔；程式放進程式碼資料夾（分類成「程式碼」、可預覽）。
            // title、genDir 在建提示之前就定好了（見上面），這裡照用，交付檔名才會和沙箱裡寫的一樣。
            string sourceSummary = $"{orchestrationPlan.PipelineId} / 沙箱執行 {model}";
            var collected = CollectSandboxFiles(node, workspace, result, job, genDir, title, tableTitle, sourceSummary, model);
            var produced = collected.Produced;
            var codeFiles = collected.CodeFiles;
            bool reportDone = collected.Report != null, tableDone = collected.TableDone;

            if (!reportDone && !tableDone)
            {
                AppLog.Warn("DataAnalysis", "沙箱沒有交出報告或表格，改走原本流程");
                return null;
            }

            if (reportDone || tableDone)
                orchestration.MarkSuccess("generate_file", "沙箱執行 / " + string.Join(" / ", produced));

            // 審查後自動修正（個人化）要重跑同一個沙箱任務：把模型、已上傳的檔案、任務與程式路徑記在 workspace。
            // 不給使用者看；修正那一步（AgentRuntime.AutoFix）才會讀。
            if (reportDone)
                workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                    workspace, node, "data-analysis", ReviewAutoFix.ContextItemType,
                    new ReviewAutoFix.SandboxContext(model, uploads, job, title,
                        codeFiles.FirstOrDefault()?.FilePath ?? "", sourceSummary),
                    isUserVisibleOverride: false, modelId: model));

            string summary = string.IsNullOrWhiteSpace(result.Summary)
                ? $"已在沙箱實際執行分析，產出：{string.Join("、", produced)}。"
                : result.Summary.Trim();
            string text = summary + "\n\n" + DataAnalysisJob.ChartLanguageNote;

            AppLog.Info("DataAnalysis", $"產出 {produced.Count} 個檔案：{string.Join("、", produced)}");
            return new DataAnalysisOutcome(
                new AiFallbackExecutionResult
                {
                    IsSuccess = true,
                    Text = text,
                    ActualModelId = model,
                    Summary = "沙箱執行",
                    Attempts = new List<AiFallbackAttempt> { new() { AttemptIndex = 1, ModelId = model, Success = true } },
                },
                reportDone, tableDone, codeFiles);
        }

        private sealed record CollectedSandboxFiles(
            List<string> Produced, GeneratedFilePayload? Report, bool TableDone, List<GeneratedFilePayload> Tables,
            List<GeneratedFilePayload> CodeFiles);

        /// <summary>
        /// 把沙箱交回的檔案寫進產出資料夾並放進 workspace：報告、表格照一般產出寫檔，程式放進程式碼資料夾。
        /// 第一趟分析和審查後的自動修正都用這裡，兩邊的檔名規則才會一樣。
        /// codeTargetPath：自動修正時，程式寫回原本的路徑（原本那一份已移進「修正前」），不另開新的程式資料夾。
        /// </summary>
        private static CollectedSandboxFiles CollectSandboxFiles(
            INodeContext node, AgentWorkspace workspace, ClaudeDocumentSkillsService.Result result,
            DataAnalysisJob.Request job, string genDir, string title, string tableTitle, string sourceSummary, string model,
            string? codeTargetPath = null)
        {
            var produced = new List<string>();
            GeneratedFilePayload? report = null;
            bool tableDone = false, resultTableDone = false;
            var tables = new List<GeneratedFilePayload>();
            var codeFiles = new List<GeneratedFilePayload>();

            void Emit(GeneratedFilePayload payload, string agentId)
            {
                if (payload == null || !payload.Success) return;
                workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(workspace, node, agentId, "generated_file", payload, modelId: model));
                produced.Add(payload.FileName);
            }

            foreach (var f in result.Files)
            {
                switch (f.Extension.ToLowerInvariant())
                {
                    case ".docx" when job.WantsReport && report == null:
                        var docx = GeneratedFileWriter.WriteDocx(genDir, title, f.Bytes, sourceSummary);
                        Emit(docx, "report-agent");
                        if (docx.Success) report = docx;
                        break;
                    case ".xlsx" when !tableDone && !resultTableDone:
                        // 沒被要求表格時也收：報告常引用「完整結果見 xxx.xlsx」，丟掉會讓報告指向不存在的檔案。
                        var xlsx = GeneratedFileWriter.WriteXlsx(genDir, tableTitle, f.Bytes, sourceSummary);
                        Emit(xlsx, "table-agent");
                        if (xlsx.Success) tables.Add(xlsx);
                        if (job.WantsTable) tableDone = xlsx.Success;
                        else resultTableDone = xlsx.Success;
                        break;
                    case ".py" when codeFiles.Count == 0:
                        string code = System.Text.Encoding.UTF8.GetString(f.Bytes).TrimStart('﻿');
                        var written = !string.IsNullOrWhiteSpace(codeTargetPath)
                            ? new[] { GeneratedFileWriter.WriteCodeFileAt(codeTargetPath!, code, title, sourceSummary) }
                            : GeneratedFileWriter.WriteCodeFiles(genDir, title,
                                new[] { new CodeFileExtractor.CodeFile(System.IO.Path.GetFileName(f.FileName), "python", code) },
                                sourceSummary);
                        foreach (var w in written.Where(w => w.Success))
                        {
                            Emit(w, "code-agent");
                            codeFiles.Add(w);
                        }
                        break;
                }
            }

            return new CollectedSandboxFiles(produced, report, tableDone, tables, codeFiles);
        }
    }
}
