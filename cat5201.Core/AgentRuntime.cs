using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    public sealed partial class AgentRuntime
    {
        private const int MaxCodeSnapshotPromptCharsTotal = 24000;
        private const int MaxCodeSnapshotPromptCharsPerFile = 18000;
        private const int MaxCodeSourceOutlineCharsPerFile = 2500;
        private const int MaxRepairPreviousOutputChars = 2500;
        private const int MaxRepairDiffChars = 14000;

        private readonly IAgentHost _main;   // #3：抽服務層第一刀——依賴抽象而非具體 MainWindow。

        // 這次執行（或這次預覽操作）可以呼叫哪些模型。
        // 2026-10-29 使用者指示：手動選擇模型時，不該出現選擇之外的模型參與；
        // 文件的撰寫者與配圖由節點選的模型擔任，或由使用者在確認框裡親手改選。
        // AgentRuntime 每次執行／每次預覽操作都是新的實例，所以這個欄位不會跨執行殘留。
        private ModelScope _scope = ModelScope.Auto;

        /// <summary>預覽裡的單頁操作不經過 ExecuteAsync，要自己依目前模式與節點的模型建立範圍。</summary>
        private void EnsureScopeFor(INodeContext node)
        {
            _scope = _main.IsAutoModelSelectionEnabled()
                ? ModelScope.Auto
                : ModelScope.Manual(_main.GetNodeSelectedModel(node));
        }

        /// <summary>這次執行（最外層）的第一層語意判斷結果；製作方式、明確不要圖、檔名主題都從這裡讀。</summary>
        private OutputIntent? _requestIntent;

        // 這次執行可不可以退回關鍵字（只有規則式 Auto 可以，2026-11-05 使用者定的模式規則）。
        // 產出階段（藝術方向等）沿用同一個結果，不再各自讀全域開關。
        private bool _allowKeywordFallback;

        // 這次要不要把「現在的日期」交給模型（需要時才有值，見 NeedsCurrentDate）。主回覆、搜尋、
        // 以及之後的文件／資料分析／簡報撰寫都用同一刻，不各自讀時鐘。
        private DateTimeOffset? _currentDate;

        /// <summary>
        /// 這一次實際用的文件引擎：使用者明講「快速版」或「最佳品質」就照講的做，沒講才用個人化的設定
        /// （2026-11-03：寫明「快速版」的簡報照樣走了最佳品質）。由語意判斷決定，不比對關鍵字。
        /// </summary>
        private DocumentEngine EffectiveDocumentEngine()
            => RequestedQuality.Apply(_requestIntent?.Quality ?? RequestedQuality.Level.None, _main.GetDocumentEngine());

        /// <summary>
        /// 檔名用的主題：語意判斷抽出的主題優先（「BMI」），沒有才退回舊的刪字清理。
        /// 2026-11-03：「做一份 3 頁關於BMI簡報（快速版、不配圖）」的檔名成了「BMI（快速版、不）」。
        /// </summary>
        private string DeriveCleanTitle(string? userInput, string fallback)
        {
            // 跟存檔自動命名同一套規則與整理（ShortTitle）。
            string topic = ShortTitle.Normalize(_requestIntent?.Topic);
            return topic.Length > 0 ? topic : CleanTitleFromText(userInput, fallback);
        }

        private string ExtractReportTitle(string userInput) => DeriveCleanTitle(userInput, "報告");

        /// <summary>Auto 模式下（使用者沒改）文件會由誰寫——確認框的預設值要跟實際行為一致。</summary>
        private string AutoDocumentAuthor()
            => EffectiveDocumentEngine() == DocumentEngine.ClaudeSkills
                ? DocumentEngineHelper.ResolveSkillsModel(AiAutoCostPolicy.BlockOpus)
                : PresentationEngineHelper.ToAuthorModelId(_main.GetPresentationEngine());

        /// <summary>
        /// 這次要不要把現在的日期交給模型。語意判斷說需要、或這次有搜尋（要靠日期分辨結果新舊）就給；
        /// 語意判斷拿不到時也給——不知道需不需要時，多給只多幾十個 token，少給可能答錯。不用關鍵字。
        /// </summary>
        /// <summary>
        /// 產出物的藝術方向：語意判斷有結果就用它；沒有時只有規則式 Auto 退回主題文字的關鍵字規則，
        /// 其他模式用現代簡約（不用關鍵字）。2026-11-06：「介紹京都」一個關鍵字都沒中，被套上商業科技藍。
        /// </summary>
        internal static string ResolveArtDirectionId(OutputIntent? intent, bool allowKeywordFallback, string? topicText)
        {
            if (DeckArtDirection.IsKnownId(intent?.ArtDirection))
                return intent!.ArtDirection.Trim().ToLowerInvariant();
            return allowKeywordFallback ? DeckArtDirection.ForTopic(topicText).Id : "modern";
        }

        /// <summary>這次請求的藝術方向（見 ResolveArtDirectionId）。</summary>
        private string ArtIdFor(string? topicText)
            => ResolveArtDirectionId(_requestIntent, _allowKeywordFallback, topicText);

        /// <summary>
        /// 文件、資料分析、簡報撰寫這些「另外組提示」的步驟前面要加的日期段落；不需要日期時是空字串。
        /// 2026-11-06：主回覆已經有日期，但交給 Claude 做的簡報與報告沒有——「做一份本週市場簡報」會遇到跟天氣一樣的問題。
        /// </summary>
        internal static string DatePrefix(DateTimeOffset? currentDate)
            => currentDate.HasValue ? CurrentDateBlock.Build(currentDate.Value) + "\n\n" : "";

        internal static bool NeedsCurrentDate(OutputIntent? intent, AgentCapabilityExecutionPlan plan)
        {
            var needs = intent?.Capabilities;
            if (needs == null)
                return true;
            return needs.NeedsCurrentDate || plan.RequiresFreshFacts;
        }

        private GenerationChoices BuildGenerationChoices(OrchestrationTaskType taskType, OutputIntent? intent)
        {
            bool wantsDeck = taskType == OrchestrationTaskType.Presentation || (intent?.WantsPresentation ?? false);
            bool wantsReport = intent?.WantsReport ?? false;
            bool wantsTable = intent?.WantsTable ?? false;
            if (!wantsDeck && !wantsReport && !wantsTable)
                return GenerationChoices.None;

            // Auto：撰寫者與配圖已經在個人化設定寫好，確認框不再問一次（2026-10-29 第二輪手測）。
            // 只有手動選了模型，「所選模型以外的模型要不要參與」才需要使用者在這裡親手決定。
            if (!_scope.IsManual)
                return GenerationChoices.None;

            // 撰寫者選單：深度研究與 Sonar 不寫文件——那是搜尋模型，不是寫作模型
            // （深度研究一次還以美元計）。選單攤開成膠囊後，多一個不該選的選項就多一份干擾。
            var options = AiModelRegistry.Available
                .Where(m => !m.IsDeepResearch)
                .Where(m => AiModelHelper.GetProviderKind(m.Id) is not (AiProviderKind.PerplexitySonar or AiProviderKind.PerplexityTool))
                .Select(m => m.Id)
                .ToList();

            string defaultAuthor = _scope.DocumentAuthorModel(AutoDocumentAuthor());
            if (!options.Contains(defaultAuthor, StringComparer.OrdinalIgnoreCase))
                options.Insert(0, defaultAuthor);

            return new GenerationChoices
            {
                IncludesDocuments = true,
                DefaultAuthorModelId = defaultAuthor,
                AuthorOptions = options,
                ShowIllustrationOption = wantsDeck || wantsReport,   // 表格不配圖
                // 使用者明講不要圖（語意判斷），確認框預設就不勾。
                IllustrationsDefault = _scope.IllustrationsAllowed() && !(intent?.DeclinesIllustrations ?? false),
                BestQualityEnabled = EffectiveDocumentEngine() == DocumentEngine.ClaudeSkills,
            };
        }

        /// <summary>載入提示與決策窗用的撰寫者名稱：使用者改選過就顯示實際模型，而不是個人化引擎的名字。</summary>
        private static string AuthorDisplayName(PresentationEngine engine, string authorModelId)
            => string.Equals(authorModelId, PresentationEngineHelper.ToAuthorModelId(engine), StringComparison.OrdinalIgnoreCase)
                ? PresentationEngineHelper.ToDisplayName(engine)
                : AiModelRegistry.Find(authorModelId)?.DisplayName ?? authorModelId;
        private readonly IExecutionDecisionResolver _decisionResolver;
        private readonly Func<INodeContext, string, NodeExecutionDecision, Action<string>?, bool, CancellationToken, Task<AiFallbackExecutionResult>> _executeWithFallbackAsync;
        private readonly IDecisionFinalizer _executionFinalizer;
        private readonly AgentDelegationPlanner _delegationPlanner = new();

        public AgentRuntime(
            IAgentHost main,
            IExecutionDecisionResolver decisionResolver,
            Func<INodeContext, string, NodeExecutionDecision, Action<string>?, bool, CancellationToken, Task<AiFallbackExecutionResult>> executeWithFallbackAsync,
            IDecisionFinalizer executionFinalizer)
        {
            _main = main;
            _decisionResolver = decisionResolver;
            // 每一次經過執行器的呼叫都檢查「有沒有換過模型」：所選模型確定失敗時可以換，
            // 但一定要告訴使用者（2026-10-29 使用者指示）。包在這裡，所有步驟就不會漏掉。
            _executeWithFallbackAsync = async (node, prompt, decision, onDelta, stream, ct) =>
            {
                RecordStepInput(UsageMeter.CurrentPurpose, decision.ModelId, prompt);
                var result = await executeWithFallbackAsync(node, prompt, decision, onDelta, stream, ct);
                RecordModelSwitch(decision, result);
                return result;
            };
            _executionFinalizer = executionFinalizer;
        }

        // 放在答案最上方的說明：「所選模型失敗、改由其他模型完成」，以及「數字可能有單位錯誤」。
        // 封面圖與內容頁配圖會同時跑，所以加鎖。
        private readonly List<string> _topNotices = new();

        private void AddTopNotice(string? notice)
        {
            if (string.IsNullOrWhiteSpace(notice)) return;
            lock (_topNotices) _topNotices.Add(notice);
        }

        /// <summary>
        /// 只有「使用者選的模型」被換掉才需要講：手動模式的每一步，以及使用者在確認框改選的撰寫者。
        /// Auto 模式本來就由系統挑模型，換模型是它的本分，不需要每次都講。
        /// </summary>
        private void RecordModelSwitch(NodeExecutionDecision decision, AiFallbackExecutionResult result)
        {
            if (decision == null) return;

            bool userChose = _scope.IsManual ||
                (_scope.ApprovedAuthorModelId != null &&
                 string.Equals(_scope.ApprovedAuthorModelId, decision.ModelId, StringComparison.OrdinalIgnoreCase));
            if (!userChose) return;

            AddTopNotice(ModelSwitchNotice.Build(decision.ModelId, result, UsageMeter.CurrentPurpose));
        }

        // ── 各步驟實際收到了什麼（2026-10-29）──
        // 子代理（平行代理）跑在同一個實例上，所以它們收到的內容也會記在這裡——
        // 這正好回答「主控把任務分給子代理時，是給原文還是自己的摘要」：看那幾步有沒有「含原始要求 ✓」。
        private string _originalRequest = "";
        private readonly List<(StepInputRecord Record, string FullText)> _stepInputs = new();

        private void RecordStepInput(string? step, string? modelId, string? input)
        {
            var record = StepInputRecord.From(step, modelId, input, _originalRequest);
            lock (_stepInputs) _stepInputs.Add((record, input ?? ""));
        }

        /// <summary>
        /// 完整內容寫成一個檔案（每一步的用途、模型、雜湊與全文），專案檔只存摘要。
        /// 寫不出來不影響這次執行，只是少了可對照的全文。
        /// </summary>
        private string WriteStepInputLog(INodeContext node)
        {
            List<(StepInputRecord Record, string FullText)> steps;
            lock (_stepInputs) steps = _stepInputs.ToList();
            if (steps.Count == 0) return "";

            try
            {
                string dir = System.IO.Path.Combine(_main.GetAttachmentsRootDir(), "_steplog");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir,
                    $"{DateTime.Now:yyyyMMdd_HHmmss}_{node.Id.ToString("N").Substring(0, 8)}.txt");

                var sb = new System.Text.StringBuilder();
                sb.Append("# 各步驟實際收到的內容\n");
                sb.Append("原始要求的雜湊：").Append(StepInputRecord.ShortHash(_originalRequest)).Append("\n\n");
                for (int i = 0; i < steps.Count; i++)
                {
                    var r = steps[i].Record;
                    sb.Append("════════ 第 ").Append(i + 1).Append(" 步：").Append(r.Step)
                      .Append("｜").Append(r.ModelId).Append("｜").Append(r.Chars).Append(" 字｜")
                      .Append(r.ContainsOriginalRequest ? "含原始要求" : "未完整包含原始要求")
                      .Append("｜").Append(r.Sha256).Append(" ════════\n")
                      .Append(steps[i].FullText).Append("\n\n");
                }

                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
                return path;
            }
            catch (Exception ex)
            {
                AppLog.Warn("AgentRuntime", "各步驟內容檔寫入失敗（不影響執行）", ex);
                return "";
            }
        }

        /// <summary>協作中某一個模型的單次呼叫：只准用這個模型，不 fallback 到別人。</summary>
        private static NodeExecutionDecision CollaboratorDecision(NodeExecutionDecision baseDecision, string modelId)
            => new()
            {
                RequestedAgentId = baseDecision.RequestedAgentId,
                ActualAgentId = baseDecision.ActualAgentId,
                RequestedModelId = AiModelHelper.NormalizeNodeModel(modelId),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                ActualModelId = "",
                TaskMode = baseDecision.TaskMode,
                ResolverLabel = "Collaboration",
                ResolverReason = "協作模式：使用者指定的模型之一",
                StatusLabel = "Collaboration",
                ForceSingleModel = true,
            };

        private AiFallbackExecutionResult WithTopNotices(AiFallbackExecutionResult execution)
        {
            string banner;
            lock (_topNotices) banner = ModelSwitchNotice.Combine(_topNotices);
            return banner.Length == 0 || !execution.IsSuccess
                ? execution
                : ReplaceExecutionText(execution, banner + execution.Text);
        }

        public async Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (request.Agent == null)
                throw new InvalidOperationException("AgentExecutionRequest.Agent 不可為 null。");

            string topText = request.TopText ?? "";

            // 協作模式：使用者指定了這次由哪幾個模型一起回答。只有最外層才算（子代理不協作）。
            var collaborators = request.DelegationDepth == 0
                ? ModelCollaboration.Normalize(request.CollaborationModels)
                : Array.Empty<string>();
            bool collaborating = collaborators.Count >= ModelCollaboration.MinModels;

            // 這個節點的原始要求——之後每一步都拿它比對「有沒有完整收到」。子代理不覆寫它。
            if (request.DelegationDepth == 0)
                _originalRequest = topText;
            if (request.Workspace == null)
            {
                throw new InvalidOperationException(
                    "AgentExecutionRequest.Workspace 不可為 null。Parallel / Delegate flow 必須共享同一個 workspace instance。");
            }

            var workspace = request.Workspace;
            var node = request.Node ?? throw new InvalidOperationException("AgentExecutionRequest.Node 不可為 null。");
            var parallelRunner = new AgentParallelRunner(ExecuteAsync);
            if (string.IsNullOrWhiteSpace(topText))
            {
                return new AgentExecutionResult
                {
                    Decision = new NodeExecutionDecision
                    {
                        RequestedAgentId = request.Agent.Id,
                        ActualAgentId = request.Agent.Id,
                        ResolverLabel = "AgentRuntime",
                        StatusLabel = _main.IsAutoModelSelectionEnabled() ? "Auto" : "Manual"
                    },
                    Execution = new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = "",
                        ActualModelId = request.Agent.DefaultModelId
                    }
                };
            }

            var capabilityTrace = new List<AgentCapabilityTraceItem>();
            var delegationTrace = new List<AgentDelegationTraceItem>();
            var capabilityData = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // 1. resolve decision
            var decision = await _decisionResolver.ResolveAsync(
                node,
                topText,
                request.CancellationToken);

            if (string.IsNullOrWhiteSpace(decision.RequestedAgentId))
                decision.RequestedAgentId = request.Agent.Id;

            if (string.IsNullOrWhiteSpace(decision.ActualAgentId))
                decision.ActualAgentId = request.Agent.Id;

            if (request.ForceAgentProfile)
            {
                var forcedAgent = request.Agent;

                var profile = _agentProfileResolver.Resolve(
                    forcedAgent,
                    preferredModelId: forcedAgent.DefaultModelId,
                    preferredTaskMode: decision.TaskMode);

                string normalizedRuntime = AiModelHelper.NormalizeNodeModel(profile.RuntimeModelId);
                string forcedRuntimeModel = (_main.IsAutoModelSelectionEnabled() &&
                    AiAutoCostPolicy.TryEnforceUserBlock(normalizedRuntime, out var blockedModel))
                    ? blockedModel
                    : normalizedRuntime;

                decision.RequestedAgentId = forcedAgent.Id;
                decision.ActualAgentId = forcedAgent.Id;

                decision.RequestedModelId = AiModelHelper.NormalizeNodeModel(profile.RuntimeModelId);
                decision.ModelId = forcedRuntimeModel;
                decision.ActualModelId = "";
                decision.ForceSingleModel = true;
                decision.ResolverLabel += " + Forced Agent Profile";
                decision.ResolverReason =
                    $"Delegated agent forced profile: {forcedAgent.Id} / model: {profile.RuntimeModelId}" +
                    (string.Equals(profile.RuntimeModelId, forcedRuntimeModel, StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : $" / Auto cost policy: {profile.RuntimeModelId} → {forcedRuntimeModel}");
            }

            var runtimeAgent = AgentRegistry.Get(decision.ActualAgentId);
            // 協作模式下不跑 Auto 的平行代理：使用者已經指定了由哪幾個模型回答。
            bool allowAgentFirstAutomation =
                !collaborating &&
                _main.IsAutoModelSelectionEnabled() &&
                _main.IsAdvancedAutoResolverEnabled();

            ApplyAutoCostPolicyToDecision(decision);

            _main.SetLiveDecisionResolving(node, decision);
            // 2. capability layer
            string capabilityAugmentedText = topText;

            var capabilityContext = new AgentExecutionContext
            {
                Node = node,
                Agent = runtimeAgent,
                TopText = topText,
                TaskMode = decision.TaskMode,
                Attachments = _main.GetAttachmentsForNode(node),
                AttachmentsRootDir = _main.GetAttachmentsRootDir(),
                // 手動模式：能力層先用使用者選的模型（2026-10-29 使用者指示）。
                // 協作模式也算「使用者選定」：幫手只用協作名單裡的第一個模型，不讓名單外的模型進來。
                Scope = _scope = collaborating
                    ? ModelScope.Manual(collaborators[0])
                    : _main.IsAutoModelSelectionEnabled()
                        ? ModelScope.Auto
                        : ModelScope.Manual(decision.ModelId)
            };

            // 要不要查即時資料：看使用者自己寫的要求，不看上游貼進來的內容（見 UpstreamMerge.OwnInstruction）。
            string intentText = string.IsNullOrWhiteSpace(request.OwnInstruction) ? topText : request.OwnInstruction!;

            bool hasImageAttachment = capabilityContext.Attachments != null &&
                capabilityContext.Attachments.Any(a => a != null &&
                    ((!string.IsNullOrEmpty(a.MimeType) && a.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                     || a.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || a.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || a.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || a.FileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)));

            // 使用者可以在確認框裡授權「這一次超過今日上限」；範圍限這一次執行。
            using var budgetScope = SpendReservations.BeginOverBudgetScope();

            // 輸出判斷（§6）到這一步才需要結果。NodeService 已經先把它發出去了，
            // 這段期間「自動選模型」已經跑完了，所以多半已經回來，await 不用再等一次來回。
            var outputIntent = request.OutputIntent;
            if (outputIntent == null && request.OutputIntentTask != null)
            {
                try { outputIntent = await request.OutputIntentTask.ConfigureAwait(true); }
                catch (Exception ex) { AppLog.Warn("AgentRuntime", "輸出判斷失敗，改用關鍵字", ex); }
            }
            if (request.DelegationDepth == 0)
                _requestIntent = outputIntent;

            // 能力規劃（要不要搜尋／推理／寫程式／拆步驟）用同一次語意判斷的結果，不另外呼叫模型。
            // 放在收意圖之後：規劃本身是純計算，挪過來不會多等。
            //
            // 2026-11-05 使用者指示，三種模式各自的規則：
            //  - 手動（含協作，模型都是使用者選的）：只在語意判斷說要即時資料時搜尋，其他能力一律不加；
            //    搜尋結果由使用者選的模型讀、寫答案（跟 ChatGPT 網頁版一樣）。語意判斷拿不到就不搜尋。
            //    （2026-11-05 使用者先選了「完全不搜尋」，實測手動問天氣只得到「無法取得即時天氣」，改成這樣。）
            //  - 規則式 Auto（Auto＋關閉 API）：語意判斷優先，拿不到才退回關鍵字；只在需要即時資料時跑能力層。
            //    關鍵字判斷只留在這個模式。
            //  - API Auto：只看語意判斷；拿不到就不加額外能力（不退回關鍵字）。
            //
            // 模式看「這次決策本身是在哪個模式下做的」（跟執行紀錄的「模式」欄位同一個來源），不要在這裡再讀一次全域開關：
            // 這段要等意圖判斷回來，中間隔了幾秒。2026-11-05 實測：使用者在這幾秒內切到手動去跑下一個節點，
            // 規則式 Auto 的節點就被當成手動，沒有搜尋，回答「無法取得即時天氣」。
            bool decidedAsManual = string.Equals(decision.StatusLabel, "Manual", StringComparison.OrdinalIgnoreCase);
            bool decidedAsRuleAuto = string.Equals(decision.StatusLabel, "Rule Auto", StringComparison.OrdinalIgnoreCase);
            bool decidedAsApiAuto = decision.UsedApiResolver;
            bool knownMode = decidedAsManual || decidedAsRuleAuto || decidedAsApiAuto;

            bool manualCapabilities = collaborating ||
                (knownMode ? decidedAsManual : !_main.IsAutoModelSelectionEnabled());
            bool ruleBasedAuto = !manualCapabilities &&
                (knownMode ? decidedAsRuleAuto : !_main.IsAdvancedAutoResolverEnabled());
            if (request.DelegationDepth == 0)
                _allowKeywordFallback = ruleBasedAuto;

            var capabilityPlan = AgentCapabilityPlanner.Build(
                runtimeAgent,
                intentText,
                decision.TaskMode,
                capabilityContext.Attachments != null && capabilityContext.Attachments.Count > 0,
                outputIntent,
                allowKeywordFallback: ruleBasedAuto,
                searchOnly: manualCapabilities);

            // 決策紀錄：這次能力怎麼判的、誰判的。子代理委派本來就沒有意圖結果、也不跑能力層，不記。
            if (request.DelegationDepth == 0)
            {
                decision.CapabilityPlanSummary = !manualCapabilities
                    ? capabilityPlan.ToSummary()
                    : capabilityPlan.RequiresFreshFacts
                        ? capabilityPlan.ToSummary() + AgentCapabilityExecutionPlan.ManualSearchNote
                        : AgentCapabilityExecutionPlan.ManualModeSummary;
            }

            // 語意判斷拿不到要留日誌（跟輸出判斷失敗時一樣）。
            if (request.DelegationDepth == 0 &&
                capabilityPlan.Source != AgentCapabilityPlanner.SourceSemantic)
            {
                string from = string.IsNullOrEmpty(outputIntent?.Source) ? "無" : outputIntent!.Source.Split(':')[0];
                AppLog.Warn("AgentRuntime", capabilityPlan.Source == AgentCapabilityPlanner.SourceKeywords
                    ? $"能力規劃拿不到語意判斷（意圖來源：{from}），規則式 Auto 改用關鍵字後援"
                    : $"能力規劃拿不到語意判斷（意圖來源：{from}），{(manualCapabilities ? "手動" : "API Auto")}不用關鍵字，這次不搜尋、不加額外能力");
            }

            // 現在的日期：需要時才從 UserClock 讀一次，給模型的提示和搜尋查詢用同一刻（2026-11-06 使用者指示：需要時才讀）。
            DateTimeOffset? currentDate = NeedsCurrentDate(outputIntent, capabilityPlan) ? UserClock.Now : null;
            capabilityContext.CurrentDate = currentDate;
            if (request.DelegationDepth == 0)
                _currentDate = currentDate;

            // 第一層意圖閘門（§first-layer-intent-routing）：
            // 在規劃之前先判斷這次是否會產生影片/圖片/檔案。
            //  - 純手動模式：完全不自動轉換——選什麼模型就只做純文字回答，不偷偷加生影片/圖片/檔案。
            //  - Auto 模式：產出前一律二次確認，避免關鍵字誤判（例如聊天裡提到「影片」就被當成要生影片）。
            // 委派子代理（DelegationDepth > 0）不在此閘門範圍：產出步驟本來就只由根節點執行。
            bool autoMode = _main.IsAutoModelSelectionEnabled();
            bool allowGeneration = true;
            if (request.DelegationDepth == 0)
            {
                var detectedTaskType = OrchestrationPlanner.ResolveTaskType(topText, decision.TaskMode);
                if (hasImageAttachment && OrchestrationPlanner.IsImageEditIntent(topText))
                    detectedTaskType = OrchestrationTaskType.ImageEdit;
                // 與 OrchestrationPlanner.Build 同步：關鍵字沒抓到但第一層 LLM 判斷要影片/圖片時，
                // 讓 detectedTaskType 反映出來，確認框才會顯示正確的「影片／圖片」而非籠統的「檔案／媒體」。
                else if (outputIntent?.WantsVideo == true && !OrchestrationPlanner.ProducesMediaOrFile(detectedTaskType))
                    detectedTaskType = OrchestrationTaskType.VideoGeneration;
                else if (outputIntent?.WantsImage == true && !OrchestrationPlanner.ProducesMediaOrFile(detectedTaskType))
                    detectedTaskType = OrchestrationTaskType.ImageGeneration;

                bool producesOutput =
                    OrchestrationPlanner.ProducesMediaOrFile(detectedTaskType) ||
                    (outputIntent?.WantsAny ?? false);

                if (producesOutput)
                {
                    // 手動/Auto 一律彈二次確認（2026-07-04 修正）：先前手動模式直接封殺媒體生成，
                    // 導致使用者明確要求「生成照片」也只得到純文字。確認框＝明確徵詢，不是偷偷轉換，
                    // 不違反「純手動不自動轉換」的本意；使用者按「否」仍是純文字。
                    // 2026-10-29：確認框也讓使用者選撰寫者與要不要配圖。手動模式下所選模型以外的模型
                    // 要嘛從這裡由使用者親手選，要嘛是所選模型確定失敗後的替補（會在答案最上方告知）。
                    var approval = await _main.ConfirmGenerationAsync(
                        detectedTaskType, outputIntent, BuildGenerationChoices(detectedTaskType, outputIntent));
                    allowGeneration = approval.Approved;
                    _scope = _scope.WithApproval(approval);
                }
            }

            var orchestrationPlan = OrchestrationPlanner.Build(
                topText,
                decision,
                runtimeAgent,
                capabilityPlan,
                autoMode,
                capabilityContext.Attachments != null && capabilityContext.Attachments.Count > 0,
                hasImageAttachment,
                outputIntent,
                allowGeneration);

            // 圖片任務：下面 finalInput 會被整段換成一句確認語，真正產圖的 GenerateImageFile
            // 也只吃 topText（不是 capabilityAugmentedText）——能力層與平行代理產出的東西
            // 沒有任何一行進得了最終提示。跑了就是純白花，所以這裡直接不跑。
            bool discardsContext =
                orchestrationPlan.TaskType == OrchestrationTaskType.ImageGeneration ||
                orchestrationPlan.TaskType == OrchestrationTaskType.ImageEdit;

            // Sub-agents (DelegationDepth > 0) write internal orchestration_plans only;
            // the root agent's plan is the authoritative visible one.
            bool isRootRun = request.DelegationDepth == 0;
            var orchestrationItem = AgentWorkspaceBuilder.FromCapabilityData(
                workspace,
                node,
                runtimeAgent.Id,
                "orchestration_plan",
                orchestrationPlan,
                isUserVisibleOverride: isRootRun ? (bool?)null : false);
            workspace.Add(orchestrationItem);

            // Orchestrator v1：執行狀態機。規劃階段在 Build 當下已完成，直接記為 success。
            var orchestration = new OrchestrationStateMachine(orchestrationPlan);
            orchestration.MarkSuccess("detect_task", $"TaskType={orchestrationPlan.TaskType}");
            orchestration.MarkSuccess("select_pipeline", orchestrationPlan.PipelineId);
            orchestration.MarkSuccess("select_agent", runtimeAgent.Id);
            orchestration.MarkSuccess("select_model", decision.ModelId);

            var workflowPlan = WorkflowPlanBuilder.FromOrchestrationPlan(
                orchestrationPlan,
                node.Id.ToString(),
                topText);

            workspace.Add(
                AgentWorkspaceBuilder.FromCapabilityData(
                    workspace,
                    node,
                    runtimeAgent.Id,
                    "workflow_plan",
                    workflowPlan));

            var downstreamNodePlan = DownstreamNodePlanBuilder.FromWorkflowPlan(workflowPlan);
            if (downstreamNodePlan.ProposedNodes.Count > 0)
            {
                workspace.Add(
                    AgentWorkspaceBuilder.FromCapabilityData(
                        workspace,
                        node,
                        runtimeAgent.Id,
                        "downstream_node_plan",
                        downstreamNodePlan));
            }

            System.Diagnostics.Debug.WriteLine(
                $"[CapabilityPlan] Agent={runtimeAgent.Id} Required={string.Join(", ", capabilityPlan.RequiredCapabilityIds)} Order={string.Join(" -> ", capabilityPlan.OrderedCapabilityIds)} Reason={capabilityPlan.Reason}");

            // Orchestrator v1：能力執行順序以 orchestration plan 為正式來源
            // （research_first 等 pipeline 的順序與必跑能力由它決定）。
            var orderedCapabilities = orchestrationPlan.CapabilityOrder
                .Select(id => AgentCapabilityRegistry.All.FirstOrDefault(c =>
                    string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
                .Where(c => c != null)
                .Cast<IAgentCapability>()
                .ToList();
            // 模式決定能力層跑不跑：手動只在要即時資料時跑（計畫裡也只有搜尋）；
            // 規則式 Auto 只在需要即時資料時跑（搜尋要花錢）；API Auto 照計畫跑。
            bool layerAllowedByMode =
                manualCapabilities ? capabilityPlan.RequiresFreshFacts
                : !ruleBasedAuto || capabilityPlan.ShouldRunWhenSearchGated(topText);
            bool runCapabilityLayer =
                !request.SkipCapabilities &&
                layerAllowedByMode &&
                !discardsContext;   // 圖片任務：產出會被丟掉，見上面 discardsContext

            if (discardsContext && !request.SkipCapabilities)
                AppLog.Info("AgentRuntime", "圖片任務：略過能力層與平行代理（它們的產出不會進入圖片提示）");

            if (runCapabilityLayer)
            {
                foreach (var capability in orderedCapabilities)
                {
                    if (capability == null)
                        continue;

                    if (runtimeAgent.IsCapabilityBlocked(capability.Id))
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = false,
                            Executed = false,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "blocked by agent policy"
                        });
                        orchestration.MarkCapabilitySkipped(capability.Id, "blocked by agent policy");
                        continue;
                    }
                    bool isRequired = orchestrationPlan.IsCapabilityRequired(capability.Id);

                    if (!runtimeAgent.IsCapabilityAllowed(capability.Id) && !isRequired)
                    {
                        orchestration.MarkCapabilitySkipped(capability.Id, "agent policy not allowed");
                        continue;
                    }

                    // ? 關鍵：Required capability 強制允許
                    if (isRequired)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Capability Override] Agent={runtimeAgent.Id}, Capability={capability.Id} forced by planner");
                    }
                    if (capability.RequiredAgentCapability != AgentCapability.None &&
    !runtimeAgent.Capabilities.HasFlag(capability.RequiredAgentCapability) &&
    !isRequired)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = false,
                            Executed = false,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "agent capability not allowed"
                        });
                        orchestration.MarkCapabilitySkipped(capability.Id, "agent capability not allowed");
                        continue;
                    }

                    bool canHandle;
                    try
                    {
                        canHandle = capability.CanHandle(capabilityContext);
                    }
                    catch (Exception ex)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = false,
                            Executed = false,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = false,
                            Summary = "",
                            ErrorMessage = ex.Message
                        });
                        orchestration.MarkCapabilityFailed(capability.Id, $"CanHandle error: {ex.Message}");
                        continue;
                    }


                    // 語意判斷選了這個能力（例如判斷要寫程式），就不讓 CanHandle 裡的關鍵字清單推翻它：
                    // 「這段為什麼會掛掉」沒有「程式」「bug」這些字，但確實是程式問題。
                    if (!canHandle && capabilityPlan.IsIntentSelected(capability.Id))
                        canHandle = true;

                    if (!canHandle && !isRequired)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = false,
                            Executed = false,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "skipped"
                        });
                        orchestration.MarkCapabilitySkipped(capability.Id, "not applicable");
                        continue;
                    }

                    if (isRequired)
                        canHandle = true;

                    orchestration.MarkCapabilityRunning(capability.Id);

                    AgentCapabilityResult capabilityResult;
                    try
                    {
                        using var capabilityPurpose = UsageMeter.Purpose(
                            capability.Id == "search-capability" ? "搜尋研究" : $"能力層：{capability.Id}");
                        // 能力層不經過執行器，收到的是 capabilityContext.TopText——同樣要記。
                        // 搜尋記實際送出去的查詢（需要時會帶上換算好的日期）。
                        RecordStepInput(UsageMeter.CurrentPurpose, "", capability.Id == "search-capability"
                            ? SearchCapability.BuildSearchQuery(capabilityContext.TopText, capabilityContext.CurrentDate)
                            : capabilityContext.TopText);
                        capabilityResult = await capability.ExecuteAsync(
                            capabilityContext,
                            request.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Capability Execute ERROR] Agent={runtimeAgent.Id}, Capability={capability.Id}, Error={ex}");

                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = false,
                            Summary = "",
                            ErrorMessage = ex.Message
                        });

                        orchestration.MarkCapabilityFailed(capability.Id, ex.Message);

                        if (orchestrationPlan.IsCapabilityRequired(capability.Id))
                        {
                            orchestration.CompleteRun(
                                executionSuccess: false,
                                failureDetail: $"Required capability failed: {capability.Id}");
                            orchestrationItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(orchestrationPlan);

                            throw new InvalidOperationException(
                                $"Required capability failed: {capability.Id}. {ex.Message}",
                                ex);
                        }

                        continue;
                    }
                    System.Diagnostics.Debug.WriteLine(
    $"[Capability Execute] " +
    $"Agent={runtimeAgent.Id}, " +
    $"Capability={capability.Id}, " +
    $"ResultNull={(capabilityResult == null)}, " +
    $"Handled={(capabilityResult == null ? false : capabilityResult.Handled)}, " +
    $"Keys={(capabilityResult?.Data == null ? "(null)" : string.Join(", ", capabilityResult.Data.Keys))}");


                    if (capabilityResult == null)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "null result"
                        });
                        orchestration.MarkCapabilitySuccess(capability.Id, "null result");
                        continue;
                    }

                    // 能力層換過模型（例如所選模型無法搜尋）也要講。
                    AddTopNotice(capabilityResult.Notice);

                    if (capabilityResult.Data != null && capabilityResult.Data.Count > 0)
                    {
                        foreach (var kv in capabilityResult.Data)
                        {
                            capabilityData[kv.Key] = kv.Value;

                            workspace.Add(
                                AgentWorkspaceBuilder.FromCapabilityData(
                                    workspace,
                                    node,
                                    runtimeAgent.Id,
                                    kv.Key,
                                    kv.Value,
                                    // 能力層自己回報用了哪個模型；推導不出來就留空，不要猜。
                                    modelId: capabilityResult.ModelId));
                        }
                        System.Diagnostics.Debug.WriteLine(
                            $"[Capability] Agent={runtimeAgent.Id} Keys={string.Join(", ", capabilityData.Keys)}");

                        System.Diagnostics.Debug.WriteLine(
                            $"[Workspace] Count={workspace.GetAll().Count}");

                        System.Diagnostics.Debug.WriteLine(
                            $"[Workspace] Types={string.Join(", ", workspace.GetAll().Select(x => x.ItemType))}");
                        // 搜尋服務失敗 / 沒有結果時仍會帶一個 search_summary 讓流程繼續——但那不是成功，
                        // stage 與 trace 必須如實標失敗，決策窗才不會顯示「查證完成」。
                        var unusableSearch = capabilityResult.Data.TryGetValue("search_summary", out var searchValue) &&
                                             searchValue is SearchSummaryPayload searchPayload &&
                                             searchPayload.Status != SearchStatus.Succeeded
                            ? (SearchSummaryPayload)searchValue
                            : null;

                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = unusableSearch == null,
                            Summary = unusableSearch == null
                                ? $"data produced: {string.Join(", ", capabilityResult.Data.Keys)}"
                                : $"{SearchStatus.ToLabel(unusableSearch.Status)}：{unusableSearch.StatusDetail}",
                            ErrorMessage = unusableSearch == null ? "" : unusableSearch.StatusDetail
                        });

                        if (unusableSearch == null)
                        {
                            orchestration.MarkCapabilitySuccess(
                                capability.Id,
                                $"data: {string.Join(", ", capabilityResult.Data.Keys)}");
                        }
                        else
                        {
                            orchestration.MarkCapabilityFailed(
                                capability.Id,
                                $"{SearchStatus.ToLabel(unusableSearch.Status)}：{unusableSearch.StatusDetail}");
                        }
                    }

                    if (capabilityResult.Handled &&
                        !string.IsNullOrWhiteSpace(capabilityResult.Output) &&
                        (capabilityResult.Data == null || capabilityResult.Data.Count == 0))
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = true,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "direct handled"
                        });

                        var directExecution = new AiFallbackExecutionResult
                        {
                            IsSuccess = true,
                            Text = capabilityResult.Output,
                            ActualModelId = decision.ModelId,
                            UsedFallback = false,
                            Summary = $"handled by capability: {capability.Id}",
                            ErrorMessage = "",
                            Attempts = Array.Empty<AiFallbackAttempt>()
                        };

                        decision = _executionFinalizer.FinalizeDecision(decision, directExecution);
                        decision.ActualAgentId = runtimeAgent.Id;
                        decision.CapabilityTrace = capabilityTrace;
                        decision.DelegationTrace = delegationTrace;

                        orchestration.MarkCapabilitySuccess(capability.Id, "direct handled");
                        orchestration.MarkSuccess("write_workspace", $"artifacts: {workspace.GetAll().Count}");
                        orchestration.MarkSkipped("final_synthesis", "handled by capability");
                        orchestration.CompleteRun(executionSuccess: true);
                        orchestrationItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(orchestrationPlan);

                        return new AgentExecutionResult
                        {
                            Decision = decision,
                            Execution = directExecution,
                            CapabilityTrace = capabilityTrace,
                            DelegationTrace = delegationTrace
                        };
                    }

                    if (!string.IsNullOrWhiteSpace(capabilityResult.AugmentedPrompt))
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = true,
                            Success = true,
                            Summary = "prompt augmented"
                        });

                        orchestration.MarkCapabilitySuccess(capability.Id, "prompt augmented");

                        capabilityAugmentedText = capabilityResult.AugmentedPrompt;

                        capabilityContext = new AgentExecutionContext
                        {
                            Node = node,
                            Agent = runtimeAgent,
                            TopText = capabilityAugmentedText,
                            TaskMode = decision.TaskMode,
                            Attachments = _main.GetAttachmentsForNode(node),
                            AttachmentsRootDir = _main.GetAttachmentsRootDir()
                        };

                        continue;
                    }

                    if (capabilityResult.Data == null || capabilityResult.Data.Count == 0)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "executed without output change"
                        });
                        orchestration.MarkCapabilitySuccess(capability.Id, "executed, no output change");
                    }
                }
            }
            // 需要最新資料的任務：看「實際拿到什麼」（FreshDataAssessment），不是看有沒有 search_summary 物件。
            // 資料不足 → 不再整個節點失敗，而是交付「部分結果」：最終答案頂端由程式加警告、模型收到硬規則不得假裝查證。
            // 「要不要對使用者講話」由 FreshDataAssessment.NeedsUserNotice 單點決定。
            // 2026-10-29 實測：這裡原本寫 !HasUsableData，於是「資料拿到了但只核對過網域」
            // （domain_checked，HasUsableData=true）整段被跳過，新增的橫幅一次都沒出現過。
            //
            // 2026-10-29：只有「答案會過時」的要求才檢查——研究型任務也會搜尋，但不代表需要即時資料
            // （「介紹澳洲特有美食」曾被警告「即時資料未取得」，那句話還寫進了簡報的資料來源頁）。
            FreshDataAssessment? freshDataNotice = null;
            if (runCapabilityLayer && orchestrationPlan.RequiresFreshFacts &&
                FinanceTaskDetector.IsTimeSensitive(intentText))
            {
                var freshData = FreshDataAssessment.Evaluate(capabilityData);
                if (freshData.NeedsUserNotice)
                {
                    freshDataNotice = freshData;
                    AppLog.Info("AgentRuntime",
                        $"即時資料狀態需提醒使用者（{freshData.Status}）：{freshData.Detail}");
                }
            }

            // 2.5 parallel multi-agent execution
            bool parallelExecuted = false;
            AgentParallelExecutionResult? parallelResult = null;

            if (allowAgentFirstAutomation &&
                request.DelegationDepth == 0 &&
                !discardsContext)
            {
                var parallelTasks = _parallelPlanner.Plan(
                    runtimeAgent,
                    capabilityAugmentedText,
                    decision.TaskMode);

                if (parallelTasks.Count > 0)
                {
                    using var parallelPurpose = UsageMeter.Purpose("平行代理");
                    parallelResult = await parallelRunner.RunAsync(
                        new AgentParallelExecutionRequest
                        {
                            Node = node,
                            OriginalInput = capabilityAugmentedText,
                            Tasks = parallelTasks,
                            Workspace = workspace
                        },
                        request.CancellationToken);

                    parallelExecuted = parallelResult.HasAnySuccess;

                    foreach (var r in parallelResult.Results)
                    {
                        workspace.Add(
                                AgentWorkspaceBuilder.FromCapabilityData(
                                    workspace,
                                    node,
                                    r.AgentId,
                                    "parallel_agent_output",
                                    new DelegateOutputPayload
                                    {
                                        FromAgentId = runtimeAgent.Id,
                                        ToAgentId = r.AgentId,
                                        Instruction = "parallel execution",
                                        Output = r.Output,
                                        Success = r.Success,
                                        ActualModelId = r.ModelId,
                                        ErrorMessage = r.ErrorMessage
                                    }));
                    }
                }
            }
            // 3. delegation
            IReadOnlyList<AgentDelegationRequest> plans;

            if (!allowAgentFirstAutomation || request.DelegationDepth >= 2)
            {
                plans = Array.Empty<AgentDelegationRequest>();
            }
            else
            {
                plans = _delegationPlanner.Plan(
                    runtimeAgent,
                    capabilityAugmentedText,
                    decision.TaskMode);
            }

            string delegatedContext = "";

            foreach (var plan in plans)
            {
                if (string.IsNullOrWhiteSpace(plan.TargetAgentId))
                    continue;

                var subAgent = AgentRegistry.Get(plan.TargetAgentId);

                try
                {
                    var subResult = await ExecuteAsync(new AgentExecutionRequest
                    {
                        Node = node,
                        Agent = subAgent,
                        TopText = plan.Instruction,
                        UseStreaming = false,
                        OnDelta = null,
                        DelegationDepth = request.DelegationDepth + 1,
                        ForceAgentProfile = true,
                        SkipCapabilities = true,
                        Workspace = workspace,
                        CancellationToken = request.CancellationToken
                    });

                    string subText = subResult.FinalText ?? "";

                    if (!string.IsNullOrWhiteSpace(subText))
                    {
                        delegatedContext +=
                            $"\n[Delegate:{subAgent.Id}]\n{subText}\n";
                    }

                    delegationTrace.Add(new AgentDelegationTraceItem
                    {
                        Depth = request.DelegationDepth + 1,
                        FromAgentId = runtimeAgent.Id,
                        ToAgentId = subAgent.Id,
                        Instruction = plan.Instruction,
                        OutputSummary = subText,
                        Success = subResult.IsSuccess,
                        ErrorMessage = ""
                    });

                    workspace.Add(
    AgentWorkspaceBuilder.FromCapabilityData(
        workspace,
        node,
        runtimeAgent.Id,
        "delegate_output",
       new DelegateOutputPayload
       {
           FromAgentId = runtimeAgent.Id,
           ToAgentId = subAgent.Id,
           Instruction = plan.Instruction,
           Output = subText,
           Success = subResult.IsSuccess,
           ActualModelId =
        !string.IsNullOrWhiteSpace(subResult.Decision?.ActualModelId)
            ? subResult.Decision.ActualModelId
            : subResult.Execution?.ActualModelId ?? "",
           ErrorMessage = ""
       }));

                    if (subResult.DelegationTrace != null && subResult.DelegationTrace.Count > 0)
                        delegationTrace.AddRange(subResult.DelegationTrace);
                }
                catch (Exception ex)
                {
                    delegationTrace.Add(new AgentDelegationTraceItem
                    {
                        Depth = request.DelegationDepth + 1,
                        FromAgentId = runtimeAgent.Id,
                        ToAgentId = subAgent.Id,
                        Instruction = plan.Instruction,
                        OutputSummary = "",
                        Success = false,
                        ErrorMessage = ex.Message
                    });

                    workspace.Add(
    AgentWorkspaceBuilder.FromCapabilityData(
        workspace,
        node,
        runtimeAgent.Id,
        "delegate_output",
        new DelegateOutputPayload
        {
            FromAgentId = runtimeAgent.Id,
            ToAgentId = subAgent.Id,
            Instruction = plan.Instruction,
            Output = "",
            Success = false,

            // ?? 加這行
            ActualModelId = "",

            ErrorMessage = ex.Message
        }));
                }
            }
            AiFallbackExecutionResult? synthesisExecution = null;

            if (parallelExecuted && request.DelegationDepth == 0)
            {
                synthesisExecution = await RunFinalSynthesisAsync(
                    node,
                    runtimeAgent,
                    capabilityAugmentedText,
                    workspace,
                    decision,
                    request.PreferenceBlock,
                    request.CancellationToken,
                    freshDataNotice?.BuildSynthesisNotice() ?? "");

                if (synthesisExecution != null)
                {
                    synthesisExecution = FinalAnswerSanitizer.Sanitize(
                        synthesisExecution,
                        enforceSynthesisFormat: workspace.GetByType("verified_facts").Count > 0);
                }

                if (synthesisExecution != null && synthesisExecution.IsSuccess)
                {
                    workspace.Add(
                        AgentWorkspaceBuilder.FromCapabilityData(
                            workspace,
                            node,
                            "general-agent",
                            "final_synthesis",
                            new FinalSynthesisPayload
                            {
                                SynthesizerAgentId = "general-agent",
                                ModelId = synthesisExecution.ActualModelId ?? "",
                                Output = synthesisExecution.Text ?? "",
                                Success = true,
                                ErrorMessage = ""
                            }));
                }
            }
            // 4. merge
            string capabilityDataBlock = BuildCapabilityDataBlock(capabilityData);
            string workspaceBlock = workspace.BuildPromptBlock();
            bool hasVerifiedFacts =
                capabilityData.ContainsKey("verified_facts") ||
                workspace.GetByType("verified_facts").Count > 0;
            bool hasCodeDiffDraft =
                capabilityData.ContainsKey("code_diff_draft") ||
                workspace.GetByType("code_diff_draft").Count > 0;

            // Code Agent v1.5：bug 清單模式偵測 + 大型任務規模/風險評估（過程資訊，放決策區）。
            string codeRequestType = TryGetCodeRequestType(capabilityData, workspace);
            bool isBugListing = string.Equals(codeRequestType, "bug_listing", StringComparison.OrdinalIgnoreCase);

            var codeSnapshotForAssessment = TryGetCodeSnapshot(capabilityData, workspace);
            CodeTaskAssessmentPayload? codeAssessment = null;
            if (codeSnapshotForAssessment != null)
            {
                codeAssessment = CodeTaskAssessor.Assess(codeSnapshotForAssessment);
                workspace.Add(
                    AgentWorkspaceBuilder.FromCapabilityData(
                        workspace,
                        node,
                        runtimeAgent.Id,
                        "code_task_assessment",
                        codeAssessment));
            }

            // 要寫程式時，程式碼存成檔案（見 CodeFileExtractor）。修改附件程式碼走 diff 流程，不在這裡處理。
            bool wantsCodeFiles = request.DelegationDepth == 0 && !hasCodeDiffDraft &&
                                  CodeFileExtractor.WantsCodeFiles(intentText, decision.TaskMode);

            // 三段 finalInput 組裝都用同一套 code 指令區塊，避免漏掉某條路徑。
            void AddCodeInstructionBlocks(List<string> parts)
            {
                if (isBugListing)
                    parts.Add(BuildBugListingInstructionBlock());

                if (hasCodeDiffDraft)
                    parts.Add(BuildCodeDiffOutputInstructionBlock());

                if (wantsCodeFiles)
                    parts.Add(CodeFileExtractor.PromptBlock);

                if (codeAssessment != null &&
                    !string.Equals(codeAssessment.RiskLevel, "low", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(BuildCodeRiskNoteBlock(codeAssessment));
                }
            }

            bool isFinanceTask = FinanceTaskDetector.IsFinanceFocused(capabilityAugmentedText);
            bool enforceFinalSynthesisFormat = hasVerifiedFacts && isFinanceTask;
            bool requiresSources = RequiresSourceEvidence(capabilityData, workspace, decision.TaskMode);

            // 回答語言一律講明：沒有明確指示時模型會從上下文猜，而記憶裡一條
            // 「我是西班牙人」比提問用的中文更像指示（2026-10-15 實測，整段答案變成西班牙文）。
            string languageBlock = AnswerLanguage.BuildBlock(
                request.TopText, request.OutputIntent?.RequestedLanguage);

            string finalInput = capabilityAugmentedText;

            if (!string.IsNullOrWhiteSpace(capabilityDataBlock) ||
    !string.IsNullOrWhiteSpace(workspaceBlock))
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(workspaceBlock))
                    parts.Add(workspaceBlock);

                if (!string.IsNullOrWhiteSpace(capabilityDataBlock))
                    parts.Add(capabilityDataBlock);

                if (enforceFinalSynthesisFormat)
                    parts.Add(BuildFinalOutputFormatBlock());

                if (requiresSources)
                    parts.Add(BuildSourceEvidenceBlock());

                parts.Add(languageBlock);

                AddCodeInstructionBlocks(parts);

                parts.Add("【目前任務】\n" + capabilityAugmentedText);

                finalInput = string.Join("\n\n", parts);
            }

            if (!string.IsNullOrWhiteSpace(delegatedContext) && !hasVerifiedFacts)
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(workspaceBlock))
                    parts.Add(workspaceBlock);

                if (!string.IsNullOrWhiteSpace(capabilityDataBlock))
                    parts.Add(capabilityDataBlock);

                if (enforceFinalSynthesisFormat)
                    parts.Add(BuildFinalOutputFormatBlock());

                if (requiresSources)
                    parts.Add(BuildSourceEvidenceBlock());

                parts.Add(languageBlock);

                AddCodeInstructionBlocks(parts);

                parts.Add(
                    "以下是其他代理提供的補充資訊：\n" +
                    delegatedContext);

                parts.Add("請基於以上資訊完成目前任務：\n" + capabilityAugmentedText);

                finalInput =
                    string.Join("\n\n", parts);
            }
            else if (!string.IsNullOrWhiteSpace(delegatedContext) && hasVerifiedFacts)
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(workspaceBlock))
                    parts.Add(workspaceBlock);

                if (!string.IsNullOrWhiteSpace(capabilityDataBlock))
                    parts.Add(capabilityDataBlock);

                if (enforceFinalSynthesisFormat)
                    parts.Add(BuildFinalOutputFormatBlock());

                if (requiresSources)
                    parts.Add(BuildSourceEvidenceBlock());

                parts.Add(languageBlock);
                AddCodeInstructionBlocks(parts);
                parts.Add("【Delegated Analysis Omitted】\nDelegated/parallel agent text was omitted because structured verified_facts exist. Use delegated analysis only as internal reasoning, not as a source for numeric facts.");
                parts.Add("【目前任務】\n" + capabilityAugmentedText);

                finalInput = string.Join("\n\n", parts);
            }

            // Image Gen v1：圖片任務不需要 LLM 描述圖片，只讓它輸出一句確認語。
            if (orchestrationPlan.TaskType == OrchestrationTaskType.ImageGeneration ||
                orchestrationPlan.TaskType == OrchestrationTaskType.ImageEdit)
            {
                bool isEdit = orchestrationPlan.TaskType == OrchestrationTaskType.ImageEdit;
                finalInput =
                    (isEdit
                        ? "你是圖片編輯助理。使用者上傳了圖片並請你依指令修改，圖片編輯程式已準備好。請只用一句繁體中文確認你會依指令修改上傳的圖片，不要展開描述、不要給提示詞。"
                        : "你是圖片生成助理。使用者請你依描述生成圖片，圖片生成程式已準備好。請只用一句繁體中文確認你會根據描述生成圖片，不要展開描述圖片內容，不要給提示詞。") +
                    "\n\n【使用者描述】\n" + topText;
            }
            else if (currentDate.HasValue)
            {
                // 現在的日期：需要時才從 UserClock 讀、交給模型，不需要就不給（2026-11-06 使用者指示）。
                // API 呼叫的模型不知道今天是哪一天；不給的話「這週末」「今天」換算不出來，
                // 搜尋結果裡的舊文章也分不出新舊（2026-11-06 實測）。
                finalInput = CurrentDateBlock.Build(currentDate.Value) + "\n\n" + finalInput;
            }

            if (!string.IsNullOrWhiteSpace(request.PreferenceBlock) &&
                orchestrationPlan.TaskType != OrchestrationTaskType.ImageGeneration &&
                orchestrationPlan.TaskType != OrchestrationTaskType.ImageEdit)
            {
                // 非 parallel 路徑：把使用者偏好放在最前面當最高優先指令（圖片任務除外）。
                finalInput =
                    request.PreferenceBlock.Trim() +
                    "\n（以上偏好為最高優先，蓋過任何「使用繁體中文」等預設規則；若指定輸出語言或格式，必須改用該語言與格式。）\n\n" +
                    finalInput;
            }

            string freshDataBanner = freshDataNotice?.BuildUserBanner() ?? "";
            if (freshDataNotice != null &&
                orchestrationPlan.TaskType != OrchestrationTaskType.ImageGeneration &&
                orchestrationPlan.TaskType != OrchestrationTaskType.ImageEdit)
            {
                finalInput += "\n\n" + freshDataNotice.BuildSynthesisNotice();
            }

            // 5. execution
            orchestration.MarkRunning("final_synthesis");

            AiFallbackExecutionResult execution;
            // 圖片任務不串流：確認語很短，且串流後還要 append「已生成圖片」/錯誤訊息，
            // 串流會讓最終修改與清理（含 [[END_OF_RESPONSE]] 移除）來不及蓋回畫面。
            bool useStreamingForFinalExecution =
                request.UseStreaming &&
                !hasCodeDiffDraft &&
                orchestrationPlan.TaskType != OrchestrationTaskType.ImageGeneration &&
                orchestrationPlan.TaskType != OrchestrationTaskType.ImageEdit;

            // 附了資料檔又要交報告／表格：在沙箱裡實際執行，一趟做完（DataAnalysisJob）。
            // 成功就不再跑主回覆——那一步每輪續寫都要重送整份資料，又產不出真實數字（2026-11-03 機器學習作業）。
            DataAnalysisOutcome? analysis = null;
            if (!collaborating && request.DelegationDepth == 0 && allowGeneration &&
                !hasCodeDiffDraft && outputIntent != null)
            {
                analysis = await TryRunDataAnalysisAsync(
                    node, topText, workspace, orchestrationPlan, orchestration, outputIntent,
                    request.PreferenceBlock, request.CancellationToken);
            }

            if (analysis != null)
            {
                execution = analysis.Execution;
                if (request.UseStreaming && request.OnDelta != null)
                    request.OnDelta(execution.Text);
            }
            else if (collaborating)
            {
                // 協作：每個模型各自回答同一份輸入（相同的搜尋結果與背景）→ 互看後交叉檢視 → 共識與分歧。
                // 每次呼叫都鎖定單一模型，失敗就標「未回應」，不找別的模型頂替。
                execution = await ModelCollaboration.RunAsync(
                    collaborators,
                    finalInput,
                    _originalRequest,
                    async (modelId, prompt, purpose) =>
                    {
                        using var stepPurpose = UsageMeter.Purpose(purpose);
                        return await _executeWithFallbackAsync(
                            node, prompt, CollaboratorDecision(decision, modelId), null, false, request.CancellationToken);
                    },
                    hint => node.SetLoadingHint(hint),
                    request.CancellationToken);

                if (!string.IsNullOrEmpty(freshDataBanner) && execution.IsSuccess)
                    execution = ReplaceExecutionText(execution, freshDataBanner + execution.Text);

                if (request.UseStreaming && request.OnDelta != null && !string.IsNullOrWhiteSpace(execution.Text))
                    request.OnDelta(execution.Text);
            }
            else if (synthesisExecution != null && synthesisExecution.IsSuccess)
            {
                execution = FinalAnswerSanitizer.Sanitize(
                    synthesisExecution,
                    enforceSynthesisFormat: enforceFinalSynthesisFormat);

                if (!string.IsNullOrEmpty(freshDataBanner) && execution.IsSuccess)
                    execution = ReplaceExecutionText(execution, freshDataBanner + execution.Text);

                if (request.UseStreaming && request.OnDelta != null &&
                    !string.IsNullOrWhiteSpace(execution.Text))
                {
                    request.OnDelta(execution.Text);
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(freshDataBanner) && useStreamingForFinalExecution)
                    request.OnDelta?.Invoke(freshDataBanner);

                execution = await _executeWithFallbackAsync(
                    node,
                    finalInput,
                    decision,
                    useStreamingForFinalExecution ? request.OnDelta : null,
                    useStreamingForFinalExecution,
                    request.CancellationToken);

                execution = FinalAnswerSanitizer.Sanitize(
                    execution,
                    enforceSynthesisFormat: enforceFinalSynthesisFormat);

                if (!string.IsNullOrEmpty(freshDataBanner) && execution.IsSuccess)
                    execution = ReplaceExecutionText(execution, freshDataBanner + execution.Text);
            }

            if (hasCodeDiffDraft)
            {
                CodeDiffArtifactPayload? readyDiffForValidation = null;
                CodeDiffValidationPayload? validationForReadyDiff = null;

                var readyDiff = CodeDiffArtifactExtractor.TryExtractReadyDiff(
                    execution.Text,
                    capabilityAugmentedText);

                if (readyDiff != null)
                {
                    workspace.Add(
                        AgentWorkspaceBuilder.FromCapabilityData(
                            workspace,
                            node,
                            runtimeAgent.Id,
                            "code_diff",
                            readyDiff));

                    var snapshot = capabilityData.TryGetValue("code_file_snapshot", out var snapshotValue)
                        ? snapshotValue as CodeFileSnapshotPayload
                        : workspace.GetByType("code_file_snapshot")
                            .Select(x => x.Payload as CodeFileSnapshotPayload)
                            .FirstOrDefault(x => x != null);

                    var validation = CodeDiffDryRunValidator.Validate(
                        readyDiff,
                        snapshot);

                    workspace.Add(
                        AgentWorkspaceBuilder.FromCapabilityData(
                            workspace,
                            node,
                            runtimeAgent.Id,
                            "code_diff_validation",
                            validation));

                    readyDiffForValidation = readyDiff;
                    validationForReadyDiff = validation;
                }

                if (readyDiffForValidation != null &&
                    validationForReadyDiff != null &&
                    string.Equals(validationForReadyDiff.Status, "invalid", StringComparison.OrdinalIgnoreCase))
                {
                    var repairedExecution = await TryRepairInvalidCodeDiffAsync(
                        node,
                        finalInput,
                        capabilityAugmentedText,
                        execution,
                        readyDiffForValidation,
                        validationForReadyDiff,
                        decision,
                        request.CancellationToken);

                    if (repairedExecution != null)
                    {
                        var repairedDiff = CodeDiffArtifactExtractor.TryExtractReadyDiff(
                            repairedExecution.Text,
                            capabilityAugmentedText);

                        if (repairedDiff != null)
                        {
                            workspace.Add(
                                AgentWorkspaceBuilder.FromCapabilityData(
                                    workspace,
                                    node,
                                    runtimeAgent.Id,
                                    "code_diff",
                                    repairedDiff));

                            var snapshot = capabilityData.TryGetValue("code_file_snapshot", out var snapshotValue)
                                ? snapshotValue as CodeFileSnapshotPayload
                                : workspace.GetByType("code_file_snapshot")
                                    .Select(x => x.Payload as CodeFileSnapshotPayload)
                                    .FirstOrDefault(x => x != null);

                            var repairedValidation = CodeDiffDryRunValidator.Validate(
                                repairedDiff,
                                snapshot);

                            workspace.Add(
                                AgentWorkspaceBuilder.FromCapabilityData(
                                    workspace,
                                    node,
                                    runtimeAgent.Id,
                                    "code_diff_validation",
                                    repairedValidation));

                            execution = repairedExecution;

                            if (string.Equals(repairedValidation.Status, "invalid", StringComparison.OrdinalIgnoreCase))
                            {
                                execution = WithSafeInvalidPatchMessage(
                                    execution,
                                    repairedValidation);
                            }
                        }
                    }
                }
                else if (validationForReadyDiff != null &&
                    string.Equals(validationForReadyDiff.Status, "invalid", StringComparison.OrdinalIgnoreCase))
                {
                    execution = WithSafeInvalidPatchMessage(
                        execution,
                        validationForReadyDiff);
                }

                if (request.UseStreaming &&
                    request.OnDelta != null &&
                    !useStreamingForFinalExecution &&
                    !string.IsNullOrWhiteSpace(execution.Text))
                {
                    request.OnDelta(execution.Text);
                }
            }

            // 6. finalize
            if (execution.IsSuccess && freshDataNotice != null)
                orchestration.MarkSuccess("final_synthesis",
                    $"model: {execution.ActualModelId}（" +
                    (freshDataNotice.NeedsSourceCaution
                        ? "來源僅核對網域，未取原文"
                        : "部分結果：未取得即時資料") + "）");
            else if (execution.IsSuccess)
                orchestration.MarkSuccess("final_synthesis", $"model: {execution.ActualModelId}");
            else
                orchestration.MarkFailed("final_synthesis", execution.ErrorMessage);

            // §6 第一層輸出判斷：優先用 NodeService 先跑 API 得到的 OutputIntent（要簡報/報告/表格哪幾個）；
            // 沒有時（例如子代理委派）退回關鍵字 + TaskType。報告→docx、表格→xlsx、簡報→pptx，且一律再配一份 pdf。
            OutputIntent intent = outputIntent ?? new OutputIntent
            {
                WantsReport = orchestrationPlan.TaskType == OrchestrationTaskType.GenerateFile
                              || OutputFormatDetector.WantsWrittenReport(capabilityAugmentedText),
                WantsTable = OutputFormatDetector.WantsSpreadsheet(capabilityAugmentedText),
                WantsPresentation = orchestrationPlan.TaskType == OrchestrationTaskType.Presentation
                                    || OutputFormatDetector.WantsPresentation(capabilityAugmentedText)
            };

            // 程式碼存成檔案：排在報告、簡報之前。
            // 同時要求程式與報告時，報告拿到的是「程式碼已換成檔案參照」的文字——
            // 程式碼歸程式碼檔、報告只放說明，不會把原始碼整段抄進 Word 或投影片（2026-11-03 使用者要求分類好）。
            // 不花額外的 API 呼叫，所以不經過產檔確認框。
            var codeFiles = new List<GeneratedFilePayload>();
            // 要求裡沒提到寫程式、但答案本身在交付程式（例如作業要求寫在附件裡）也算——見 LooksLikeCodeDeliverable。
            bool extractCode = execution.IsSuccess && !string.IsNullOrWhiteSpace(execution.Text) &&
                               (wantsCodeFiles ||
                                (request.DelegationDepth == 0 && !hasCodeDiffDraft &&
                                 CodeFileExtractor.LooksLikeCodeDeliverable(execution.Text)));
            if (extractCode)
            {
                var extracted = CodeFileExtractor.Extract(execution.Text);
                if (extracted.Files.Count > 0)
                {
                    var written = GeneratedFileWriter.WriteCodeFiles(
                        _main.GetGeneratedFilesDir(),
                        DeriveCleanTitle(intentText, "程式碼"),
                        extracted.Files,
                        $"{orchestrationPlan.PipelineId} / {execution.ActualModelId}");

                    foreach (var file in written.Where(f => f.Success))
                    {
                        workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                            workspace, node, runtimeAgent.Id, "generated_file", file, modelId: execution.ActualModelId));
                        codeFiles.Add(file);
                    }

                    // 全部寫成功才把程式碼從文字裡拿掉；有任何一個沒寫成，就保留原文——不能讓程式碼兩邊都不見。
                    if (codeFiles.Count == extracted.Files.Count)
                    {
                        execution = ReplaceExecutionText(execution, extracted.Text);
                        AppLog.Info("AgentRuntime", $"程式碼已存成 {codeFiles.Count} 個檔案");
                    }
                    else
                    {
                        AppLog.Warn("AgentRuntime",
                            $"程式碼存檔不完整（{codeFiles.Count}/{extracted.Files.Count}），程式碼保留在答案裡");
                    }
                }
            }

            // 沙箱執行已經交出的報告、表格與程式，不再重做一次。
            if (analysis != null)
                codeFiles.AddRange(analysis.CodeFiles);

            // 意圖閘門：未獲准產出（純手動 / 確認框選「否」）時，一律不產報告/表格/簡報。
            // 沙箱逾時（Stopped）：已經告訴使用者沒做完，不再另外產出沒有真實數字的報告。
            bool analysisStopped = analysis?.Stopped ?? false;
            bool doReport = allowGeneration && intent.WantsReport && !(analysis?.ReportDone ?? false) && !analysisStopped;
            bool doTable = allowGeneration && intent.WantsTable && !(analysis?.TableDone ?? false) && !analysisStopped;
            bool doDeck = allowGeneration && intent.WantsPresentation && !analysisStopped;

            // 文件技能（個人化預設）：簡報 / 報告 / 表格交給 Claude 用官方技能一次做完（與 Claude App 同一套機制）。
            // 成功產出的類型就不再走內建流程；沒金鑰、失敗、或某類型沒產出時，該類型照舊由下方內建流程補上。
            // 使用者明確選了 Gamma 的簡報維持走 Gamma。
            if (execution.IsSuccess &&
                (doReport || doTable || doDeck) &&
                request.DelegationDepth == 0 &&
                !string.IsNullOrWhiteSpace(execution.Text) &&
                EffectiveDocumentEngine() == DocumentEngine.ClaudeSkills &&
                // 最佳品質只有 Claude 會；這次的撰寫者不是 Claude 就改走一般版面，
                // 不能為了「最佳品質」把撰寫者換成 Claude。
                _scope.SkillsModel(DocumentEngineHelper.ResolveSkillsModel(AiAutoCostPolicy.BlockOpus)) != null)
            {
                // 簡報仍然交給 Claude 排版（使用者說那幾份做得不錯），只是**不掛 pptx 技能**
                // ——2026-10-15 量到技能那包自己就注入 118 萬 token，而我們的請求只有 1.5 萬。
                // 沙箱預裝 python-pptx，改用明確指示一樣做得出來，見 DocumentEngine.SkillIds。
                bool deckViaSkills = doDeck && _main.GetPresentationEngine() != PresentationEngine.Gamma;
                if (doReport || doTable || deckViaSkills)
                {
                    var skills = await TryGenerateDocumentsWithSkillsAsync(
                        node, topText, workspace, orchestrationPlan, execution, orchestration,
                        doReport, doTable, deckViaSkills, request.PreferenceBlock, request.CancellationToken, intent);

                    execution = skills.Execution;
                    doReport &= !skills.Report;
                    doTable &= !skills.Table;
                    doDeck &= !skills.Deck;
                }
            }

            // 報告 / 表格：GenerateReportFile 內部依 intent 決定 .docx（報告）/ .xlsx（表格），且一律配一份 .pdf。
            if (execution.IsSuccess &&
                (doReport || doTable) &&
                request.DelegationDepth == 0 &&
                !string.IsNullOrWhiteSpace(execution.Text))
            {
                execution = await GenerateReportFile(
                    node,
                    runtimeAgent,
                    capabilityAugmentedText,
                    workspace,
                    orchestrationPlan,
                    execution,
                    orchestration,
                    new OutputIntent
                    {
                        WantsReport = doReport,
                        WantsTable = doTable,
                        WantsPresentation = intent.WantsPresentation,
                        WantsImage = intent.WantsImage,
                        WantsVideo = intent.WantsVideo,
                        // 其他判斷照樣帶過去：以前這裡只複製了上面五項，配圖、張數、語言、製作方式都被丟掉。
                        WantsIllustrations = intent.WantsIllustrations,
                        DeclinesIllustrations = intent.DeclinesIllustrations,
                        RequestedSlides = intent.RequestedSlides,
                        RequestedLanguage = intent.RequestedLanguage,
                        Quality = intent.Quality,
                        Topic = intent.Topic,
                        Capabilities = intent.Capabilities,
                        ArtDirection = intent.ArtDirection,
                        Source = intent.Source
                    },
                    request.CancellationToken);
            }

            // 內建報告流程只看 intent；技能已產出的類型要從 intent 拿掉，避免重複產檔。
            // （intent 在上面 GenerateReportFile 呼叫前已依 doReport/doTable 調整，這裡只補簡報。）
            // 簡報：輸出投影片大綱 + .pptx。若同一請求也產了報告/表格（已配 pdf）就用 append 模式接文字、不再配 pdf；
            // 若這次只有簡報，alsoPdf=true 由簡報這邊補一份 deck 的 .pdf（不管輸出什麼都要配一個 pdf）。
            if (execution.IsSuccess &&
                doDeck &&
                request.DelegationDepth == 0 &&
                !string.IsNullOrWhiteSpace(execution.Text))
            {
                execution = await GeneratePresentation(
                    node,
                    runtimeAgent,
                    capabilityAugmentedText,
                    workspace,
                    orchestrationPlan,
                    execution,
                    orchestration,
                    request.CancellationToken,
                    appendToText: doReport || doTable,
                    intent: intent);
            }

            // Image Gen v1：ImageGeneration 任務在最終答案後呼叫 DALL-E 3 生成圖片，
            // 存檔、加入 workspace artifact，並在輸出區直接顯示。
            if (execution.IsSuccess &&
                allowGeneration &&
                orchestrationPlan.TaskType == OrchestrationTaskType.ImageGeneration &&
                request.DelegationDepth == 0)
            {
                execution = await GenerateImageFile(
                    node,
                    runtimeAgent,
                    topText,
                    workspace,
                    orchestrationPlan,
                    execution,
                    orchestration,
                    request.CancellationToken);
            }

            // Image Edit：上傳圖 + 編輯指令 → 用 OpenAI images/edits 改圖，輸出改建後的照片。
            if (execution.IsSuccess &&
                allowGeneration &&
                orchestrationPlan.TaskType == OrchestrationTaskType.ImageEdit &&
                request.DelegationDepth == 0)
            {
                execution = await GenerateImageEdit(
                    node,
                    runtimeAgent,
                    topText,
                    workspace,
                    orchestrationPlan,
                    execution,
                    orchestration,
                    request.CancellationToken);
            }

            // Video Gen v1：VideoGeneration 任務在最終答案後呼叫影片 API（非同步、需輪詢），
            // 完成後存檔、加入 workspace artifact。未啟用時（休眠）標記未啟用，不影響主答案。
            if (execution.IsSuccess &&
                allowGeneration &&
                orchestrationPlan.TaskType == OrchestrationTaskType.VideoGeneration &&
                request.DelegationDepth == 0)
            {
                execution = await GenerateVideoFile(
                    node,
                    runtimeAgent,
                    topText,
                    workspace,
                    orchestrationPlan,
                    execution,
                    orchestration,
                    request.CancellationToken);
            }

            // 獨立審查：產出了報告就由沒有參與撰寫的一方核對數字與文字、找矛盾，另存修改清單（不改報告）。
            // 個人化可以關掉（審查要另外花錢）。
            if (execution.IsSuccess && allowGeneration && request.DelegationDepth == 0 && DeliveryOptions.IndependentReview)
                execution = await TryReviewDeliverablesAsync(node, workspace, execution, request.CancellationToken);

            // 同時產了報告／簡報時，答案會被換成「已產出：報告.docx…」的摘要，程式碼的參照跟著不見。
            // 程式碼檔另列一行，跟文件分開——使用者一眼分得出哪些是程式、哪些是文件。
            if (codeFiles.Count > 0 && execution.IsSuccess)
            {
                string text = execution.Text ?? "";
                if (codeFiles.Any(f => !text.Contains(f.FileName, StringComparison.Ordinal)))
                    execution = ReplaceExecutionText(execution,
                        text.TrimEnd() + "\n\n程式碼檔案：" + string.Join("、", codeFiles.Select(f => f.FileName)) +
                        "（在節點下方點開即可預覽）");
            }

            orchestration.MarkSuccess("write_workspace", $"artifacts: {workspace.GetAll().Count}");
            orchestration.CompleteRun(execution.IsSuccess, execution.ErrorMessage);
            orchestrationItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(orchestrationPlan);

            // 換過模型的說明放在答案最上方——文件撰寫等步驟在主回覆之後才跑，所以在這裡統一加。
            // 只在最外層加：子代理也跑在這個實例上，若每一層都加，說明會被重複塞進整合的輸入裡。
            if (request.DelegationDepth == 0)
            {
                // 答案裡的數字跟查到的數字比一次：抓「數字相同、單位差十倍」（2026-10-29 第一階段九份裡有三份）。
                if (execution.IsSuccess)
                {
                    var facts = workspace.GetByType("verified_facts")
                        .Select(x => x.Payload).OfType<VerifiedFactPayload>()
                        .SelectMany(x => x.Facts ?? new List<VerifiedFactItem>());
                    var mismatches = NumberConsistency.FindScaleMismatches(facts, execution.Text);
                    if (mismatches.Count > 0)
                    {
                        AppLog.Warn("AgentRuntime", $"答案與查到的數字疑似單位不一致：{mismatches.Count} 處");
                        AddTopNotice(NumberConsistency.BuildNotice(mismatches));
                    }
                }

                execution = WithTopNotices(execution);
            }

            var workspaceSummary = workspace.BuildSummary();
            decision = _executionFinalizer.FinalizeDecision(decision, execution);
            decision.ActualAgentId = runtimeAgent.Id;
            decision.CapabilityTrace = capabilityTrace;
            decision.DelegationTrace = delegationTrace;
            decision.WorkspaceSummary = workspaceSummary?.SummaryText ?? "";
            decision.WorkspaceArtifactDetails = workspaceSummary?.ArtifactDetails ?? Array.Empty<string>();
            decision.WorkspaceArtifacts = workspaceSummary?.Artifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>();

            if (request.DelegationDepth == 0)
            {
                lock (_stepInputs) decision.StepInputs = _stepInputs.Select(x => x.Record).ToList();
                decision.StepInputLogPath = WriteStepInputLog(node);
            }

            return new AgentExecutionResult
            {
                Decision = decision,
                Execution = execution,
                CapabilityTrace = capabilityTrace,
                DelegationTrace = delegationTrace,
                WorkspaceSummary = workspaceSummary
            };
        }

        // 影片 / 報告 / 表格等產出型任務：主模型的回答直接取代（不要夾在「我不能生成影片」後面）。
        private static AiFallbackExecutionResult ReplaceExecutionText(
            AiFallbackExecutionResult execution, string text)
        {
            return new AiFallbackExecutionResult
            {
                IsSuccess = execution.IsSuccess,
                Text = text,
                ActualModelId = execution.ActualModelId ?? "",
                UsedFallback = execution.UsedFallback,
                Summary = execution.Summary,
                ErrorMessage = execution.ErrorMessage,
                Attempts = execution.Attempts
            };
        }

        private static AiFallbackExecutionResult AppendExecutionNote(
            AiFallbackExecutionResult execution, string note)
        {
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

        private void ApplyAutoCostPolicyToDecision(NodeExecutionDecision decision)
        {
            if (decision == null || !_main.IsAutoModelSelectionEnabled())
                return;

            string requested = AiModelHelper.NormalizeNodeModel(decision.ModelId);
            // §15 個人化：成本降級的唯一依據＝使用者的個人化開關。
            // 只有使用者明確關閉該高成本模型時才降級；未關閉就原樣保留。
            if (!AiAutoCostPolicy.TryEnforceUserBlock(requested, out string resolved))
                return;

            decision.ModelId = resolved;
            decision.CapabilityResolvedModelId = resolved;
            decision.CapabilityAdjusted = true;

            string costReason = $"Auto cost policy: {requested} → {resolved}";
            decision.CapabilityReason = string.IsNullOrWhiteSpace(decision.CapabilityReason)
                ? costReason
                : decision.CapabilityReason + " / " + costReason;

            decision.ResolverReason = string.IsNullOrWhiteSpace(decision.ResolverReason)
                ? costReason
                : decision.ResolverReason + " / " + costReason;

            if (!string.IsNullOrWhiteSpace(decision.ResolverLabel) &&
                !decision.ResolverLabel.Contains("Auto Cost Policy", StringComparison.OrdinalIgnoreCase))
            {
                decision.ResolverLabel += " + Auto Cost Policy";
            }
        }
    }
}
