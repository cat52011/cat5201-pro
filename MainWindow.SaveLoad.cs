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
    // MainWindow 部分類別 — 存檔 / 讀檔、自動命名（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        private void SaveState()
        {
            if (!_hasStarted) return;
            if (_suppressSave) return;

            var state = BuildState(MainCanvas.Children.OfType<NodeControl>().ToList(), _connections, _initialNode, _fileNameLockedByUser);

            if (string.IsNullOrEmpty(_currentFilePath))
                _currentFilePath = Store.NewProjectPath();

            Store.Save(state, _currentFilePath!);

            // 個人化偏好與專案檔分離，但任何一次存檔都順手把全域偏好也寫回，確保隨時最新。
            SavePreferences();

            RefreshCurrentFileLabel();
        }

        /// <summary>
        /// 把一組節點與連線組成要存檔的狀態。目前畫面（SaveState）和背景執行中的存檔（SaveBackgroundSession）共用。
        /// 附件只收這組節點的——表裡也有其他存檔（背景執行中）的節點。
        /// </summary>
        private AppState BuildState(
            IReadOnlyList<NodeControl> canvasNodes, IEnumerable<Connection> connections, NodeControl? initialNode, bool fileNameLocked)
        {
            var nodeIds = canvasNodes.Select(n => n.Id).ToHashSet();
            var nodes = new List<NodeState>();
            foreach (var child in canvasNodes)
            {
                double x = SafeFinite(Canvas.GetLeft(child), 0);
                double y = SafeFinite(Canvas.GetTop(child), 0);
                double width = SafePositiveFinite(child.Width, 300);
                double height = SafePositiveFinite(child.Height, 400);
                double fontSize = SafePositiveFinite(child.GetFontSize(), 20);

                nodes.Add(new NodeState(
    child.Id.ToString(),
    x,
    y,
    width,
    height,
    child.GetTopText(),
    child.GetBottomText(),
    child.GetTopLocked(),
    fontSize,
    GetNodeSelectedAgent(child),
    GetNodeSelectedModel(child),
    GetNodeTaskModeStorageValue(child),
    _unsupportedDownstreamNodeIds.Contains(child.Id),
    child.GetOutputFilePaths().ToList(),
    child.GetOutputImagePath(),
    GetNodeCollaborationModels(child) is { Count: > 0 } collab ? collab.ToList() : null
));
            }

            var conns = new List<ConnState>();
            foreach (var c in connections)
            {
                conns.Add(new ConnState(
                    c.StartNode.Id.ToString(),
                    c.EndNode.Id.ToString(),
                    c.StartThumb,
                    c.EndThumb,
                    c.FlowMode
                ));
            }

            var atts = new List<AttachmentState>();
            foreach (var kv in _attachmentsByNode.Where(kv => nodeIds.Contains(kv.Key)))
            {
                var nodeId = kv.Key.ToString();
                foreach (var a in kv.Value)
                {
                    atts.Add(new AttachmentState(
                        nodeId,
                        a.FileName,
                        a.RelativePath,
                        a.MimeType,
                        a.Kind
                    ));
                }
            }

            var logs = new List<ExecutionLogState>();

            foreach (var child in canvasNodes)
            {
                var nodeLogs = GetExecutionLogs(child);
                foreach (var log in nodeLogs)
                {
                    logs.Add(ToExecutionLogState(log));
                }
            }

            return new AppState(
    DateTime.Now,
    initialNode?.Id.ToString(),
    nodes,
    conns,
    atts,
    logs,
    FileNameLocked: fileNameLocked,
    AutoModelSelectionEnabled: _isAutoModelSelectionEnabled,
    AdvancedAutoResolverEnabled: _isAdvancedAutoResolverEnabled,
    DownstreamAutoMode: DownstreamAutoModeHelper.ToStorageValue(_downstreamAutoMode),
    PresentationEngine: PresentationEngineHelper.ToStorageValue(_presentationEngine),
    TaskRoutingOverrides: _taskRoutingOverrides.ToStorage(),
    BlockOpus: AiAutoCostPolicy.BlockOpus,
    BlockDeepResearch: AiAutoCostPolicy.BlockDeepResearch,
    ManualTimeoutSeconds: _manualTimeoutSeconds,
    SchemaVersion: ProjectStore.CurrentSchemaVersion
);
        }

        private void LoadState(string path)
        {
            if (!File.Exists(path)) return;

            // 目前的存檔有節點在跑：整包留在背景繼續，不中止（見 MainWindow.Sessions.cs）。
            StashActiveProjectIfRunning();

            // 要開的存檔正在背景執行：掛回同一批節點，不從 JSON 重建（重建會丟掉執行中的進度與動畫）。
            if (TryRestoreBackgroundSession(path))
                return;

            // Store.Load 含 .bak fallback:主檔毀損時自動退上一版備份(原本直接 ReadAllText 沒這層救)。
            var state = Store.Load(path);
            if (state == null) return;

            StartUI.Visibility = Visibility.Collapsed;
            MainUI.Visibility = Visibility.Visible;
            _hasStarted = true;
            _currentFilePath = path;
            RefreshCurrentFileLabel();

            _fileNameLockedByUser = state.FileNameLocked;

            // 個人化設定（Auto/Manual、進階解析、下游展開、簡報引擎、任務指定模型、成本控制、逾時上限）
            // 一律以全域偏好為準，開啟舊專案「不」用檔案裡的舊值覆蓋。只重新同步 UI 反映目前的全域設定。
            SyncDownstreamAutoModeRadios();
            SyncPresentationEngineRadios();
            SyncDocumentEngineRadios();
            EnsureApiStatusRows();
            _lastAppliedAutoKeyword = "";
            _lastInitialTopSnapshot = "";

            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = _isAutoModelSelectionEnabled;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();

            // 背景仍在執行的存檔，它的節點設定要留著（節點 Id 全域唯一，不會和這個存檔混到）。
            ClearPerNodeStateExceptBackground();

            _hoveredDecisionNode = null;
            _lastDecisionNode = null;

            _editingNode = null;
            _editingReason = EditReason.None;

            _suppressSave = true;
            try
            {
                ClearAll();

                foreach (var a in state.Attachments ?? new List<AttachmentState>())
                {
                    if (!Guid.TryParse(a.NodeId, out var gid)) continue;

                    if (!_attachmentsByNode.TryGetValue(gid, out var list))
                    {
                        list = new List<AttachmentInfo>();
                        _attachmentsByNode[gid] = list;
                    }

                    list.Add(new AttachmentInfo
                    {
                        FileName = a.FileName ?? "",
                        RelativePath = a.RelativePath ?? "",
                        MimeType = string.IsNullOrWhiteSpace(a.MimeType) ? "application/octet-stream" : a.MimeType,
                        Kind = string.IsNullOrWhiteSpace(a.Kind) ? "file" : a.Kind
                    });
                }

                foreach (var logState in state.ExecutionLogs ?? new List<ExecutionLogState>())
                {
                    if (string.IsNullOrWhiteSpace(logState.NodeId))
                        continue;

                    // 載入歷史 log：只還原顯示，不重複計入 SpendLedger（重開專案不再累加舊成本）。
                    AddExecutionLog(ToExecutionLogEntry(logState));
                }

                var idMap = new Dictionary<string, NodeControl>();

                foreach (var n in state.Nodes ?? new List<NodeState>())
                {
                    var node = new NodeControl(n.Id);
                    node.Width = SafePositiveFinite(n.Width, 300);
                    node.Height = SafePositiveFinite(n.Height, 400);
                    Canvas.SetLeft(node, SafeFinite(n.X, 0));
                    Canvas.SetTop(node, SafeFinite(n.Y, 0));
                    Canvas.SetZIndex(node, GetNextZIndex());
                    MainCanvas.Children.Add(node);
                    HookNode(node);

                    string loadedAgentId = NormalizeOrDefaultAgentId(n.AgentId);
                    _nodeAgentsById[node.Id] = loadedAgentId;

                    var loadedAgent = AgentRegistry.Get(loadedAgentId);

                    string loadedModel = string.IsNullOrWhiteSpace(n.NodeModel)
                        ? AiModelHelper.NormalizeNodeModel(loadedAgent.DefaultModelId)
                        : _aiRouter.NormalizeNodeModel(n.NodeModel);

                    _nodeModelsById[node.Id] = loadedModel;

                    _nodeTaskModesById[node.Id] = string.IsNullOrWhiteSpace(n.TaskMode)
                        ? NodeTaskModeHelper.Normalize(loadedAgent.DefaultTaskMode)
                        : ParseNodeTaskMode(n.TaskMode);

                    if (n.UnsupportedDownstreamNode)
                        _unsupportedDownstreamNodeIds.Add(node.Id);

                    node.SetCommittedModelId(loadedModel, syncEditingModel: true);

                    node.SetTopText(n.TopText ?? "");
                    node.SetBottomText(n.BottomText ?? "");
                    // 已有輸出的節點：還原右下角「重新生成答案」鈕（重開程式後不該消失）。
                    if (!string.IsNullOrWhiteSpace(n.BottomText))
                        node.RestoreRegenerateAffordance();
                    SyncAutoFlowTemplate(node, n.TopText ?? "");
                    node.SetTopLocked(n.TopLocked);
                    node.SetFontSize(SafePositiveFinite(n.FontSize, 20));

                    // 還原上次執行產生、可點擊開啟的檔案 chip 與 inline 圖片（檔案需仍存在）。
                    node.RestoreOutputFiles(n.OutputFilePaths);
                    node.SetOutputImage(n.OutputImagePath);
                    SetNodeCollaborationModels(node, n.CollaborationModels);

                    idMap[n.Id] = node;
                }

                _initialNode = null;
                if (!string.IsNullOrWhiteSpace(state.InitialNodeId) && idMap.TryGetValue(state.InitialNodeId!, out var bySaved))
                {
                    _initialNode = bySaved;
                }
                else
                {
                    var incoming = new HashSet<string>((state.Connections ?? new List<ConnState>()).Select(c => c.EndId));
                    var rootId = (state.Nodes ?? new List<NodeState>()).Select(n => n.Id).FirstOrDefault(id => !incoming.Contains(id));
                    if (rootId != null && idMap.TryGetValue(rootId, out var byInference))
                        _initialNode = byInference;
                    else if ((state.Nodes?.Count ?? 0) > 0 && idMap.TryGetValue(state.Nodes![0].Id, out var byFirst))
                        _initialNode = byFirst;
                }

                Dispatcher.InvokeAsync(() =>
                {
                    foreach (var c in state.Connections ?? new List<ConnState>())
                    {
                        if (!idMap.TryGetValue(c.StartId, out var sn)) continue;
                        if (!idMap.TryGetValue(c.EndId, out var en)) continue;
                        CreateCurve(sn, c.StartThumb, en, c.EndThumb, c.FlowMode);
                    }

                    RefreshAllNodeAttachmentUIs();
                }, DispatcherPriority.Loaded);
            }
            finally
            {
                _suppressSave = false;
                SaveState();
                RefreshFileList();
                SelectFileInList(path);
                RefreshAllNodeModelSelectionUIs();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RestoreDecisionPanelAfterLoad();
                }), DispatcherPriority.Loaded);
            }

        }

        private void ClearAll()
        {
            MainCanvas.Children.Clear();
            _connections.Clear();
            _zIndexCounter = 0;
            _initialNode = null;
            ClearPerNodeStateExceptBackground();
            _editingNode = null;
            _editingReason = EditReason.None;
        }

        private void ScheduleAutoRenameFromInitialNode(NodeControl initialNode)
        {
            if (!_hasStarted) return;
            if (_suppressSave) return;
            if (_fileNameLockedByUser) return;
            if (string.IsNullOrWhiteSpace(_currentFilePath)) return;

            var top = (initialNode.GetTopText() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(top)) return;

            if (string.Equals(_lastInitialTopSnapshot, top, StringComparison.Ordinal))
                return;

            _lastInitialTopSnapshot = top;

            _autoRenameTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };

            _autoRenameTimer.Stop();
            _autoRenameTimer.Tick -= AutoRenameTimer_Tick;
            _autoRenameTimer.Tick += AutoRenameTimer_Tick;
            _autoRenameTimer.Start();

            void AutoRenameTimer_Tick(object? s, EventArgs e)
            {
                _autoRenameTimer?.Stop();
                _ = TryAutoRenameWithChatGPTStyleAsync(initialNode, top);
            }
        }

        private async Task TryAutoRenameWithChatGPTStyleAsync(NodeControl initialNode, string topSnapshot)
        {
            if (!_hasStarted) return;
            if (_suppressSave) return;
            if (_fileNameLockedByUser) return;
            if (string.IsNullOrWhiteSpace(_currentFilePath)) return;

            if (!File.Exists(_currentFilePath))
                return;

            string originalPath = _currentFilePath;

            try { _autoRenameCts?.Cancel(); } catch { }
            _autoRenameCts?.Dispose();
            _autoRenameCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ct = _autoRenameCts.Token;

            string keyword;

            try
            {
                keyword = await GenerateFileKeywordByAIAsync(initialNode, topSnapshot, ct).ConfigureAwait(true);
            }
            catch
            {
                keyword = ExtractFirstNonEmptyLine(topSnapshot);
            }

            keyword = NormalizeKeywordForFileName(keyword);
            if (string.IsNullOrWhiteSpace(keyword))
                return;

            // 等 AI 想名稱的這幾秒，使用者可能已經切到別的存檔（原本的存檔留在背景執行）。
            // 這時「目前的存檔」已經不是當初那個，不能拿去改名；原本的存檔先維持原名。
            if (!string.Equals(_currentFilePath, originalPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(_lastAppliedAutoKeyword, keyword, StringComparison.OrdinalIgnoreCase))
                return;

            var currentName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath);
            if (string.Equals(currentName, keyword, StringComparison.OrdinalIgnoreCase))
            {
                _lastAppliedAutoKeyword = keyword;
                return;
            }

            var desiredPath = System.IO.Path.Combine(SavesDir, keyword + ".json");
            var newPath = EnsureUniquePath(desiredPath);

            try
            {
                var oldBaseName = DisplayNameFromPath(_currentFilePath);
                var newBaseName = DisplayNameFromPath(newPath);

                // 低階搬移（不鎖檔名——鎖了自動命名就再也不會跑）；.bak 與附件資料夾一起搬、失敗自動回滾。
                var moved = Store.MoveProject(_currentFilePath, newPath);
                if (!moved.Ok)
                {
                    Debug.WriteLine("Auto rename aborted because attachment folder move failed: " + moved.Error);
                    return;
                }

                _currentFilePath = newPath;

                UpdateAttachmentRelativePathsInMemory(oldBaseName, newBaseName);
                RefreshAllNodeAttachmentUIs();
                MoveGeneratedFolderForRename(oldBaseName, newBaseName, null);
                SaveState();

                _lastAppliedAutoKeyword = keyword;

                RefreshCurrentFileLabel();
                RefreshFileList();
                SelectFileInList(newPath);
            }
            catch (Exception ex) { AppLog.Warn("Project", "自動改名流程失敗（沿用原檔名）", ex); }
        }

        private async Task<string> GenerateFileKeywordByAIAsync(NodeControl node, string topText, CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("自動命名");
            if (string.IsNullOrWhiteSpace(topText))
                return "";

            string model = GetEffectiveNodeModel(node, topText);
            var route = _aiRouter.GetRouteInfo(model);

            string instructions = "你是一個善於替筆記自動命名的助手。";

            string user =
$@"請將下面內容取一個標題，規則：{ShortTitle.Rules}
只輸出標題本身，不要任何解釋。

內容：
{Truncate(topText.Trim(), 800)}";

            switch (route.Provider)
            {
                case AiProviderKind.PerplexitySonar:
                    {
                        var svc = _aiRouter.GetPerplexitySonarService(route.ServiceModel);
                        var text = await svc.GenerateAsync(
                            instructions,
                            user,
                            maxOutputTokens: 200,
                            ct: ct);

                        return (text ?? "").Trim();
                    }

                case AiProviderKind.Claude:
                    {
                        var text = await _aiRouter.GetClaudeService(route.NodeModel).GenerateAsync(
                            instructions,
                            user,
                            maxOutputTokens: 200,
                            ct: ct);

                        return (text ?? "").Trim();
                    }

                case AiProviderKind.OpenAI:
                default:
                    {
                        var text = await _aiRouter.GetOpenAiService(route.NodeModel).GenerateAsync(
                            instructions,
                            user,
                            maxOutputTokens: 200,
                            ct: ct);

                        return (text ?? "").Trim();
                    }
            }
        }

        private static string ExtractFirstNonEmptyLine(string text)
        {
            foreach (var line in (text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var t = line.Trim();
                if (!string.IsNullOrWhiteSpace(t))
                    return t;
            }
            return "";
        }

        // 存檔與產出檔共用同一套整理規則（ShortTitle）。
        private static string NormalizeKeywordForFileName(string raw) => ShortTitle.Normalize(raw);

        private string EnsureUniquePath(string desiredFullPath)
        {
            if (!File.Exists(desiredFullPath))
                return desiredFullPath;

            var dir = System.IO.Path.GetDirectoryName(desiredFullPath) ?? SavesDir;
            var baseName = System.IO.Path.GetFileNameWithoutExtension(desiredFullPath);
            var ext = System.IO.Path.GetExtension(desiredFullPath);

            for (int i = 2; i < 9999; i++)
            {
                var candidate = System.IO.Path.Combine(dir, $"{baseName}_{i}{ext}");
                if (!File.Exists(candidate))
                    return candidate;
            }

            return desiredFullPath;
        }

        private void MarkFileNameLockedOnDisk(string filePath) => Store.MarkFileNameLockedOnDisk(filePath);
    }
}
