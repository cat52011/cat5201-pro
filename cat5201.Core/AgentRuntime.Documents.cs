using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    // AgentRuntime 的一部分：文件產出：報告、表格與 Claude 文件技能（最佳品質）。
    // （2026-11-03 依職責拆檔：方法原樣搬出，沒有改任何邏輯；主流程在 AgentRuntime.cs。）
    public sealed partial class AgentRuntime
    {
        /// <summary>
        /// 替「交給 Claude 排版的簡報」先生好照片並上傳。
        ///
        /// 沙箱裡沒有圖片模型——Claude 在容器裡只畫得出色塊、線條與圖表，永遠生不出照片。
        /// 使用者 2026-10-15：「這個簡報沒圖我覺得不太合適」。
        /// 所以照片由我們這邊生（跟內建版型同一套配圖判斷與藝術方向），
        /// 上傳到 Files API 之後以 container_upload 送進容器。
        /// 生不出來、上傳失敗都不影響簡報本身，只是少了照片。
        /// </summary>
        private async Task<List<ClaudeDocumentSkillsService.ContainerFile>> BuildContainerPhotosAsync(
            INodeContext node,
            AgentDefinition? runtimeAgent,
            string userInput,
            AiFallbackExecutionResult execution,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            CancellationToken ct)
        {
            var files = new List<ClaudeDocumentSkillsService.ContainerFile>();

            try
            {
                // 借用內建版型那一套：從最終答案切出大綱，再用同樣的配圖企劃生圖，
                // 這樣兩條路徑的照片風格與「哪些主題不該生圖」的判斷完全一致。
                var outline = PresentationOutlineBuilder.Build(new PresentationOutlineBuilder.Request
                {
                    UserInput = userInput,
                    FinalAnswer = execution.Text ?? "",
                    Workspace = workspace,
                    PipelineId = orchestrationPlan.PipelineId,
                    ModelId = execution.ActualModelId ?? orchestrationPlan.ModelId,
                    AgentId = runtimeAgent?.Id ?? "",
                });

                var coverTask = TryGenerateCoverImageAsync(node, runtimeAgent, outline, workspace, orchestrationPlan, ct);
                var slidesTask = GenerateSlideIllustrationsAsync(node, outline, ct);
                await Task.WhenAll(coverTask, slidesTask);

                var (cover, _) = await coverTask;
                if (cover is { Length: > 0 })
                    await AddPhotoAsync(files, cover, "cover.png", $"封面用；主題：{outline.Title}", ct);

                // 檔名不用頁碼：2026-10-29 實測，照片叫 photo-3.png，Claude 就放到它自己簡報的第 3 頁——
                // 但那是「我們從答案切出的大綱」的第 3 頁，跟 Claude 實際排的頁序不一樣，
                // 結果海鮮那頁配了牛排。改用字母編號，並把每張照片畫什麼、原本為哪一頁生成寫進提示。
                int index = 0;
                foreach (var slide in outline.Slides.Where(x => x.ImageBytes is { Length: > 0 }).Take(6))
                {
                    char letter = (char)('a' + index++);
                    await AddPhotoAsync(files, slide.ImageBytes!, $"photo-{letter}.png",
                        DescribeSlidePhoto(slide.Heading, slide.ImageSubject), ct);
                }

                if (files.Count > 0)
                    AppLog.Info("DocSkills", $"已把 {files.Count} 張照片送進沙箱供簡報使用");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("DocSkills", "替簡報準備照片失敗（簡報照常做，只是沒有照片）", ex);
            }

            return files;
        }

        private static async Task AddPhotoAsync(
            List<ClaudeDocumentSkillsService.ContainerFile> files, byte[] png, string name, string description, CancellationToken ct)
        {
            string? id = await ClaudeFilesApi.UploadAsync(png, name, "image/png", ct);
            if (!string.IsNullOrWhiteSpace(id))
                files.Add(new ClaudeDocumentSkillsService.ContainerFile(id!, name, description));
        }

        /// <summary>一張內容頁照片的說明：原本為哪一頁生成、畫面主體是什麼。</summary>
        internal static string DescribeSlidePhoto(string? heading, string? subject)
        {
            string h = (heading ?? "").Trim();
            string s = (subject ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length > 200) s = s.Substring(0, 200) + "…";
            string forPage = h.Length > 0 ? $"為講「{h}」的內容生成" : "為內容頁生成";
            return s.Length > 0 ? $"{forPage}；畫面主體：{s}" : forPage;
        }

        /// <summary>告訴 Claude 容器裡有哪些照片、各畫什麼、怎麼放。沒有照片時回空字串。</summary>
        internal static string PhotoBlock(IReadOnlyList<ClaudeDocumentSkillsService.ContainerFile> photos)
        {
            if (photos.Count == 0)
                return "";

            var sb = new System.Text.StringBuilder();
            sb.Append("\n\n【可用的照片】\n");
            sb.Append("這些檔案已經放進你的工作目錄，是依這份內容生成的照片，請實際嵌進簡報：\n");
            foreach (var f in photos)
            {
                sb.Append("- ").Append(f.FileName);
                if (!string.IsNullOrWhiteSpace(f.Description))
                    sb.Append("：").Append(f.Description!.Trim());
                else if (f.FileName == "cover.png")
                    sb.Append("（封面用）");
                sb.Append('\n');
            }
            sb.Append("擺放規則：每張照片只能放在「內容跟它的畫面主體相符」的那一頁。檔名的字母不代表頁碼，不要依檔名順序擺。")
              .Append("找不到相符的頁就不要放那張；寧可少一張，也不要圖文不符。\n");
            sb.Append("用 python-pptx 的 add_picture 放進去；滿版或大面積配置都可以，但不要把主體裁掉，也不要壓在文字上。\n");
            sb.Append("用不到的就不要硬放。除了這些照片，其餘視覺元素仍然只能用色塊、線條與圖表。");
            return sb.ToString();
        }

        private sealed record SkillsOutcome(AiFallbackExecutionResult Execution, bool Deck, bool Report, bool Table);

        /// <summary>
        /// Claude 文件技能：一次呼叫產出所有要求的文件（pptx / docx / xlsx ＋ 各自的 PDF）。
        /// 任何失敗都不拋出（取消除外）——回傳哪些類型已完成，其餘由內建流程接手。
        /// </summary>
        private async Task<SkillsOutcome> TryGenerateDocumentsWithSkillsAsync(
            INodeContext node,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            bool wantsReport,
            bool wantsTable,
            bool wantsDeck,
            string preferenceBlock,
            CancellationToken ct,
            OutputIntent? intent = null)
        {
            var none = new SkillsOutcome(execution, false, false, false);
            string? model = _scope.SkillsModel(DocumentEngineHelper.ResolveSkillsModel(AiAutoCostPolicy.BlockOpus));
            if (model == null)
                return none;
            var service = new ClaudeDocumentSkillsService(model);
            if (!service.IsConfigured)
            {
                AppLog.Info("DocSkills", "未設定 ANTHROPIC_API_KEY，文件改用內建快速版");
                return none;
            }

            using var usagePurpose = UsageMeter.Purpose("Claude 文件技能");

            var sources = workspace.GetByType("verified_facts")
                .Select(x => x.Payload).OfType<VerifiedFactPayload>()
                .SelectMany(p => p.Facts ?? Array.Empty<VerifiedFactItem>())
                .Where(f => f != null && !string.IsNullOrWhiteSpace(f.SourceUrl))
                .Select(f => (f.SourceTitle ?? "", f.SourceUrl ?? ""))
                .Concat(workspace.GetByType("search_summary")
                    .Select(x => x.Payload).OfType<SearchSummaryPayload>()
                    .Where(p => p.HasUsableResults)
                    .SelectMany(p => p.Items)
                    .Where(i => !string.IsNullOrWhiteSpace(i.Source) && i.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    .Select(i => (i.Title ?? "", i.Source ?? "")))
                .GroupBy(s => s.Item2, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var prompt = new DocumentSkillsPrompt.Request
            {
                UserInput = userInput,
                MainContent = execution.Text ?? "",
                WantsDeck = wantsDeck,
                WantsReport = wantsReport,
                WantsTable = wantsTable,
                // 張數與配圖一律以第一層語意判斷為準；那一步失敗才退回只認繁中的關鍵字。
                RequestedSlides = !wantsDeck ? 0
                    : intent?.RequestedSlides > 0 ? intent.RequestedSlides
                    : PresentationOutlineBuilder.DetectRequestedSlideCount(userInput),
                // 跟內建版型同一套判斷：使用者沒明講時，看內容需不需要圖，不是看請求那一行字。
                WantsImages = _scope.IllustrationsAllowed() && DeckIllustrationPolicy.ShouldIllustrate(
                    userInput, null, intent?.WantsIllustrations ?? false, out _, execution.Text,
                    declinedFromResolver: intent?.DeclinesIllustrations ?? false),
                Sources = sources,
                PreferenceBlock = preferenceBlock ?? "",
                Language = intent?.RequestedLanguage ?? "",
                // 個人化關掉對照 PDF 時，這條路也得照辦。
                // 以前這裡沒接上：設定關著，提示詞照樣要求「每個檔都另外轉一份 PDF」，
                // 於是沙箱多跑好幾步 LibreOffice 轉檔，而那幾步的脈絡後面每一輪都要再買一次。
                WantsPdf = DeliveryOptions.CompanionPdf
            };

            var whatParts = new List<string>();
            if (wantsDeck) whatParts.Add("簡報");
            if (wantsReport) whatParts.Add("報告");
            if (wantsTable) whatParts.Add("表格");
            string what = string.Join("、", whatParts);

            if (wantsDeck) orchestration.MarkRunning("presentation_outline");
            if (wantsReport || wantsTable) orchestration.MarkRunning("generate_file");

            ClaudeDocumentSkillsService.Result result;
            var budget = ClaudeDocumentSkillsService.DefaultBudget;
            var skillsStarted = DateTime.UtcNow;

            // 這一步可能跑好幾分鐘、花好幾美元；先把額度佔住，別讓同時啟動的其他任務一起放行。
            // 佔不到（會超過今日上限）就不要開始——跑到一半才發現超支是最糟的結果。
            using var skillsReservation = SpendReservations.TryReserveWithinBudget(
                $"Claude 文件技能（{what}）", ClaudeDocumentSkillsService.DefaultJobBudgetUsd);
            if (skillsReservation == null)
            {
                // 佔不到額度就不要開始：跑到一半才發現超支是最糟的結果。
                // 回 none 代表「這條路沒產出任何東西」，內建的快速版引擎會接手。
                node.SetLoadingHint(null);
                AppLog.Info("Spend", $"文件技能未啟動：會超過今日預算上限（{SpendLedger.CommittedDisplay()}）");
                return none;
            }
            int skillSteps = 0;

            // 技能一輪可能在伺服器端跑好幾分鐘，只在換輪時更新提示等於畫面像當掉。
            // 自己每 10 秒報一次「跑了多久 / 上限多久」，使用者才知道還要不要等。
            // 這裡在 UI 執行緒上 await，不能加 ConfigureAwait(false)。
            using var ticker = new System.Threading.PeriodicTimer(TimeSpan.FromSeconds(10));
            using var tickerStop = new CancellationTokenSource();

            // 「為什麼這麼久」要寫在等待的那句話裡。使用者看到的是一份簡報做了四分鐘，
            // 而個人化裡其實有一個「快速版」選項——不講的話他只會覺得這個軟體很慢。
            // budget 現在是「含重試的總時間」，顯示的也是總時間。
            void ShowProgress() => node.SetLoadingHint(
                $"最佳品質模式製作{what}中（{(DateTime.UtcNow - skillsStarted).TotalMinutes:0.0} / {budget.TotalMinutes:0} 分鐘" +
                (skillSteps > 0 ? $"，已完成 {skillSteps} 個步驟" : "") +
                "）・想要快一點可到 個人化 → 簡報與報告品質 改成快速版");

            async Task TickAsync()
            {
                try { while (await ticker.WaitForNextTickAsync(tickerStop.Token)) ShowProgress(); }
                catch (OperationCanceledException) { }
            }

            ShowProgress();
            var ticking = TickAsync();

            // 失敗就重來，不要一次不成就退回內建版型。
            //
            // 使用者要的是高品質簡報，不是一句「這次超時了」。實測紀錄顯示失敗有兩種樣子：
            // ① 串流開了之後完全沒動靜（1 輪 / 0 步，白等整個上限）；
            // ② 跑了十幾二十步卻沒有回傳任何檔案。
            // 兩種都是「這一次不順」而不是「這題做不出來」——同一份提示隔幾十秒再送一次就成功過。
            // 所以：整段給一個總時間預算，在預算內一直重試；只有預算用完才交給內建版型。
            try
            {
            var totalDeadline = DateTime.UtcNow + budget;
            int attempt = 0;
            result = new ClaudeDocumentSkillsService.Result { Success = false, ErrorMessage = "尚未開始" };

            // 一次只掛一個技能。
            //
            // 每個技能的說明與參考檔都由伺服器注入並算進輸入 token。三個一起掛，
            // 2026-10-15 被退回 prompt is too long: 1,634,773 tokens——我們自己的提示只有 1,419 字，
            // 所以那一百多萬幾乎全是技能內容。拆開之後注入量剩三分之一，
            // 而且簡報做不出來也不會連累報告與表格。
            var subRequests = DocumentSkillsPrompt.SplitByArtifact(prompt);

            // 照片：沙箱裡沒有圖片模型，Claude 只畫得出色塊與圖表。
            // 所以我們這邊先用 gpt-image 生好，上傳到 Files API，
            // 再以 container_upload 送進容器讓它嵌進簡報。
            var deckPhotos = prompt.WantsDeck && prompt.WantsImages
                ? await BuildContainerPhotosAsync(node, null, userInput, execution, workspace, orchestrationPlan, ct)
                : new List<ClaudeDocumentSkillsService.ContainerFile>();

            var collected = new List<ClaudeDocumentSkillsService.SkillFile>();
            var summaries = new List<string>();
            var failures = new List<string>();

            // 費用上限：預留了多少就真的只准花多少。
            //
            // 之前只有時間預算，於是「在 14 分鐘內一直重試」等於「在 14 分鐘內一直花錢」——
            // 2026-10-15 一份簡報跑掉 US$10.48，而同一天的預留額度寫的是 US$3。
            double skillsBudgetUsd = ClaudeDocumentSkillsService.DefaultJobBudgetUsd;
            double skillsSpentUsd = 0;
            bool droppedPhotosAfterTooLong = false;

            foreach (var sub in subRequests)
            {
            attempt = 0;
            result = new ClaudeDocumentSkillsService.Result { Success = false, ErrorMessage = "尚未開始" };

            while (true)
            {
                attempt++;
                var remaining = totalDeadline - DateTime.UtcNow;
                if (remaining < TimeSpan.FromSeconds(45))
                    break;

                double remainingUsd = skillsBudgetUsd - skillsSpentUsd;
                if (remainingUsd <= 0.25)
                {
                    AppLog.Warn("DocSkills",
                        $"已用 US${skillsSpentUsd:0.00}／上限 US${skillsBudgetUsd:0.00}，不再重試{what}，改用內建版型。");
                    break;
                }

                // 上一次跑了步驟卻沒交出檔案：把問題直接講給它聽，不要原封不動再送一次。
                string retryNote = attempt == 1 ? "" :
                    result.ToolSteps > 0
                        ? "\n\n【重要】上一次你執行了步驟，但沒有把最終檔案輸出成可下載的檔案。" +
                          "這一次請務必把成品存成檔案，最後複製到 \"$OUTPUT_DIR\" 並在同一個指令裡 ls 確認，不要只在訊息裡描述內容。"
                        : "\n\n【重要】上一次這個請求沒有回應。請直接開始動手做，不要先寫長篇說明。";

                try
                {
                    // 上一次是「整包太長」而這次有照片要帶 → 先把照片拿掉再試一次。
                    //
                    // 我們自己送的字只有 8,000 token，伺服器卻回 1,230,182（2026-10-27），
                    // 而這趟沒有掛任何技能——整包裡唯一的大東西就是 container_upload 的照片。
                    // 沒有照片的 Claude 版面，仍然比直接退回內建版型好。
                    bool dropPhotos = droppedPhotosAfterTooLong ||
                        (attempt > 1 && result.ErrorMessage.Contains("too long", StringComparison.OrdinalIgnoreCase));
                    if (dropPhotos && !droppedPhotosAfterTooLong)
                    {
                        droppedPhotosAfterTooLong = true;
                        AppLog.Warn("DocSkills", "整包過長，改成不帶照片重試一次（版面仍由 Claude 設計）");
                        node.SetLoadingHint($"最佳品質模式製作{what}中（改成不帶照片重試）");
                    }

                    var photos = sub.WantsDeck && !dropPhotos
                        ? deckPhotos
                        : new List<ClaudeDocumentSkillsService.ContainerFile>();

                    // retryNote 另外送：跟主提示黏成同一個 text block 的話，整塊內容會因為
                    // 這幾句話而改變，前面幾千 token 的共同前綴就永遠命中不了快取。
                    result = await service.GenerateAsync(
                        DocumentSkillsPrompt.SystemPromptFor(sub.WantsPdf),
                        DocumentSkillsPrompt.BuildUserPrompt(sub) + PhotoBlock(photos),
                        DocumentSkillsPrompt.SkillIds(sub),
                        DocumentSkillsPrompt.WantedExtensions(sub),
                        (round, steps) => { skillSteps = steps; ShowProgress(); },
                        ct,
                        remaining,
                        photos,
                        remainingUsd,
                        retryNote);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warn("DocSkills", $"文件技能第 {attempt} 次執行拋出例外", ex);
                    result = new ClaudeDocumentSkillsService.Result { Success = false, ErrorMessage = ex.Message };
                }

                skillsSpentUsd += result.UsdCost;

                if (result.Success)
                {
                    if (attempt > 1)
                        AppLog.Info("DocSkills", $"第 {attempt} 次成功產出{what}。");
                    break;
                }

                if (!ClaudeDocumentSkillsService.IsRetryable(result))
                    break;

                AppLog.Warn("DocSkills",
                    $"第 {attempt} 次沒有產出{what}（{result.Rounds} 輪 / {result.ToolSteps} 步）：{result.ErrorMessage}　→ 重試");
                skillSteps = 0;
                node.SetLoadingHint($"最佳品質模式製作{what}中（第 {attempt + 1} 次嘗試）・上一次沒有產出檔案，正在重來");
            }

            // 這一種產出的結果收起來，再做下一種。
            if (result.Success)
            {
                collected.AddRange(result.Files);
                if (!string.IsNullOrWhiteSpace(result.Summary)) summaries.Add(result.Summary.Trim());
            }
            else
            {
                failures.Add(result.ErrorMessage);
                AppLog.Warn("DocSkills",
                    $"{string.Join("/", DocumentSkillsPrompt.SkillIds(sub))} 沒有產出：{result.ErrorMessage}");
            }
            }

            // 合併：只要有任何一種做出來就算成功，其餘走內建版型補。
            result = new ClaudeDocumentSkillsService.Result
            {
                Success = collected.Count > 0,
                Files = collected,
                Summary = string.Join("\n", summaries),
                ErrorMessage = collected.Count > 0 ? "" : string.Join("；", failures.Distinct()),
                Rounds = result.Rounds,
                ToolSteps = result.ToolSteps,
            };
            }
            finally
            {
                tickerStop.Cancel();
                await ticking;
                AppLog.Info("DocSkills", $"文件技能結束，耗時 {(DateTime.UtcNow - skillsStarted).TotalMinutes:0.0} 分鐘");
                node.SetLoadingHint(null);
            }

            if (!result.Success)
            {
                AppLog.Warn("DocSkills", $"文件技能未完成（{result.Rounds} 輪 / {result.ToolSteps} 步）：{result.ErrorMessage}");
                string fallbackNote = $"\n\nℹ Claude 文件技能這次沒有完成（{result.ErrorMessage}），已改用內建快速版產出{what}。";
                return new SkillsOutcome(ReplaceExecutionText(execution, (execution.Text ?? "").TrimEnd() + fallbackNote), false, false, false);
            }

            string genDir = _main.GetGeneratedFilesDir();
            string sourceSummary = $"{orchestrationPlan.PipelineId} / Claude 文件技能 {model}";
            string deckTitle = DeriveCleanTitle(userInput, "簡報");
            string reportTitle = wantsDeck ? DeriveCleanTitle(userInput, "報告") + "（報告）" : DeriveCleanTitle(userInput, "報告");
            string tableTitle = (wantsDeck || wantsReport) ? DeriveCleanTitle(userInput, "表格") + "（表格）" : DeriveCleanTitle(userInput, "表格");

            var byExt = result.Files.GroupBy(f => f.Extension).ToDictionary(g => g.Key, g => g.ToList());
            ClaudeDocumentSkillsService.SkillFile? First(string ext) => byExt.TryGetValue(ext, out var l) ? l[0] : null;
            var pptx = First(".pptx");
            var docx = First(".docx");
            var xlsx = First(".xlsx");
            var pdfs = byExt.TryGetValue(".pdf", out var pl) ? pl : new List<ClaudeDocumentSkillsService.SkillFile>();

            ClaudeDocumentSkillsService.SkillFile? PdfFor(ClaudeDocumentSkillsService.SkillFile? primary)
            {
                if (primary == null) return null;
                string stem = System.IO.Path.GetFileNameWithoutExtension(primary.FileName);
                return pdfs.FirstOrDefault(p => string.Equals(System.IO.Path.GetFileNameWithoutExtension(p.FileName), stem, StringComparison.OrdinalIgnoreCase))
                       ?? (pdfs.Count == 1 && result.Files.Count(f => f.Extension != ".pdf") == 1 ? pdfs[0] : null);
            }

            var produced = new List<string>();
            bool Emit(GeneratedFilePayload payload, string agentId)
            {
                if (payload == null || !payload.Success)
                {
                    AppLog.Warn("DocSkills", "寫檔失敗：" + payload?.ErrorMessage);
                    return false;
                }
                workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                    workspace, node, agentId, "generated_file", payload, modelId: model));
                produced.Add(payload.FileName);
                return true;
            }

            bool deckDone = false, reportDone = false, tableDone = false;

            if (wantsDeck && pptx != null)
            {
                var deckFile = GeneratedFileWriter.WritePptx(genDir, deckTitle, pptx.Bytes, sourceSummary);
                deckDone = Emit(deckFile, "presentation-agent");
                var pdf = DeliveryOptions.CompanionPdf ? PdfFor(pptx) : null;
                GeneratedFilePayload? deckPdf = null;
                if (deckDone && pdf != null)
                {
                    deckPdf = GeneratedFileWriter.WritePdf(genDir, deckTitle, pdf.Bytes, sourceSummary);
                    Emit(deckPdf, "presentation-agent");
                }
                if (deckDone) GeneratedFileCompanion.Register(deckFile.FilePath, deckPdf?.Success == true ? deckPdf.FilePath : null);
                if (deckDone) orchestration.MarkSuccess("presentation_outline", $"Claude 文件技能 / {produced.Last()}");
            }

            if (wantsReport && docx != null)
            {
                reportDone = Emit(GeneratedFileWriter.WriteDocx(genDir, reportTitle, docx.Bytes, sourceSummary), "report-agent");
                var pdf = DeliveryOptions.CompanionPdf ? PdfFor(docx) : null;
                if (reportDone && pdf != null)
                    Emit(GeneratedFileWriter.WritePdf(genDir, reportTitle, pdf.Bytes, sourceSummary), "report-agent");
            }

            if (wantsTable && xlsx != null)
            {
                tableDone = Emit(GeneratedFileWriter.WriteXlsx(genDir, tableTitle, xlsx.Bytes, sourceSummary), "table-agent");
                var pdf = DeliveryOptions.CompanionPdf ? PdfFor(xlsx) : null;
                if (tableDone && pdf != null)
                    Emit(GeneratedFileWriter.WritePdf(genDir, tableTitle, pdf.Bytes, sourceSummary), "table-agent");
            }

            if ((reportDone || tableDone) && !((wantsReport && !reportDone) || (wantsTable && !tableDone)))
                orchestration.MarkSuccess("generate_file", "Claude 文件技能 / " + string.Join(" / ", produced));

            string summary = string.IsNullOrWhiteSpace(result.Summary)
                ? $"已由 Claude 文件技能產出：{string.Join("、", produced)}。"
                : result.Summary.Trim();

            // 這條路徑是 Claude 在沙箱裡自己排版，沒有圖片模型可用；使用者要了圖就要講清楚差別。
            if (prompt.WantsImages)
                summary += "\n\nℹ 這份是由 Claude 文件技能排版的，沙箱內無法生成照片，視覺元素是程式繪製的圖形與圖表。" +
                           "需要生成式的配圖時，可以在預覽裡逐頁用「生成配圖」補。";

            // 簡報單獨產出時沿用既有行為（輸出區顯示簡報說明）；有報告/表格時保留完整答案、把說明接在後面。
            string text = wantsDeck && !wantsReport && !wantsTable && deckDone
                ? summary
                : (execution.Text ?? "").TrimEnd() + "\n\n" + summary;

            return new SkillsOutcome(ReplaceExecutionText(execution, text), deckDone, reportDone, tableDone);
        }

        /// <summary>
        /// File Generation v1：把最終答案寫成 Markdown 報告檔，加入 workspace artifact，
        /// 並在答案尾端附上檔案位置說明。回傳（可能被附註過的）execution。
        /// 寫檔失敗不影響主答案，只把 generate_file 階段標記為 failed。
        /// </summary>
        private async Task<AiFallbackExecutionResult> GenerateReportFile(
            INodeContext node,
            AgentDefinition runtimeAgent,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            OutputIntent intent,
            CancellationToken ct)
        {
            orchestration.MarkRunning("generate_file");

            int factCount = workspace.GetByType("verified_facts")
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .Sum(p => p.Facts?.Count ?? 0);

            string sourceSummary = factCount > 0
                ? $"{orchestrationPlan.PipelineId} / {factCount} 筆 verified_facts"
                : orchestrationPlan.PipelineId;

            // ── 內容作者：報告代理 + 表格代理「同時」整理乾淨內容（多個 agent 並行工作）。──
            // 不再直接把主答案（可能是 ASCII 簡報草稿）塞進檔案，避免「亂做」。
            // 主答案已是乾淨散文 / 已含表格時就直接用，不額外呼叫模型（省成本、不改壞）。
            string sourceMaterial = execution.Text ?? "";
            bool dirty = DocumentAuthor.LooksDirty(sourceMaterial);
            string? existingTable = DocumentAuthor.ExtractMarkdownTable(sourceMaterial);

            // 報告一律請 Report Author 重寫成完整書面散文：避免「同時生簡報＋報告」時，
            // 報告沿用偏簡報風（條列 / 濃縮）的主答案而變得跟簡報一樣——書面報告必須是文章體。
            // （dirty 仍保留給日後判斷用，但不再是是否重寫的條件。）
            _ = dirty;
            bool authorReport = intent.WantsReport;
            bool authorTable = intent.WantsTable && existingTable == null;

            if (authorReport || authorTable)
                node.SetLoadingHint(authorReport && authorTable
                    ? "報告與表格代理同時整理內容中"
                    : authorTable ? "表格代理整理內容中" : "報告代理整理內容中");

            Task<string?> reportTask = authorReport
                ? AuthorCleanAsync(node, DocumentAuthor.BuildReportPrompt(userInput, sourceMaterial, intent.RequestedLanguage), "Report Author", ct)
                : Task.FromResult<string?>(null);

            Task<string?> tableTask = authorTable
                ? AuthorCleanAsync(node, DocumentAuthor.BuildTablePrompt(userInput, sourceMaterial, intent.RequestedLanguage), "Table Author", ct)
                : Task.FromResult<string?>(null);

            await Task.WhenAll(reportTask, tableTask).ConfigureAwait(false);
            node.SetLoadingHint(null);

            // 報告正文：作者成功用作者內容；否則退回「清理過」的主答案（去 ASCII 線框）。
            string reportBody = !string.IsNullOrWhiteSpace(reportTask.Result)
                ? reportTask.Result!
                : DocumentAuthor.Sanitize(sourceMaterial);

            // 表格 Markdown：作者成功用作者表格；否則用主答案裡既有的表格；再退回清理。
            string tableMarkdown = !string.IsNullOrWhiteSpace(tableTask.Result)
                ? tableTask.Result!
                : (existingTable ?? DocumentAuthor.Sanitize(sourceMaterial));

            // 報告 Markdown（含中繼資料 + 來源）：docx 與 report.pdf 都用同一份 → 內容完全一致。
            string reportMarkdown = MarkdownReportBuilder.Build(new MarkdownReportBuilder.Request
            {
                UserInput = userInput,
                FinalAnswer = reportBody,
                Workspace = workspace,
                TaskType = orchestrationPlan.TaskType,
                PipelineId = orchestrationPlan.PipelineId,
                ModelId = execution.ActualModelId ?? orchestrationPlan.ModelId,
                AgentId = runtimeAgent?.Id ?? ""
            });

            string title = ExtractReportTitle(userInput);
            string genDir = _main.GetGeneratedFilesDir();
            var producedFiles = new List<string>();
            string lastError = "";

            // 各檔以各自的「代理身分」入庫，工作區才看得到報告代理 / 表格代理分工。
            void TryEmit(Func<GeneratedFilePayload> writer, string agentId)
            {
                try
                {
                    var result = writer();
                    if (result != null && result.Success)
                    {
                        workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                            workspace, node, agentId, "generated_file", result));
                        producedFiles.Add(result.FileName);
                    }
                    else if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        lastError = result.ErrorMessage;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            // 報告：.docx + 內容一致的 report.pdf（兩者都吃 reportMarkdown）。
            if (intent.WantsReport)
            {
                // 階段3：報告智慧配圖（同簡報規格）——使用者要圖時，Claude 挑適合的章節生成呼應的圖並嵌入。
                // 報告跟簡報同一個道理：內容需要圖就配圖，純數字與條文的報告不配。
                if (_scope.IllustrationsAllowed() && DeckIllustrationPolicy.ShouldIllustrate(
                        userInput, null, intent.WantsIllustrations, out _, reportMarkdown,
                        declinedFromResolver: intent.DeclinesIllustrations))
                {
                    reportMarkdown = await IllustrateReportMarkdownAsync(node, reportMarkdown, genDir, ct);
                }

                // 藝術方向跟表格同一套算法（標題 + 使用者輸入）：使用者講的風格線索常在輸入裡，
                // 只看標題會讓同一次請求的報告與表格長得不像同一份東西。
                var docArt = DeckArtDirection.ForTopic($"{title} {userInput}");
                TryEmit(() => GeneratedFileWriter.WriteDocx(
                    genDir, title, DocxReportBuilder.Build(reportMarkdown, docArt, intent.RequestedLanguage), sourceSummary), "report-agent");
                if (DeliveryOptions.CompanionPdf)
                    TryEmit(() => GeneratedFileWriter.WritePdf(
                        genDir, title, PdfReportBuilder.Build(reportMarkdown, docArt, intent.RequestedLanguage), sourceSummary), "report-agent");
            }

            // 表格：.xlsx + 內容一致的 table.pdf（兩者都吃 tableMarkdown）。
            if (intent.WantsTable)
            {
                // 整欄都查不到的欄位先拿掉——那一欄存在的唯一效果是把空白排得很整齊。
                tableMarkdown = TableQuality.DropAllMissingColumns(tableMarkdown);

                // 整張表都查不到時，交出一張空表比誠實地說「查不到」更糟
                //（2026-10-15：四個品牌 × 四欄全是「資料不足」，使用者的評語是「在跟我開玩笑嗎」）。
                if (!TableQuality.HasUsableData(tableMarkdown))
                {
                    AppLog.Warn("AgentRuntime", "表格每一欄都沒有資料，不產生 xlsx（交一張空表比誠實說查不到更糟）");
                    orchestration.MarkFailed("generate_file", "查不到足以組成表格的資料");
                    return ReplaceExecutionText(execution,
                        (execution.Text ?? "").TrimEnd() +
                        "\n\nℹ 這次查不到足以組成表格的數據，所以沒有產生 .xlsx。" +
                        "表格裡每一格都寫「資料不足」沒有意義——需要的話可以指定資料來源、縮小範圍，或改問特定一家的數字。");
                }
            }

            if (intent.WantsTable)
            {
                string tablePdfMarkdown = $"# {title}\n\n{tableMarkdown}\n";
                var tableArt = DeckArtDirection.ForTopic($"{title} {userInput}");
                // 表格也要說清楚「這是什麼、哪天做的、資料哪來的」——來源用這次實際查證的紀錄。
                string tableSourceNote = MarkdownReportBuilder.SourceNote(workspace, intent.RequestedLanguage);
                TryEmit(() => GeneratedFileWriter.WriteXlsx(
                    genDir, title,
                    XlsxReportBuilder.Build(tableMarkdown, title, tableArt, tableSourceNote, intent.RequestedLanguage),
                    sourceSummary), "table-agent");
                // 報告也在時，表格 pdf 用不同檔名避免和報告 pdf 撞名。
                string tablePdfTitle = intent.WantsReport ? title + "（表格）" : title;
                if (DeliveryOptions.CompanionPdf)
                    TryEmit(() => GeneratedFileWriter.WritePdf(
                        genDir, tablePdfTitle, PdfReportBuilder.Build(tablePdfMarkdown, tableArt, intent.RequestedLanguage), sourceSummary), "table-agent");
            }

            if (producedFiles.Count > 0)
                orchestration.MarkSuccess("generate_file", string.Join(" / ", producedFiles));
            else
                orchestration.MarkFailed("generate_file", string.IsNullOrWhiteSpace(lastError) ? "報告寫檔失敗" : lastError);

            // 產出檔案之後，對話框不再貼一次一模一樣的全文。
            //
            // 使用者 2026-10-13 的原話：「生了報告還在輸出端打一次一模一樣的幹嘛」。
            // 這份內容已經在 .docx / .xlsx 裡，chip 點下去就看得到；在節點裡再放一份，
            // 畫布上每個產檔節點都會變成一面要捲好幾頁的牆，真正的重點反而被埋掉。
            if (producedFiles.Count > 0)
                return ReplaceExecutionText(execution, BuildFileHandoffText(execution.Text, producedFiles));

            return execution;
        }

        /// <summary>
        /// 檔案產出後要顯示在節點裡的那段話：留一個「不用開檔也看得懂」的摘要，其餘交給檔案。
        ///
        /// 保留的是開頭那一段（報告的摘要、表格的前言）與「資料來源」段——
        /// 來源要能在畫布上直接看到，那是查證類任務的憑據，不該只藏在檔案裡。
        /// </summary>
        private static string BuildFileHandoffText(string? fullText, IReadOnlyList<string> producedFiles)
        {
            const int LeadLimit = 420;

            string text = (fullText ?? "").Trim();
            var lines = text.Replace("\r\n", "\n").Split('\n');

            // 開頭：跳過標題行，取到第一個空行為止（最多 LeadLimit 字）。
            var lead = new System.Text.StringBuilder();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.Length == 0 && lead.Length > 0) break;
                if (line.Length == 0) continue;
                if (lead.Length + line.Length > LeadLimit)
                {
                    lead.Append(line.AsSpan(0, Math.Max(0, LeadLimit - lead.Length))).Append('…');
                    break;
                }
                if (lead.Length > 0) lead.Append('\n');
                lead.Append(line);
            }

            // 來源段原樣保留。
            var sources = new System.Text.StringBuilder();
            bool inSources = false;
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Contains("資料來源", StringComparison.Ordinal) && line.Length <= 24)
                {
                    inSources = true;
                    sources.Append("資料來源\n");
                    continue;
                }
                if (!inSources) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) break;
                if (line.Length > 0) sources.Append(line).Append('\n');
            }

            var parts = new List<string>();
            if (lead.Length > 0) parts.Add(lead.ToString());
            parts.Add($"📄 已產出：{string.Join("、", producedFiles)}（完整內容在檔案裡，點一下就看得到）");
            if (sources.Length > 0) parts.Add(sources.ToString().TrimEnd());

            return string.Join("\n\n", parts);
        }


        // 從使用者輸入提煉「乾淨簡短主題」當檔名（像聊天存檔那樣乾淨）：
        // 去掉祈使動詞 / 格式詞（做一份、輸出成…）/ 頁數 / 配圖等雜訊，只留核心主題。
        // 提煉後為空（純指令、無主題）時回 fallback。
        internal static string CleanTitleFromText(string? userInput, string fallback)
        {
            string t = (userInput ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length == 0) return fallback;

            string[] prefixes = { "幫我", "請幫我", "請", "麻煩", "幫忙", "我要", "我想要", "我想", "想要", "可以" };
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var p in prefixes)
                    if (t.StartsWith(p, System.StringComparison.Ordinal))
                    {
                        t = t.Substring(p.Length).TrimStart('，', '、', ' ');
                        changed = true;
                    }
            }

            string[] noise =
            {
                "做一份", "做一個", "做成", "製作", "生成", "寫一份", "寫一個", "幫我做", "整理成", "彙整成", "輸出成", "輸出為", "產生",
                "關於", "有關", "針對",
                "並幫我生成一張封面圖", "並生成一張封面圖", "並生成封面圖", "幫我生成封面圖", "加上封面圖", "封面圖",
                "都要配圖", "並配圖", "要配圖", "也要配圖", "配圖", "附圖", "加圖", "插圖",
                "的書面報告", "書面報告", "報告", "簡報", "投影片", "文件", "表格", "懶人包",
                "以及", "並且", "並", "還有",
                "pdf", "PDF", "word", "Word", "ppt", "PPT", "pptx", "docx", "xlsx", "excel", "Excel", "marp"
            };
            foreach (var n in noise) t = t.Replace(n, "");

            t = System.Text.RegularExpressions.Regex.Replace(t, @"\d+\s*[頁張]", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[一二三四五六七八九十兩]+\s*[頁張]", "");
            // 指示拿掉後剩下的空括號，例如「（、）」。
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[（(][\s、，,。;；]*[）)]", "");

            t = t.Trim('，', '。', '、', '：', ':', '；', ';', ' ', '的', '　');
            if (t.Length == 0) return fallback;
            return t.Length > 24 ? t.Substring(0, 24).Trim() : t;
        }
    }
}
