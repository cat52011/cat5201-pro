using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    // AgentRuntime 的一部分：簡報：內建版型、Gamma、撰寫者產出大綱與單頁重生。
    // （2026-11-03 依職責拆檔：方法原樣搬出，沒有改任何邏輯；主流程在 AgentRuntime.cs。）
    public sealed partial class AgentRuntime
    {
        /// <summary>
        /// Presentation Agent v1：把最終答案拆解成投影片大綱，加入 PresentationOutline artifact，
        /// 渲染成 Marp Markdown deck 並寫檔（同時產生 GeneratedFile artifact），最後在答案尾端附上說明。
        /// 寫檔失敗不影響主答案，只把 presentation_outline 階段標記為 failed。
        /// </summary>
        private async Task<AiFallbackExecutionResult> GeneratePresentation(
            INodeContext node,
            AgentDefinition runtimeAgent,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            CancellationToken ct,
            bool appendToText = false,
            OutputIntent? intent = null)
        {
            orchestration.MarkRunning("presentation_outline");

            // 「要不要配圖」「幾張投影片」一律以第一層的語意判斷為準（任何語言都認得）；
            // 只有那一步失敗（沒金鑰／API 不通）時才退回關鍵字，關鍵字只認繁中講法。

            int factCount = workspace.GetByType("verified_facts")
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .Sum(p => p.Facts?.Count ?? 0);

            string sourceSummary = factCount > 0
                ? $"{orchestrationPlan.PipelineId} / {factCount} 筆 verified_facts"
                : orchestrationPlan.PipelineId;

            // Presentation v1.5（Gamma 之前）：先用 Perplexity 查資料，再請 Claude/GPT 真正「設計」一份簡報。
            // 兩段任一失敗都優雅退回原本的確定性切段（PresentationOutlineBuilder），demo 不會開天窗。
            int requestedSlides = intent?.RequestedSlides > 0
                ? intent.RequestedSlides
                : PresentationOutlineBuilder.DetectRequestedSlideCount(userInput);
            var outline = await BuildAuthoredPresentationAsync(
                              node, userInput, workspace, execution, requestedSlides, orchestrationPlan, runtimeAgent, ct)
                          ?? PresentationOutlineBuilder.Build(new PresentationOutlineBuilder.Request
                          {
                              UserInput = userInput,
                              FinalAnswer = execution.Text ?? "",
                              Workspace = workspace,
                              PipelineId = orchestrationPlan.PipelineId,
                              ModelId = execution.ActualModelId ?? orchestrationPlan.ModelId,
                              AgentId = runtimeAgent?.Id ?? ""
                          });

            // §7.1 投影片張數精準控制：使用者說 N 頁＝封面＋內容 N 頁，資料來源另加 1 頁（2026-10-29），
            // 而且以「排版後真的會是幾頁」驗收——排不下自動拆的續頁也算在內。
            // 資料來源頁固定放在最後（使用者指定 N＋1 頁）；沒有外部來源也照實寫一頁。
            outline = PresentationOutlineBuilder.EnsureSourcesSlide(outline);
            var outlineBeforeTrim = outline;
            int bulletsBeforeTrim = PresentationOutlineBuilder.CountBullets(outline);
            if (requestedSlides > 0)
                outline = PresentationOutlineBuilder.EnforcePageCountPlusSources(outline, requestedSlides);
            int bulletsAfterTrim = PresentationOutlineBuilder.CountBullets(outline);

            node.SetLoadingHint(null);

            // 結構化大綱 artifact（slide plan）。
            workspace.Add(
                AgentWorkspaceBuilder.FromCapabilityData(
                    workspace,
                    node,
                    runtimeAgent?.Id ?? "presentation-agent",
                    "presentation_outline",
                    outline));

            // 要不要配圖，看這份簡報**實際寫了什麼**。
            //
            // 以前的規則是「使用者沒特別提就不配圖」，於是一份介紹日本高檔餐飲的簡報
            // 做出來是 10 頁純文字，而使用者得每次都記得加一句「附配圖」。
            // 那不是設定問題，是判斷問題——內容需要圖就該配圖。
            // 判斷放在大綱做好之後，因為請求那一行字看不出這份簡報會長成什麼樣子。
            bool wantsImages = DeckIllustrationPolicy.ShouldIllustrate(
                userInput, outline, intent?.WantsIllustrations ?? false, out var illustrationReason,
                declinedFromResolver: intent?.DeclinesIllustrations ?? false);
            if (wantsImages && !_scope.IllustrationsAllowed())
            {
                // 內容需要圖，但手動模式下圖片模型不是使用者選的，確認框也沒勾——不配。
                wantsImages = false;
                AppLog.Info("AgentRuntime", "簡報配圖：手動模式且確認框未勾選配圖，不使用圖片模型");
            }

            AppLog.Info("AgentRuntime",
                $"簡報配圖判斷：{(wantsImages ? "要配圖" : "不配圖")}（{illustrationReason}）");

            // Image Gen → Presentation：要配圖時，生成一張封面圖嵌入 deck / pptx（不另外輸出 png chip）。
            byte[]? coverImageBytes = null;
            if (wantsImages)
            {
                // 封面圖與內容頁配圖彼此不相干，一張圖就要一兩分鐘，排隊跑等於把等待時間加倍。
                // 兩邊同時發，帳目靠 UsageScope（有鎖）照樣分得清楚。
                var coverTask = TryGenerateCoverImageAsync(
                    node, runtimeAgent, outline, workspace, orchestrationPlan, ct);
                var slidesTask = GenerateSlideIllustrationsAsync(node, outline, ct);

                await Task.WhenAll(coverTask, slidesTask);
                (coverImageBytes, _) = await coverTask;
            }

            // .pptx 二進位檔：只有使用者在個人化選了「Gamma」生成器才走 Gamma（Claude 內容 + Gamma 設計）；
            // 其餘（Claude / GPT）一律用內建 PptxBuilder。Gamma 未設定金鑰或失敗時回 null → 自動 fallback 內建 builder
            //（個人化已開放 Gamma 選項；沒 GAMMA_API_KEY 的使用者選了也安全，只是拿到內建版）。
            string? gammaUrl = _main.GetPresentationEngine() == PresentationEngine.Gamma
                ? await TryAddGammaPptxAsync(
                    node, runtimeAgent, outline, execution.Text ?? "", workspace, sourceSummary, ct)
                : null;

            GeneratedFilePayload? pptxResult = null;
            if (gammaUrl == null)
            {
                try
                {
                    byte[] pptxBytes = PptxBuilder.Build(outline, coverImageBytes);
                    pptxResult = GeneratedFileWriter.WritePptx(
                        _main.GetGeneratedFilesDir(),
                        title: DeriveCleanTitle(userInput, outline.Title),
                        content: pptxBytes,
                        sourceSummary: sourceSummary);

                    if (pptxResult.Success)
                    {
                        GeneratedFileCompanion.Register(pptxResult.FilePath, null);
                        workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                            workspace, node, runtimeAgent?.Id ?? "presentation-agent",
                            "generated_file", pptxResult));
                    }
                }
                catch (Exception ex) { AppLog.Warn("AgentRuntime", "簡報 PPTX 產出失敗（已略過，主答案不受影響）", ex); }
            }

            // Native exports share a scene. A native PDF cannot represent Gamma's external design.
            // 個人化關掉對照 PDF 時也跳過——那是整個產出流程裡最慢的一步。
            if (gammaUrl == null && DeliveryOptions.CompanionPdf)
            {
            try
            {
                var pdfResult = GeneratedFileWriter.WritePdf(
                    _main.GetGeneratedFilesDir(),
                    title: DeriveCleanTitle(userInput, outline.Title),
                    content: DeckPdfBuilder.Build(outline, coverImageBytes),
                    sourceSummary: sourceSummary);

                if (pdfResult.Success)
                {
                    if (pptxResult?.Success == true) GeneratedFileCompanion.Register(pptxResult.FilePath, pdfResult.FilePath);
                    workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                        workspace, node, runtimeAgent?.Id ?? "presentation-agent",
                        "generated_file", pdfResult));
                }
            }
            catch (Exception ex) { AppLog.Warn("AgentRuntime", "簡報 PDF 對照產出失敗（已略過）", ex); }
            }

            // §7 NotebookLM 匯出輔助（B 方案）：使用者要求時，附一份可匯入 NotebookLM 的來源文字包。
            string? notebookLmFile = null;
            if (OutputFormatDetector.WantsNotebookLmBundle(userInput))
            {
                try
                {
                    string bundle = NotebookLmBundleBuilder.Build(
                        outline.Title, outline.Topic, execution.Text ?? "", workspace);

                    var nb = GeneratedFileWriter.WriteMarkdown(
                        _main.GetGeneratedFilesDir(),
                        title: outline.Title + "_NotebookLM匯入包",
                        content: bundle,
                        sourceSummary: sourceSummary,
                        extension: ".txt");

                    if (nb.Success)
                    {
                        workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                            workspace, node, runtimeAgent?.Id ?? "presentation-agent",
                            "generated_file", nb));
                        notebookLmFile = nb.FileName;
                    }
                }
                catch (Exception ex) { AppLog.Warn("AgentRuntime", "NotebookLM 匯入包產出失敗（已略過）", ex); }
            }

            if (pptxResult?.Success == true || gammaUrl != null)
            {
                string fileName = pptxResult?.FileName ?? "Gamma 線上版";
                orchestration.MarkSuccess("presentation_outline", $"{outline.SlideCount} 張 / {fileName}");

                // 節點上要看得出「做出了什麼檔案」，不是只有一句大綱張數。
                // 最佳品質那條路會放 Claude 寫的說明，內建這條以前只留一行狀態，
                // 兩邊落差大到使用者以為備註不見了（2026-10-27）。
                string note = $"📄 已產出：{fileName}（完整內容在檔案裡，點一下就看得到）";
                if (!string.IsNullOrWhiteSpace(outline.Title))
                    note = outline.Title.Trim() + "\n" + note;
                note += $"\n共 {DeckScene.CountPages(outline.Slides, coverImageBytes)} 頁。";

                // 「檔案寫出來了」不等於「這份簡報可以拿出去」：字級、溢出、圖片比例都檢查一次，
                // 有問題照實說，不要安靜交付一份版面壞掉的檔案。
                // 為了守住指定頁數而砍掉的內容，要讓使用者看得到——
                // 悄悄交出一份少了三分之二重點的簡報，比交出超頁的還糟（2026-10-28）。
                var droppedHeadings = PresentationOutlineBuilder.DroppedHeadings(outlineBeforeTrim, outline);
                if (droppedHeadings.Count > 0)
                    note += "\n⚠ 下列主題未納入：" + string.Join("、", droppedHeadings.Take(6)) +
                            (droppedHeadings.Count > 6 ? $" 等 {droppedHeadings.Count} 項" : "") + "。";

                if (bulletsAfterTrim < bulletsBeforeTrim)
                    note += $"\n⚠ 為符合「{requestedSlides} 頁」，已精簡內容：重點 {bulletsBeforeTrim} → {bulletsAfterTrim} 個。" +
                            "需要完整內容請提高頁數或改用報告格式。";

                string quality = DeckQuality.CheckAndLog(outline, coverImageBytes, fileName);
                if (!string.IsNullOrWhiteSpace(quality))
                    note += "\n⚠ " + quality;

                // 使用者要了圖卻一張都沒有時，要講原因——不然看起來就是「說了沒用」。
                if (wantsImages)
                {
                    int images = DeckScene.CountRenderedImages(outline, coverImageBytes);
                    if (images == 0)
                        note += "\n⚠ 這次沒有配圖：" + (ImageSkipReason() ?? "圖片步驟沒有產出可用的圖。") +
                                "\n（可以在預覽裡展開任一頁的「編輯這一頁」，用「生成配圖」單獨補一張。）";
                    else
                        note += $"\n已配 {images} 張圖。";
                }

                if (!string.IsNullOrWhiteSpace(gammaUrl))
                    note += $"\n線上版（Gamma）：{gammaUrl}";
                if (!string.IsNullOrWhiteSpace(notebookLmFile))
                    note += $"\n附 NotebookLM 匯入包：{notebookLmFile}（可貼入／上傳到 NotebookLM 當來源）。";

                // 同一請求也產了書面報告時，把簡報說明接在報告文字之後，不覆蓋報告內容。
                string combinedText = appendToText && !string.IsNullOrWhiteSpace(execution.Text)
                    ? execution.Text.TrimEnd() + "\n\n" + note
                    : note;

                return new AiFallbackExecutionResult
                {
                    IsSuccess = execution.IsSuccess,
                    Text = combinedText,
                    ActualModelId = execution.ActualModelId ?? "",
                    UsedFallback = execution.UsedFallback,
                    Summary = execution.Summary ?? "",
                    ErrorMessage = execution.ErrorMessage ?? "",
                    Attempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>()
                };
            }

            orchestration.MarkFailed("presentation_outline", pptxResult?.ErrorMessage ?? "pptx 寫檔失敗");
            return execution;
        }

        // Presentation v2 前置：用 Gamma 產出高品質 .pptx（Claude 內容 + Gamma 設計）。
        // 回傳 gammaUrl（成功，可能為空字串表示無連結）；回傳 null 代表未啟用或失敗，呼叫端應 fallback 到 PptxBuilder。
        // 目前休眠：沒有 GAMMA_API_KEY 時 IsConfigured=false，直接回 null，行為與現在相同。
        private async Task<string?> TryAddGammaPptxAsync(
            INodeContext node,
            AgentDefinition? runtimeAgent,
            PresentationOutlinePayload outline,
            string contentText,
            AgentWorkspace workspace,
            string sourceSummary,
            CancellationToken ct)
        {
            var gamma = new GammaPresentationService();
            if (!gamma.IsConfigured || string.IsNullOrWhiteSpace(contentText))
                return null;

            try
            {
                var result = await gamma.GeneratePresentationAsync(new GammaGenerationInput
                {
                    InputText = contentText,
                    Title = outline.Title,
                    NumCards = outline.RequestedSlideCount > 0 ? outline.RequestedSlideCount + 1 : 0,   // ＋1 頁資料來源
                    Language = "zh-tw"
                }, ct);

                if (!result.Success || !result.HasExport)
                    return null;

                byte[] pptxBytes = await gamma.DownloadExportAsync(result.ExportUrl, ct);
                if (pptxBytes == null || pptxBytes.Length == 0)
                    return null;

                var pptxGenerated = GeneratedFileWriter.WritePptx(
                    _main.GetGeneratedFilesDir(),
                    title: outline.Title,
                    content: pptxBytes,
                    sourceSummary: $"{sourceSummary} / Gamma");

                if (!pptxGenerated.Success)
                    return null;

                GeneratedFileCompanion.Register(pptxGenerated.FilePath, null);

                workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                    workspace, node, runtimeAgent?.Id ?? "presentation-agent",
                    "generated_file", pptxGenerated));

                return result.GammaUrl ?? "";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 任何問題（網路 / 欄位不符 / 額度不足）→ 回 null 讓呼叫端 fallback 到 PptxBuilder。
                return null;
            }
        }

        // 使用者是否明確要求簡報配圖（避免每次簡報都呼叫圖片 API 造成額外成本）。
        /// <summary>
        /// 使用者這次有沒有要圖。
        ///
        /// 舊版是一串「相鄰字串」比對（配圖、附圖、要有圖…），所以「多放一些圖片」「圖文並茂」「每頁配一張照片」
        /// 這些自然講法全都漏掉——使用者明明說了要圖，簡報卻是純文字。
        /// 現在改成：先看有沒有明講不要，再看有沒有「圖片類名詞」，最後才是只寫「圖」時要搭動詞
        /// （避免「圖表」「地圖」「藍圖」被當成要配圖）。
        /// </summary>
        public static bool PresentationWantsCoverImage(string? userInput)
        {
            string text = userInput ?? "";
            string lower = text.ToLowerInvariant();

            // 明確說不要就是不要（使用者的規則：沒特別提就不配圖）。
            if (DeckIllustrationPolicy.UserDeclined(text))
            {
                return false;
            }

            // 圖片類名詞單獨出現就算數（「圖表」「地圖」「藍圖」不在其中，不會誤判）。
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    text, @"圖片|照片|插畫|插圖|配圖|附圖|示意圖|圖像|圖文並茂|視覺素材"))
            {
                return true;
            }

            // 帶量詞的講法：「配一張圖」「多放兩張圖」「幾張圖」。排除「圖表」。
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    text, @"[一二三四五六七八九十兩幾些\d]+\s*[張幅]\s*圖(?!表)"))
            {
                return true;
            }

            return ContainsAnyText(text, lower,
                "封面圖", "加圖", "加上圖", "搭配圖", "有圖", "要圖", "帶圖",
                "含圖", "包含圖", "需要圖", "加入圖", "放圖", "要有圖",
                "with image", "with images", "cover image", "with picture", "with pictures",
                "illustration", "illustrated", "with a picture", "with photo", "with photos");
        }

        private static bool ContainsAnyText(string text, string lower, params string[] keywords)
        {
            foreach (var kw in keywords)
            {
                if (string.IsNullOrEmpty(kw))
                    continue;

                if (kw.Any(ch => ch > 127))
                {
                    if (text.Contains(kw, StringComparison.Ordinal))
                        return true;
                }
                else if (lower.Contains(kw, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // 為簡報生成一張封面圖：回傳 (png bytes, 寫出的檔名)；失敗時回傳 (null, null) 不影響簡報。
        private async Task<(byte[]?, string?)> TryGenerateCoverImageAsync(
            INodeContext node,
            AgentDefinition? runtimeAgent,
            PresentationOutlinePayload outline,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("簡報封面圖");
            try
            {
                string subject = string.IsNullOrWhiteSpace(outline.Topic)
                    ? outline.Title
                    : $"{outline.Title}：{outline.Topic}";

                // 「沒意義」的舊作法是把標題塞進死板模板；改成讓 Claude 讀簡報實際內容，
                // 萃取「一個能代表整份簡報的具體象徵畫面」，再套統一的簡約資訊示意風格。
                var art = DeckArtDirection.ForOutline(outline);
                string brief = await BuildIllustrationBriefAsync(
                    node, BuildCoverBriefMaterial(outline), "簡報封面", ct, art);
                if (string.IsNullOrWhiteSpace(brief))
                {
                    NoteImageSkipped("這份內容沒有適合入畫的具體主體（或主題屬於地圖、商標這類不該生成的東西），所以沒有硬湊一張圖。");
                    return (null, null); // No meaningful brief: a typographic cover is preferable to an arbitrary image.
                }
                // 封面圖跟著簡報的藝術方向走，並用寬幅構圖（左側留白給大標題壓字）。
                string prompt = IllustrationStyle.Compose(brief, art, isCover: true);

                var imageService = new OpenAIImageService("gpt-image-2");
                using var reservation = SpendReservations.TryReserveWithinBudget(
                    "簡報封面圖", ModelCostEstimator.ImageCostUsd(DeckImageSize, IllustrationQuality));
                if (reservation == null)
                {
                    NoteImageSkipped($"會超過今日預算上限（{SpendLedger.CommittedDisplay()}）");
                    return (null, null);
                }
                var image = await imageService.GenerateAsync(prompt, DeckImageSize, ct, IllustrationQuality);

                if (!image.Success || image.PngBytes == null || image.PngBytes.Length == 0)
                {
                    NoteImageSkipped(FailureExplainer.Describe(image.ErrorMessage));
                    return (null, null);
                }

                // 封面圖只嵌進 .pptx 封面頁，不另外寫成 .png 檔、也不加進 workspace 產出物，
                // 避免在輸出區多出一個獨立的封面 png chip（使用者只要簡報，不要散落的封面圖）。
                return (image.PngBytes, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 配圖失敗不影響簡報主體，但要留下原因（最常見是沒有設定 OPENAI_API_KEY）。
                NoteImageSkipped(FailureExplainer.Describe(ex.Message));
                return (null, null);
            }
        }

        // Presentation v1.5（Gamma / NotebookLM 之前的「夠用」占位）：兩段式作者流程
        // ——研究（Perplexity）→ 選定生成器（Claude / GPT）一次撰寫結構化 JSON deck。
        // 刻意保持簡單：內容品質的真正投資留到接上 Gamma / NotebookLM，這層不做逐頁深寫等重工。
        // 任一步失敗回 null，呼叫端 fallback 回確定性切段。
        private async Task<PresentationOutlinePayload?> BuildAuthoredPresentationAsync(
            INodeContext node,
            string userInput,
            AgentWorkspace workspace,
            AiFallbackExecutionResult execution,
            int requestedSlides,
            OrchestrationPlanPayload orchestrationPlan,
            AgentDefinition? runtimeAgent,
            CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("簡報撰寫");
            try
            {
                var engine = _main.GetPresentationEngine();
                string authorModelId = _scope.DocumentAuthorModel(PresentationEngineHelper.ToAuthorModelId(engine));
                string engineName = AuthorDisplayName(engine, authorModelId);

                // 1) 研究（Perplexity）：best-effort，失敗 / 無金鑰就用空素材，作者改用自身知識。
                //    手動模式下若節點選的不是 Sonar，這一步不呼叫任何模型（見 ResearchForPresentationAsync）。
                if (!_scope.IsManual)
                    node.SetLoadingHint("Perplexity 正在查資料");
                string research = await ResearchForPresentationAsync(node, userInput, requestedSlides, ct);

                // 2) 作者（選定生成器，必要時 fallback）：一次產出結構化 JSON deck。
                node.SetLoadingHint($"{engineName} 正在撰寫簡報內容");
                string authorPrompt = PresentationAuthor.BuildAuthorPrompt(
                    userInput, research, execution.Text, requestedSlides);

                var authored = await RunAuthorStepAsync(
                    node, authorPrompt, authorModelId, $"Presentation Author ({engineName})", ct);
                if (!authored.IsSuccess || string.IsNullOrWhiteSpace(authored.Text))
                    return null;

                node.SetLoadingHint(null);

                // 模型 id 帶上實際作者模型，決策窗才看得到「簡報是誰寫的」（Claude / GPT → 成功；pplx → 退回切段）。
                return PresentationAuthor.Parse(
                    authored.Text,
                    userInput,
                    requestedSlides,
                    orchestrationPlan.PipelineId,
                    string.IsNullOrWhiteSpace(authored.ActualModelId) ? authorModelId : authored.ActualModelId,
                    runtimeAgent?.Id ?? "presentation-agent");
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return null;
            }
        }

        // §7.2 單張投影片重生：用作者模型只重寫指定那一張，回傳替換後的新大綱（失敗回 null，呼叫端保留原樣）。
        public async Task<PresentationOutlinePayload?> RegeneratePresentationSlideAsync(
            INodeContext node,
            PresentationOutlinePayload outline,
            int slideOrder,
            string userInput,
            CancellationToken ct)
        {
            if (outline == null)
                return null;

            try
            {
                EnsureScopeFor(node);
                var engine = _main.GetPresentationEngine();
                string authorModelId = _scope.DocumentAuthorModel(PresentationEngineHelper.ToAuthorModelId(engine));
                string engineName = AuthorDisplayName(engine, authorModelId);

                node.SetLoadingHint($"{engineName} 正在重生第 {slideOrder} 張投影片");
                string prompt = PresentationAuthor.BuildSingleSlidePrompt(outline, slideOrder, userInput);

                var authored = await RunAuthorStepAsync(
                    node, prompt, authorModelId, $"Slide Regen ({engineName})", ct);

                node.SetLoadingHint(null);

                if (!authored.IsSuccess || string.IsNullOrWhiteSpace(authored.Text))
                    return null;

                var parsed = PresentationAuthor.ParseSingleSlide(authored.Text);
                if (parsed == null)
                    return null;

                return PresentationAuthor.ReplaceSlide(
                    outline, slideOrder, parsed.Value.Heading, parsed.Value.Bullets);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                node.SetLoadingHint(null);
                return null;
            }
        }

        // 內容作者共用 executor：以選定的作者模型跑一步（簡報骨架 / 逐頁內容 / 報告 / 表格），必要時 fallback。
        private async Task<AiFallbackExecutionResult> RunAuthorStepAsync(
            INodeContext node, string prompt, string authorModelId, string label, CancellationToken ct)
        {
            var decision = new NodeExecutionDecision
            {
                // 這一步是「做文件」，不是泛用對話；決策窗的 Agent 欄要講實話。
                RequestedAgentId = "presentation-agent",
                ActualAgentId = "presentation-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(authorModelId),
                ModelId = AiModelHelper.NormalizeNodeModel(authorModelId),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = label,
                ResolverReason = label,
                StatusLabel = "Auto",
                ForceSingleModel = false
            };

            return await _executeWithFallbackAsync(node, prompt, decision, null, false, ct);
        }

        // §6 報告 / 表格內容作者：用作者模型把主答案整理成乾淨內容，回傳去圍欄後的文字（失敗回 null）。
        private async Task<string?> AuthorCleanAsync(
            INodeContext node, string prompt, string label, CancellationToken ct)
        {
            try
            {
                string modelId = _scope.DocumentAuthorModel(PresentationEngineHelper.ToAuthorModelId(_main.GetPresentationEngine()));
                var r = await RunAuthorStepAsync(node, prompt, modelId, label, ct).ConfigureAwait(false);
                if (!r.IsSuccess || string.IsNullOrWhiteSpace(r.Text))
                    return null;
                return DocumentAuthor.StripCodeFence(r.Text);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        // 用 Perplexity 蒐集簡報素材；無金鑰 / 失敗時回空字串（不中斷，作者改用自身知識）。
        private async Task<string> ResearchForPresentationAsync(
            INodeContext node, string userInput, int requestedSlides, CancellationToken ct)
        {
            // 手動模式：只有節點選的就是 Perplexity Sonar 時才做這一步。
            // 其他模型的情況下，能力層的搜尋研究已經做過了，再叫 Sonar 來就是背後多一個模型。
            if (_scope.IsManual &&
                !string.Equals(AiModelHelper.NormalizeNodeModel(_scope.SelectedModelId),
                               AiModels.Perplexity_Sonar, StringComparison.OrdinalIgnoreCase))
                return "";

            using var usagePurpose = UsageMeter.Purpose("簡報研究");
            try
            {
                string researchPrompt = PresentationAuthor.BuildResearchPrompt(userInput, requestedSlides);

                var researchDecision = new NodeExecutionDecision
                {
                    RequestedAgentId = "general-agent",
                    ActualAgentId = "general-agent",
                    RequestedModelId = AiModelHelper.NormalizeNodeModel(AiModels.Perplexity_Sonar),
                    ModelId = AiModelHelper.NormalizeNodeModel(AiModels.Perplexity_Sonar),
                    ActualModelId = "",
                    TaskMode = NodeTaskMode.Research,
                    ResolverLabel = "Presentation Research (Perplexity)",
                    ResolverReason = "用 Perplexity 取得簡報所需的即時事實與數據。",
                    StatusLabel = "Auto",
                    ForceSingleModel = false
                };

                var r = await _executeWithFallbackAsync(node, researchPrompt, researchDecision, null, false, ct);
                return r.IsSuccess ? (r.Text ?? "") : "";
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return "";
            }
        }
    }
}
