using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    public static class NodeDecisionViewBuilder
    {
        public static NodeDecisionViewData BuildFromLog(AiExecutionLogEntry log)
        {
            if (log == null)
                return CreateEmpty();

            string requestedLabel = GetModelLabel(log.RequestedModelId);
            string plannedLabel = GetModelLabel(log.PlannedModelId);
            string actualLabel = GetModelLabel(log.ActualModelId);

            string capabilityRequestedLabel = GetModelLabel(log.CapabilityRequestedModelId);
            string capabilityResolvedLabel = GetModelLabel(log.CapabilityResolvedModelId);

            string displayFromLabel = requestedLabel;
            string displayToLabel = actualLabel;

            string agent = string.IsNullOrWhiteSpace(log.ActualAgentId)
                ? (string.IsNullOrWhiteSpace(log.RequestedAgentId) ? "-" : log.RequestedAgentId)
                : log.ActualAgentId;

            if (log.CapabilityAdjusted &&
                !string.IsNullOrWhiteSpace(log.CapabilityRequestedModelId) &&
                !string.IsNullOrWhiteSpace(log.CapabilityResolvedModelId))
            {
                displayFromLabel = capabilityRequestedLabel;
                displayToLabel = capabilityResolvedLabel;
            }

            string modelLabel =
                string.Equals(displayFromLabel, displayToLabel, StringComparison.OrdinalIgnoreCase)
                    ? displayToLabel
                    : $"{displayToLabel} ← {displayFromLabel}";

            // 實際參與的代理 / 模型常不只一個（研究 + 報告 + 表格 + 簡報…）。
            // 多於一個就在頂部「Agent / 模型」欄顯示「A + B」。Agent 一律用英文 id，與執行中即時視圖一致。
            var participantAgents = CollectParticipantAgents(log, agent);
            if (participantAgents.Count > 0)
                agent = string.Join(" + ", participantAgents);

            var participantModels = CollectParticipantModels(log, displayToLabel);
            if (participantModels.Count > 1)
                modelLabel = string.Join(" + ", participantModels);

            string status = log.SelectionMode switch
            {
                "API Auto" => "API Auto",
                "Auto" => "Rule Auto",
                "Collaboration" => UiLanguage.P("協作", "Collaboration"),
                _ => "Manual"
            };

            string mode = log.SelectionMode switch
            {
                "Manual" => "Manual",
                _ => "Auto"
            };

            string resolver = string.IsNullOrWhiteSpace(log.Resolver)
                ? "-"
                : log.Resolver;

            string taskSummary = BuildTaskSummary(log);

            string reason = string.IsNullOrWhiteSpace(log.ResolverReason)
                ? "-"
                : log.ResolverReason;

            string keywords = BuildKeywordSummary(log);
            if (string.IsNullOrWhiteSpace(keywords))
                keywords = "-";

            string capabilitySummary = BuildCapabilityTraceSummary(log);
            if (string.IsNullOrWhiteSpace(capabilitySummary))
                capabilitySummary = "-";

            var capabilityDetails = BuildCapabilityTraceDetails(log);

            string delegationSummary = "-";
            var delegationDetails = new List<string>();
            // 歷史 log 目前若尚未存 delegation trace，可先留空
            // 若你之後把 delegation trace 也加進 log，可在這裡一併接回

            string extra = BuildExtraSummary(log);
            if (string.IsNullOrWhiteSpace(extra))
                extra = "-";

            bool apiFallbackUsed =
                log.Resolver?.Contains("fallback", StringComparison.OrdinalIgnoreCase) == true;

            return new NodeDecisionViewData
            {
                Status = status,
                Mode = mode,
                Resolver = resolver,
                Agent = agent,
                Model = modelLabel,
                Participants = BuildParticipantSummary(log),
                TaskSummary = taskSummary,
                Reason = reason,
                Keywords = keywords,
                Extra = extra,

                CapabilitySummary = capabilitySummary,
                CapabilityDetails = capabilityDetails,

                DelegationSummary = delegationSummary,
                DelegationDetails = delegationDetails,

                CapabilityAdjusted = log.CapabilityAdjusted,
                RuntimeFallbackUsed = log.RuntimeFallbackUsed,
                ApiFallbackUsed = apiFallbackUsed,

                Steps = BuildSteps(
                    log,
                    requestedLabel,
                    plannedLabel,
                    actualLabel,
                    resolver,
                    apiFallbackUsed)
            };
        }

        private static NodeDecisionViewData CreateEmpty()
        {
            return new NodeDecisionViewData
            {
                Status = "Manual",
                Mode = "Manual",
                Resolver = "-",
                Model = "-",
                TaskSummary = "-",
                Reason = "-",
                Keywords = "-",
                Extra = "-",
                CapabilitySummary = "-",
                CapabilityDetails = Array.Empty<string>(),
                DelegationSummary = "-",
                DelegationDetails = Array.Empty<string>(),
                Agent = "-",
                Steps = Array.Empty<NodeDecisionStepViewData>()
            };
        }

        private static IReadOnlyList<NodeDecisionStepViewData> BuildSteps(
            AiExecutionLogEntry log,
            string requestedLabel,
            string plannedLabel,
            string actualLabel,
            string resolver,
            bool apiFallbackUsed)
        {
            var steps = new List<NodeDecisionStepViewData>();

            // §13 可讀執行日誌：最上方放一張「白話總結」卡，一眼看懂這次做了什麼（其餘卡是技術細節）。
            steps.Add(BuildReadableSummaryStep(log));

            steps.Add(BuildTaskModeStep(log));
            steps.Add(BuildModelSelectionStep(log, requestedLabel, plannedLabel, actualLabel));
            steps.Add(BuildAiTeamStep(log));
            steps.Add(BuildResolverStep(log, resolver, apiFallbackUsed));
            steps.Add(BuildCapabilityStep(log));
            steps.Add(BuildMemoryStep(log));
            steps.Add(BuildWorkspaceStep(log));
            steps.Add(BuildFallbackStep(log, apiFallbackUsed));
            steps.Add(BuildExecutionStep(log));

            return steps;
        }

        // §13：把整次執行濃縮成幾行白話 —— 模式（手動/自動）、任務、記憶、產出、結果。
        private static NodeDecisionStepViewData BuildReadableSummaryStep(AiExecutionLogEntry log)
        {
            string actual = GetModelLabel(log.ActualModelId);

            // 第一行就是清楚的 Manual/Auto 提示。
            string modeLine = log.SelectionMode switch
            {
                "Manual" => UiLanguage.P($"🛠 手動指定模型：{actual}", $"🛠 Model chosen manually: {actual}"),
                "API Auto" => UiLanguage.P($"🤖 自動選模型：{actual}（AI 先判斷任務再挑）", $"🤖 Model chosen automatically: {actual} (AI judged the task first)"),
                _ => UiLanguage.P($"🤖 自動選模型：{actual}（依關鍵字規則挑）", $"🤖 Model chosen automatically: {actual} (by keyword rules)")
            };

            var lines = new List<string> { modeLine };

            if (!string.IsNullOrWhiteSpace(log.TaskMode))
                // 分數留在下面的「任務判斷」區塊。摘要第一眼只要回答「它把這件事當成什麼」，
                // 一個沒有校準過、也不是機率的小數放在這裡只會讓人停下來想它是什麼意思。
                lines.Add(UiLanguage.P($"📌 判斷任務：{Safe(log.TaskMode)}", $"📌 Task: {Safe(log.TaskMode)}"));

            // §6 第一層輸出判斷：這次想要的輸出（報告 / 表格 / 簡報）。
            if (!string.IsNullOrWhiteSpace(log.OutputIntentSummary))
                lines.Add(UiLanguage.P($"🎯 想要的輸出：{Safe(log.OutputIntentSummary)}", $"🎯 Wanted output: {Safe(log.OutputIntentSummary)}"));

            // 各步驟實際收到了什麼——「要求有沒有傳到」要能查，不能只靠設計上應該如此。
            foreach (string line in BuildStepInputLines(log))
                lines.Add(line);

            var recall = log.MemoryRecall ?? MemoryRecallStats.Empty;
            if (recall.Suppressed)
                lines.Add(UiLanguage.P("🧠 記憶：本次略過", "🧠 Memory: skipped this time"));
            else if (recall.HasAny)
                lines.Add(UiLanguage.P($"🧠 記憶：用了偏好 {recall.PreferenceCount} 條、相關歷史 {recall.EpisodicCount} 筆", $"🧠 Memory: {recall.PreferenceCount} preference(s), {recall.EpisodicCount} related history item(s)"));

            // 即時資料：照實呈現拿到了什麼（官方來源 / 搜尋摘錄 / 服務失敗），不讓決策窗看起來「已完成查證」。
            var evidence = (log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.VerificationLabel))
                .Select(x => x.VerificationLabel)
                .Distinct()
                .ToList();
            if (evidence.Count > 0)
                lines.Add(UiLanguage.P("🔎 即時資料：", "🔎 Live data:") + string.Join("；", evidence));

            var files = CollectOutputFileNames(log);
            if (files.Count > 0)
                lines.Add(UiLanguage.P("📄 產出：", "📄 Output:") + string.Join("、", files));

            string durationText = FormatDuration(log.DurationMs);

            var failure = FailureExplainer.Explain(log.ErrorMessage);
            string resultLine = log.Success
                ? UiLanguage.P($"✅ 結果：成功 · {durationText}", $"✅ Result: success · {durationText}")
                : UiLanguage.P($"❌ 結果：失敗 · {(failure != null ? failure.Title : Safe(log.ErrorMessage))}", $"❌ Result: failed · {(failure != null ? failure.Title : Safe(log.ErrorMessage))}");

            if (log.Success && !string.IsNullOrWhiteSpace(log.CostDisplay))
                resultLine += $" · {log.CostDisplay}";

            lines.Add(resultLine);

            // 失敗時：把「該怎麼辦」直接寫出來（餘額不足、金鑰無效、沒網路…原本只有翻日誌才知道）。
            if (!log.Success && failure != null && !string.IsNullOrWhiteSpace(failure.Action))
                lines.Add(UiLanguage.P($"💡 怎麼辦：{failure.Action}", $"💡 What to do: {failure.Action}"));

            // 哪個模型不能用、換成了誰（自動後備本來只寫在技術區的英文行裡）。
            foreach (var attemptLine in FailureExplainer.DescribeAttempts(log.FallbackAttempts, GetModelLabel))
                lines.Add($"⚠ {attemptLine}");

            // 個人化可稽查：這次實際採用了哪些偏好、哪一項被本次請求覆寫。
            if (!string.IsNullOrWhiteSpace(log.EffectivePreferences))
                lines.Add(UiLanguage.P("⚙ 個人化：", "⚙ Personal:") + log.EffectivePreferences);

            // 成本可稽查：逐筆明細（用途 · 模型 · 用量 · 金額）。失敗的執行也照列——錢可能已經花了。
            var breakdown = UsageSummary.BuildBreakdownLines(log.UsageRecords);
            if (breakdown.Count > 0)
            {
                lines.Add(UiLanguage.P("💰 成本明細：", "💰 Cost breakdown:"));
                foreach (var item in breakdown)
                    lines.Add("　・" + item);
            }

            string detail = (string.Equals(log.SelectionMode, "Manual", StringComparison.Ordinal) ? UiLanguage.P("手動", "Manual") : UiLanguage.P("自動", "Auto"))
                            + $" · {actual} · " + (log.Success ? UiLanguage.P("成功", "success") : UiLanguage.P("失敗", "failed"));

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("執行摘要", "Run summary"),
                Detail = detail,
                State = log.Success ? NodeDecisionStepState.Success : NodeDecisionStepState.Error,
                Highlight = true,
                DetailLines = lines
            };
        }

        /// <summary>
        /// 頂部「參與」欄：這次實際花到錢的每一個模型／服務，以及它做了什麼。
        /// 來源是逐筆帳目（UsageRecords），不是選模型時的那一個決定——
        /// 一份簡報常常是 Perplexity 搜尋 + Claude 撰寫 + 圖片模型配圖，只顯示「最後那個模型」會誤導。
        /// 例：「Perplexity Sonar（搜尋研究）· Claude Sonnet 5（簡報撰寫、配圖企劃）· gpt-image-2（簡報封面圖）」
        /// </summary>
        private static string BuildParticipantSummary(AiExecutionLogEntry log)
        {
            var records = (log?.UsageRecords ?? Array.Empty<UsageRecord>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ModelId))
                .ToList();
            if (records.Count == 0)
                return "-";

            var parts = records
                .GroupBy(r => r.ModelId, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var purposes = g.Select(r => string.IsNullOrWhiteSpace(r.Purpose) ? UsageMeter.DefaultPurpose : r.Purpose)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(4)
                        .ToList();
                    return $"{ParticipantLabel(g.Key)}（{string.Join("、", purposes)}）";
                });

            return string.Join(" · ", parts);
        }

        /// <summary>
        /// 「各步驟實際收到了什麼」的顯示行。
        /// 每一步：用途、模型、字數、有沒有完整包含這個節點的原始要求、內容雜湊。
        /// 雜湊對得上完整內容檔，才算證明——不然就只是一句「有傳到」。
        /// </summary>
        internal static IEnumerable<string> BuildStepInputLines(AiExecutionLogEntry log)
        {
            var steps = log?.StepInputs ?? Array.Empty<StepInputRecord>();
            if (steps.Count == 0)
                yield break;

            int missing = steps.Count(s => !s.ContainsOriginalRequest);
            yield return missing == 0
                ? UiLanguage.P($"📥 各步驟收到的內容：{steps.Count} 步，每一步都完整包含原始要求", $"📥 Input to each step: {steps.Count} step(s), each containing the full original request")
                : UiLanguage.P($"📥 各步驟收到的內容：{steps.Count} 步，其中 {missing} 步**沒有完整包含**原始要求", $"📥 Input to each step: {steps.Count} step(s), {missing} of which **don't fully include** the original request");

            foreach (var s in steps)
            {
                string model = string.IsNullOrWhiteSpace(s.ModelId) ? "—" : ParticipantLabel(s.ModelId);
                yield return UiLanguage.P($"　· {s.Step}｜{model}｜{s.Chars:N0} 字｜", $"　· {s.Step}｜{model}｜{s.Chars:N0} chars｜") +
                             (s.ContainsOriginalRequest ? UiLanguage.P("含原始要求 ✓", "includes original request ✓") : UiLanguage.P("未完整包含原始要求 ✗", "doesn't fully include original request ✗")) +
                             $"｜{s.Sha256}";
            }

            if (!string.IsNullOrWhiteSpace(log!.StepInputLogPath))
                yield return UiLanguage.P($"　完整內容：{log.StepInputLogPath}", $"　Full content: {log.StepInputLogPath}");
        }

        /// <summary>
        /// 參與清單專用的模型標籤。GetModelLabel 對「不在 LLM 名錄裡」的 ID（圖片、影片、搜尋服務）
        /// 會退回預設模型的名字——那等於把 gpt-image-2 寫成一個語言模型的名字，是假資訊。
        /// 這裡認不得就照實顯示原始 ID。
        /// </summary>
        private static string ParticipantLabel(string modelId)
        {
            // 經 Perplexity Agent API 跑的模型（例如 anthropic/claude-sonnet-5-5）：
            // 模型就是使用者認得的那一個，只是搜尋是透過 Perplexity 的服務做的——兩件事都照實寫。
            if (ModelScope.IsAgentApiModel(modelId))
                return ParticipantLabel(ModelScope.FromAgentApiModel(modelId)) + UiLanguage.P(" 經 Perplexity", " via Perplexity");

            var legacy = AiModelRegistry.TryGetLegacyDisplayName(modelId);
            if (legacy != null) return legacy;

            var def = AiModelHelper.GetDefinition(modelId);
            return !string.IsNullOrWhiteSpace(def.DisplayName) &&
                   string.Equals(def.Id, modelId, StringComparison.OrdinalIgnoreCase)
                ? def.DisplayName
                : modelId;
        }

        // 頂部「Agent」欄用：實際參與的代理（含主代理 + 各產出物來源代理），去重後回英文 id 清單。
        private static List<string> CollectParticipantAgents(AiExecutionLogEntry log, string primaryAgentId)
        {
            var ids = new List<string>();

            void Add(string? id)
            {
                if (string.IsNullOrWhiteSpace(id) || id.Trim() == "-")
                    return;
                string clean = id.Trim();
                if (!ids.Contains(clean, StringComparer.OrdinalIgnoreCase))
                    ids.Add(clean);
            }

            Add(primaryAgentId);
            foreach (var a in log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>())
                Add(a?.SourceAgentId);

            return ids;
        }

        // 手機鏡像（§17）用：回本次執行「實際參與的模型」顯示標籤清單（主模型 + 各產出物模型，去重）。
        // 與決策窗頂部「模型」欄同一來源，避免手機只顯示單一 committed 模型。
        public static IReadOnlyList<string> GetParticipantModelLabels(AiExecutionLogEntry? log)
        {
            if (log == null)
                return Array.Empty<string>();
            string primary = GetModelLabel(
                string.IsNullOrWhiteSpace(log.ActualModelId) ? log.PlannedModelId : log.ActualModelId);
            return CollectParticipantModels(log, primary);
        }

        // 「真的動手交付」的產出物種類——與 BuildAiTeamStep 會掛上非系統 AI 角色的集合一致。
        // 刻意排除 orchestration/workflow/downstream_node_plan/task_plan 等「路由規劃 meta」：
        // 它們被標的是節點「被規劃到」的模型，常因 fallback 從未實際執行，算進參與模型會與
        // 「參與的 AI」對不上、且把沒跑過的模型講成參與者＝違反可稽查。
        private static readonly HashSet<string> WorkDeliveringArtifactKinds = new(StringComparer.OrdinalIgnoreCase)
        {
            "facts", "verified_facts",
            "search", "search_summary",
            "analysis", "reasoning_analysis",
            "presentation", "presentation_outline",
            "media", "image", "video",
            "code", "code_snapshot", "diff", "patch", "code_diff",
            "final", "final_synthesis"
        };

        // 頂部「模型」欄用：實際參與的模型（主模型 + 各「真的動手」產出物的模型），去重後回顯示標籤清單。
        private static List<string> CollectParticipantModels(AiExecutionLogEntry log, string primaryModelLabel)
        {
            var labels = new List<string>();

            void Add(string? label)
            {
                if (string.IsNullOrWhiteSpace(label) || label.Trim() == "-")
                    return;
                if (!labels.Contains(label))
                    labels.Add(label);
            }

            Add(primaryModelLabel);

            // 有逐筆帳目就以帳目為準：真的被呼叫、真的計費的每一個模型都列出來。
            // 2026-10-29 使用者回報：協作模式兩個模型一起回答，「模型」欄卻只寫 GPT-5.6 Sol——
            // 原本這一欄只看「主模型＋產出物上掛的模型」，協作的第二個模型、Auto 的模型自動選擇、
            // 文件技能、配圖模型都不在裡面。搜尋 API 回傳的是連結清單、不是模型，不列。
            var usage = (log.UsageRecords ?? Array.Empty<UsageRecord>())
                .Where(u => u != null && !string.IsNullOrWhiteSpace(u.ModelId) &&
                            !string.Equals(u.Kind, UsageKinds.Search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (usage.Count > 0)
            {
                foreach (var u in usage)
                    Add(ParticipantLabel(ModelScope.FromAgentApiModel(u.ModelId)));
                return labels;
            }

            foreach (var a in log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>())
            {
                if (a == null || string.IsNullOrWhiteSpace(a.ModelId))
                    continue;

                string kind = (a.ArtifactKind ?? "").Trim().ToLowerInvariant();
                string itemType = (a.ItemType ?? "").Trim().ToLowerInvariant();
                string k = (kind.Length == 0 || kind == "artifact") ? itemType : kind;

                if (!WorkDeliveringArtifactKinds.Contains(k))
                    continue;

                Add(GetModelLabel(a.ModelId));
            }

            return labels;
        }

        private static List<string> CollectOutputFileNames(AiExecutionLogEntry log)
        {
            var names = new List<string>();

            foreach (var a in log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>())
            {
                if (a == null)
                    continue;

                string kind = (a.ArtifactKind ?? "").Trim().ToLowerInvariant();
                string itemType = (a.ItemType ?? "").Trim().ToLowerInvariant();
                bool isFile = kind is "file" or "generated_file" || itemType is "file" or "generated_file";
                if (!isFile)
                    continue;

                string name = StripGeneratedFilePrefix(a.Title);
                if (!string.IsNullOrWhiteSpace(name) && name != "產生檔案失敗" && !names.Contains(name))
                    names.Add(name);
            }

            return names;
        }

        private static NodeDecisionStepViewData BuildTaskModeStep(AiExecutionLogEntry log)
        {
            var lines = new List<string>
            {
                UiLanguage.P($"判斷為：{Safe(log.TaskMode)}", $"Judged as: {Safe(log.TaskMode)}"),
                UiLanguage.P($"判斷分數：{log.Confidence:0.00}（0～1 的比對分數，不是機率）", $"Score: {log.Confidence:0.00} (a 0–1 match score, not a probability)")
            };

            if (!string.IsNullOrWhiteSpace(log.ResolverReason))
                lines.Add(UiLanguage.P($"判斷理由：{log.ResolverReason}", $"Reason: {log.ResolverReason}"));

            if (log.ResolverKeywords != null && log.ResolverKeywords.Count > 0)
            {
                var keywords = log.ResolverKeywords
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                lines.Add(UiLanguage.P("命中的字詞：", "Matched words:") + string.Join("、", keywords));
            }

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("任務判斷", "Task detection"),
                IsTechnical = true,
                Detail = UiLanguage.P($"{Safe(log.TaskMode)}・分數 {log.Confidence:0.00}", $"{Safe(log.TaskMode)} · score {log.Confidence:0.00}"),
                State = NodeDecisionStepState.Info,
                Highlight = true,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildModelSelectionStep(
            AiExecutionLogEntry log,
            string requestedLabel,
            string plannedLabel,
            string actualLabel)
        {
            string summary;

            if (string.Equals(requestedLabel, actualLabel, StringComparison.OrdinalIgnoreCase))
                summary = actualLabel;
            else
                summary = $"{requestedLabel} → {actualLabel}";

            // 只留使用者看得懂的：選了誰、實際跑了誰、為什麼換、這個模型的等級與擅長。
            var lines = new List<string>();

            if (!string.Equals(requestedLabel, actualLabel, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(UiLanguage.P($"原本要用：{requestedLabel}", $"Originally requested: {requestedLabel}"));
                lines.Add(UiLanguage.P($"實際執行：{actualLabel}", $"Actually ran: {actualLabel}"));
            }
            else
            {
                lines.Add(UiLanguage.P($"使用模型：{actualLabel}", $"Model used: {actualLabel}"));
            }

            if (log.CapabilityAdjusted &&
                !string.IsNullOrWhiteSpace(log.CapabilityRequestedModelId) &&
                !string.IsNullOrWhiteSpace(log.CapabilityResolvedModelId))
            {
                string why = string.IsNullOrWhiteSpace(log.CapabilityReason) ? UiLanguage.P("這個任務需要的能力它沒有", "it lacks a capability this task needs") : Safe(log.CapabilityReason);
                lines.Add(UiLanguage.P($"換模型原因：{why}", $"Why the model changed: {why}"));
            }

            foreach (var attemptLine in FailureExplainer.DescribeAttempts(log.FallbackAttempts, GetModelLabel))
                lines.Add($"⚠ {attemptLine}");

            var actualDef = AiModelHelper.GetDefinition(log.ActualModelId);
            if (actualDef != null)
            {
                lines.Add(UiLanguage.P($"成本等級：{actualDef.CostTierLabel}", $"Cost tier: {actualDef.CostTierLabel}"));
                lines.Add(UiLanguage.P($"擅長：{actualDef.CapabilitySummary}", $"Good at: {actualDef.CapabilitySummary}"));
            }

            var state =
                string.Equals(requestedLabel, plannedLabel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(plannedLabel, actualLabel, StringComparison.OrdinalIgnoreCase)
                    ? NodeDecisionStepState.Success
                    : NodeDecisionStepState.Warning;

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("使用的模型", "Model used"),
                Detail = summary,
                State = state,
                Highlight = true,
                DetailLines = lines
            };
        }

        // 「參與的 AI」：用白話列出這個節點裡有哪些 AI 參與、各自做了什麼。
        //
        // 設計原則：以「產出物的種類（ArtifactKind）」反推是哪個 AI 做的，而不是讀每個 artifact
        // 自帶的 ModelId / SourceAgentId——後者對 facts/search/presentation/file 常常沒填或被覆寫，
        // 直接用會得到「圖片 — 事實」這種錯誤掛名。種類是可靠的，且每種產出物固定由哪個 AI 產生是已知的。
        // 同一個 AI 做的多件事聚合成一行（例：研究 AI — 查資料、查證事實）。
        private static NodeDecisionStepViewData BuildAiTeamStep(AiExecutionLogEntry log)
        {
            var roles = new List<TeamRole>();
            var index = new Dictionary<string, TeamRole>(StringComparer.Ordinal);

            TeamRole RoleFor(string key, string icon, string label, int order)
            {
                if (!index.TryGetValue(key, out var r))
                {
                    r = new TeamRole { Icon = icon, Label = label, Order = order };
                    index[key] = r;
                    roles.Add(r);
                }
                return r;
            }

            var artifacts = (log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>())
                .Where(x => x != null)
                .ToList();

            foreach (var a in artifacts)
            {
                string kind = (a.ArtifactKind ?? "").Trim().ToLowerInvariant();
                string itemType = (a.ItemType ?? "").Trim().ToLowerInvariant();
                string format = (a.ContentFormat ?? "").Trim().ToLowerInvariant();
                string k = (kind.Length == 0 || kind == "artifact") ? itemType : kind;

                switch (k)
                {
                    // 編排 / workflow 計畫是「路由 meta」，不是某個 AI 做的內容交付，且其 model 是節點被路由到的
                    // 模型（簡報任務常被路由成 pplx），掛上去會誤導。這張卡只列真正做事的 AI，編排細節留在 Workspace/Capability 步驟。
                    case "workflow":
                    case "orchestration":
                    case "orchestration_plan":
                        break;

                    case "facts":
                    case "verified_facts":
                        // 只有含官方/報價/新聞等級來源才叫「查證事實」；全是搜尋摘錄就照實寫。
                        string verification = a.VerificationLabel ?? "";
                        bool searchOnly = verification.Length > 0 &&
                                          !verification.Contains("官方") &&
                                          !verification.Contains("報價") &&
                                          !verification.Contains("新聞");
                        RoleFor("research", "🔍", UiLanguage.P("研究 AI", "Research AI"), 1).Add(searchOnly ? UiLanguage.P("整理搜尋摘錄", "Summarize search excerpts") : UiLanguage.P("查證事實", "Verify facts"), a.ModelId);
                        break;

                    case "search":
                    case "search_summary":
                        RoleFor("research", "🔍", UiLanguage.P("研究 AI", "Research AI"), 1).Add(UiLanguage.P("查資料", "Look things up"), a.ModelId);
                        break;

                    case "analysis":
                    case "reasoning_analysis":
                        RoleFor("reasoning", "🧠", UiLanguage.P("分析 AI", "Analysis AI"), 2).Add(UiLanguage.P("分析推理", "Analysis & reasoning"), a.ModelId);
                        break;

                    case "presentation":
                    case "presentation_outline":
                        RoleFor("author", "📊", UiLanguage.P("簡報 AI", "Deck AI"), 3).Add(UiLanguage.P("撰寫簡報", "Write the deck"), a.ModelId);
                        break;

                    case "media":
                    case "image":
                        if (format.Contains("video") || itemType.Contains("video"))
                            RoleFor("video", "🎬", UiLanguage.P("影片 AI", "Video AI"), 5).Add(UiLanguage.P("生成影片", "Generate video"), a.ModelId);
                        else
                            RoleFor("image", "🖼", UiLanguage.P("圖片 AI", "Image AI"), 4).Add(UiLanguage.P("生成圖片", "Generate image"), a.ModelId);
                        break;

                    case "video":
                        RoleFor("video", "🎬", UiLanguage.P("影片 AI", "Video AI"), 5).Add(UiLanguage.P("生成影片", "Generate video"), a.ModelId);
                        break;

                    case "code":
                    case "code_snapshot":
                        RoleFor("coder", "💻", UiLanguage.P("程式 AI", "Code AI"), 6).Add(UiLanguage.P("產生程式碼", "Write code"), a.ModelId);
                        break;

                    case "diff":
                    case "patch":
                    case "code_diff":
                        RoleFor("coder", "💻", UiLanguage.P("程式 AI", "Code AI"), 6).Add(UiLanguage.P("產生修改", "Write changes"), a.ModelId);
                        break;

                    case "final":
                    case "final_synthesis":
                        RoleFor("primary", "🧠", UiLanguage.P("統整 AI", "Synthesis AI"), 7).Add(UiLanguage.P("統整最終答案", "Combine the final answer"), a.ModelId);
                        break;

                    case "file":
                    case "generated_file":
                        var f = RoleFor("file", "📄", UiLanguage.P("檔案輸出", "File output"), 8);
                        f.IsSystem = true;
                        f.Add(StripGeneratedFilePrefix(a.Title), null);
                        break;

                    // sandbox / downstream_node_plan / 其餘 meta：不算「某個 AI 做了一件交付的事」，略過。
                }
            }

            // 純對話（沒有任何管線產出物）：就是主模型直接回答。
            bool hasAiRole = roles.Any(r => !r.IsSystem);
            if (!hasAiRole)
            {
                RoleFor("primary", "🧠", "", 7)
                    .Add(UiLanguage.P("回答", "Answer"), log.ActualModelId);
            }

            var lines = roles
                .OrderBy(r => r.Order)
                .Select(r => r.Render())
                .ToList();

            int aiCount = roles.Count(r => !r.IsSystem);
            string detail = aiCount <= 1
                ? (roles.FirstOrDefault(r => !r.IsSystem)?.Head() ?? GetModelLabel(log.ActualModelId))
                : UiLanguage.P($"{aiCount} 個 AI 協作", $"{aiCount} AIs collaborating");

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("參與的 AI", "AIs involved"),
                Detail = detail,
                State = NodeDecisionStepState.Info,
                Highlight = aiCount > 1,
                DetailLines = lines
            };
        }

        // 一個參與角色（依產出物種類聚合）：固定的 AI 角色 + 它做過的事 + 已知的模型名。
        private sealed class TeamRole
        {
            public string Icon = "";
            public string Label = "";
            public string Model = "";
            public int Order;
            public bool IsSystem;
            private readonly List<string> _actions = new();

            public void Add(string action, string? modelId)
            {
                if (!string.IsNullOrWhiteSpace(action) && !_actions.Contains(action))
                    _actions.Add(action);

                // 只在 artifact 真的帶了模型 id 時才掛名，避免假資訊。
                if (string.IsNullOrWhiteSpace(Model) &&
                    !string.IsNullOrWhiteSpace(modelId) &&
                    modelId.Trim() != "-")
                {
                    Model = GetModelLabel(modelId);
                }
            }

            // 「角色（模型）」；模型未知就只顯示角色，不硬湊。
            public string Head()
            {
                if (string.IsNullOrWhiteSpace(Label))
                    return string.IsNullOrWhiteSpace(Model) ? "AI" : Model;
                return string.IsNullOrWhiteSpace(Model) ? Label : $"{Label}（{Model}）";
            }

            public string Render()
            {
                string actions = _actions.Count > 0 ? string.Join("、", _actions) : "—";
                return $"{Icon} {Head()} — {actions}";
            }
        }

        // "Generated File - NVIDIA….pptx" → "NVIDIA….pptx"；失敗則白話化。
        private static string StripGeneratedFilePrefix(string? title)
        {
            string t = (title ?? "").Trim();
            const string prefix = "Generated File - ";
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                t = t.Substring(prefix.Length).Trim();

            if (t.Length == 0 || string.Equals(t, "failed", StringComparison.OrdinalIgnoreCase))
                return "產生檔案失敗";

            return Trim(t, 40);
        }

        private static NodeDecisionStepViewData BuildResolverStep(
            AiExecutionLogEntry log,
            string resolver,
            bool apiFallbackUsed)
        {
            var lines = new List<string>
            {
                UiLanguage.P($"挑選方式：{Safe(resolver)}", $"Selection: {Safe(resolver)}"),
                UiLanguage.P("模式：", "Mode: ") + (string.Equals(log.SelectionMode, "Manual", StringComparison.Ordinal) ? UiLanguage.P("手動指定", "manual") : UiLanguage.P("自動選擇", "automatic"))
            };

            if (!string.IsNullOrWhiteSpace(log.ResolverReason))
                lines.Add(UiLanguage.P($"理由：{log.ResolverReason}", $"Reason: {log.ResolverReason}"));

            if (apiFallbackUsed)
                lines.Add(UiLanguage.P("讓 AI 判斷這一步沒成功，已改用關鍵字規則挑模型。", "Letting the AI decide failed for this step, so keyword rules were used to pick the model."));

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("怎麼挑模型的", "How the model was chosen"),
                IsTechnical = true,
                Detail = Safe(resolver),
                State = apiFallbackUsed ? NodeDecisionStepState.Warning : NodeDecisionStepState.Info,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildCapabilityStep(AiExecutionLogEntry log)
        {
            string detail;
            IReadOnlyList<string> lines;

            var traceSummary = BuildCapabilityTraceSummary(log);
            var traceDetails = BuildCapabilityTraceDetails(log);

            if (!string.IsNullOrWhiteSpace(traceSummary) && traceSummary != "-")
            {
                detail = traceSummary;
                lines = traceDetails.Count > 0
                    ? traceDetails
                    : BuildCapabilityGuardLines(log);
            }
            else
            {
                detail = log.CapabilityAdjusted
                    ? $"{GetModelLabel(log.CapabilityRequestedModelId)} → {GetModelLabel(log.CapabilityResolvedModelId)}"
                    : "OK";

                lines = BuildCapabilityGuardLines(log);
            }

            var state =
                (!string.IsNullOrWhiteSpace(traceSummary) && traceSummary != "-") || log.CapabilityAdjusted
                    ? NodeDecisionStepState.Warning
                    : NodeDecisionStepState.Success;

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("模型能力檢查", "Model capability check"),
                IsTechnical = true,
                Detail = detail,
                State = state,
                Highlight = !string.IsNullOrWhiteSpace(traceSummary) && traceSummary != "-",
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildMemoryStep(AiExecutionLogEntry log)
        {
            var recall = log.MemoryRecall ?? MemoryRecallStats.Empty;

            if (recall.Suppressed)
            {
                return new NodeDecisionStepViewData
                {
                    Title = UiLanguage.P("記憶", "Memory"),
                    IsTechnical = true,
                    Detail = UiLanguage.P("本次略過記憶", "Memory skipped this time"),
                    State = NodeDecisionStepState.Info,
                    DetailLines = new List<string>
                    {
                        string.IsNullOrWhiteSpace(recall.SuppressReason)
                            ? UiLanguage.P("本次未注入記憶。", "No memory was injected this time.")
                            : recall.SuppressReason
                    }
                };
            }

            string detail = UiLanguage.P($"偏好 {recall.PreferenceCount}・記憶 {recall.EpisodicCount}", $"Preferences {recall.PreferenceCount} · memories {recall.EpisodicCount}");

            var lines = new List<string>
            {
                UiLanguage.P($"偏好（一律注入）：{recall.PreferenceCount} 條", $"Preferences (always injected): {recall.PreferenceCount}"),
                UiLanguage.P($"相關記憶（召回）：{recall.EpisodicCount} 筆", $"Related memories (recalled): {recall.EpisodicCount}")
                    + (recall.EpisodicCount > 0 ? UiLanguage.P($"（Agent {recall.AgentCount}・共享 {recall.SharedCount}）", $" (agent {recall.AgentCount} · shared {recall.SharedCount})") : "")
            };

            if (recall.Details != null && recall.Details.Count > 0)
            {
                lines.Add(UiLanguage.P("--- 本次召回內容 ---", "--- Recalled this time ---"));
                lines.AddRange(recall.Details);
            }
            else if (!recall.HasAny)
            {
                lines.Add(UiLanguage.P("本次沒有可用的偏好或相關記憶。", "No preferences or related memories were available this time."));
            }

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("記憶", "Memory"),
                IsTechnical = true,
                Detail = detail,
                State = recall.HasAny ? NodeDecisionStepState.Success : NodeDecisionStepState.Info,
                Highlight = recall.HasAny,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildWorkspaceStep(AiExecutionLogEntry log)
        {
            var detailLines = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.WorkspaceSummary))
            {
                detailLines.AddRange(
                    log.WorkspaceSummary
                        .Replace("\r\n", "\n")
                        .Replace('\r', '\n')
                        .Split('\n')
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim()));
            }

            if (log.WorkspaceArtifactDetails != null && log.WorkspaceArtifactDetails.Count > 0)
            {
                if (detailLines.Count > 0)
                    detailLines.Add("--- Artifacts ---");

                detailLines.AddRange(log.WorkspaceArtifactDetails.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            if ((log.WorkspaceArtifactDetails == null || log.WorkspaceArtifactDetails.Count == 0) &&
                log.WorkspaceArtifacts != null &&
                log.WorkspaceArtifacts.Count > 0)
            {
                if (detailLines.Count > 0)
                    detailLines.Add("--- Artifact Index ---");

                detailLines.AddRange(log.WorkspaceArtifacts
                    .Where(x => x != null)
                    .Take(20)
                    .Select(x => $"{Safe(x.Title)}　（{Safe(x.ArtifactKind)}・{Safe(x.ContentFormat)}・" +
                                 $"{(x.IsUserVisible ? "使用者看得到" : "只給系統內部用")}・來自 {Safe(x.SourceAgentId)}）"));
            }

            string detail = "-";
            var state = NodeDecisionStepState.Info;

            if (detailLines.Count == 0)
            {
                detail = UiLanguage.P("這次沒有產生共享資料", "No shared data was produced this time");
            }
            else
            {
                var artifactCount = log.WorkspaceArtifacts?.Count ?? 0;
                if (artifactCount == 0)
                {
                    artifactCount = log.WorkspaceArtifactDetails?
                        .Count(x => x.StartsWith("Artifact:", StringComparison.OrdinalIgnoreCase)) ?? 0;
                }

                var factCount = log.WorkspaceArtifactDetails?
                    .Count(x => x.TrimStart().StartsWith("Fact:", StringComparison.OrdinalIgnoreCase)) ?? 0;

                if (factCount == 0 && log.WorkspaceArtifacts != null)
                    factCount = log.WorkspaceArtifacts.Sum(x => x?.FactCount ?? 0);

                detail = UiLanguage.P($"資料 {artifactCount} 項・查到的事實 {factCount} 筆", $"{artifactCount} data item(s) · {factCount} fact(s) found");
                state = factCount > 0 ? NodeDecisionStepState.Success : NodeDecisionStepState.Info;
            }

            var artifactRecords = log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>();

            if (detail == "-" && artifactRecords.Count > 0)
            {
                int visible = artifactRecords.Count(x => x != null && x.IsUserVisible);
                detail = UiLanguage.P($"產出物 {artifactRecords.Count} 項（可見 {visible}）", $"{artifactRecords.Count} output(s) ({visible} visible)");
                state = NodeDecisionStepState.Success;
            }

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("共享工作區", "Shared workspace"),
                IsTechnical = true,
                Detail = detail,
                State = state,
                Highlight = detailLines.Count > 0 || artifactRecords.Count > 0,
                DetailLines = detailLines,
                WorkspaceArtifacts = artifactRecords
            };
        }

        private static IReadOnlyList<string> BuildCapabilityGuardLines(AiExecutionLogEntry log)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequestedModelId))
                lines.Add(UiLanguage.P($"原本要用：{GetModelLabel(log.CapabilityRequestedModelId)}", $"Originally requested: {GetModelLabel(log.CapabilityRequestedModelId)}"));

            if (!string.IsNullOrWhiteSpace(log.CapabilityResolvedModelId))
                lines.Add(UiLanguage.P($"實際改用：{GetModelLabel(log.CapabilityResolvedModelId)}", $"Switched to: {GetModelLabel(log.CapabilityResolvedModelId)}"));

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequired))
                lines.Add(UiLanguage.P($"這個任務需要：{log.CapabilityRequired}", $"This task needs: {log.CapabilityRequired}"));

            if (!string.IsNullOrWhiteSpace(log.CapabilityMissing))
                lines.Add(UiLanguage.P($"原本那個模型沒有：{log.CapabilityMissing}", $"The original model lacks: {log.CapabilityMissing}"));

            if (log.CapabilityStreamingAdjusted)
                lines.Add(UiLanguage.P("已關閉逐字輸出（這個模型不支援）", "Streaming output turned off (this model doesn't support it)"));

            if (!string.IsNullOrWhiteSpace(log.CapabilityReason))
                lines.Add(UiLanguage.P($"原因：{log.CapabilityReason}", $"Reason: {log.CapabilityReason}"));

            if (lines.Count == 0)
                lines.Add(UiLanguage.P("模型能力符合這個任務，沒有調整。", "The model's capabilities fit this task; nothing was changed."));

            return lines;
        }

        private static NodeDecisionStepViewData BuildFallbackStep(AiExecutionLogEntry log, bool apiFallbackUsed)
        {
            string detail = BuildFallbackDetail(log, apiFallbackUsed);
            if (string.IsNullOrWhiteSpace(detail))
                detail = UiLanguage.P("無", "None");

            var lines = new List<string>();

            if (apiFallbackUsed)
                lines.Add(UiLanguage.P("讓 AI 挑模型失敗，改用關鍵字規則。", "Letting the AI pick the model failed, so keyword rules were used."));

            if (log.RuntimeFallbackUsed && !string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                lines.Add(UiLanguage.P($"經過：{log.RuntimeFallbackSummary}", $"What happened: {log.RuntimeFallbackSummary}"));

            if (log.FallbackAttempts != null && log.FallbackAttempts.Count > 0)
            {
                foreach (var attempt in log.FallbackAttempts)
                {
                    if (attempt == null)
                        continue;

                    string modelLabel = GetModelLabel(attempt.ModelId);
                    string symbol = attempt.Success ? "✅" : "❌";
                    string reason = string.IsNullOrWhiteSpace(attempt.Reason) ? "-" : attempt.Reason;
                    string error = string.IsNullOrWhiteSpace(attempt.ErrorMessage) ? "" : $" / {attempt.ErrorMessage}";

                    lines.Add($"{attempt.AttemptIndex}. {modelLabel} {symbol} / {reason}{error}");
                }
            }

            if (lines.Count == 0)
                lines.Add(UiLanguage.P("第一個模型就成功了，沒有換過。", "The first model succeeded; no switch was needed."));

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("換過的模型", "Model switches"),
                IsTechnical = true,
                Detail = detail,
                State = (log.RuntimeFallbackUsed || apiFallbackUsed)
                    ? NodeDecisionStepState.Warning
                    : NodeDecisionStepState.Success,
                DetailLines = lines
            };
        }

        /// <summary>毫秒 → 人類可讀時長：850 毫秒 / 12.4 秒 / 2 分 50 秒。</summary>
        internal static string FormatDuration(long ms)
        {
            if (ms < 1000) return UiLanguage.P($"{ms} 毫秒", $"{ms} ms");
            if (ms < 60_000) return UiLanguage.P($"{ms / 1000.0:0.#} 秒", $"{ms / 1000.0:0.#} s");
            long totalSec = (long)Math.Round(ms / 1000.0);
            return UiLanguage.P($"{totalSec / 60} 分 {totalSec % 60} 秒", $"{totalSec / 60} min {totalSec % 60} s");
        }

        private static NodeDecisionStepViewData BuildExecutionStep(AiExecutionLogEntry log)
        {
            string executionDetail = log.Success
                ? UiLanguage.P($"成功 · {FormatDuration(log.DurationMs)}", $"Success · {FormatDuration(log.DurationMs)}")
                : UiLanguage.P($"失敗 / {Safe(log.ErrorMessage)}", $"Failed / {Safe(log.ErrorMessage)}");

            if (!string.IsNullOrWhiteSpace(log.CostDisplay))
                executionDetail += $" · {log.CostDisplay}";

            var lines = new List<string>
            {
                UiLanguage.P($"開始：{log.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}", $"Start: {log.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"),
                UiLanguage.P($"結束：{log.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}", $"End: {log.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"),
                UiLanguage.P($"花了：{FormatDuration(log.DurationMs)}", $"Took: {FormatDuration(log.DurationMs)}"),
                UiLanguage.P("結果：", "Result: ") + (log.Success ? UiLanguage.P("成功", "success") : UiLanguage.P("失敗", "failed"))
            };

            if (!string.IsNullOrWhiteSpace(log.CostDisplay))
                lines.Add(UiLanguage.P($"成本：{log.CostDisplay}（讀進 {log.InputTokens}、寫出 {log.OutputTokens} 個 token）", $"Cost: {log.CostDisplay} ({log.InputTokens} tokens in, {log.OutputTokens} out)"));

            foreach (var r in log.UsageRecords ?? Array.Empty<UsageRecord>())
            {
                string amount = r.Kind == UsageKinds.Llm
                    ? UiLanguage.P($"讀進 {r.InputTokens}、寫出 {r.OutputTokens} 個 token", $"{r.InputTokens} tokens in, {r.OutputTokens} out")
                    : $"{r.Quantity:0.##} {r.Unit}";
                string note = string.IsNullOrWhiteSpace(r.Note) ? "" : $"（{r.Note}）";
                string estimated = r.IsActual ? "" : UiLanguage.P("（估算）", " (estimate)");
                lines.Add($"{r.AtUtc.ToLocalTime():HH:mm:ss}　{r.Purpose}・{GetModelLabel(r.ModelId)}・{amount}・{ModelCostEstimator.FormatTwd(r.UsdCost)}{estimated}{note}");
            }

            if (!string.IsNullOrWhiteSpace(log.ErrorMessage))
                lines.Add(UiLanguage.P($"錯誤訊息：{log.ErrorMessage}", $"Error message: {log.ErrorMessage}"));

            return new NodeDecisionStepViewData
            {
                Title = UiLanguage.P("執行細節", "Run details"),
                IsTechnical = true,
                Detail = executionDetail,
                State = log.Success ? NodeDecisionStepState.Success : NodeDecisionStepState.Error,
                Highlight = !log.Success,
                DetailLines = lines
            };
        }

        private static string BuildTaskSummary(AiExecutionLogEntry log)
        {
            return UiLanguage.P($"{Safe(log.TaskMode)}・分數 {log.Confidence:0.00}・{FormatDuration(log.DurationMs)}", $"{Safe(log.TaskMode)} · score {log.Confidence:0.00} · {FormatDuration(log.DurationMs)}");
        }

        private static string BuildKeywordSummary(AiExecutionLogEntry log)
        {
            if (log?.ResolverKeywords == null || log.ResolverKeywords.Count == 0)
                return "";

            var keywords = log.ResolverKeywords
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (keywords.Count == 0)
                return "";

            return "keywords: " + string.Join(", ", keywords);
        }

        private static string BuildCapabilityTraceSummary(AiExecutionLogEntry log)
        {
            if (log?.CapabilityTrace == null || log.CapabilityTrace.Count == 0)
                return "";

            return AgentCapabilityTraceFormatter.BuildSummary(log.CapabilityTrace);
        }

        private static IReadOnlyList<string> BuildCapabilityTraceDetails(AiExecutionLogEntry log)
        {
            if (log?.CapabilityTrace == null || log.CapabilityTrace.Count == 0)
                return new List<string>();

            return AgentCapabilityTraceFormatter.BuildDetailLines(log.CapabilityTrace);
        }
        private static string Trim(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static string BuildExtraSummary(AiExecutionLogEntry log)
        {
            var extraParts = new List<string>();

            string capabilityTraceSummary = BuildCapabilityTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(capabilityTraceSummary) && capabilityTraceSummary != "-")
                extraParts.Add("capability: " + capabilityTraceSummary);

            if (!string.IsNullOrWhiteSpace(log.CapabilityReason))
                extraParts.Add(log.CapabilityReason);

            string capabilityDetail = BuildCapabilityDetail(log);
            if (!string.IsNullOrWhiteSpace(capabilityDetail))
                extraParts.Add("capability-guard: " + capabilityDetail);

            if (!string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                extraParts.Add(log.RuntimeFallbackSummary);

            string trace = BuildFallbackTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(trace))
                extraParts.Add(trace);

            if (!log.Success && !string.IsNullOrWhiteSpace(log.ErrorMessage))
                extraParts.Add(log.ErrorMessage);

            if (!string.IsNullOrWhiteSpace(log.WorkspaceSummary))
                extraParts.Add(UiLanguage.P("多代理協作：", "Multi-agent collaboration:") + Trim(log.WorkspaceSummary, 360));


            return extraParts.Count == 0 ? "" : string.Join(" / ", extraParts);
        }

        private static string BuildCapabilityDetail(AiExecutionLogEntry log)
        {
            if (log == null || !log.CapabilityAdjusted)
                return "";

            string requestedLabel = GetModelLabel(log.CapabilityRequestedModelId);
            string resolvedLabel = GetModelLabel(log.CapabilityResolvedModelId);

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.CapabilityMissing))
                parts.Add(UiLanguage.P($"缺少 {log.CapabilityMissing}", $"missing {log.CapabilityMissing}"));

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequired) &&
                !string.Equals(log.CapabilityRequired, AiModelCapability.None.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(UiLanguage.P($"需要 {log.CapabilityRequired}", $"needs {log.CapabilityRequired}"));
            }

            if (!string.Equals(requestedLabel, resolvedLabel, StringComparison.OrdinalIgnoreCase))
                parts.Add($"{requestedLabel} → {resolvedLabel}");

            if (log.CapabilityStreamingAdjusted)
                parts.Add(UiLanguage.P("改成不逐字輸出", "switched to non-streaming output"));

            return parts.Count == 0 ? "" : string.Join(" / ", parts);
        }

        private static string BuildFallbackDetail(AiExecutionLogEntry log, bool apiFallbackUsed)
        {
            var parts = new List<string>();

            if (apiFallbackUsed)
                parts.Add("resolver fallback");

            if (log.RuntimeFallbackUsed && !string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                parts.Add(log.RuntimeFallbackSummary);

            string trace = BuildFallbackTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(trace))
                parts.Add(trace);

            return parts.Count == 0 ? "" : string.Join(" / ", parts);
        }

        private static string BuildFallbackTraceSummary(AiExecutionLogEntry log)
        {
            if (log?.FallbackAttempts == null || log.FallbackAttempts.Count == 0)
                return "";

            var parts = new List<string>();

            foreach (var attempt in log.FallbackAttempts)
            {
                if (attempt == null || string.IsNullOrWhiteSpace(attempt.ModelId))
                    continue;

                string modelLabel = GetModelLabel(attempt.ModelId);
                string symbol = attempt.Success ? "✅" : "❌";

                parts.Add($"{attempt.AttemptIndex}.{modelLabel}{symbol}");
            }

            if (parts.Count == 0)
                return "";

            return "trace: " + string.Join(" → ", parts);
        }

        private static string GetModelLabel(string? modelId)
        {
            // Agent API 的 id 先轉回內部 id：GetDefinition 查不到時會退回預設模型的名字，
            // 那會讓手動選 Claude 的執行顯示成「Claude Sonnet 5.5 + GPT-5.6 Sol」。
            modelId = ModelScope.FromAgentApiModel(modelId);

            // 歷史紀錄照實顯示當時跑的舊世代模型（GetDefinition 會把舊 ID 路由到後繼模型，標籤不能跟著改寫）。
            var legacyName = AiModelRegistry.TryGetLegacyDisplayName(modelId);
            if (legacyName != null)
                return legacyName;

            var def = AiModelHelper.GetDefinition(modelId);

            if (!string.IsNullOrWhiteSpace(def.DisplayName))
                return def.DisplayName;

            if (!string.IsNullOrWhiteSpace(def.Id))
                return def.Id;

            return AiModelRegistry.Default.DisplayName;
        }

        private static string Safe(string? text)
        {
            return string.IsNullOrWhiteSpace(text) ? "-" : text;
        }
    }
}
