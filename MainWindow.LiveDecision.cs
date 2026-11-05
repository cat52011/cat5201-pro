using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PathShape = System.Windows.Shapes.Path;

namespace Cat5201
{
    // MainWindow 部分類別 — 執行中的即時決策窗、讀檔後還原（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        public void SetLiveDecisionExecuting(
            NodeControl node,
            NodeExecutionDecision decision,
            string modelId,
            bool isFallbackAttempt,
            int attemptIndex,
            string reason)
        {
            if (node == null || decision == null)
                return;

            string requestedLabel = GetDecisionModelLabel(
                string.IsNullOrWhiteSpace(decision.RequestedModelId)
                    ? decision.ModelId
                    : decision.RequestedModelId);

            string plannedLabel = GetDecisionModelLabel(modelId);

            string modelText = string.Equals(requestedLabel, plannedLabel, StringComparison.OrdinalIgnoreCase)
                ? plannedLabel
                : $"{plannedLabel} ← {requestedLabel}";

            // ===== Capability =====
            var capabilityLines = new List<string>();
            string capabilityDetail;

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
            {
                capabilityDetail = AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace);
                capabilityLines.AddRange(AgentCapabilityTraceFormatter.BuildDetailLines(decision.CapabilityTrace));
            }
            else
            {
                capabilityDetail = decision.CapabilityAdjusted ? UiLanguage.P("已調整", "Adjusted") : "OK";
                capabilityLines.AddRange(BuildCapabilityLines(decision, forcePendingText: false));
            }

            // ===== Delegation =====
            var delegationLines = new List<string>();
            string delegationDetail;

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
            {
                delegationDetail = AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace);
                delegationLines.AddRange(AgentDelegationTraceFormatter.BuildDetailLines(decision.DelegationTrace));
            }
            else
            {
                delegationDetail = UiLanguage.P("尚未觸發", "Not triggered");
                delegationLines.Add(UiLanguage.P("目前尚未進入 agent delegation", "Agent delegation not reached yet"));
            }

            // ===== Fallback =====
            var fallbackLines = new List<string>();

            if (isFallbackAttempt)
            {
                fallbackLines.Add(UiLanguage.P($"目前為第 {attemptIndex} 次嘗試", $"Attempt {attemptIndex}"));
                fallbackLines.Add(UiLanguage.P($"候選模型：{plannedLabel}", $"Candidate model: {plannedLabel}"));
                fallbackLines.Add(UiLanguage.P("原因：", "Reason: ") + (string.IsNullOrWhiteSpace(reason) ? "-" : reason));
            }
            else
            {
                fallbackLines.Add(UiLanguage.P("目前使用 primary candidate 執行", "Running with the primary candidate"));
                fallbackLines.Add(UiLanguage.P($"模型：{plannedLabel}", $"Model: {plannedLabel}"));
            }

            if (decision.RuntimeFallbackAttempts != null && decision.RuntimeFallbackAttempts.Count > 0)
            {
                foreach (var attempt in decision.RuntimeFallbackAttempts)
                {
                    if (attempt == null)
                        continue;

                    string attemptModelLabel = GetDecisionModelLabel(attempt.ModelId);
                    string state = attempt.Success ? "Success" : "Failed";

                    fallbackLines.Add(
                        $"{attempt.AttemptIndex}. {attemptModelLabel} / {state} / {attempt.Reason} / {attempt.ErrorMessage}");
                }
            }

            // ===== Execution =====
            var executionLines = new List<string>
    {
        UiLanguage.P("Execution 狀態：執行中", "Execution status: running"),
        $"Model：{plannedLabel}",
        $"Streaming：{decision.UseStreaming}"
    };

            var steps = new List<NodeDecisionStepViewData>
    {
        new NodeDecisionStepViewData
        {
            Title = "Task Mode",
            Detail = $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / confidence {decision.Confidence:0.00}",
            State = NodeDecisionStepState.Success,
            DetailLines = BuildTaskModeLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Model Selection",
            Detail = modelText,
            State = decision.CapabilityAdjusted || isFallbackAttempt
                ? NodeDecisionStepState.Warning
                : NodeDecisionStepState.Success,
            DetailLines = BuildModelLines(decision, actualModelId: modelId)
        },
        new NodeDecisionStepViewData
        {
            Title = "Resolver",
            Detail = string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel,
            State = NodeDecisionStepState.Success,
            DetailLines = BuildResolverLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Capability",
            Detail = capabilityDetail,
            State = decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0
                ? NodeDecisionStepState.Warning
                : (decision.CapabilityAdjusted ? NodeDecisionStepState.Warning : NodeDecisionStepState.Success),
            Highlight = decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0,
            DetailLines = capabilityLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Delegation",
            Detail = delegationDetail,
            State = decision.DelegationTrace != null && decision.DelegationTrace.Count > 0
                ? NodeDecisionStepState.Warning
                : NodeDecisionStepState.Info,
            Highlight = decision.DelegationTrace != null && decision.DelegationTrace.Count > 0,
            DetailLines = delegationLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Fallback",
            Detail = isFallbackAttempt ? UiLanguage.P($"已進入第 {attemptIndex} 次嘗試", $"On attempt {attemptIndex}") : UiLanguage.P("尚未觸發", "Not triggered"),
            State = isFallbackAttempt ? NodeDecisionStepState.Warning : NodeDecisionStepState.Info,
            Highlight = isFallbackAttempt,
            IsActive = isFallbackAttempt,
            DetailLines = fallbackLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Execution",
            Detail = UiLanguage.P("執行中", "Running"),
            State = NodeDecisionStepState.Info,
            Highlight = true,
            IsActive = true,
            DetailLines = executionLines
        }
    };

            var extraParts = new List<string>();

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
                extraParts.Add("capability: " + AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace));
            else if (decision.CapabilityAdjusted && !string.IsNullOrWhiteSpace(decision.CapabilityReason))
                extraParts.Add(decision.CapabilityReason);

            if (isFallbackAttempt)
                extraParts.Add($"fallback attempt {attemptIndex}");

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
                extraParts.Add("delegation: " + AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace));

            var view = BuildLiveDecisionViewData(
                decision,
                modelText,
                UiLanguage.P($"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / 執行中", $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / running"),
                extra: extraParts.Count == 0 ? "-" : string.Join(" / ", extraParts),
                steps: steps);

            _liveDecisionViewsByNode[node.Id] = view;
            RefreshDecisionForNode(node);
        }
        public void SetLiveDecisionFailed(NodeControl node, NodeExecutionDecision decision, string errorMessage)
        {
            if (node == null || decision == null)
                return;

            string requestedLabel = GetDecisionModelLabel(
                string.IsNullOrWhiteSpace(decision.RequestedModelId)
                    ? decision.ModelId
                    : decision.RequestedModelId);

            string actualLabel = GetDecisionModelLabel(
                string.IsNullOrWhiteSpace(decision.ActualModelId)
                    ? decision.ModelId
                    : decision.ActualModelId);

            string modelText = string.Equals(requestedLabel, actualLabel, StringComparison.OrdinalIgnoreCase)
                ? actualLabel
                : $"{actualLabel} ← {requestedLabel}";

            // ===== Capability =====
            var capabilityLines = new List<string>();
            string capabilityDetail;

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
            {
                capabilityDetail = AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace);
                capabilityLines.AddRange(AgentCapabilityTraceFormatter.BuildDetailLines(decision.CapabilityTrace));
            }
            else
            {
                capabilityDetail = decision.CapabilityAdjusted ? UiLanguage.P("已調整", "Adjusted") : "OK";
                capabilityLines.AddRange(BuildCapabilityLines(decision, forcePendingText: false));
            }

            // ===== Delegation =====
            var delegationLines = new List<string>();
            string delegationDetail;

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
            {
                delegationDetail = AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace);
                delegationLines.AddRange(AgentDelegationTraceFormatter.BuildDetailLines(decision.DelegationTrace));
            }
            else
            {
                delegationDetail = UiLanguage.P("尚未觸發", "Not triggered");
                delegationLines.Add(UiLanguage.P("目前尚未進入 agent delegation", "Agent delegation not reached yet"));
            }

            var steps = new List<NodeDecisionStepViewData>
    {
        new NodeDecisionStepViewData
        {
            Title = "Task Mode",
            Detail = $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / confidence {decision.Confidence:0.00}",
            State = NodeDecisionStepState.Success,
            DetailLines = BuildTaskModeLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Model Selection",
            Detail = modelText,
            State = decision.CapabilityAdjusted || decision.RuntimeFallbackUsed
                ? NodeDecisionStepState.Warning
                : NodeDecisionStepState.Success,
            DetailLines = BuildModelLines(decision, decision.ActualModelId)
        },
        new NodeDecisionStepViewData
        {
            Title = "Resolver",
            Detail = string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel,
            State = NodeDecisionStepState.Success,
            DetailLines = BuildResolverLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Capability",
            Detail = capabilityDetail,
            State = decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0
                ? NodeDecisionStepState.Warning
                : (decision.CapabilityAdjusted ? NodeDecisionStepState.Warning : NodeDecisionStepState.Success),
            Highlight = decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0,
            DetailLines = capabilityLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Delegation",
            Detail = delegationDetail,
            State = decision.DelegationTrace != null && decision.DelegationTrace.Count > 0
                ? NodeDecisionStepState.Warning
                : NodeDecisionStepState.Info,
            Highlight = decision.DelegationTrace != null && decision.DelegationTrace.Count > 0,
            DetailLines = delegationLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Fallback",
            Detail = decision.RuntimeFallbackUsed ? UiLanguage.P("已觸發", "Triggered") : UiLanguage.P("無", "None"),
            State = decision.RuntimeFallbackUsed ? NodeDecisionStepState.Warning : NodeDecisionStepState.Success,
            DetailLines = BuildFallbackLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Execution",
            Detail = UiLanguage.P("失敗", "Failed"),
            State = NodeDecisionStepState.Error,
            Highlight = true,
            DetailLines = new[]
            {
                $"Error: {errorMessage}"
            }
        }
    };

            var extraParts = new List<string>();

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
                extraParts.Add("capability: " + AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace));

            if (!string.IsNullOrWhiteSpace(errorMessage))
                extraParts.Add(errorMessage);

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
                extraParts.Add("delegation: " + AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace));

            var view = BuildLiveDecisionViewData(
                decision,
                modelText,
                UiLanguage.P($"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / 失敗", $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / failed"),
                extra: extraParts.Count == 0 ? "-" : string.Join(" / ", extraParts),
                steps: steps);

            _liveDecisionViewsByNode[node.Id] = view;
            RefreshDecisionForNode(node);
        }
        public void ClearLiveDecisionState(NodeControl node)
        {
            if (node == null)
                return;

            if (_liveDecisionViewsByNode.Remove(node.Id))
                RefreshDecisionForNode(node);
        }

        private NodeDecisionViewData BuildLiveDecisionViewData(
    NodeExecutionDecision decision,
    string modelText,
    string taskSummary,
    string extra,
    IReadOnlyList<NodeDecisionStepViewData> steps)
        {
            string status = decision.StatusLabel;
            if (string.IsNullOrWhiteSpace(status))
                status = _isAutoModelSelectionEnabled ? "Auto" : "Manual";

            string mode = _isAutoModelSelectionEnabled ? "Auto" : "Manual";
            string resolver = string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel;

            string reason = string.IsNullOrWhiteSpace(decision.ResolverReason)
                ? "-"
                : decision.ResolverReason;

            string keywords = "-";
            if (decision.ResolverKeywords != null && decision.ResolverKeywords.Count > 0)
            {
                keywords = "keywords: " + string.Join(
                    ", ",
                    decision.ResolverKeywords
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
            }

            // ===== Capability =====
            string capabilitySummary = "-";
            var capabilityDetails = new List<string>();

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
            {
                capabilitySummary = AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace);
                capabilityDetails = AgentCapabilityTraceFormatter.BuildDetailLines(decision.CapabilityTrace).ToList();
            }

            // ===== Delegation =====
            string delegationSummary = "-";
            var delegationDetails = new List<string>();

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
            {
                delegationSummary = AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace);
                delegationDetails = AgentDelegationTraceFormatter.BuildDetailLines(decision.DelegationTrace).ToList();
            }

            string agent = string.IsNullOrWhiteSpace(decision.ActualAgentId)
                ? (string.IsNullOrWhiteSpace(decision.RequestedAgentId) ? "-" : decision.RequestedAgentId)
                : decision.ActualAgentId;

            return new NodeDecisionViewData
            {
                Status = status,
                Mode = mode,
                Resolver = resolver,
                Agent = agent,
                Model = modelText,
                TaskSummary = taskSummary,
                Reason = reason,
                Keywords = keywords,
                Extra = string.IsNullOrWhiteSpace(extra) ? "-" : extra,

                CapabilitySummary = capabilitySummary,
                CapabilityDetails = capabilityDetails,

                DelegationSummary = delegationSummary,
                DelegationDetails = delegationDetails,

                CapabilityAdjusted = decision.CapabilityAdjusted,
                RuntimeFallbackUsed = decision.RuntimeFallbackUsed,
                ApiFallbackUsed = decision.UsedFallbackToRules,
                Steps = AppendLiveWorkspaceStep(
                    steps,
                    decision.WorkspaceSummary ?? "",
                    decision.WorkspaceArtifactDetails ?? Array.Empty<string>(),
                    decision.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>())
            };
        }

        private static IReadOnlyList<NodeDecisionStepViewData> AppendLiveWorkspaceStep(
            IReadOnlyList<NodeDecisionStepViewData> steps,
            string workspaceSummary,
            IReadOnlyList<string> workspaceArtifactDetails,
            IReadOnlyList<AgentWorkspaceArtifactRecord> workspaceArtifacts)
        {
            var result = steps?.ToList() ?? new List<NodeDecisionStepViewData>();
            var detailLines = new List<string>();

            if (!string.IsNullOrWhiteSpace(workspaceSummary))
            {
                detailLines.AddRange(
                    workspaceSummary
                        .Replace("\r\n", "\n")
                        .Replace('\r', '\n')
                        .Split('\n')
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim()));
            }

            if (workspaceArtifactDetails != null && workspaceArtifactDetails.Count > 0)
            {
                if (detailLines.Count > 0)
                    detailLines.Add("--- Artifacts ---");

                detailLines.AddRange(workspaceArtifactDetails.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            var safeArtifacts = workspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>();

            if (detailLines.Count == 0 && safeArtifacts.Count == 0)
                return result;

            int artifactCount = safeArtifacts.Count > 0
                ? safeArtifacts.Count
                : (workspaceArtifactDetails?
                    .Count(x => x.StartsWith("Artifact:", StringComparison.OrdinalIgnoreCase)) ?? 0);

            int factCount = safeArtifacts.Count > 0
                ? safeArtifacts.Sum(x => x?.FactCount ?? 0)
                : (workspaceArtifactDetails?
                    .Count(x => x.TrimStart().StartsWith("Fact:", StringComparison.OrdinalIgnoreCase)) ?? 0);

            string workspaceDetail = safeArtifacts.Count > 0
                ? UiLanguage.P($"產出物 {artifactCount} 項（可見 {safeArtifacts.Count(x => x != null && x.IsUserVisible)}）", $"{artifactCount} output(s) ({safeArtifacts.Count(x => x != null && x.IsUserVisible)} visible)")
                : $"Artifacts: {artifactCount}, facts: {factCount}";

            result.Insert(Math.Max(0, result.Count - 2), new NodeDecisionStepViewData
            {
                Title = "Workspace",
                Detail = workspaceDetail,
                State = factCount > 0 || artifactCount > 0 ? NodeDecisionStepState.Success : NodeDecisionStepState.Info,
                Highlight = true,
                DetailLines = detailLines,
                WorkspaceArtifacts = safeArtifacts
            });

            return result;
        }


        private static IReadOnlyList<string> BuildTaskModeLines(NodeExecutionDecision decision)
        {
            var lines = new List<string>
    {
        $"Task Mode: {NodeTaskModeHelper.ToDisplayName(decision.TaskMode)}",
        $"Confidence: {decision.Confidence:0.00}"
    };

            if (!string.IsNullOrWhiteSpace(decision.ResolverReason))
                lines.Add($"Reason: {decision.ResolverReason}");

            if (decision.ResolverKeywords != null && decision.ResolverKeywords.Count > 0)
            {
                lines.Add("Keywords: " + string.Join(
                    ", ",
                    decision.ResolverKeywords
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)));
            }

            return lines;
        }

        private static IReadOnlyList<string> BuildModelLines(NodeExecutionDecision decision, string? actualModelId)
        {
            var lines = new List<string>
    {
        $"Requested Model: {GetDecisionModelLabel(string.IsNullOrWhiteSpace(decision.RequestedModelId) ? decision.ModelId : decision.RequestedModelId)}",
        $"Planned Model: {GetDecisionModelLabel(decision.ModelId)}"
    };

            if (!string.IsNullOrWhiteSpace(actualModelId))
                lines.Add($"Current/Actual Model: {GetDecisionModelLabel(actualModelId)}");

            return lines;
        }

        private static IReadOnlyList<string> BuildResolverLines(NodeExecutionDecision decision, string? extra = null)
        {
            var lines = new List<string>
    {
        $"Resolver: {(string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel)}",
        $"UsedApiResolver: {decision.UsedApiResolver}",
        $"UsedFallbackToRules: {decision.UsedFallbackToRules}"
    };

            if (!string.IsNullOrWhiteSpace(extra))
                lines.Add(extra);

            return lines;
        }

        private static IReadOnlyList<string> BuildCapabilityLines(NodeExecutionDecision decision, bool forcePendingText)
        {
            var lines = new List<string>();

            if (forcePendingText && !decision.CapabilityAdjusted)
            {
                lines.Add("Capability Guard: waiting / checking");
                return lines;
            }

            lines.Add($"Requested: {GetDecisionModelLabel(decision.CapabilityRequestedModelId)}");
            lines.Add($"Resolved: {GetDecisionModelLabel(decision.CapabilityResolvedModelId)}");
            lines.Add($"Required: {decision.CapabilityRequired}");
            lines.Add($"Missing: {decision.CapabilityMissing}");
            lines.Add($"StreamingAdjusted: {decision.CapabilityStreamingAdjusted}");

            if (!string.IsNullOrWhiteSpace(decision.CapabilityReason))
                lines.Add($"Reason: {decision.CapabilityReason}");

            return lines;
        }

        private static IReadOnlyList<string> BuildFallbackLines(NodeExecutionDecision decision)
        {
            var lines = new List<string>();

            if (decision.RuntimeFallbackUsed && !string.IsNullOrWhiteSpace(decision.RuntimeFallbackSummary))
                lines.Add($"Summary: {decision.RuntimeFallbackSummary}");

            if (decision.RuntimeFallbackAttempts != null && decision.RuntimeFallbackAttempts.Count > 0)
            {
                foreach (var attempt in decision.RuntimeFallbackAttempts)
                {
                    if (attempt == null)
                        continue;

                    string modelLabel = GetDecisionModelLabel(attempt.ModelId);
                    string state = attempt.Success ? "Success" : "Failed";

                    lines.Add($"{attempt.AttemptIndex}. {modelLabel} / {state} / {attempt.Reason} / {attempt.ErrorMessage}");
                }
            }

            if (lines.Count == 0)
                lines.Add("No fallback used.");

            return lines;
        }


        private static string GetDecisionModelLabel(string? modelId)
        {
            var def = AiModelHelper.GetDefinition(modelId);

            if (!string.IsNullOrWhiteSpace(def.DisplayName))
                return def.DisplayName;

            if (!string.IsNullOrWhiteSpace(def.Id))
                return def.Id;

            return AiModelRegistry.Default.DisplayName;
        }

        private void RestoreDecisionPanelAfterLoad()
        {
            NodeControl? target = null;

            if (_lastDecisionNode != null && MainCanvas.Children.Contains(_lastDecisionNode))
            {
                target = _lastDecisionNode;
            }
            else if (_hoveredDecisionNode != null && MainCanvas.Children.Contains(_hoveredDecisionNode))
            {
                target = _hoveredDecisionNode;
            }
            else if (_initialNode != null && MainCanvas.Children.Contains(_initialNode))
            {
                target = _initialNode;
            }
            else
            {
                target = MainCanvas.Children.OfType<NodeControl>().FirstOrDefault();
            }

            _hoveredDecisionNode = null;
            _lastDecisionNode = target;

            if (target != null)
                ShowDecisionForNode(target);
            else
                UpdateDecisionPanelForCurrentMode();
        }

        private class SimpleInputDialog : Window
        {
            private readonly TextBox _tb;
            private string _result = "";

            private SimpleInputDialog(string title, string prompt, string defaultValue)
            {
                Title = title;
                Width = 380;
                Height = 170;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.NoResize;
                Background = Brushes.White;

                var root = new Grid { Margin = new Thickness(14) };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var txt = new TextBlock
                {
                    Text = prompt,
                    Foreground = Brushes.Black,
                    Margin = new Thickness(0, 0, 0, 10),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetRow(txt, 0);
                root.Children.Add(txt);

                _tb = new TextBox
                {
                    Text = defaultValue,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                Grid.SetRow(_tb, 1);
                root.Children.Add(_tb);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };

                var ok = new Button { Content = UiLanguage.P("確定", "OK"), Width = 72, Margin = new Thickness(0, 0, 8, 0) };
                ok.Click += (_, __) =>
                {
                    _result = _tb.Text;
                    DialogResult = true;
                    Close();
                };

                var cancel = new Button { Content = UiLanguage.P("取消", "Cancel"), Width = 72 };
                cancel.Click += (_, __) =>
                {
                    DialogResult = false;
                    Close();
                };

                btnPanel.Children.Add(ok);
                btnPanel.Children.Add(cancel);

                Grid.SetRow(btnPanel, 2);
                root.Children.Add(btnPanel);

                Content = root;

                Loaded += (_, __) =>
                {
                    _tb.Focus();
                    _tb.SelectAll();
                };
            }

            public static string? Show(Window owner, string title, string prompt, string defaultValue)
            {
                var dlg = new SimpleInputDialog(title, prompt, defaultValue) { Owner = owner };
                var ok = dlg.ShowDialog();
                if (ok == true) return dlg._result;
                return null;
            }
        }
    }
}
