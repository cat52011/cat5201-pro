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
    // MainWindow 部分類別 — 上下游自動流程、工作流串跑（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        public void ClearAutoFlowTemplate(NodeControl node)
        {
            if (node == null)
                return;

            _autoFlowTemplatesByNode.Remove(node.Id);
        }

        public NodeControl? GetFirstDownstreamNode(NodeControl node)
        {
            return GetDownstreamNodes(node).FirstOrDefault();
        }

        public bool TryPrepareAutoFlowInput(NodeControl fromNode, NodeControl toNode)
        {
            // 不只看 fromNode：toNode 若是匯流節點，所有上游都要對等地放進去（見 TryBuildInputFromUpstreams）。
            if (fromNode == null || !TryBuildInputFromUpstreams(toNode, out var injected))
                return false;

            toNode.SetTopText(injected);
            toNode.RefreshModelSelectionUI();
            RefreshDecisionForNode(toNode);
            return true;
        }

        /// <summary>
        /// 下游節點的主要輸入：所有上游的產出，以對等的格式完整放進模板的 {{input}}。
        ///
        /// 原本叫 TryBuildInputFromFirstUpstream，只取 FirstOrDefault——匯流節點只完整看到
        /// 先接上的那一條，其他上游只剩背景脈絡裡被截到 1,200 字的版本（2026-10-29 發現）。
        /// 單一上游時行為不變。
        /// </summary>
        public bool TryBuildInputFromUpstreams(NodeControl node, out string injectedPrompt)
        {
            injectedPrompt = "";

            if (node == null)
                return false;

            var sources = GetConnectionsForContext()
                .Where(c => ReferenceEquals(c.EndNode, node))
                .Select(c => c.StartNode)
                .Where(n => n != null && !string.IsNullOrWhiteSpace(n.GetBottomText()))
                .Distinct()
                .Select(n => new UpstreamMerge.Source(
                    ShortLabel(n),
                    AiModelRegistry.Find(GetNodeSelectedModel(n))?.DisplayName ?? "",
                    // 上游的程式碼存成了檔案時，把檔案內容接回去，下游才看得到程式本身。
                    UpstreamMerge.WithCodeFiles(UpstreamMerge.CleanOutput(n.GetBottomText()), n.GetOutputFilePaths())))
                .ToList();

            if (sources.Count == 0)
                return false;

            if (sources.Count > 1)
                AppLog.Info("Flow", $"「{ShortLabel(node)}」匯流 {sources.Count} 個上游，全部完整放進輸入");

            return TryBuildAutoFlowInjectedPrompt(UpstreamMerge.Combine(sources), node, out injectedPrompt);
        }

        private bool TryBuildAutoFlowInjectedPrompt(
            string sourceText,
            NodeControl toNode,
            out string injectedPrompt)
        {
            injectedPrompt = "";

            if (toNode == null)
                return false;

            sourceText = (sourceText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(sourceText))
                return false;

            if (toNode.GetTopLocked())
                return false;

            string template = GetAutoFlowTemplate(toNode);
            if (string.IsNullOrWhiteSpace(template))
                return false;

            const string placeholder = "{{input}}";

            if (!template.Contains(placeholder, StringComparison.Ordinal))
                return false;

            string injected = template.Replace(placeholder, sourceText, StringComparison.Ordinal);

            if (string.IsNullOrWhiteSpace(injected))
                return false;

            injectedPrompt = injected;
            return true;
        }

        public bool TryPrepareInputFromFirstUpstream(NodeControl node)
        {
            if (node == null)
                return false;

            string topText = node.GetTopText() ?? "";
            if (!topText.Contains("{{input}}", StringComparison.Ordinal))
                return false;

            var incoming = GetConnectionsForContext()
                .Where(c => ReferenceEquals(c.EndNode, node))
                .Select(c => c.StartNode)
                .FirstOrDefault(n => n != null);

            return incoming != null && TryPrepareAutoFlowInput(incoming, node);
        }

        public void FocusDecisionNode(NodeControl node)
        {
            if (node == null)
                return;

            // 背景執行中的存檔的節點不搶決策面板：使用者正在看的是另一個存檔。
            if (!MainCanvas.Children.Contains(node))
                return;

            _lastDecisionNode = node;
            _hoveredDecisionNode = node;
            ShowDecisionForNode(node);
        }

        public NodeAutoFlowPolicy GetNodeAutoFlowPolicy(NodeControl node)
        {
            if (node == null)
                return NodeAutoFlowPolicy.Default;

            if (_autoFlowPoliciesByNode.TryGetValue(node.Id, out var policy))
                return policy ?? NodeAutoFlowPolicy.Default;

            _autoFlowPoliciesByNode[node.Id] = NodeAutoFlowPolicy.Default;
            return NodeAutoFlowPolicy.Default;
        }

        public void SetNodeAutoFlowPolicy(NodeControl node, NodeAutoFlowPolicy policy)
        {
            if (node == null)
                return;

            _autoFlowPoliciesByNode[node.Id] = policy ?? NodeAutoFlowPolicy.Default;
            SaveState();
        }

        public bool IsNodeAutoRunEnabled(NodeControl node)
        {
            return GetNodeAutoFlowPolicy(node).AutoRunEnabled;
        }

        public async Task RunManualWorkflowChainAsync(NodeControl startNode)
        {
            if (startNode == null || !MainCanvas.Children.Contains(startNode))
                return;

            // 已有工作流在跑 → 不重入（避免兩條鏈搶同一批節點）。使用者可先「停止工作流」再重跑。
            if (IsWorkflowChainRunning)
                return;

            using var cts = new CancellationTokenSource();
            _workflowChainCts = cts;
            _chainProject = _activeProject;   // 切到別的存檔時，用來判斷這條鏈屬於哪個存檔（流光、背景釋放）
            try
            {
                var visited = new HashSet<Guid>();
                var current = startNode;

                for (int step = 0; step < 12; step++)
                {
                    // 使用者切到別的存檔時，這條鏈的節點留在背景，鏈照常往下跑（IsNodeLive，見 MainWindow.Sessions.cs）
                    if (current == null || !IsNodeLive(current))
                        return;

                    if (cts.IsCancellationRequested)
                        return;

                    if (!visited.Add(current.Id))
                        return;

                    FocusDecisionNode(current);

                    if (ShouldStopBeforeUnsupportedDownstreamNode(current))
                    {
                        current.SetBottomText(
                            UiLanguage.P("（此下游節點需要尚未接上的專用代理，已停止以避免不必要的 token 消耗。）\n", "(This downstream node needs a specialized agent that isn't connected yet, so it stopped to avoid wasting tokens.)\n") +
                            UiLanguage.P("目前可先使用前一個節點的結果；等 presentation/file/media/workflow agent 完成後再啟用此步。", "For now you can use the previous node's result; this step will be enabled once the presentation/file/media/workflow agents are ready."));
                        return;
                    }

                    _runningChainNode = current;
                    bool success = await current.RunCurrentTopTextAsync(cts.Token);
                    _runningChainNode = null;

                    // 被手動停止 → 安靜結束（節點本身已標記為「已停止」）。
                    if (cts.IsCancellationRequested)
                        return;

                    // 這一步失敗 → 停在這裡。使用者可右鍵「略過此步、從下一步續跑」或「執行此節點與下游」重跑。
                    if (!success)
                        return;

                    current = GetFirstDownstreamNode(current);
                    if (current == null)
                        return;
                }
            }
            finally
            {
                _runningChainNode = null;
                if (ReferenceEquals(_workflowChainCts, cts))
                    _workflowChainCts = null;
            }
        }

        // §4 stop：立即停止正在跑的工作流鏈（取消 in-flight 節點 + 阻止後續步驟）。
        public void StopWorkflowChain()
        {
            try { _workflowChainCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // §4 skip：略過此步，從它的下游節點繼續整條鏈。
        // 被略過的節點轉為 passthrough（直接帶上一個好的上游輸出），下游的 {{input}} 仍讀得到正確內容。
        public async Task SkipStepAndContinueAsync(NodeControl node)
        {
            if (node == null || !MainCanvas.Children.Contains(node))
                return;

            if (IsWorkflowChainRunning)
                return;

            var next = GetFirstDownstreamNode(node);

            var upstream = GetFirstUpstreamNode(node);
            string carried = (upstream?.GetBottomText() ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(carried))
                node.SetBottomText(carried);

            node.MarkChainStepSkipped();

            if (next == null)
                return;

            await RunManualWorkflowChainAsync(next);
        }

        // 此節點的第一個上游（入邊來源）節點；無入邊回 null。
        public NodeControl? GetFirstUpstreamNode(NodeControl node)
        {
            if (node == null)
                return null;

            return GetConnectionsForContext()
                .Where(c => ReferenceEquals(c.EndNode, node))
                .Select(c => c.StartNode)
                .FirstOrDefault(n => n != null);
        }

        // 此節點是否有下游節點（給右鍵「略過此步」決定是否啟用）。
        public bool NodeHasDownstream(NodeControl node) => GetFirstDownstreamNode(node) != null;

        public void RunDryWorkflowChain(NodeControl startNode)
        {
            if (startNode == null || !MainCanvas.Children.Contains(startNode))
                return;

            var visited = new HashSet<Guid>();
            var current = startNode;
            string upstreamText = (startNode.GetBottomText() ?? "").Trim();

            for (int step = 0; step < 12; step++)
            {
                if (current == null || !MainCanvas.Children.Contains(current))
                    return;

                if (!visited.Add(current.Id))
                {
                    current.SetBottomText(UiLanguage.P("（Dry run 停止：偵測到循環連線。）", "(Dry run stopped: a circular link was detected.)"));
                    FocusDecisionNode(current);
                    return;
                }

                FocusDecisionNode(current);

                if (ShouldStopBeforeUnsupportedDownstreamNode(current))
                {
                    current.SetBottomText(
                        UiLanguage.P("（Dry run 停止：此節點需要尚未接上的專用代理。）\n", "(Dry run stopped: this node needs a specialized agent that isn't connected yet.)\n") +
                        UiLanguage.P("真實執行時也會在此停止，以避免不必要的 token 消耗。", "A real run would also stop here to avoid wasting tokens."));
                    return;
                }

                string label = ExtractWorkflowStepLabel(current.GetTopText());
                string inputSummary = string.IsNullOrWhiteSpace(upstreamText)
                    ? UiLanguage.P("無上游輸出或由本節點原始輸入開始。", "No upstream output, or starting from this node's own input.")
                    : SummarizeForDryRun(upstreamText);

                current.SetBottomText(
                    UiLanguage.P($"（Dry run，未呼叫模型）\n", $"(Dry run, no model called)\n") +
                    $"Step: {label}\n" +
                    $"Input: {inputSummary}\n" +
                    $"Output: simulated result for downstream wiring test.");

                upstreamText = current.GetBottomText();
                current = GetFirstDownstreamNode(current);
                if (current == null)
                    return;
            }
        }

        private static string ExtractWorkflowStepLabel(string? topText)
        {
            string text = topText ?? "";
            foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Step:", StringComparison.OrdinalIgnoreCase))
                    return line.Substring("Step:".Length).Trim();
            }

            var first = text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Select(x => x.Trim())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            if (string.IsNullOrWhiteSpace(first))
                return "workflow step";

            int pipeIndex = first.IndexOf('｜');
            return pipeIndex >= 0 && pipeIndex < first.Length - 1
                ? first.Substring(pipeIndex + 1).Trim()
                : first;
        }

        private static string SummarizeForDryRun(string text)
        {
            string normalized = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (normalized.Length <= 90)
                return normalized;

            return normalized.Substring(0, 90) + "...";
        }

        private bool ShouldStopBeforeUnsupportedDownstreamNode(NodeControl node)
        {
            if (node != null && _unsupportedDownstreamNodeIds.Contains(node.Id))
                return true;

            string top = node?.GetTopText() ?? "";
            if (string.IsNullOrWhiteSpace(top))
                return false;

            bool isFutureTarget = top.Contains("(future target:", StringComparison.OrdinalIgnoreCase);
            if (!isFutureTarget)
                return false;

            return top.Contains("future target: presentation-agent", StringComparison.OrdinalIgnoreCase) ||
                   top.Contains("future target: file-agent", StringComparison.OrdinalIgnoreCase) ||
                   top.Contains("future target: media-agent", StringComparison.OrdinalIgnoreCase) ||
                   top.Contains("future target: workflow-agent", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>所有入邊的上游節點（不分一般／流動連線）——匯流判斷與對等輸入都用這一份。</summary>
        public IReadOnlyList<NodeControl> GetUpstreamNodes(NodeControl node)
        {
            if (node == null)
                return Array.Empty<NodeControl>();

            return GetConnectionsForContext()
                .Where(c => ReferenceEquals(c.EndNode, node))
                .Select(c => c.StartNode)
                .Where(n => n != null)
                .Distinct()
                .ToList();
        }

        public IReadOnlyList<NodeControl> GetDownstreamNodes(NodeControl node)
        {
            if (node == null)
                return Array.Empty<NodeControl>();

            return GetConnectionsForContext()
                .Where(c => ReferenceEquals(c.StartNode, node))
                .Select(c => c.EndNode)
                .Where(n => n != null)
                .Distinct()
                .ToList();
        }

        // #4：此節點所有「流動模式」出邊的下游節點（執行路徑）。
        public IReadOnlyList<NodeControl> GetFlowDownstreamNodes(NodeControl node)
        {
            if (node == null)
                return Array.Empty<NodeControl>();

            return LiveConnections()
                .Where(c => ReferenceEquals(c.StartNode, node) && c.FlowMode && c.EndNode != null)
                .Select(c => c.EndNode)
                .Distinct()
                .ToList();
        }

        // #4：此節點所有「流動模式」入邊的上游節點（匯流判斷用）。
        public IReadOnlyList<NodeControl> GetFlowUpstreamNodes(NodeControl node)
        {
            if (node == null)
                return Array.Empty<NodeControl>();

            return LiveConnections()
                .Where(c => ReferenceEquals(c.EndNode, node) && c.FlowMode && c.StartNode != null)
                .Select(c => c.StartNode)
                .Distinct()
                .ToList();
        }

        // #4：此節點是否有流動模式出邊（給右鍵選單決定是否啟用「執行此節點與下游」）。
        public bool NodeHasFlowDownstream(NodeControl node) => GetFlowDownstreamNodes(node).Count > 0;

        // #4 流動工作流執行：從 startNode 沿「流動模式」邊扇出。
        // 一律「等父節點完全跑完」才跑子節點；同層多個下游依序執行；扇出涵蓋所有流動邊（不再只跑第一個）。
        public async Task RunFlowWorkflowAsync(NodeControl startNode, bool runStartNode)
        {
            if (startNode == null || !MainCanvas.Children.Contains(startNode))
                return;

            // 已有工作流在跑 → 不重入（避免兩條鏈搶同一批節點）。
            if (IsWorkflowChainRunning)
                return;

            using var cts = new CancellationTokenSource();
            _workflowChainCts = cts;
            _chainProject = _activeProject;   // 切到別的存檔時，用來判斷這條鏈屬於哪個存檔（流光、背景釋放）
            // 下游先全部標記「等待中」（琥珀框 + 手機顯示），輪到誰執行時 Running 會蓋過；
            // startNode 自己不標（runStartNode 時它馬上就跑、否則它是剛跑完的上游）。
            MarkFlowSubtreeWaiting(startNode, includeRoot: false);
            try
            {
                var runState = new FlowRunState();
                CollectFlowReachable(startNode, runState.InThisRun);
                await RunFlowSubtreeAsync(startNode, runStartNode, runState, cts);

                // 收尾掃描：匯流節點靠「最後一個上游跑完時順便叫它」才會啟動，
                // 那個交棒只要在任何一個分支上斷掉（失敗、取消、競態），它就永遠停在空白。
                // 掃一遍「上游其實都好了、自己卻沒開始過」的節點並補跑，直到沒有人可以補為止。
                await SweepReadyJoinNodesAsync(runState, cts);
            }
            finally
            {
                _runningChainNode = null;
                if (ReferenceEquals(_workflowChainCts, cts))
                    _workflowChainCts = null;
                // 仍停在等待中的（上游失敗、被取消、或空節點被略過）→ 復位為閒置。
                ClearFlowSubtreeWaiting(startNode);
            }
        }

        /// <summary>
        /// 補跑那些「上游都完成了、自己卻沒被叫到」的節點。
        ///
        /// 菱形（A→B→D、A→C→D）的 D 是靠「B 或 C 跑完之後順便看一眼」才會啟動的。
        /// 那個交棒只要斷在任何一個分支上，D 就永遠是空白，而且畫面上沒有任何訊息。
        /// 與其相信每條分支都會乖乖交棒，不如在最後補一次掃描——
        /// 它只會啟動「規則上本來就該跑」的節點，不會多跑任何東西。
        /// </summary>
        private async Task SweepReadyJoinNodesAsync(FlowRunState state, CancellationTokenSource cts)
        {
            // 每一輪最多補一圈；補完可能又讓更下游的節點符合條件，所以重複到沒有進展。
            for (int pass = 0; pass < 8 && !cts.IsCancellationRequested; pass++)
            {
                var ready = MainCanvas.Children.OfType<NodeControl>()
                    .Where(n => state.InThisRun.Contains(n.Id))
                    .Where(n => !state.Started.Contains(n.Id))
                    .Where(n =>
                    {
                        var ups = GetFlowUpstreamNodes(n);
                        return ups.Count > 0 && ups.All(u => state.Completed.Contains(u.Id));
                    })
                    .ToList();

                if (ready.Count == 0)
                    return;

                foreach (var node in ready)
                {
                    if (cts.IsCancellationRequested) return;
                    AppLog.Info("Flow", $"收尾補跑：「{ShortLabel(node)}」的上游都完成了，但沒有人叫它");
                    await RunFlowSubtreeAsync(node, runThis: true, state, cts);
                }
            }
        }

        /// <summary>節點的短名稱，給紀錄用。</summary>
        private static string ShortLabel(NodeControl node)
        {
            string text = (node?.GetTopText() ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 18 ? (text.Length == 0 ? UiLanguage.P("（空節點）", "(empty node)") : text) : text.Substring(0, 18) + "…";
        }

        // 這一輪會走到哪些節點：沿流動邊從起點展開（含起點）。
        private void CollectFlowReachable(NodeControl root, HashSet<Guid> into)
        {
            if (root == null || !into.Add(root.Id))
                return;
            foreach (var child in GetFlowDownstreamNodes(root))
                CollectFlowReachable(child, into);
        }

        // 沿流動邊（藍色虛線）走整棵子樹，把節點標記為「等待中」。includeRoot=false 時不標 root 本身。
        // public：NodeControl 手動送出時也要「送出當下」就標整條下游（不等母節點跑完），使用者才看得到路徑已排隊。
        public void MarkFlowSubtreeWaiting(NodeControl root, bool includeRoot)
        {
            var visited = new HashSet<Guid>();
            void Walk(NodeControl n, bool mark)
            {
                if (n == null || !visited.Add(n.Id))
                    return;
                if (mark)
                    n.MarkWaiting();
                foreach (var child in GetFlowDownstreamNodes(n))
                    Walk(child, true);
            }
            Walk(root, includeRoot);
        }

        // 復位整棵流動子樹裡「仍停在等待中」的節點（已執行/失敗的不動）。
        // public：手動送出失敗 / 鏈忙碌被擋時，NodeControl 兜底復位（只動 Waiting，安全可重複呼叫）。
        public void ClearFlowSubtreeWaiting(NodeControl root)
        {
            var visited = new HashSet<Guid>();
            void Walk(NodeControl n)
            {
                if (n == null || !visited.Add(n.Id))
                    return;
                n.ClearWaitingStatus();
                foreach (var child in GetFlowDownstreamNodes(n))
                    Walk(child);
            }
            Walk(root);
        }

        /// <summary>
        /// 一次流動工作流執行的進度。所有分支的接續都回到 UI 執行緒上跑，
        /// 所以這幾個集合不需要鎖——但它們確實會被多條分支交錯存取。
        /// </summary>
        private sealed class FlowRunState
        {
            /// <summary>已經開始（或已結束）的節點，防止兩條分支同時啟動同一個。</summary>
            public HashSet<Guid> Started { get; } = new();

            /// <summary>已成功完成的節點。匯流節點要等它的上游全部落在這裡面。</summary>
            public HashSet<Guid> Completed { get; } = new();

            /// <summary>失敗的節點。它的下游不會再有人來叫，直接放棄那條路。</summary>
            public HashSet<Guid> Failed { get; } = new();

            /// <summary>
            /// 這一輪會走到的所有節點（沿流動邊從起點展開）。
            /// 用途：判斷某個上游的「舊的成功結果」還算不算數——會重跑的那些不算。
            /// </summary>
            public HashSet<Guid> InThisRun { get; } = new();
        }

        private async Task RunFlowSubtreeAsync(
            NodeControl node,
            bool runThis,
            FlowRunState state,
            CancellationTokenSource cts)
        {
            if (node == null || !IsNodeLive(node))
                return;

            if (cts.IsCancellationRequested)
                return;

            if (runThis)
            {
                // 匯流：所有「流動模式」上游都完成，才輪到這個節點。規則本身在 FlowSchedule，
                // 這裡只負責把「誰連到誰」交給它。
                //
                // 少了這道檢查，A→B→D 與 A→C→D 這種菱形裡，B、C 並行，先跑完的那條
                // 會直接啟動 D；另一條稍後走到 D 時已被當成「跑過了」而略過——
                // D 於是少吃了一條分支的結果，而且全程沒有任何錯誤訊息。
                var upstream = GetFlowUpstreamNodes(node);

                // 這一輪不會重跑、而且手上已經有成功結果的上游，算它已經滿足。
                // 少了這一段，「先手動跑 B、再手動跑 C」永遠湊不齊 D 的上游。
                var satisfied = upstream
                    .Where(u => !state.InThisRun.Contains(u.Id) &&
                                string.Equals(u.GetRunStatusKey(), "success", StringComparison.Ordinal))
                    .Select(u => u.Id)
                    .ToHashSet();

                var gate = FlowSchedule.Evaluate(
                    node.Id,
                    upstream.Select(u => u.Id),
                    state.Started, state.Completed, state.Failed, satisfied);

                // WaitForUpstream 不是「永遠不跑」：晚完成的那條分支稍後會再走到這裡。
                if (gate != FlowSchedule.Gate.Run)
                {
                    // 但如果最後沒有人回來叫它，畫面上就只是一個永遠空白的節點，
                    // 沒有任何訊息說它在等什麼（使用者 2026-10-15 回報的就是這個畫面）。
                    // 至少把「在等誰」留在紀錄裡，收尾掃描也靠這個判斷要不要補跑。
                    if (gate == FlowSchedule.Gate.WaitForUpstream)
                    {
                        var missing = upstream
                            .Where(u => !state.Completed.Contains(u.Id) && !satisfied.Contains(u.Id))
                            .Select(u => ShortLabel(u))
                            .ToList();
                        AppLog.Info("Flow", $"「{ShortLabel(node)}」在等上游：{string.Join("、", missing)}");
                    }
                    return;
                }

                state.Started.Add(node.Id);
                FocusDecisionNode(node);

                if (ShouldStopBeforeUnsupportedDownstreamNode(node))
                {
                    node.SetBottomText(
                        UiLanguage.P("（此下游節點需要尚未接上的專用代理，已停止以避免不必要的 token 消耗。）\n", "(This downstream node needs a specialized agent that isn't connected yet, so it stopped to avoid wasting tokens.)\n") +
                        UiLanguage.P("目前可先使用前一個節點的結果；等 presentation/file/media/workflow agent 完成後再啟用此步。", "For now you can use the previous node's result; this step will be enabled once the presentation/file/media/workflow agents are ready."));
                    return;
                }

                _runningChainNode = node;
                bool success = await node.RunCurrentTopTextAsync(cts.Token);
                _runningChainNode = null;

                if (cts.IsCancellationRequested)
                    return;

                // 父失敗 → 不往下跑這條分支（避免把錯誤內容往下游灌）。
                if (!success)
                {
                    state.Failed.Add(node.Id);
                    return;
                }

                state.Completed.Add(node.Id);
            }
            else
            {
                // 起點：它剛剛才跑完（或由使用者指定從這裡往下），視為已完成，
                // 否則它的下游會因為「上游未完成」而永遠不啟動。
                if (!state.Started.Add(node.Id))
                    return;
                state.Completed.Add(node.Id);
            }

            // 等父節點跑完 → 所有一級流動下游「同時並行」扇出（不再一個跑完才換下一個）。
            // 每個子節點各自再遞迴往下，所以整棵子樹仍維持「父先跑完、子才開始」的層序。
            var children = GetFlowDownstreamNodes(node);
            if (children.Count == 0)
                return;

            var branchTasks = new List<Task>(children.Count);
            foreach (var child in children)
            {
                if (cts.IsCancellationRequested)
                    break;

                branchTasks.Add(RunFlowSubtreeAsync(child, runThis: true, state, cts));
            }

            await Task.WhenAll(branchTasks);
        }
    }
}
