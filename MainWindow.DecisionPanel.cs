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
    // MainWindow 部分類別 — 決策窗顯示、版面與主題（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        private void DecisionPanelToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isDecisionPanelExpanded = !_isDecisionPanelExpanded;
            ApplyDecisionPanelSize();
        }

        private void ApplyDecisionPanelSize()
        {
            if (DecisionPanel != null)
            {
                DecisionPanel.Width = _isDecisionPanelExpanded
                    ? DecisionPanelExpandedWidth
                    : DecisionPanelCompactWidth;
                DecisionPanel.Height = _isDecisionPanelExpanded
                    ? DecisionPanelExpandedHeight
                    : DecisionPanelCompactHeight;
            }

            if (DecisionPanelToggleIcon != null)
                DecisionPanelToggleIcon.Data = Geometry.Parse(_isDecisionPanelExpanded
                    ? DecisionPanelCollapseIconData
                    : DecisionPanelExpandIconData);

            if (DecisionPanelToggleButton != null)
                DecisionPanelToggleButton.ToolTip = _isDecisionPanelExpanded ? UiLanguage.P("縮回決策窗", "Collapse decision panel") : UiLanguage.P("放大決策窗", "Expand decision panel");
        }

        private void ShowDecisionForNode(NodeControl node)
        {
            if (node == null)
                return;

            _lastDecisionNode = node;

            if (_liveDecisionViewsByNode.TryGetValue(node.Id, out var liveView))
            {
                ApplyDecisionViewData(liveView);
                return;
            }

            var latest = GetLatestExecutionLog(node);
            if (latest != null)
            {
                var viewData = NodeDecisionViewBuilder.BuildFromLog(latest);
                ApplyDecisionViewData(viewData);
                return;
            }

            // 沒有 execution log 時，顯示即時預估資訊
            NodeTaskModeResolution previewResolution = NodeTaskModeResolver.Resolve(node.GetTopText() ?? "");
            NodeTaskMode previewTask = NodeTaskModeHelper.Normalize(previewResolution.Mode);
            string previewTaskName = NodeTaskModeHelper.ToDisplayName(previewTask);

            string previewAgent = GetEffectiveNodeAgent(node, node.GetTopText());
            string requestedModel = GetNodeSelectedModel(node);
            string effectiveModel = GetEffectiveNodeModel(node, node.GetTopText());
            string requestedLabel2 = AiModelHelper.GetDefinition(requestedModel).DisplayName;
            string effectiveLabel2 = AiModelHelper.GetDefinition(effectiveModel).DisplayName;

            string previewModel =
                string.Equals(requestedLabel2, effectiveLabel2, StringComparison.OrdinalIgnoreCase)
                    ? effectiveLabel2
                    : $"{effectiveLabel2} ← {requestedLabel2}";

            string modeText;
            string statusText;
            string resolverText;

            if (!IsAutoModelSelectionEnabled())
            {
                modeText = "Manual";
                statusText = "Manual";
                resolverText = "Manual";
            }
            else if (IsAdvancedAutoResolverEnabled())
            {
                modeText = "Auto";
                statusText = "API Auto";
                resolverText = UiLanguage.P("Responses API（預估）", "Responses API (estimate)");
            }
            else
            {
                modeText = "Auto";
                statusText = "Rule Auto";
                resolverText = UiLanguage.P("Rules（預估）", "Rules (estimate)");
            }

            string previewSummary = UiLanguage.P($"{previewTaskName} / 尚未執行", $"{previewTaskName} / not run yet");

            string previewReason = string.IsNullOrWhiteSpace(previewResolution.Reason)
                ? "-"
                : previewResolution.Reason;

            string previewKeywords = "-";
            if (previewResolution.MatchedKeywords != null && previewResolution.MatchedKeywords.Count > 0)
            {
                previewKeywords = "keywords: " + string.Join(
                    ", ",
                    previewResolution.MatchedKeywords.Distinct(StringComparer.OrdinalIgnoreCase));
            }

            var previewViewData = new NodeDecisionViewData
            {
                Status = statusText,
                Mode = modeText,
                Resolver = resolverText,
                Agent = previewAgent,
                Model = previewModel,
                TaskSummary = previewSummary,
                Reason = previewReason,
                Keywords = previewKeywords,
                Extra = "-",
                CapabilitySummary = "-",
                CapabilityDetails = new List<string>(),
                DelegationSummary = "-",
                DelegationDetails = new List<string>(),
                CapabilityAdjusted = false,
                RuntimeFallbackUsed = false,
                ApiFallbackUsed = false,
                Steps = new[]
    {
        new NodeDecisionStepViewData
        {
            Title = "Task Mode",
            Detail = UiLanguage.P($"{previewTaskName} / 預估", $"{previewTaskName} / estimate"),
            State = NodeDecisionStepState.Info,
            Highlight = true
        },
        new NodeDecisionStepViewData
        {
            Title = "Model Selection",
            Detail = previewModel,
            State = NodeDecisionStepState.Info,
            Highlight = true
        },
        new NodeDecisionStepViewData
        {
            Title = "Capability",
            Detail = UiLanguage.P("尚未觸發", "Not triggered"),
            State = NodeDecisionStepState.Info,
            DetailLines = new[]
            {
                UiLanguage.P("Preview 階段尚未實際執行 capability", "Capabilities aren't actually run during preview")
            }
        },
        new NodeDecisionStepViewData
        {
            Title = "Delegation",
            Detail = UiLanguage.P("尚未觸發", "Not triggered"),
            State = NodeDecisionStepState.Info,
            DetailLines = new[]
            {
                UiLanguage.P("Preview 階段尚未實際執行 agent delegation", "Agent delegation isn't actually run during preview")
            }
        },
        new NodeDecisionStepViewData
        {
            Title = "Execution",
            Detail = UiLanguage.P("尚未執行", "Not run yet"),
            State = NodeDecisionStepState.Info
        }
    }
            };

            ApplyDecisionViewData(previewViewData);
        }
        public void NotifyNodeHoverEntered(NodeControl node)
        {
            if (node == null)
                return;

            _hoveredDecisionNode = node;
            ShowDecisionForNode(node);
        }

        public void NotifyNodeHoverLeft(NodeControl node)
        {
            if (node == null)
                return;

            if (ReferenceEquals(_hoveredDecisionNode, node))
                _hoveredDecisionNode = null;

            // 這版先保留最後顯示內容，不自動重置
        }

        private static double SafeFinite(double value, double fallback = 0)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return fallback;

            return value;
        }

        private static double SafePositiveFinite(double value, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return fallback;

            return value;
        }

        private static string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "…";
        }

        private void UpdateDecisionPanelForCurrentMode()
        {
            if (!_isAutoModelSelectionEnabled)
            {
                SetDecisionVisualization(
    status: "Manual",
    mode: "Manual",
    resolver: "Manual",
    model: "-",
    taskSummary: "-",
    reason: "-",
    keywords: "-",
    extra: "-",
    statusBrushHex: "#EDEDED",
    statusTextBrushHex: "#404040");
                return;
            }

            if (_isAdvancedAutoResolverEnabled)
            {
                SetDecisionVisualization(
     status: "API Auto",
     mode: "Auto",
     resolver: "Responses API",
     model: UiLanguage.P("等待送出", "Waiting to send"),
     taskSummary: "-",
     reason: "-",
     keywords: "-",
     extra: "-",
     statusBrushHex: "#EAF4FF",
     statusTextBrushHex: "#245A9B");
                return;
            }

            SetDecisionVisualization(
     status: "Rule Auto",
     mode: "Auto",
     resolver: "Rules",
     model: UiLanguage.P("等待送出", "Waiting to send"),
     taskSummary: "-",
     reason: "-",
     keywords: "-",
     extra: "-",
     statusBrushHex: "#EEF7EA",
     statusTextBrushHex: "#2E6A2E");
        }

        public void SetDecisionVisualization(
    string status,
    string mode,
    string resolver,
    string model,
    string taskSummary,
    string reason = "-",
    string keywords = "-",
    string extra = "-",
    string agent = "-",
    string statusBrushHex = "#EDEDED",
    string statusTextBrushHex = "#404040")
        {
            Dispatcher.Invoke(() =>
            {
                if (DecisionStatusText != null)
                    DecisionStatusText.Text = string.IsNullOrWhiteSpace(status) ? "-" : status;

                if (DecisionModeText != null)
                    DecisionModeText.Text = string.IsNullOrWhiteSpace(mode) ? "-" : mode;

                if (DecisionResolverText != null)
                    DecisionResolverText.Text = string.IsNullOrWhiteSpace(resolver) ? "-" : resolver;

                if (DecisionAgentText != null)
                    DecisionAgentText.Text = string.IsNullOrWhiteSpace(agent) ? "-" : agent;

                if (DecisionModelText != null)
                    DecisionModelText.Text = string.IsNullOrWhiteSpace(model) ? "-" : model;

                if (DecisionTaskText != null)
                    DecisionTaskText.Text = string.IsNullOrWhiteSpace(taskSummary) ? "-" : taskSummary;

                if (DecisionReasonText != null)
                    DecisionReasonText.Text = string.IsNullOrWhiteSpace(reason) ? "-" : reason;

                if (DecisionKeywordsText != null)
                    DecisionKeywordsText.Text = string.IsNullOrWhiteSpace(keywords) ? "-" : keywords;

                if (DecisionExtraText != null)
                    DecisionExtraText.Text = string.IsNullOrWhiteSpace(extra) ? "-" : extra;

                if (DecisionStatusBadge != null)
                    DecisionStatusBadge.Background = CreateBrush(statusBrushHex, "#EDEDED");

                if (DecisionStatusText != null)
                    DecisionStatusText.Foreground = CreateBrush(statusTextBrushHex, "#404040");
            });
        }
        private static SolidColorBrush CreateBrush(string? hex, string fallbackHex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            }
            catch { }

            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex)!);
        }

        private void ApplyDecisionViewData(NodeDecisionViewData viewData)
        {
            if (viewData == null)
                return;

            string extra = viewData.Extra;

            if (!string.IsNullOrWhiteSpace(viewData.DelegationSummary) &&
                !string.Equals(viewData.DelegationSummary, "-", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(extra) || extra == "-")
                    extra = "delegation: " + viewData.DelegationSummary;
                else
                    extra += " / delegation: " + viewData.DelegationSummary;
            }

            ApplyDecisionThemeByMode(
                status: viewData.Status,
                mode: viewData.Mode,
                resolver: viewData.Resolver,
                agent: viewData.Agent,
                model: viewData.Model,
                taskSummary: viewData.TaskSummary,
                reason: viewData.Reason,
                keywords: viewData.Keywords,
                extra: extra,
                capabilityAdjusted: viewData.CapabilityAdjusted,
                runtimeFallbackUsed: viewData.RuntimeFallbackUsed,
                apiFallbackUsed: viewData.ApiFallbackUsed);

            // 「模型」是這次決定用哪一個；「這次實際參與」是整段跑下來真的動手的每一個（含搜尋與圖片）。
            if (DecisionParticipantsText != null)
                DecisionParticipantsText.Text = string.IsNullOrWhiteSpace(viewData.Participants)
                    ? "-" : viewData.Participants;

            RenderDecisionTimeline(viewData.Steps);
        }

        // 決策窗 Timeline 渲染 → 已抽出到 DecisionTimelineRenderer.cs(第 2 刀)。
        private DecisionTimelineRenderer? _decisionRendererInstance;
        private DecisionTimelineRenderer DecisionRenderer => _decisionRendererInstance ??=
            new DecisionTimelineRenderer(
                DecisionTimelineHost, this, OpenGeneratedFile, () => GeneratedFilesDir,
                refreshDecisionView: () =>
                {
                    var target = _lastDecisionNode ?? _hoveredDecisionNode;
                    if (target != null)
                        ShowDecisionForNode(target);
                },
                materializeDownstreamPlan: TryMaterializeDownstreamNodePlanFromText);

        private void RenderDecisionTimeline(IReadOnlyList<NodeDecisionStepViewData> steps)
            => DecisionRenderer.Render(steps);
        private void ApplyDecisionThemeByMode(
    string status,
    string mode,
    string resolver,
    string agent,
    string model,
    string taskSummary,
    string reason,
    string keywords,
    string extra,
    bool capabilityAdjusted,
    bool runtimeFallbackUsed,
    bool apiFallbackUsed)
        {
            if (runtimeFallbackUsed)
            {
                SetDecisionVisualization(
                    status: status,
                    mode: mode,
                    resolver: resolver,
                    model: model,
                    taskSummary: taskSummary,
                    reason: reason,
                    keywords: keywords,
                    extra: extra,
                    agent: agent,
                    statusBrushHex: "#FFE9E9",
                    statusTextBrushHex: "#9B2C2C");
                return;
            }

            if (capabilityAdjusted || apiFallbackUsed)
            {
                SetDecisionVisualization(
                    status: status,
                    mode: mode,
                    resolver: resolver,
                    model: model,
                    taskSummary: taskSummary,
                    reason: reason,
                    keywords: keywords,
                    extra: extra,
                    agent: agent,
                    statusBrushHex: "#FFF4E8",
                    statusTextBrushHex: "#9A5A00");
                return;
            }

            if (string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(status, "API Auto", StringComparison.OrdinalIgnoreCase))
            {
                SetDecisionVisualization(
                    status: status,
                    mode: mode,
                    resolver: resolver,
                    model: model,
                    taskSummary: taskSummary,
                    reason: reason,
                    keywords: keywords,
                    extra: extra,
                    agent: agent,
                    statusBrushHex: "#EAF4FF",
                    statusTextBrushHex: "#245A9B");
                return;
            }

            if (string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                SetDecisionVisualization(
                    status: status,
                    mode: mode,
                    resolver: resolver,
                    model: model,
                    taskSummary: taskSummary,
                    reason: reason,
                    keywords: keywords,
                    extra: extra,
                    agent: agent,
                    statusBrushHex: "#EEF7EA",
                    statusTextBrushHex: "#2E6A2E");
                return;
            }

            SetDecisionVisualization(
                status: status,
                mode: mode,
                resolver: resolver,
                model: model,
                taskSummary: taskSummary,
                reason: reason,
                keywords: keywords,
                extra: extra,
                agent: agent,
                statusBrushHex: "#EDEDED",
                statusTextBrushHex: "#404040");
        }
    }
}
