using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    public sealed class NodeExecutionLogFactory
    {
        public AiExecutionLogEntry Create(
            NodeControl node,
            NodeExecutionDecision decision,
            DateTime startedAtUtc,
            bool success,
            string errorMessage = "")
        {
            var endedAtUtc = DateTime.UtcNow;
            long durationMs = Math.Max(0, (long)(endedAtUtc - startedAtUtc).TotalMilliseconds);

            string actualModelId = AiModelHelper.NormalizeNodeModel(
                string.IsNullOrWhiteSpace(decision.ActualModelId)
                    ? decision.ModelId
                    : decision.ActualModelId);

            // 成本可稽查：逐筆帳目（每次呼叫各自的模型 × 用量 × 單價）加總，不再用「總 token × 最後模型單價」。
            var usageRecords = node.GetUsageRecords();
            var usage = UsageSummary.From(usageRecords);
            string costDisplay = usage.BuildCostDisplay();

            return new AiExecutionLogEntry
            {
                NodeId = node.Id.ToString(),

                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CostDisplay = costDisplay,
                UsageRecords = usageRecords.ToList(),

                StartedAtUtc = startedAtUtc,
                EndedAtUtc = endedAtUtc,
                DurationMs = durationMs,

                SelectionMode = GetSelectionModeLabel(decision),
                Resolver = decision.ResolverLabel ?? "",

                RequestedModelId = AiModelHelper.NormalizeNodeModel(decision.RequestedModelId),
                PlannedModelId = AiModelHelper.NormalizeNodeModel(decision.ModelId),
                ActualModelId = AiModelHelper.NormalizeNodeModel(
                    string.IsNullOrWhiteSpace(decision.ActualModelId)
                        ? decision.ModelId
                        : decision.ActualModelId),

                TaskMode = NodeTaskModeHelper.ToDisplayName(decision.TaskMode),
                Confidence = decision.Confidence,

                ResolverReason = decision.ResolverReason ?? "",
                ResolverKeywords = decision.ResolverKeywords?.ToList() ?? new List<string>(),
                WorkspaceSummary = decision.WorkspaceSummary ?? "",
                WorkspaceArtifactDetails = decision.WorkspaceArtifactDetails?.ToList() ?? new List<string>(),
                WorkspaceArtifacts = decision.WorkspaceArtifacts?.ToList() ?? new List<AgentWorkspaceArtifactRecord>(),
                CapabilityAdjusted = decision.CapabilityAdjusted,
                CapabilityReason = decision.CapabilityReason ?? "",

                CapabilityRequestedModelId = AiModelHelper.NormalizeNodeModel(decision.CapabilityRequestedModelId),
                CapabilityResolvedModelId = AiModelHelper.NormalizeNodeModel(decision.CapabilityResolvedModelId),
                CapabilityRequired = decision.CapabilityRequired.ToString(),
                CapabilityMissing = decision.CapabilityMissing.ToString(),
                CapabilityStreamingAdjusted = decision.CapabilityStreamingAdjusted,

                CapabilityTrace = decision.CapabilityTrace?.ToList() ?? new List<AgentCapabilityTraceItem>(),

                RuntimeFallbackUsed = decision.RuntimeFallbackUsed,
                RuntimeFallbackSummary = decision.RuntimeFallbackSummary ?? "",

                Success = success,
                ErrorMessage = errorMessage ?? "",

                FallbackAttempts = decision.RuntimeFallbackAttempts?.ToList() ?? new List<AiFallbackAttempt>(),
                RequestedAgentId = decision.RequestedAgentId ?? "",
                ActualAgentId = decision.ActualAgentId ?? "",

                MemoryRecall = decision.MemoryRecall ?? MemoryRecallStats.Empty,
                OutputIntentSummary = decision.OutputIntentSummary ?? "",
            };
        }

        private static string GetSelectionModeLabel(NodeExecutionDecision decision)
        {
            if (decision.UsedApiResolver)
                return "API Auto";

            if (string.Equals(decision.StatusLabel, "Rule Auto", StringComparison.OrdinalIgnoreCase))
                return "Auto";

            return "Manual";
        }
    }
}
