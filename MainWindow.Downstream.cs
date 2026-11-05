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
    // MainWindow 部分類別 — 自動下游節點：規劃、展開、排版（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        // ===== §4 自動下游節點：偵測 / 一鍵展開並執行 / 完全自動 =====

        public DownstreamAutoMode GetDownstreamAutoMode() => _downstreamAutoMode;

        // 簡報生成器：AgentRuntime 撰寫簡報時讀這個決定交給哪個 AI。
        public PresentationEngine GetPresentationEngine() => _presentationEngine;

        // 文件產出引擎（個人化）：預設 Claude 文件技能（與 Claude App 同級），可切回內建快速版。
        // 預設＝內建快速版。
        //
        // 2026-10-13 實測，最佳品質模式（Claude 文件技能）做簡報：三次全部失敗，單次 8～14 分鐘，
        // 而且每次失敗都還是會收錢。使用者的結論是「這甚至不如原本的簡報，不管品質、時間還是成本」。
        // 一個預設開啟、但大多數時候更慢更貴又做不出東西的路徑，不該是預設。
        // 它仍然留在「個人化 → 簡報與報告品質」裡，等它穩定了再把預設換回去。
        private DocumentEngine _documentEngine = DocumentEngine.Builtin;
        public DocumentEngine GetDocumentEngine() => _documentEngine;








        // 由節點目前的 prompt + task mode 重推導任務型別，建出「可實際執行」的下游工作流計畫。
        // 回 null 代表此任務不是多階段任務（例如一般對話），不需展開。
        public DownstreamNodePlanPayload? BuildDownstreamPlanForNode(NodeControl node)
        {
            if (node == null || !MainCanvas.Children.Contains(node))
                return null;

            string top = node.GetTopText() ?? "";
            if (string.IsNullOrWhiteSpace(top))
                return null;

            var taskType = OrchestrationPlanner.ResolveTaskType(top, GetNodeTaskMode(node));
            var plan = DownstreamNodePlanBuilder.FromTaskType(taskType, node.Id.ToString());
            return plan.ProposedNodes.Count > 0 ? plan : null;
        }

        // 此節點是否「可被展開為多階段工作流」（給 UI 決定是否顯示一鍵入口 / 右鍵選單啟用）。
        public bool NodeCanExpandToWorkflow(NodeControl node)
        {
            if (node == null)
                return false;

            // 已是自動生成的下游節點 → 不再提供展開（避免層層展開）。
            if (_generatedDownstreamNodeIds.Contains(node.Id))
                return false;

            return BuildDownstreamPlanForNode(node) != null;
        }

        private bool HasIncomingConnection(NodeControl node)
        {
            if (node == null)
                return false;

            return LiveConnections().Any(c => ReferenceEquals(c.EndNode, node));
        }

        // 一鍵（Mode 1）與完全自動（Mode 2）共用：把節點展開成下游工作流並依序執行整條。
        public async Task ExpandAndRunDownstreamWorkflowAsync(NodeControl sourceNode)
        {
            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
                return;

            if (_generatedDownstreamNodeIds.Contains(sourceNode.Id))
                return;

            var plan = BuildDownstreamPlanForNode(sourceNode);
            if (plan == null || plan.ProposedNodes.Count == 0)
                return;

            var created = MaterializeDownstreamNodePlan(sourceNode, plan);
            if (created.Count == 0)
                return;

            FocusDecisionNode(created[0]);

            // #4：來源節點已有輸出，從它沿流動邊扇出執行所有生成的下游（等父跑完才跑子）。
            await RunFlowWorkflowAsync(sourceNode, runStartNode: false);
        }

        // Mode 2（完全自動）：使用者送出後呼叫。只在「根節點 + 策略為 FullyAuto + 確為多階段任務」時才自動展開並執行。
        public async Task MaybeAutoExpandAfterSubmitAsync(NodeControl sourceNode)
        {
            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
                return;

            if (_downstreamAutoMode != DownstreamAutoMode.FullyAuto)
                return;

            // 生成的下游節點 / 已是某條鏈的中間節點 → 不自動再展開。
            if (_generatedDownstreamNodeIds.Contains(sourceNode.Id))
                return;

            if (HasIncomingConnection(sourceNode))
                return;

            if (!NodeCanExpandToWorkflow(sourceNode))
                return;

            // 來源節點必須已產出可用內容（作為下游第一步的輸入）。
            if (string.IsNullOrWhiteSpace(sourceNode.GetBottomText()))
                return;

            await ExpandAndRunDownstreamWorkflowAsync(sourceNode);
        }

        public void SetDownstreamAutoMode(DownstreamAutoMode mode, bool save = true)
        {
            _downstreamAutoMode = mode;
            SyncDownstreamAutoModeRadios();
            if (save)
                SaveState();
        }

        private bool _suppressDownstreamModeRadioEvents = false;

        private void SyncDownstreamAutoModeRadios()
        {
            _suppressDownstreamModeRadioEvents = true;
            try
            {
                if (DownstreamModeOneClick != null)
                    DownstreamModeOneClick.IsChecked = _downstreamAutoMode == DownstreamAutoMode.OneClick;
                if (DownstreamModeFullyAuto != null)
                    DownstreamModeFullyAuto.IsChecked = _downstreamAutoMode == DownstreamAutoMode.FullyAuto;
                if (DownstreamModeOff != null)
                    DownstreamModeOff.IsChecked = _downstreamAutoMode == DownstreamAutoMode.Off;
            }
            finally
            {
                _suppressDownstreamModeRadioEvents = false;
            }
        }

        private void DownstreamMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressDownstreamModeRadioEvents)
                return;

            if (ReferenceEquals(sender, DownstreamModeFullyAuto))
                SetDownstreamAutoMode(DownstreamAutoMode.FullyAuto);
            else if (ReferenceEquals(sender, DownstreamModeOff))
                SetDownstreamAutoMode(DownstreamAutoMode.Off);
            else
                SetDownstreamAutoMode(DownstreamAutoMode.OneClick);
        }

        private double ResolveAvailableDownstreamRowY(
            NodeControl sourceNode,
            double startX,
            double preferredY,
            int nodeCount,
            double spacingX,
            double rowGap)
        {
            if (nodeCount <= 0)
                return preferredY;

            const double probeWidth = 260;
            const double probeHeight = 250;
            const double padding = 24;

            var existingRects = MainCanvas.Children
                .OfType<NodeControl>()
                .Where(x => !ReferenceEquals(x, sourceNode))
                .Select(GetNodeCanvasRect)
                .Where(x => x.Width > 0 && x.Height > 0)
                .ToList();

            for (int attempt = 0; attempt < 12; attempt++)
            {
                double y = preferredY + attempt * rowGap;
                bool overlaps = false;

                for (int i = 0; i < nodeCount; i++)
                {
                    var probe = new Rect(
                        startX + i * spacingX - padding,
                        y - padding,
                        probeWidth + padding * 2,
                        probeHeight + padding * 2);

                    if (existingRects.Any(existing => existing.IntersectsWith(probe)))
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (!overlaps)
                    return y;
            }

            return preferredY + 12 * rowGap;
        }

        private static Rect GetNodeCanvasRect(NodeControl node)
        {
            if (node == null)
                return Rect.Empty;

            double left = Canvas.GetLeft(node);
            double top = Canvas.GetTop(node);

            if (double.IsNaN(left))
                left = 0;

            if (double.IsNaN(top))
                top = 0;

            double width = node.ActualWidth > 0
                ? node.ActualWidth
                : (!double.IsNaN(node.Width) && node.Width > 0 ? node.Width : 260);

            double height = node.ActualHeight > 0
                ? node.ActualHeight
                : (!double.IsNaN(node.Height) && node.Height > 0 ? node.Height : 250);

            return new Rect(left, top, width, height);
        }

        private void RefreshConnectionsAfterLayout(IReadOnlyList<NodeControl> nodes)
        {
            if (nodes == null || nodes.Count == 0)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                MainCanvas.UpdateLayout();

                foreach (var node in nodes.Where(x => x != null && MainCanvas.Children.Contains(x)))
                    UpdateConnectionsFor(node);

                SaveState();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// async void 的進入點：例外沒人接的話會變成未處理例外，所以在這一層收掉。
        /// 真正的工作在 TryMaterializeDownstreamNodePlanFromTextAsync。
        /// </summary>
        private async void TryMaterializeDownstreamNodePlanFromText(string artifactText)
        {
            try
            {
                await TryMaterializeDownstreamNodePlanFromTextAsync(artifactText);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Workflow", "自動展開下游節點失敗", ex);
            }
        }

        private async Task TryMaterializeDownstreamNodePlanFromTextAsync(string artifactText)
        {
            if (string.IsNullOrWhiteSpace(artifactText))
                return;

            var sourceNode = _lastDecisionNode;
            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
            {
                sourceNode = _hoveredDecisionNode;
            }

            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
            {
                sourceNode = _initialNode;
            }

            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
            {
                sourceNode = MainCanvas.Children.OfType<NodeControl>().FirstOrDefault();
            }

            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
                return;

            var plan = TryParseDownstreamNodePlan(artifactText, sourceNode.Id.ToString());
            if (plan == null || plan.ProposedNodes.Count == 0)
                return;

            var created = MaterializeDownstreamNodePlan(sourceNode, plan);
            if (created.Count > 0)
            {
                FocusDecisionNode(created[0]);
                // Mode 1：展開後沿流動邊扇出執行整條工作流（手動點此卡片即代表使用者同意花 token）。
                await RunFlowWorkflowAsync(sourceNode, runStartNode: false);
            }
        }

        private static double GetNodeLayoutHeight(NodeControl node)
        {
            if (node == null)
                return 0;

            if (node.ActualHeight > 0)
                return node.ActualHeight;

            if (node.Height > 0 && !double.IsNaN(node.Height))
                return node.Height;

            return 360;
        }

        private static DownstreamNodePlanPayload? TryParseDownstreamNodePlan(
            string text,
            string sourceNodeId)
        {
            var lines = (text ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (!lines.Any(x => x.StartsWith("DownstreamPlan:", StringComparison.OrdinalIgnoreCase)))
                return null;

            string pipelineId = "";
            var taskType = OrchestrationTaskType.Workflow;
            var mutableNodes = new List<MutableDownstreamNodeProposal>();
            MutableDownstreamNodeProposal? currentNode = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("DownstreamPlan:", StringComparison.OrdinalIgnoreCase))
                {
                    pipelineId = ExtractSlashField(line, "Pipeline");
                    string taskTypeText = ExtractSlashField(line, "TaskType");
                    if (Enum.TryParse(taskTypeText, true, out OrchestrationTaskType parsed))
                        taskType = parsed;

                    continue;
                }

                if (line.StartsWith("DownstreamNode:", StringComparison.OrdinalIgnoreCase))
                {
                    string body = line.Substring("DownstreamNode:".Length).Trim();
                    var parts = body
                        .Split('/')
                        .Select(x => x.Trim())
                        .ToList();

                    currentNode = new MutableDownstreamNodeProposal
                    {
                        Id = parts.Count > 0 ? parts[0] : "",
                        Label = parts.Count > 1 ? parts[1] : "",
                        AgentId = ExtractSlashField(line, "Agent"),
                        CapabilityId = ExtractSlashField(line, "Capability"),
                        Status = ExtractSlashField(line, "Status")
                    };

                    mutableNodes.Add(currentNode);
                    continue;
                }

                if (currentNode != null &&
                    line.StartsWith("InputSource:", StringComparison.OrdinalIgnoreCase))
                {
                    currentNode.InputSource = line.Substring("InputSource:".Length).Trim();
                    continue;
                }

                if (currentNode != null &&
                    line.StartsWith("ExpectedOutput:", StringComparison.OrdinalIgnoreCase))
                {
                    currentNode.ExpectedOutput = line.Substring("ExpectedOutput:".Length).Trim();
                }
            }

            var nodes = mutableNodes
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .Select(x => new DownstreamNodeProposalPayload
                {
                    Id = x.Id,
                    Label = x.Label,
                    AgentId = x.AgentId,
                    CapabilityId = x.CapabilityId,
                    InputSource = x.InputSource,
                    ExpectedOutput = x.ExpectedOutput,
                    Status = string.IsNullOrWhiteSpace(x.Status) ? "proposed" : x.Status
                })
                .ToList();

            if (nodes.Count == 0)
                return null;

            var edges = new List<DownstreamNodeEdgeProposalPayload>();
            for (int i = 1; i < nodes.Count; i++)
            {
                edges.Add(new DownstreamNodeEdgeProposalPayload
                {
                    FromNodeId = nodes[i - 1].Id,
                    ToNodeId = nodes[i].Id
                });
            }

            return new DownstreamNodePlanPayload
            {
                Status = "proposal",
                PipelineId = pipelineId,
                TaskType = taskType,
                SourceNodeId = sourceNodeId ?? "",
                CreatesCanvasNodes = false,
                ProposedNodes = nodes,
                ProposedEdges = edges,
                Notes = new[]
                {
                    "Materialized from workspace downstream_node_plan card.",
                    "Created nodes do not auto-run by default."
                }
            };
        }

        private static string ExtractSlashField(string line, string key)
        {
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(key))
                return "";

            var match = Regex.Match(
                line,
                $@"(?:^|/)\s*{Regex.Escape(key)}\s*=\s*(?<value>[^/]+)",
                RegexOptions.IgnoreCase);

            return match.Success
                ? match.Groups["value"].Value.Trim()
                : "";
        }

        private sealed class MutableDownstreamNodeProposal
        {
            public string Id { get; set; } = "";
            public string Label { get; set; } = "";
            public string AgentId { get; set; } = "";
            public string CapabilityId { get; set; } = "";
            public string InputSource { get; set; } = "";
            public string ExpectedOutput { get; set; } = "";
            public string Status { get; set; } = "proposed";
        }

        private static NodeTaskMode ResolveTaskModeForDownstreamProposal(
            DownstreamNodeProposalPayload proposal,
            AgentDefinition agent)
        {
            string capability = proposal?.CapabilityId ?? "";
            string label = proposal?.Label ?? "";

            if (capability.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("research", StringComparison.OrdinalIgnoreCase))
            {
                return NodeTaskMode.Research;
            }

            if (capability.Contains("generation", StringComparison.OrdinalIgnoreCase) ||
                capability.Contains("planning", StringComparison.OrdinalIgnoreCase))
            {
                return NodeTaskMode.Summarize;
            }

            return NodeTaskModeHelper.Normalize(agent.DefaultTaskMode);
        }

        private static string BuildDownstreamNodePrompt(
            DownstreamNodeProposalPayload proposal)
        {
            string label = BuildDownstreamNodeShortLabel(proposal);
            string detailLabel = string.IsNullOrWhiteSpace(proposal?.Label)
                ? "Downstream step"
                : proposal.Label.Trim();

            string instruction = BuildDownstreamStepInstruction(proposal?.Id ?? "");

            // 第一行為步驟標題（畫布可讀）；第二行為「帶路由關鍵字的指令」，
            // 讓此節點實際執行時走到對應 orchestration（簡報 / 檔案 / 圖片 / 影片…）。
            // {{input}} 為上游輸出注入點（auto-flow 模板）。
            return
                $"{label}｜{TranslateDownstreamStepLabel(detailLabel)}\n" +
                instruction + "\n\n" +
                "{{input}}";
        }

        // 依下游步驟 id 給「可觸發正確 orchestration 的中文指令」。
        // 關鍵字需與 OrchestrationPlanner.ResolveTaskType 對齊（簡報 / 報告·輸出成檔案 / 圖片 / 影片）。
        private static string BuildDownstreamStepInstruction(string id)
        {
            switch ((id ?? "").Trim().ToLowerInvariant())
            {
                case "research":
                    return "請研究並蒐集以下主題的最新關鍵事實、數據與可查證來源，條列成可直接引用的要點。";
                case "outline":
                    return "請依據以下資料，分析並整理出結構化的重點與大綱。";
                case "draft":
                    return "請依據以下重點，撰寫完整、有條理的內容。";
                case "synthesize":
                    return "請整合以下所有階段的結果，輸出最終結論與建議。";
                case "deck":
                    return "請依據以下內容，製作一份結構完整的簡報（投影片），含封面與重點頁。";
                case "export":
                    return "請把以下內容整理成一份完整文件並輸出成檔案（報告）。";
                case "prompt":
                    return "請依以下需求，撰寫一段精準、具體的圖片生成提示詞。";
                case "image":
                    return "請根據以下提示詞生成一張圖片。";
                case "brief":
                    return "請為以下需求規劃影片的劇本、分鏡與旁白。";
                case "video":
                    return "請根據以下企劃生成影片。";
                case "decompose":
                    return "請把以下任務拆解成可執行的步驟與順序。";
                case "execute":
                    return "請依序執行以下步驟並輸出結果。";
                default:
                    return "請延續以下內容完成本步驟。";
            }
        }

        private static string TranslateDownstreamStepLabel(string label)
        {
            string value = label?.Trim() ?? "";

            if (value.Contains("Research", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("source facts", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("supporting facts", StringComparison.OrdinalIgnoreCase))
                return "搜尋並驗證資料";

            if (value.Contains("outline", StringComparison.OrdinalIgnoreCase))
                return "建立大綱";

            // export 要先於 deck 判斷，否則 "Export deck" 會被翻成「生成簡報」。
            if (value.Contains("export", StringComparison.OrdinalIgnoreCase))
                return "匯出檔案";

            if (value.Contains("deck", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("presentation", StringComparison.OrdinalIgnoreCase))
                return "生成簡報";

            if (value.Contains("draft", StringComparison.OrdinalIgnoreCase))
                return "撰寫草稿";

            if (value.Contains("decompose", StringComparison.OrdinalIgnoreCase))
                return "拆解任務";

            if (value.Contains("execute", StringComparison.OrdinalIgnoreCase))
                return "執行步驟";

            if (value.Contains("synthesize", StringComparison.OrdinalIgnoreCase))
                return "整合結果";

            // prompt 要先於 image 判斷（"Refine image prompt" 兩者都含）；
            // brief 要先於 video 判斷（"Create video brief" 兩者都含）。
            if (value.Contains("prompt", StringComparison.OrdinalIgnoreCase))
                return "優化圖片提示";

            if (value.Contains("image", StringComparison.OrdinalIgnoreCase))
                return "生成圖片";

            if (value.Contains("brief", StringComparison.OrdinalIgnoreCase))
                return "建立影片企劃";

            if (value.Contains("video", StringComparison.OrdinalIgnoreCase))
                return "生成影片";

            return string.IsNullOrWhiteSpace(value) ? "下游步驟" : value;
        }

        private static string BuildDownstreamNodeShortLabel(DownstreamNodeProposalPayload proposal)
        {
            string id = proposal?.Id?.Trim() ?? "";

            if (id.Equals("research", StringComparison.OrdinalIgnoreCase))
                return "Research";

            if (id.Equals("outline", StringComparison.OrdinalIgnoreCase))
                return "Outline";

            if (id.Equals("deck", StringComparison.OrdinalIgnoreCase))
                return "Deck";

            if (id.Equals("export", StringComparison.OrdinalIgnoreCase))
                return "Export";

            if (id.Equals("draft", StringComparison.OrdinalIgnoreCase))
                return "Draft";

            if (id.Equals("synthesize", StringComparison.OrdinalIgnoreCase))
                return "Synthesize";

            if (id.Equals("decompose", StringComparison.OrdinalIgnoreCase))
                return "Decompose";

            if (id.Equals("execute", StringComparison.OrdinalIgnoreCase))
                return "Execute";

            if (id.Equals("prompt", StringComparison.OrdinalIgnoreCase))
                return "Prompt";

            if (id.Equals("image", StringComparison.OrdinalIgnoreCase))
                return "Image";

            if (id.Equals("brief", StringComparison.OrdinalIgnoreCase))
                return "Brief";

            if (id.Equals("video", StringComparison.OrdinalIgnoreCase))
                return "Video";

            if (!string.IsNullOrWhiteSpace(proposal?.Label))
                return proposal.Label.Trim();

            return string.IsNullOrWhiteSpace(id) ? "Step" : id;
        }
    }
}
