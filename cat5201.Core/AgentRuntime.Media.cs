using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    // AgentRuntime 的一部分：圖片與影片：生圖、改圖、影片企劃與生成。
    // （2026-11-03 依職責拆檔：方法原樣搬出，沒有改任何邏輯；主流程在 AgentRuntime.cs。）
    public sealed partial class AgentRuntime
    {
        // 把（可能含上游搜尋結果的）原始輸入交給 Claude，萃取真正主體並寫成具體、可畫的圖片提示。
        private async Task<string> BuildImageBriefAsync(INodeContext node, string rawRequest, CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("圖片提示詞");
            string briefPrompt =
                "你是圖片生成的提示詞工程師。下面是使用者的請求，可能夾帶上游節點的搜尋結果或一大段背景文字。\n" +
                "請閱讀全部內容，找出『真正要畫的主體』（例如某家真實公司、某個產品、某個場景），" +
                "再寫出一段【具體、精煉】的圖片生成提示，直接描述畫面：主體、場景、構圖、風格、色調。\n\n" +
                "嚴格規則：\n" +
                "1. 只輸出最終的圖片提示文字本身，不要任何解釋、前後綴、引號或 markdown。\n" +
                "2. 讓人一眼認得出主體。若主體是知名、可公開查證的真實品牌（例如 Google、Apple、星巴克），" +
                "請納入其真實、為人熟知的視覺特徵（招牌配色、總部 / 門市建築、識別意象），使畫面明確指向該品牌。\n" +
                "3. 但不要捏造不存在的東西：若主體是不知名或私人公司、且背景資料沒有提供其真實的商標 / 視覺識別，" +
                "就不要憑空發明 logo、品牌名、標語或數據，改以該產業的真實情境（廠房、產品、場域、員工工作畫面）呈現。\n" +
                "4. 風格預設為乾淨、現代、專業的示意插圖或攝影，留白充足、無浮水印。\n\n" +
                "=== 使用者請求與背景資料 ===\n" + rawRequest;

            // 手動模式由節點選的模型寫提示詞；不適合時（深度研究）直接用使用者原文。
            string? briefModel = _scope.HelperModel(AiModels.Claude_Sonnet5);
            if (briefModel == null)
                return "";

            var briefDecision = new NodeExecutionDecision
            {
                RequestedAgentId = "general-agent",
                ActualAgentId = "general-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(briefModel),
                ModelId = AiModelHelper.NormalizeNodeModel(briefModel),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = "Image Brief (Claude)",
                ResolverReason = "Claude 先把上游內容轉成具體的圖片提示，避免圖片模型亂編商標 / 文字。",
                StatusLabel = "Auto",
                ForceSingleModel = true
            };

            try
            {
                var r = await _executeWithFallbackAsync(node, briefPrompt, briefDecision, null, false, ct);
                return r.IsSuccess ? (r.Text ?? "").Trim() : "";
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        /// <summary>這次要不要往使用者偏好的藝術類美感（復古雜誌）靠。只看語意判斷，不看字面。</summary>
        internal static bool IsPoetic(OutputIntent? intent)
            => string.Equals(intent?.ArtDirection, PoeticPrintStyle.Id, StringComparison.OrdinalIgnoreCase);

        private async Task<AiFallbackExecutionResult> GenerateImageFile(
            INodeContext node,
            AgentDefinition runtimeAgent,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            CancellationToken ct)
        {
            orchestration.MarkRunning("generate_image");

            string rawRequest = (userInput ?? "").Trim();
            if (string.IsNullOrWhiteSpace(rawRequest))
            {
                orchestration.MarkFailed("generate_image", "圖片描述為空。");
                return execution;
            }

            // 直接把（可能夾帶上游搜尋結果的）整段文字丟給圖片模型，它「讀不懂」而會捏造假商標 / 假公司
            // （例如憑空生出一個品牌形象牆）。先用 Claude 把內容萃取成具體、可畫的圖片提示，並禁止虛構文字 / logo。
            string prompt = await BuildImageBriefAsync(node, rawRequest, ct);
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = rawRequest; // 退回原始輸入，至少還能出圖

            // 藝術類圖片偏向使用者喜歡的復古雜誌美感（語意判斷決定，見 PoeticPrintStyle）；寫實類照舊。
            // 只加美術方向，不改構圖、不加規定的文字——使用者要的是風格，不是一份照做的規格。
            if (IsPoetic(_requestIntent))
                prompt = prompt.TrimEnd() + " " + PoeticPrintStyle.ImageStyle;
            const string size = "1024x1024";

            OpenAIImageService.ImageResult image;
            try
            {
                var imageService = new OpenAIImageService("gpt-image-2");
                using var reservation = SpendReservations.TryReserveWithinBudget(
                    "圖片生成", ModelCostEstimator.ImageCostUsd(size, "high"));
                if (reservation == null)
                {
                    throw new InvalidOperationException(
                        $"這張圖的估算費用會超過今日預算上限（{SpendLedger.CommittedDisplay()}）。"
                        + "請調高個人化裡的每日預算。");
                }
                image = await imageService.GenerateAsync(prompt, size, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                orchestration.MarkFailed("generate_image", ex.Message);
                return new AiFallbackExecutionResult
                {
                    IsSuccess = execution.IsSuccess,
                    Text = (execution.Text ?? "").TrimEnd() + $"\n\n⚠ 圖片生成失敗：{ex.Message}",
                    ActualModelId = execution.ActualModelId ?? "",
                    UsedFallback = execution.UsedFallback,
                    Summary = execution.Summary ?? "",
                    ErrorMessage = execution.ErrorMessage ?? "",
                    Attempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>()
                };
            }

            if (!image.Success)
            {
                orchestration.MarkFailed("generate_image", image.ErrorMessage);
                return new AiFallbackExecutionResult
                {
                    IsSuccess = execution.IsSuccess,
                    Text = (execution.Text ?? "").TrimEnd() + $"\n\n⚠ 圖片生成失敗：{image.ErrorMessage}",
                    ActualModelId = execution.ActualModelId ?? "",
                    UsedFallback = execution.UsedFallback,
                    Summary = execution.Summary ?? "",
                    ErrorMessage = execution.ErrorMessage ?? "",
                    Attempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>()
                };
            }

            string sourceSummary = string.IsNullOrWhiteSpace(image.RevisedPrompt)
                ? $"{orchestrationPlan.PipelineId} / gpt-image-2"
                : $"{orchestrationPlan.PipelineId} / gpt-image-2 / {image.RevisedPrompt}";

            var generated = GeneratedFileWriter.WriteImage(
                _main.GetGeneratedFilesDir(),
                title: ExtractReportTitle(prompt),
                content: image.PngBytes,
                sourceSummary: sourceSummary);

            workspace.Add(
                AgentWorkspaceBuilder.FromCapabilityData(
                    workspace,
                    node,
                    runtimeAgent?.Id ?? "image-agent",
                    "generated_file",
                    generated));

            if (generated.Success)
            {
                orchestration.MarkSuccess("generate_image", generated.FileName);

                // §8 成本提示：圖片以「每張」計價（不是 token），用真實尺寸 / 品質估算單張成本。
                string imgSize = string.IsNullOrWhiteSpace(image.Size) ? "1024x1024" : image.Size;
                string costHint = ModelCostEstimator.ImageCostDisplay(1, imgSize, "high");
                string note = $"\n\n已生成圖片（gpt-image-2）。{costHint}";

                return new AiFallbackExecutionResult
                {
                    IsSuccess = execution.IsSuccess,
                    Text = (execution.Text ?? "").TrimEnd() + note,
                    ActualModelId = execution.ActualModelId ?? "",
                    UsedFallback = execution.UsedFallback,
                    Summary = execution.Summary ?? "",
                    ErrorMessage = execution.ErrorMessage ?? "",
                    Attempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>()
                };
            }

            orchestration.MarkFailed("generate_image", generated.ErrorMessage);
            return execution;
        }

        // Image Edit：上傳圖 + 指令 → OpenAI images/edits 改圖，輸出改建後的照片。
        // 缺圖 / 缺金鑰 / 失敗一律附註說明、不中斷主答案。
        private async Task<AiFallbackExecutionResult> GenerateImageEdit(
            INodeContext node,
            AgentDefinition runtimeAgent,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            CancellationToken ct)
        {
            orchestration.MarkRunning("generate_image_edit");

            byte[]? srcImage = GetFirstImageAttachmentBytes(node);
            if (srcImage == null)
            {
                orchestration.MarkFailed("generate_image_edit", "找不到可編輯的圖片附件。");
                return AppendNote(execution, "\n\n⚠ 沒有找到可編輯的圖片附件，請在這個節點掛上要修改的照片再試。");
            }

            // 把中文指令（可能含「影片風格」這種與圖片無關的雜訊）轉成乾淨的英文編輯指令，並強調保留整體結構。
            string prompt = await BuildImageEditPromptAsync(node, userInput, ct);
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = (userInput ?? "").Trim();
            // 藝術類改圖：照使用者的要求改，再往復古雜誌的美感靠；寫實類（換背景、修圖…）不加。
            if (IsPoetic(_requestIntent))
                prompt = prompt.TrimEnd() + " " + PoeticPrintStyle.ImageStyle;

            OpenAIImageService.ImageResult image;
            try
            {
                var svc = new OpenAIImageService("gpt-image-1");
                image = await svc.EditAsync(srcImage, prompt, "1024x1024", ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                orchestration.MarkFailed("generate_image_edit", ex.Message);
                return AppendNote(execution, $"\n\n⚠ 圖片編輯失敗：{ex.Message}");
            }

            if (!image.Success)
            {
                orchestration.MarkFailed("generate_image_edit", image.ErrorMessage);
                return AppendNote(execution, $"\n\n⚠ 圖片編輯失敗：{image.ErrorMessage}");
            }

            string sourceSummary = $"{orchestrationPlan.PipelineId} / gpt-image-1 edit";
            var generated = GeneratedFileWriter.WriteImage(
                _main.GetGeneratedFilesDir(),
                title: DeriveCleanTitle(userInput, "改建照片"),
                content: image.PngBytes,
                sourceSummary: sourceSummary);

            workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                workspace, node, runtimeAgent?.Id ?? "image-agent", "generated_file", generated));

            if (generated.Success)
            {
                orchestration.MarkSuccess("generate_image_edit", generated.FileName);
                string costHint = ModelCostEstimator.ImageCostDisplay(1, "1024x1024", "high");
                return AppendNote(execution, $"\n\n已輸出改建後的照片（gpt-image-1 編輯）。{costHint}");
            }

            orchestration.MarkFailed("generate_image_edit", generated.ErrorMessage);
            return execution;
        }

        // 取節點（含沿上游鏈繼承）的第一張圖片附件 bytes；無則 null。
        private byte[]? GetFirstImageAttachmentBytes(INodeContext node)
        {
            try
            {
                string root = _main.GetAttachmentsRootDir();
                foreach (var a in _main.GetEffectiveAttachmentsForNode(node))
                {
                    bool isImg =
                        (!string.IsNullOrEmpty(a.MimeType) && a.MimeType.StartsWith("image/", System.StringComparison.OrdinalIgnoreCase))
                        || a.FileName.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)
                        || a.FileName.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase)
                        || a.FileName.EndsWith(".jpeg", System.StringComparison.OrdinalIgnoreCase)
                        || a.FileName.EndsWith(".webp", System.StringComparison.OrdinalIgnoreCase);
                    if (!isImg) continue;

                    string path = System.IO.Path.Combine(root, a.RelativePath);
                    if (System.IO.File.Exists(path))
                        return System.IO.File.ReadAllBytes(path);
                }
            }
            catch (Exception ex) { AppLog.Warn("AgentRuntime", "讀取圖片附件失敗", ex); }
            return null;
        }

        // 把使用者的中文修改指令轉成乾淨英文編輯指令（保留整體結構、去除與圖片無關的雜訊如「影片風格」）。
        private async Task<string> BuildImageEditPromptAsync(INodeContext node, string userInput, CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("圖片編輯提示詞");
            string p =
                "你是圖片編輯提示詞工程師。使用者上傳了一張照片，並用中文描述想怎麼修改它。\n" +
                "請把它轉成一段給圖片編輯模型的英文編輯指令：明確說出要改哪裡、改成什麼樣子，" +
                "並明確強調保留原圖的整體結構 / 構圖 / 視角 / 其餘部分不變（only change what is requested, keep the overall structure and composition intact）。\n" +
                "規則：只輸出英文編輯指令本身，不要解釋 / 引號 / markdown；不要提到『影片』或任何與圖片編輯無關的字；不要捏造文字或商標。\n\n" +
                "【使用者指令】\n" + (userInput ?? "").Trim();

            // 手動模式由節點選的模型寫編輯指令；不適合時直接用使用者原文。
            string? editModel = _scope.HelperModel(AiModels.Claude_Sonnet5);
            if (editModel == null)
                return "";

            var decision = new NodeExecutionDecision
            {
                RequestedAgentId = "general-agent",
                ActualAgentId = "general-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(editModel),
                ModelId = AiModelHelper.NormalizeNodeModel(editModel),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = "Image Edit Prompt (Claude)",
                ResolverReason = "Claude 把中文修改指令轉成乾淨英文編輯指令，強調保留整體結構。",
                StatusLabel = "Auto",
                ForceSingleModel = true
            };

            try
            {
                var r = await _executeWithFallbackAsync(node, p, decision, null, false, ct);
                return r.IsSuccess ? (r.Text ?? "").Trim() : "";
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        private static AiFallbackExecutionResult AppendNote(AiFallbackExecutionResult e, string note)
            => new AiFallbackExecutionResult
            {
                IsSuccess = e.IsSuccess,
                Text = (e.Text ?? "").TrimEnd() + note,
                ActualModelId = e.ActualModelId ?? "",
                UsedFallback = e.UsedFallback,
                Summary = e.Summary ?? "",
                ErrorMessage = e.ErrorMessage ?? "",
                Attempts = e.Attempts ?? System.Array.Empty<AiFallbackAttempt>()
            };

        /// <summary>
        /// Video Gen v1（多工具導演流程）：
        ///   1) Claude 當導演產出結構化影片計畫（劇本/分鏡/旁白/鏡頭/風格）——核心，永遠執行。
        ///   2) 配角工具（Flux/Midjourney 關鍵畫面、ElevenLabs 旁白、Suno 配樂）—— 缺 API 就記為略過。
        ///   3) 影片產生器：Veo 3（唯一 provider；無金鑰則只交付 Claude 計畫）。
        /// 支援取消（OperationCanceledException 往上拋）。即使 Veo API 未配置，使用者仍拿到完整 Claude 影片計畫。
        /// </summary>
        private async Task<AiFallbackExecutionResult> GenerateVideoFile(
            INodeContext node,
            AgentDefinition runtimeAgent,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            CancellationToken ct)
        {
            orchestration.MarkRunning("generate_video");

            string prompt = (userInput ?? "").Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                orchestration.MarkFailed("generate_video", "影片描述為空。");
                return execution;
            }

            // 影片產出一律歸「video-agent」（不論觸發的 runtime agent 是誰）——這步本來就是影片代理的工作，
            // 讓決策窗/工作區顯示 video-agent + video-director(claude)，符合「第一層分類 → 對應代理」原則。
            string agentId = "video-agent";

            // 想要的長度（從文字偵測，預設 8 秒，上限 148 秒）+ 生效的影片風格（個人化覆寫或原廠電影感預設）。
            int targetSeconds = VideoPlanBuilder.ParseRequestedSeconds(prompt);
            string videoStyle = _main.GetEffectiveVideoStylePrompt();

            // 第一層判定影片任務後，再細分節奏：快剪（預告片/混剪/MV…）vs 連貫長鏡頭。
            // 快剪需要 ffmpeg 本地裁切拼接；沒有 ffmpeg 就自動降級回連貫模式（並在輸出說明）。
            var requestedCutMode = VideoPlanBuilder.DetectCutMode(prompt);
            var ffmpeg = new FfmpegService();
            bool fastCut = requestedCutMode == VideoCutMode.FastCut && ffmpeg.IsAvailable;
            bool fastCutWanted = requestedCutMode == VideoCutMode.FastCut;
            var cutMode = fastCut ? VideoCutMode.FastCut : VideoCutMode.Continuous;

            // Veo 3.1 Lite 不支援延伸：連貫長片無法多段串接，只能單段 8 秒。
            // （快剪只用 base 生成、不延伸，Lite 可正常做。）
            bool canExtend = VeoModels.SupportsExtend(_main.GetVeoModelTier());
            bool liteNoExtend = !fastCut && !canExtend && targetSeconds > VideoPlanBuilder.BaseSegmentSeconds;

            // 1) Claude 導演：劇本 / 分鏡 / 旁白 / 鏡頭 / 風格（核心，永遠執行）。
            // 把主合成（general-agent 已寫好的企劃，使用者也喜歡看的那份）當改編底本餵給導演，
            // 讓「實際採用的影片計畫」與主合成一致、且建立在更完整的敘事上。
            string treatment = execution.Text ?? "";
            node.SetLoadingHint("Claude 正在寫劇本與分鏡");
            VideoPlanPayload plan = await BuildVideoPlanAsync(node, prompt, targetSeconds, videoStyle, treatment, cutMode, ct);
            node.SetLoadingHint(null);

            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "劇本/分鏡/旁白/鏡頭", "Claude", VideoProviderRoleStatus.Completed,
                $"{plan.Scenes?.Count ?? 0} 個鏡頭"));

            // 成本估算（Veo 3.1 = $0.40 USD/秒 720p）：提前顯示讓使用者知道大概花多少。
            // 快剪：N 個分鏡 × 4 秒/鏡頭；連貫：base 8 秒 + 每段延伸 7 秒；單段量化。
            {
                int planScenes = Math.Max(1, plan.Scenes?.Count ?? 1);
                int estShots = fastCut ? Math.Min(planScenes, VideoPlanBuilder.MaxFastCutShots) : 1;
                int estSecondsTotal = fastCut
                    ? estShots * VideoPlanBuilder.FastCutGenerateSeconds
                    : (planScenes > 1 && canExtend
                        ? 8 + VideoPlanBuilder.ExtendSegmentSeconds * (planScenes - 1)
                        : VideoPlanBuilder.QuantizeBaseSeconds(targetSeconds)); // Lite 不延伸→單段
                var tier = _main.GetVeoModelTier();
                double estUsd = estSecondsTotal * VeoModels.UsdPerSecond(tier);
                // 連貫模式走 I2V：多一張英雄圖（精確 9:16 或 16:9，高品質 ≈ US$0.25）當起始幀。
                double heroUsd = fastCut ? 0 : ModelCostEstimator.ImageCostUsd(VideoPlanBuilder.HeroStillSizeFor("9:16"), IllustrationQuality);
                estUsd += heroUsd;
                string modeLabel = fastCut ? $"快剪 {estShots} 鏡頭" : $"連貫 {estSecondsTotal} 秒";
                string heroNote = heroUsd > 0 ? $" + I2V 英雄圖 US${heroUsd:F2}" : "";
                plan.ProviderRoles.Add(VideoProviderRole.Of(
                    "預估成本", VeoModels.DisplayName(tier),
                    VideoProviderRoleStatus.Planned,
                    $"{modeLabel}・約生成 {estSecondsTotal} 秒影片素材{heroNote}・合計 US${estUsd:F2}（≈ {ModelCostEstimator.FormatTwd(estUsd)}）"));
            }

            // 關鍵畫面 / 風格：Claude 已產出 keyframe prompt 與風格定義；實際出圖為配角。
            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "關鍵畫面/風格", "Flux / Midjourney", VideoProviderRoleStatus.SkippedNoApi,
                "Claude 已產出 keyframe prompt 與風格定義；接上 Flux/Midjourney API 後可實際出圖"));

            // 2) 配角：旁白配音（ElevenLabs）、配樂（Suno）—— 缺 API 就略過。
            var narration = new ElevenLabsNarrationService();
            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "旁白配音", narration.ProviderName,
                narration.IsConfigured ? VideoProviderRoleStatus.Planned : VideoProviderRoleStatus.SkippedNoApi,
                narration.IsConfigured ? "已偵測到金鑰（配音接線為後續工作）" : narration.NotConfiguredReason));

            var music = new SunoMusicService();
            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "配樂", music.ProviderName,
                music.IsConfigured ? VideoProviderRoleStatus.Planned : VideoProviderRoleStatus.SkippedNoApi,
                music.IsConfigured ? "已偵測到金鑰（配樂接線為後續工作）" : music.NotConfiguredReason));

            var planItem = AgentWorkspaceBuilder.FromCapabilityData(
                workspace, node, "video-director(claude)", "video_plan", plan,
                isUserVisibleOverride: true, modelId: AiModels.Claude_Sonnet5);
            workspace.Add(planItem);

            string videoPrompt = string.IsNullOrWhiteSpace(plan.VideoPromptForGenerator)
                ? prompt
                : plan.VideoPromptForGenerator;

            // 每個 scene 一段 prompt（已前置風格）。連貫＝延伸串接；快剪＝各自獨立生成後剪接。
            var segmentPrompts = BuildSegmentPrompts(plan, videoStyle, videoPrompt, targetSeconds);

            // 快剪：鏡頭數＝分鏡數（夾上限），各鏡頭以 Veo base 4 秒生成（最便宜合法值），16:9 橫式。
            // 連貫：段數＝分鏡數，基底 8 秒 + 逐段延伸 7 秒。
            // Lite 不支援延伸 → 連貫強制單段（8 秒）。
            int segmentCount = fastCut
                ? Math.Min(segmentPrompts.Count, VideoPlanBuilder.MaxFastCutShots)
                : (canExtend ? segmentPrompts.Count : 1);
            bool multiSegment = !fastCut && segmentCount > 1;

            // Veo base 只接受 4 / 6 / 8 秒（離散，其他會 400）。
            // 快剪每鏡頭固定 4 秒；連貫多段基底 8 秒；連貫單段量化到最近合法值。
            int baseSeconds = fastCut
                ? VideoPlanBuilder.FastCutGenerateSeconds
                : (multiSegment ? 8 : VideoPlanBuilder.QuantizeBaseSeconds(targetSeconds));

            // 快剪用 16:9 橫式（預告片感）；連貫多段（要延伸）也用 16:9（延伸只接受 16:9 來源），
            // 否則第一段延伸必被拒（400 "Aspect ratio must be 16:9"）。連貫單段維持 9:16 直式。
            string aspectRatio = (fastCut || multiSegment) ? "16:9" : "9:16";
            string videoSize = (fastCut || multiSegment) ? "1280x720" : "720x1280";

            const string providerLabel = "Veo 3.1";

            // 「prompt → 影片請求」artifact，全程追蹤狀態 / 進度。
            var request = new VideoRequestPayload
            {
                Prompt = videoPrompt,
                DurationSeconds = baseSeconds,
                Size = videoSize,
                Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Queued)
            };
            var requestItem = AgentWorkspaceBuilder.FromCapabilityData(
                workspace, node, agentId, "video_request", request, isUserVisibleOverride: true);
            workspace.Add(requestItem);

            // 3) 影片產生器：Veo 3.1（唯一 provider）——依個人化選的檔位（標準 / Fast / Lite）。
            var veo = new VeoVideoService(_main.GetEffectiveVeoModel());

            // 影片是最貴的一項（依秒計價），整段先把估算額度佔住，跑完才釋放。
            // 影片是最貴的一項（依秒計價）。預留與預算檢查必須是同一個動作：
            // 分開做的話，正在獲准的這支影片自己就能把今日金額推過上限。
            using var videoReservation = SpendReservations.TryReserveWithinBudget(
                "影片生成",
                VeoModels.UsdPerSecondForModel(_main.GetEffectiveVeoModel()) * Math.Max(1, targetSeconds));

            if (videoReservation == null)
            {
                orchestration.MarkFailed("generate_video",
                    $"估算費用會超過今日預算上限（{SpendLedger.CommittedDisplay()}）");
                node.SetLoadingHint(null);
                return execution;
            }

            int curSegment = 1; // OnProgress 顯示用：目前在第幾段 / 第幾個鏡頭
            string unitWord = fastCut ? "鏡頭" : "段";
            void OnProgress(int percent, VideoGenerationStatus status)
            {
                request.ProgressPercent = percent;
                request.PollCount++;
                request.Status = VideoGenerationStatusText.ToStorageValue(status);
                string seg = segmentCount > 1 ? $"（第 {curSegment}/{segmentCount} {unitWord}）" : "";
                node.SetLoadingHint($"影片{VideoGenerationStatusText.ToLabel(status)}{seg} {percent}%");
            }

            byte[]? mp4 = null;
            string videoError = "";
            string jobRef = "";
            bool attempted = false;
            int completedExtends = 0;
            int producedShots = 0;   // 快剪：成功生成的鏡頭數
            int actualSeconds = 0;   // 成品實際秒數（兩種模式各自計算）
            // 已成功取回、但成品還沒寫進磁碟的 operation。寫檔成功才結案；中途當機時它們保持 pending，重啟可取回。
            var pendingVideoOps = new List<string>();

            // ── I2V：連貫模式先生成一張「調好色的英雄圖」當 Veo 起始幀 ──
            // 穩定拿到指定 look 的做法是「先有一張調好色的靜態圖 → 讓它動」。
            // 純 T2V 會被 Veo 自己的訓練慣性拉回乾淨現代感；先出一張在 look 裡的英雄圖當第一幀，
            // Veo 從對的畫面開始動，產出的每一格都保有那個色調 / 質感。任一步失敗則自動退回純 T2V。
            byte[]? heroImage = null;
            if (!fastCut && veo.IsConfigured)
                heroImage = await TryGenerateHeroStillAsync(node, plan, videoStyle, aspectRatio, ct);

            try
            {
                if (veo.IsConfigured)
                {
                    attempted = true;
                    request.Model = veo.Model;
                    request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Generating);

                    if (fastCut)
                    {
                        // ── 快剪：各鏡頭獨立生成（4 秒、16:9），再用 ffmpeg 各裁短後拼接 ──
                        double keepPerShot = Math.Clamp(
                            (double)targetSeconds / Math.Max(1, segmentCount),
                            1.5, VideoPlanBuilder.FastCutGenerateSeconds);

                        var clips = new List<(byte[] Mp4, double KeepSeconds)>();
                        for (int i = 0; i < segmentCount; i++)
                        {
                            curSegment = i + 1;
                            var shot = await veo.GenerateAsync(segmentPrompts[i], baseSeconds, aspectRatio, OnProgress, ct);
                            if (shot.Success && shot.Mp4Bytes.Length > 0)
                            {
                                clips.Add((shot.Mp4Bytes, keepPerShot));
                                pendingVideoOps.Add(shot.OperationName);
                                producedShots++;
                                if (string.IsNullOrWhiteSpace(jobRef)) jobRef = shot.OperationName;
                            }
                            else
                            {
                                // 單一鏡頭失敗不致命：記錄、跳過，繼續其他鏡頭（盡量湊齊快剪）。
                                videoError = shot.ErrorMessage;
                            }
                        }

                        if (clips.Count > 0)
                        {
                            node.SetLoadingHint($"ffmpeg 剪接 {clips.Count} 個鏡頭中");
                            var (ok, err, bytes) = await ffmpeg.TrimAndConcatAsync(clips, ct);
                            if (ok)
                            {
                                mp4 = bytes;
                                actualSeconds = (int)Math.Round(clips.Sum(c => c.KeepSeconds));
                            }
                            else
                            {
                                videoError = err;
                            }
                        }
                    }
                    else
                    {
                        // ── 連貫：基底段（8 秒，I2V 起始幀若有）→ 逐段延伸 +7 秒 ──
                        curSegment = 1;
                        var baseResult = await veo.GenerateAsync(segmentPrompts[0], baseSeconds, aspectRatio, OnProgress, ct, heroImage);

                        // I2V 健壯性：帶起始圖時若失敗（例如某些 Veo 檔位不支援圖生影而回 400），
                        // 不讓整支影片陣亡——用同一個 prompt 退回純 T2V 重試一次。
                        if (!baseResult.Success && heroImage != null)
                        {
                            plan.ProviderRoles.Add(VideoProviderRole.Of(
                                "I2V 起始幀", "Veo 3.1", VideoProviderRoleStatus.Failed,
                                $"此檔位可能不支援圖生影，已退回純文字生影（T2V）：{baseResult.ErrorMessage}"));
                            node.SetLoadingHint("圖生影不可用，改用文字生影重試");
                            baseResult = await veo.GenerateAsync(segmentPrompts[0], baseSeconds, aspectRatio, OnProgress, ct);
                        }

                        if (baseResult.Success)
                        {
                            mp4 = baseResult.Mp4Bytes;
                            jobRef = baseResult.OperationName;
                            pendingVideoOps.Add(baseResult.OperationName);
                            string lastUri = baseResult.VideoUri; // 延伸要用「前段影片 uri」串接

                            // 後續段：每段把「前一段影片的 uri」+ 新 prompt 送回 Veo 延伸 +7 秒，保持人物/場景連續。
                            for (int i = 1; i < segmentCount && mp4 != null; i++)
                            {
                                if (string.IsNullOrWhiteSpace(lastUri))
                                {
                                    videoError = "前段影片未回傳 uri，無法延伸。";
                                    break;
                                }

                                curSegment = i + 1;
                                var ext = await veo.ExtendAsync(lastUri, segmentPrompts[i], OnProgress, ct);
                                if (ext.Success)
                                {
                                    // 延伸成品已包含前面所有片段 → 前面的 operation 被取代，直接結案，只留最新一段待寫檔。
                                    foreach (var superseded in pendingVideoOps)
                                        VideoJobJournal.MarkDone(superseded);
                                    pendingVideoOps.Clear();
                                    pendingVideoOps.Add(ext.OperationName);
                                    mp4 = ext.Mp4Bytes;
                                    lastUri = ext.VideoUri;
                                    completedExtends++;
                                }
                                else
                                {
                                    // 延伸中斷：保留已接好的片段（部分成功），記錄原因後停止延伸。
                                    videoError = ext.ErrorMessage;
                                    break;
                                }
                            }

                            actualSeconds = baseSeconds + VideoPlanBuilder.ExtendSegmentSeconds * completedExtends;
                        }
                        else
                        {
                            videoError = baseResult.ErrorMessage;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Canceled);
                requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
                node.SetLoadingHint(null);
                orchestration.MarkFailed("generate_video", "已取消");
                throw;
            }
            finally
            {
                node.SetLoadingHint(null);
            }

            request.JobId = jobRef;
            if (actualSeconds <= 0) actualSeconds = baseSeconds; // 保險：未成功時退回基底秒數
            request.DurationSeconds = actualSeconds;

            // 把 Veo model id 補回 requestItem，讓決策窗模型欄看得到。
            if (!string.IsNullOrWhiteSpace(request.Model))
                requestItem.ModelId = request.Model;

            // 3a) Veo 未配置：只交付 Claude 計畫（仍算成功，因為導演計畫已產出）。
            if (!attempted)
            {
                plan.ProviderRoles.Add(VideoProviderRole.Of(
                    "影片", providerLabel, VideoProviderRoleStatus.SkippedNoApi, veo.NotConfiguredReason));
                request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Failed);
                request.ErrorMessage = veo.NotConfiguredReason;
                planItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(plan);
                requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
                orchestration.MarkSuccess("generate_video", "已產出 Claude 影片計畫（Veo 未配置）");
                return ReplaceExecutionText(execution,
                    BuildVideoPlanNote(plan) +
                    $"\n\nℹ Veo 未配置：{veo.NotConfiguredReason} 設定 GEMINI_API_KEY 後即可依此計畫生成影片。");
            }

            // 3b) 連基底段都失敗。
            if (mp4 == null || mp4.Length == 0)
            {
                plan.ProviderRoles.Add(VideoProviderRole.Of(
                    "影片", providerLabel, VideoProviderRoleStatus.Failed, videoError));
                request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Failed);
                request.ErrorMessage = videoError;
                planItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(plan);
                requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
                orchestration.MarkFailed("generate_video", videoError);
                return ReplaceExecutionText(execution,
                    BuildVideoPlanNote(plan) + $"\n\n⚠ 影片生成失敗（{providerLabel}）：{videoError}");
            }

            // 3c) 成功 → 寫檔。
            var generated = GeneratedFileWriter.WriteVideo(
                _main.GetGeneratedFilesDir(),
                title: string.IsNullOrWhiteSpace(plan.Title) ? ExtractReportTitle(prompt) : plan.Title,
                content: mp4,
                sourceSummary: $"{orchestrationPlan.PipelineId} / {providerLabel} / {request.Model}");

            workspace.Add(
                AgentWorkspaceBuilder.FromCapabilityData(
                    workspace, node, agentId, "generated_file", generated));

            if (generated.Success)
            {
                // 部分成功判定：連貫＝中途延伸失敗；快剪＝有鏡頭生成失敗（湊不齊原訂鏡頭數）。
                bool partial = fastCut
                    ? producedShots < segmentCount
                    : multiSegment && completedExtends < (segmentCount - 1);

                string segDetail = fastCut
                    ? $"{producedShots}/{segmentCount} 鏡頭快剪・約 {actualSeconds} 秒"
                    : multiSegment
                        ? $"{completedExtends + 1}/{segmentCount} 段・約 {actualSeconds} 秒"
                        : $"約 {actualSeconds} 秒";

                plan.ProviderRoles.Add(VideoProviderRole.Of(
                    "影片", providerLabel,
                    partial ? VideoProviderRoleStatus.Failed : VideoProviderRoleStatus.Completed,
                    partial ? $"{segDetail}（部分鏡頭中斷：{videoError}）" : $"{generated.FileName}（{segDetail}）"));
                request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Completed);
                request.ProgressPercent = 100;
                request.FilePath = generated.FilePath;
                planItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(plan);
                requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
                orchestration.MarkSuccess("generate_video", generated.FileName);

                // 成品已落地 → 相關 operation 結案（重啟不再提示取回）。
                // 成本已在各段送出時由 VeoVideoService 逐筆入帳（快剪按「生成素材秒數」而非剪完秒數）。
                foreach (var op in pendingVideoOps)
                    VideoJobJournal.MarkDone(op);

                string modeNote = fastCut ? "（快剪）" : "";
                string note = partial
                    ? $"\n\n✅ 影片已生成{modeNote}：{generated.FileName}（{segDetail}）\n⚠ 部分鏡頭未完成：{videoError}"
                    : $"\n\n✅ 影片已生成{modeNote}：{generated.FileName}（{segDetail}）";

                // 使用者想要快剪但缺 ffmpeg → 已自動降級連貫模式，提示如何啟用快剪。
                if (fastCutWanted && !fastCut)
                    note += $"\n\nℹ 偵測到你要的是快剪/預告片節奏，但本機沒有 ffmpeg，已改用連貫長鏡頭。{ffmpeg.NotAvailableReason}";

                // Lite 不支援延伸 → 連貫長片被截成單段 8 秒，提示如何做更長。
                if (liteNoExtend)
                    note += $"\n\nℹ 你要的是約 {targetSeconds} 秒的連貫長片，但目前用的是 Veo 3.1 Lite——Lite 不支援影片延伸，只生成了 8 秒。要更長請到設定把影片模型切到 Fast 或標準版，或改用快剪（預告片/混剪）走獨立鏡頭剪接。";

                return ReplaceExecutionText(execution, BuildVideoPlanNote(plan) + note);
            }

            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "影片", providerLabel, VideoProviderRoleStatus.Failed, generated.ErrorMessage));
            request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Failed);
            request.ErrorMessage = generated.ErrorMessage;
            planItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(plan);
            requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
            orchestration.MarkFailed("generate_video", generated.ErrorMessage);
            return ReplaceExecutionText(execution,
                BuildVideoPlanNote(plan) + $"\n\n⚠ 影片寫檔失敗：{generated.ErrorMessage}");
        }

        // 從分鏡組出「每段給 Veo 的英文 prompt」。每段一律前置生效風格，避免延伸過程風格漂移。
        // 段數以分鏡為準（導演已依長度決定），夾在 [1, 21]；無分鏡時退回單段（整體 video_prompt）。
        private static List<string> BuildSegmentPrompts(
            VideoPlanPayload plan, string style, string videoPrompt, int targetSeconds)
        {
            // 送進 Veo 的是「精簡視覺標籤」而非完整藝評：影片模型只認得具體名詞，
            // 整段抽象描述＋NOT 清單會稀釋訊號，所以這裡蒸餾成短而具體的正面標籤前置。
            string veoTags = VideoStyle.RenderTagsFor(style);
            string styleHead = string.IsNullOrWhiteSpace(veoTags) ? "" : veoTags.Trim() + "\n\n";

            var scenes = (plan.Scenes ?? new List<VideoScenePayload>())
                .Where(s => s != null)
                .ToList();

            var prompts = new List<string>();

            if (scenes.Count == 0)
            {
                string single = VideoStyle.SanitizeForVideoModel(videoPrompt);
                prompts.Add(styleHead + (string.IsNullOrWhiteSpace(single) ? "Cinematic short video." : single) + SettleTail);
                return prompts;
            }

            // 段數上限：分鏡數與「依目標秒數換算」取較大者再夾上限，確保使用者要長片時段數夠。
            int wanted = Math.Max(scenes.Count, VideoPlanBuilder.SegmentsForDuration(targetSeconds));
            int segCount = Math.Min(Math.Min(wanted, VideoPlanBuilder.MaxSegments), Math.Max(1, scenes.Count));

            for (int i = 0; i < segCount; i++)
            {
                var s = scenes[i];
                string body = !string.IsNullOrWhiteSpace(s.SegmentVideoPrompt) ? s.SegmentVideoPrompt
                            : !string.IsNullOrWhiteSpace(s.KeyframePrompt) ? s.KeyframePrompt
                            : !string.IsNullOrWhiteSpace(s.Visual) ? s.Visual
                            : videoPrompt;

                // 鏡頭設計欄導演常寫中文（給使用者看的）；segment_prompt 本身已描述運鏡，
                // 中文不送 Veo——混進英文 prompt 只會增加模型在畫面上「寫字」的機率。
                if (!string.IsNullOrWhiteSpace(s.Camera) && !ContainsCjk(s.Camera))
                    body = (body ?? "").Trim() + " Camera: " + s.Camera.Trim() + ".";

                body = VideoStyle.SanitizeForVideoModel(body);
                string composed = styleHead + (string.IsNullOrWhiteSpace(body) ? "Continue the scene naturally." : body.Trim());

                // 收尾保險：只對「最後一段」（單段時即唯一段）追加 settle 尾巴，
                // 強制動態減速→完全靜止→鏡頭定住，避免結尾停在動作半途像被硬切。
                // 中間延伸段不加：延伸是接前一段最後一幀續生，中間若靜止會讓後續延伸動不起來。
                if (i == segCount - 1)
                    composed += SettleTail;

                prompts.Add(composed);
            }

            return prompts;
        }

        private static bool ContainsCjk(string text)
            => text.Any(c => c >= '　' && c <= '鿿' || c >= '豈' && c <= '﫿' || c >= '＀' && c <= '￯');

        // 送進 Veo 的單段收尾指令：把「停下來」當成動作的一部分，用模型真的能渲染的物理收束
        // （動態減速→歸於靜止→鏡頭鎖定），而非影片模型做不出的 fade out。導演層也會寫，這裡是雙保險。
        private const string SettleTail =
            " In the final 2-3 seconds all motion gradually decelerates and comes to a complete, settled rest: the subject holds still and the camera locks into a steady static frame, ending the shot on a calm, fully-resolved freeze rather than stopping mid-movement. No fade out.";

        // I2V：用 gpt-image-2 生成一張「調到目標色調」的英雄圖，當 Veo 連貫基底段的起始幀。
        // 失敗（缺 OPENAI_API_KEY / 出圖失敗 / 任何例外）一律回 null → 呼叫端自動退回純 T2V，不中斷影片。
        private async Task<byte[]?> TryGenerateHeroStillAsync(
            INodeContext node, VideoPlanPayload plan, string videoStyle, string aspectRatio, CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("I2V 英雄圖");
            try
            {
                var scene0 = (plan.Scenes ?? new List<VideoScenePayload>()).FirstOrDefault(s => s != null);
                string keyframe = scene0?.KeyframePrompt ?? "";
                if (string.IsNullOrWhiteSpace(keyframe))
                    keyframe = scene0?.Visual ?? "";
                if (string.IsNullOrWhiteSpace(keyframe))
                    keyframe = plan.VideoPromptForGenerator ?? "";
                if (string.IsNullOrWhiteSpace(keyframe))
                    return null;

                // 英雄圖 prompt：色調標籤前置 + 導演關鍵畫面，讓「這張靜態圖」本身就在 look 裡。
                string tags = VideoStyle.RenderTagsFor(videoStyle);
                // 圖片模型（gpt-image-2）懂否定句，這裡可以明講不要黑邊與文字（影片模型那邊則不行，見 VideoStyle）。
                string heroPrompt = $"{tags}\n\n{VideoStyle.SanitizeForVideoModel(keyframe)}\n\n" +
                    "A single still cinematic frame. The photograph fills the entire canvas edge to edge, " +
                    "with no black bars, borders, frames or letterboxing. No text, captions, watermark or logo.";

                // 起始幀必須精確等於影片比例，否則 Veo 會補黑邊、再在黑邊上長出假字幕（見 HeroStillSizeFor）。
                string size = VideoPlanBuilder.HeroStillSizeFor(aspectRatio);

                node.SetLoadingHint("正在生成英雄圖（I2V 起始幀）");
                var img = new OpenAIImageService(); // 缺 OPENAI_API_KEY 會丟例外 → 下方 catch 退回 T2V
                var result = await img.GenerateAsync(heroPrompt, size, ct, IllustrationQuality);
                node.SetLoadingHint(null);

                if (result.Success && result.PngBytes.Length > 0)
                {
                    plan.ProviderRoles.Add(VideoProviderRole.Of(
                        "I2V 起始幀", "gpt-image-2", VideoProviderRoleStatus.Completed,
                        $"已生成調色英雄圖（{size}）作為 Veo 起始幀"));
                    return result.PngBytes;
                }

                plan.ProviderRoles.Add(VideoProviderRole.Of(
                    "I2V 起始幀", "gpt-image-2", VideoProviderRoleStatus.Failed,
                    $"英雄圖生成失敗，改用純文字生影（T2V）：{result.ErrorMessage}"));
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                node.SetLoadingHint(null);
                plan.ProviderRoles.Add(VideoProviderRole.Of(
                    "I2V 起始幀", "gpt-image-2", VideoProviderRoleStatus.SkippedNoApi,
                    $"未生成英雄圖（改用 T2V）：{ex.Message}"));
                return null;
            }
        }

        private async Task<VideoPlanPayload> BuildVideoPlanAsync(
            INodeContext node, string prompt, int targetSeconds, string stylePrompt, string? treatment,
            VideoCutMode cutMode, CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("影片導演企劃");
            string directorPrompt = VideoPlanBuilder.BuildDirectorPrompt(prompt, targetSeconds, stylePrompt, treatment, cutMode);

            // 手動模式由節點選的模型當導演；不適合時直接照使用者原文拍。
            string? directorModel = _scope.HelperModel(AiModels.Claude_Sonnet5);
            if (directorModel == null)
                return VideoPlanBuilder.Parse("", prompt, targetSeconds);

            var directorDecision = new NodeExecutionDecision
            {
                RequestedAgentId = "general-agent",
                ActualAgentId = "general-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(directorModel),
                ModelId = AiModelHelper.NormalizeNodeModel(directorModel),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = "Video Director (Claude)",
                ResolverReason = "Claude 產出影片劇本 / 分鏡 / 旁白 / 鏡頭計畫。",
                StatusLabel = "Auto",
                ForceSingleModel = true
            };

            try
            {
                var r = await _executeWithFallbackAsync(node, directorPrompt, directorDecision, null, false, ct);
                return VideoPlanBuilder.Parse(r.IsSuccess ? r.Text : "", prompt, targetSeconds);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return VideoPlanBuilder.Parse("", prompt, targetSeconds);
            }
        }

        private static string BuildVideoPlanNote(VideoPlanPayload plan)
        {
            var sb = new System.Text.StringBuilder();
            // 導演不一定是 Claude 了（手動模式由節點選的模型擔任）；是誰做的看決策窗的「參與」欄。
            sb.Append("\n\n【影片計畫】");

            if (!string.IsNullOrWhiteSpace(plan.Title))
                sb.Append($"\n片名：{plan.Title}");
            if (!string.IsNullOrWhiteSpace(plan.Logline))
                sb.Append($"\n概念：{plan.Logline}");
            if (!string.IsNullOrWhiteSpace(plan.StyleDefinition))
                sb.Append($"\n風格：{plan.StyleDefinition}");
            if (!string.IsNullOrWhiteSpace(plan.MusicBrief))
                sb.Append($"\n配樂：{plan.MusicBrief}");

            var scenes = plan.Scenes ?? new System.Collections.Generic.List<VideoScenePayload>();
            sb.Append($"\n分鏡：{scenes.Count} 個鏡頭，約 {plan.TotalDurationSeconds} 秒");

            // 逐鏡頭顯示完整劇本（旁白 / 畫面 / 運鏡），讓使用者看得到導演寫的詳細內容。
            int idx = 1;
            foreach (var s in scenes)
            {
                if (s == null) continue;
                sb.Append($"\n\n　鏡頭 {idx}");
                if (s.DurationSeconds > 0) sb.Append($"（約 {s.DurationSeconds} 秒）");
                if (!string.IsNullOrWhiteSpace(s.Visual))
                    sb.Append($"\n　· 畫面：{s.Visual.Trim()}");
                if (!string.IsNullOrWhiteSpace(s.Camera))
                    sb.Append($"\n　· 運鏡：{s.Camera.Trim()}");
                if (!string.IsNullOrWhiteSpace(s.Narration))
                    sb.Append($"\n　· 旁白：{s.Narration.Trim()}");
                idx++;
            }

            sb.Append("\n\n工具分工：");
            foreach (var role in plan.ProviderRoles)
                sb.Append($"\n· {role.Role}：{role.Provider}（{VideoProviderRoleStatus.ToLabel(role.Status)}）");

            return sb.ToString();
        }
    }
}
