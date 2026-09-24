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
    public partial class MainWindow : Window, IAgentHost
    {
        private double _scale = 1.0;
        private ScaleTransform _scaleTransform = null!;
        private TranslateTransform _translateTransform = null!;
        private bool _isSidebarCollapsed = false;
        private int _zIndexCounter = 0;

        private NodeControl? _initialNode;

        private bool _fileNameLockedByUser = false;

        private DispatcherTimer? _autoRenameTimer;
        private CancellationTokenSource? _autoRenameCts;
        private string _lastInitialTopSnapshot = "";
        private string _lastAppliedAutoKeyword = "";

        private readonly AiServiceRouter _aiRouter = new();
        private NodeService _nodeService = null!;
        public NodeService NodeService => _nodeService;

        private NodeControl? _hoveredDecisionNode;
        private NodeControl? _lastDecisionNode;
        private const double DecisionPanelCompactWidth = 340;
        private const double DecisionPanelCompactHeight = 260;
        private const double DecisionPanelExpandedWidth = 720;
        private const double DecisionPanelExpandedHeight = 760;
        private const string DecisionPanelExpandIconData = "M2,6 L2,2 L6,2 M8,2 L12,2 L12,6 M12,8 L12,12 L8,12 M6,12 L2,12 L2,8";
        private const string DecisionPanelCollapseIconData = "M2,2 L6,6 M6,2 L6,6 L2,6 M12,12 L8,8 M8,12 L8,8 L12,8";
        private bool _isDecisionPanelExpanded = false;

        private readonly NodeModelSelectionService _nodeModelSelection = new();

        // 個人化「任務 → AI 模型」自訂路由表（單一真相，注入給 UI 預覽與實際執行兩條路徑）。
        private readonly NodeTaskRoutingOverrides _taskRoutingOverrides = new();
        public NodeTaskRoutingOverrides TaskRoutingOverrides => _taskRoutingOverrides;

        // §15 個人化：手動逾時上限（秒）。0 = 自動（依任務類型）。
        private int _manualTimeoutSeconds;
        // 設定面板程式化勾選時暫時靜音事件，避免初始化就寫入。
        private bool _syncingCostControls;
        public int GetManualTimeoutSeconds() => _manualTimeoutSeconds;

        // 個人化：下游節點執行時是否一併讀取上游（母）節點掛的附件（圖片／PDF／HTML…）。
        // 預設關閉＝只讀上游的文字輸出（較省）；開啟＝沿上游鏈繼承附件，下游看得到原始檔案但較貴。
        private bool _readUpstreamAttachments;
        public bool IsReadUpstreamAttachmentsEnabled() => _readUpstreamAttachments;

        private readonly Dictionary<Guid, NodeDecisionViewData> _liveDecisionViewsByNode = new();

        private readonly Dictionary<Guid, string> _autoFlowTemplatesByNode = new();
        private readonly Dictionary<Guid, NodeAutoFlowPolicy> _autoFlowPoliciesByNode = new();
        private readonly HashSet<Guid> _unsupportedDownstreamNodeIds = new();

        // §4：本次 session 中由「自動展開」生成的下游節點 id（用於避免 Mode 2 在生成節點上再次展開、無限遞迴）。
        private readonly HashSet<Guid> _generatedDownstreamNodeIds = new();

        // §4：多階段任務自動展開策略（個人化設定可切換；預設一鍵展開）。
        private DownstreamAutoMode _downstreamAutoMode = DownstreamAutoMode.OneClick;

        // 簡報生成器（個人化設定可切換；預設 Claude，Gamma 九月才開放）。
        private PresentationEngine _presentationEngine = PresentationEngine.Claude;

        // 影片導演風格（個人化）。空 = 用原廠電影感預設（VideoStyle.DefaultCinematicPrompt）；
        // 非空 = 使用者自訂風格，覆寫預設。注入給 Claude 導演 + Veo，確保所有鏡頭風格一致。
        private string _videoStyleOverride = "";

        /// <summary>影片任務實際生效的風格 prompt（使用者自訂優先，否則原廠電影感預設）。</summary>
        public string GetEffectiveVideoStylePrompt() => VideoStyle.Resolve(_videoStyleOverride);

        // 影片模型檔位（個人化）。前期測試預設 Lite（最省）；可選 Standard / Fast / Lite。
        private VeoModelTier _videoModelTier = VeoModelTier.Lite;

        /// <summary>影片任務實際使用的 Veo model id（依個人化檔位）。</summary>
        public string GetEffectiveVeoModel() => VeoModels.ModelId(_videoModelTier);

        /// <summary>目前生效的 Veo 檔位（給成本估算用）。</summary>
        public VeoModelTier GetVeoModelTier() => _videoModelTier;

        // §4 stop/skip：目前正在依序執行的工作流鏈的取消來源 + 當前執行中的節點。
        // 取消這個 cts 會透過 linked token 同時取消 in-flight 節點，並讓鏈迴圈在下一步前停止。
        private CancellationTokenSource? _workflowChainCts;
        private NodeControl? _runningChainNode;

        // 是否有工作流鏈正在跑（給右鍵選單決定是否啟用「停止工作流」）。
        public bool IsWorkflowChainRunning =>
            _workflowChainCts != null && !_workflowChainCts.IsCancellationRequested;

        public enum EditReason
        {
            None = 0,
            UserEdit = 1,
            NewNode = 2
        }

        private NodeControl? _editingNode;
        private EditReason _editingReason = EditReason.None;

        private class Connection
        {
            public PathShape Path = null!;
            public NodeControl StartNode = null!;
            public string StartThumb = "ThumbTL";
            public NodeControl EndNode = null!;
            public string EndThumb = "ThumbTR";

            // #4 流動模式：此連接線是否屬於「執行路徑」。流動線會有上游→下游的動畫，
            // 並決定一鍵執行時要沿哪些邊扇出。右鍵連接線可切換。
            public bool FlowMode;
        }

        public sealed class ContextConnectionInfo
        {
            public NodeControl StartNode { get; init; } = null!;
            public string StartThumb { get; init; } = "ThumbTL";
            public NodeControl EndNode { get; init; } = null!;
            public string EndThumb { get; init; } = "ThumbTR";
        }

        public IReadOnlyList<ContextConnectionInfo> GetConnectionsForContext()
        {
            return _connections
                .Where(c => c.StartNode != null && c.EndNode != null)
                .Select(c => new ContextConnectionInfo
                {
                    StartNode = c.StartNode,
                    StartThumb = c.StartThumb,
                    EndNode = c.EndNode,
                    EndThumb = c.EndThumb
                })
                .ToList();
        }

        private readonly List<Connection> _connections = new();
        public int GetNextZIndex() => ++_zIndexCounter;

        private bool _isPanning = false;
        private Point _lastMousePos;

        // ===== 變更上游節點（rewire）狀態 =====
        // 設計原則：成功前完全不更動連線資料——只把「主上游連線」暫時隱藏，改用一條 ghost 虛線跟著滑鼠。
        // 因此 ESC 取消 / 中途存檔 / 關程式都自動還原回原上游（資料根本沒被改過）。
        private bool _rewireMode;
        private Connection? _rewireConn;        // 被改接的主上游連線（EndNode == 觸發節點）
        private NodeControl? _rewireChild;      // 觸發 rewire 的下游節點
        private PathShape? _rewireGhost;        // 跟著滑鼠的虛線
        public bool IsRewireActive => _rewireMode;

        private static readonly Random _random = new();

        // MVP 可攜化（Codex P0-1）：資料根目錄不再寫死 D:\。
        // 開發機：legacy 路徑存在就沿用（行為零變、既有專案原地可用）；
        // 其他機器：%LOCALAPPDATA%\cat5201-pro\file（每台 Windows 都有效，打包發佈可直接跑）。
        // 所有使用者資料（專案/附件/產出/記憶/偏好/金鑰/帳本）都從這個根長出來，改這裡=全部跟著搬。
        private static readonly string _savesDirResolved = ResolveSavesDir();
        private string SavesDir => _savesDirResolved;

        private static string ResolveSavesDir()
        {
            const string legacyDevDir = @"D:\desk\college\cat5201-pro\file";
            try
            {
                if (Directory.Exists(legacyDevDir))
                    return legacyDevDir;
            }
            catch { /* 磁碟機不存在等：直接走可攜路徑 */ }

            try
            {
                string root = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "cat5201-pro", "file");
                Directory.CreateDirectory(root);
                return root;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Startup", "LOCALAPPDATA 資料目錄建立失敗，退回程式所在目錄", ex);
                return System.IO.Path.Combine(AppContext.BaseDirectory, "file");
            }
        }
        private string AttachmentsRootDir => System.IO.Path.Combine(SavesDir, "_attachments");
        private string GeneratedFilesDir => System.IO.Path.Combine(SavesDir, "_generated");
        public string GetGeneratedFilesDir() => GeneratedFilesDir;

        // pro 記憶根目錄＝pro 自己的 SavesDir。歷史上 NodeService 誤把 MemoryStore 指向畢業版旁的
        // D:\desk\college\final\file，造成 pro 與畢業版記憶混用（Codex P0-2）。已改回 pro 專屬，
        // 並在首次存取時把舊記憶「複製」過來（畢業版原檔只讀不動，維持「絕不碰畢業版」原則）。
        private const string LegacyMemoryBaseDir = @"D:\desk\college\final\file";
        private bool _legacyMemoryMigrationDone;

        public string GetMemoryBaseDir()
        {
            MigrateLegacyMemoryIfNeeded();
            return SavesDir;
        }

        private void MigrateLegacyMemoryIfNeeded()
        {
            if (_legacyMemoryMigrationDone)
                return;
            _legacyMemoryMigrationDone = true;

            try
            {
                var newFile = System.IO.Path.Combine(SavesDir, "_memory", "memory_store.json");
                if (File.Exists(newFile))
                    return; // pro 已有自己的記憶，不覆蓋

                var legacyFile = System.IO.Path.Combine(LegacyMemoryBaseDir, "_memory", "memory_store.json");
                if (!File.Exists(legacyFile))
                    return; // 沒有舊記憶可搬

                Directory.CreateDirectory(System.IO.Path.Combine(SavesDir, "_memory"));
                File.Copy(legacyFile, newFile, overwrite: false); // 複製而非搬移：畢業版原檔保留
                AppLog.Info("Memory", "已將舊記憶從畢業版資料夾複製到 pro 專屬記憶（畢業版原檔保留）。");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Memory", "舊記憶遷移失敗（將以現有/空白記憶啟動）", ex);
            }
        }

        private string? _currentFilePath;
        private bool _hasStarted = false;
        private bool _suppressSave = false;

        private readonly Dictionary<Guid, List<AttachmentInfo>> _attachmentsByNode = new();
        private readonly Dictionary<Guid, string> _nodeAgentsById = new();
        private readonly Dictionary<Guid, string> _nodeModelsById = new();
        private readonly Dictionary<Guid, NodeTaskMode> _nodeTaskModesById = new();

        private bool _isAutoModelSelectionEnabled = false;
        private bool _isAdvancedAutoResolverEnabled = false;

        private readonly AiExecutionLogService _executionLogService = new();

        // ApplyActiveTimelineVisual / ClearTimelineAnimations → 已搬到 DecisionTimelineRenderer(第 2 刀)。

        public bool NodeAcceptsAutoFlowInput(NodeControl node)
        {
            if (node == null)
                return false;

            string template = GetAutoFlowTemplate(node);
            if (string.IsNullOrWhiteSpace(template))
                return false;

            return template.Contains("{{input}}", StringComparison.Ordinal);
        }
        // AttachmentInfo 已提出至 cat5201.Core\AttachmentInfo.cs（B3b）——同命名空間,既有 bare 引用照常解析。

        // 檔案清單項。IsEditing/EditText 支援「就地改名」（右鍵→重新命名時，名稱原地變輸入框，
        // 像 ChatGPT 側欄那樣），不再彈獨立對話框。
        private sealed class FileItem : System.ComponentModel.INotifyPropertyChanged
        {
            public string FullPath { get; }
            public string DisplayName { get; }

            private bool _isEditing;
            public bool IsEditing
            {
                get => _isEditing;
                set { if (_isEditing != value) { _isEditing = value; OnPropertyChanged(nameof(IsEditing)); } }
            }

            private string _editText = "";
            public string EditText
            {
                get => _editText;
                set { if (_editText != value) { _editText = value; OnPropertyChanged(nameof(EditText)); } }
            }

            public FileItem(string fullPath)
            {
                FullPath = fullPath;
                DisplayName = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged(string name) =>
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        // 專案檔資料模型(NodeState/ConnState/AttachmentState/ExecutionLogState/AppState)與格式版本
        // 已抽出到 cat5201.Core\ProjectState.cs(第 1 刀,拆上帝物件);純 I/O 在 ProjectStore。
        // 本類別只負責:畫布狀態的收集(SaveState)與重建(LoadState)+檔案清單 UI。

        // 持久化 I/O 單一入口(存/讀/刪/改名/.bak/附件資料夾)。
        private ProjectStore? _projectStoreInstance;
        private ProjectStore Store => _projectStoreInstance ??= new ProjectStore(SavesDir);

        private static string DisplayNameFromPath(string path)
            => System.IO.Path.GetFileNameWithoutExtension(path);

        public MainWindow()
        {
            InitializeComponent();

            Directory.CreateDirectory(SavesDir);
            Directory.CreateDirectory(AttachmentsRootDir);

            var tg = new TransformGroup();
            _scaleTransform = new ScaleTransform(1.0, 1.0);
            _translateTransform = new TranslateTransform(0.0, 0.0);
            tg.Children.Add(_scaleTransform);
            tg.Children.Add(_translateTransform);
            MainCanvas.RenderTransform = tg;

            Viewport.MouseDown += Viewport_MouseDown;
            Viewport.MouseUp += Viewport_MouseUp;
            Viewport.MouseMove += Viewport_MouseMove;

            // 變更上游進行中：ESC 取消、還原原上游。
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && _rewireMode)
                {
                    CancelRewire();
                    e.Handled = true;
                }
            };

            Loaded += MainWindow_Loaded;
        }

        private static string BuildResolverKeywordSummary(AiExecutionLogEntry log)
        {
            if (log == null || log.ResolverKeywords == null || log.ResolverKeywords.Count == 0)
                return "";

            var keywords = log.ResolverKeywords
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (keywords.Count == 0)
                return "";

            return "keywords: " + string.Join(", ", keywords);
        }

        private static string BuildFallbackTraceSummary(AiExecutionLogEntry log)
        {
            if (log == null || log.FallbackAttempts == null || log.FallbackAttempts.Count == 0)
                return "";

            var parts = new List<string>();

            foreach (var attempt in log.FallbackAttempts)
            {
                if (attempt == null || string.IsNullOrWhiteSpace(attempt.ModelId))
                    continue;

                string modelLabel = AiModelHelper.GetDefinition(attempt.ModelId).DisplayName;
                string symbol = attempt.Success ? "?" : "?";

                parts.Add($"{attempt.AttemptIndex}.{modelLabel}{symbol}");
            }

            if (parts.Count == 0)
                return "";

            return "trace: " + string.Join(" → ", parts);
        }

        private static string BuildCapabilityDetailSummary(AiExecutionLogEntry log)
        {
            if (log == null || !log.CapabilityAdjusted)
                return "";

            string requestedLabel = AiModelHelper.GetDefinition(log.CapabilityRequestedModelId).DisplayName;
            string resolvedLabel = AiModelHelper.GetDefinition(log.CapabilityResolvedModelId).DisplayName;

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.CapabilityMissing))
                parts.Add($"missing: {log.CapabilityMissing}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequired) &&
                !string.Equals(log.CapabilityRequired, AiModelCapability.None.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"required: {log.CapabilityRequired}");
            }

            if (!string.Equals(requestedLabel, resolvedLabel, StringComparison.OrdinalIgnoreCase))
                parts.Add($"{requestedLabel} → {resolvedLabel}");

            if (log.CapabilityStreamingAdjusted)
                parts.Add("streaming → off");

            if (parts.Count == 0)
                return "";

            return "capability: " + string.Join(" / ", parts);
        }

        private static ExecutionLogState ToExecutionLogState(AiExecutionLogEntry entry)
        {
            return new ExecutionLogState(
                NodeId: entry.NodeId ?? "",
                StartedAtUtc: entry.StartedAtUtc,
                EndedAtUtc: entry.EndedAtUtc,
                DurationMs: entry.DurationMs,

                SelectionMode: entry.SelectionMode ?? "",
                Resolver: entry.Resolver ?? "",
                WorkspaceSummary: entry.WorkspaceSummary ?? "",
                WorkspaceArtifactDetails: entry.WorkspaceArtifactDetails?.ToList() ?? new List<string>(),
                WorkspaceArtifacts: entry.WorkspaceArtifacts?.ToList() ?? new List<AgentWorkspaceArtifactRecord>(),

                RequestedModelId: entry.RequestedModelId ?? "",
                PlannedModelId: entry.PlannedModelId ?? "",
                ActualModelId: entry.ActualModelId ?? "",

                TaskMode: entry.TaskMode ?? "",
                Confidence: entry.Confidence,

                ResolverReason: entry.ResolverReason ?? "",
                ResolverKeywords: entry.ResolverKeywords?.ToList() ?? new List<string>(),

                CapabilityAdjusted: entry.CapabilityAdjusted,
                CapabilityReason: entry.CapabilityReason ?? "",

                CapabilityRequestedModelId: entry.CapabilityRequestedModelId ?? "",
                CapabilityResolvedModelId: entry.CapabilityResolvedModelId ?? "",
                CapabilityRequired: entry.CapabilityRequired ?? "",
                CapabilityMissing: entry.CapabilityMissing ?? "",
                CapabilityStreamingAdjusted: entry.CapabilityStreamingAdjusted,

                CapabilityTrace: entry.CapabilityTrace?.ToList() ?? new List<AgentCapabilityTraceItem>(),

                RequestedAgentId: entry.RequestedAgentId ?? "",
                ActualAgentId: entry.ActualAgentId ?? "",

                RuntimeFallbackUsed: entry.RuntimeFallbackUsed,
                RuntimeFallbackSummary: entry.RuntimeFallbackSummary ?? "",
                Success: entry.Success,
                ErrorMessage: entry.ErrorMessage ?? "",

                FallbackAttempts: entry.FallbackAttempts?.ToList() ?? new List<AiFallbackAttempt>(),

                InputTokens: entry.InputTokens,
                OutputTokens: entry.OutputTokens,
                CostDisplay: entry.CostDisplay ?? "",
                UsageRecords: entry.UsageRecords?.ToList()
            );
        }
        private static AiExecutionLogEntry ToExecutionLogEntry(ExecutionLogState state)
        {
            return new AiExecutionLogEntry
            {
                NodeId = state.NodeId ?? "",
                StartedAtUtc = state.StartedAtUtc,
                EndedAtUtc = state.EndedAtUtc,
                DurationMs = state.DurationMs,

                SelectionMode = state.SelectionMode ?? "",
                Resolver = state.Resolver ?? "",

                RequestedModelId = AiModelHelper.NormalizeNodeModel(state.RequestedModelId),
                PlannedModelId = AiModelHelper.NormalizeNodeModel(state.PlannedModelId),
                ActualModelId = AiModelHelper.NormalizeNodeModel(state.ActualModelId),

                TaskMode = state.TaskMode ?? "",
                Confidence = state.Confidence,

                ResolverReason = state.ResolverReason ?? "",
                ResolverKeywords = state.ResolverKeywords?.ToList() ?? new List<string>(),

                CapabilityAdjusted = state.CapabilityAdjusted,
                CapabilityReason = state.CapabilityReason ?? "",

                CapabilityRequestedModelId = AiModelHelper.NormalizeNodeModel(state.CapabilityRequestedModelId),
                CapabilityResolvedModelId = AiModelHelper.NormalizeNodeModel(state.CapabilityResolvedModelId),
                CapabilityRequired = state.CapabilityRequired ?? "",
                CapabilityMissing = state.CapabilityMissing ?? "",
                CapabilityStreamingAdjusted = state.CapabilityStreamingAdjusted,

                CapabilityTrace = state.CapabilityTrace?.ToList() ?? new List<AgentCapabilityTraceItem>(),

                RequestedAgentId = state.RequestedAgentId ?? "",
                ActualAgentId = state.ActualAgentId ?? "",

                RuntimeFallbackUsed = state.RuntimeFallbackUsed,
                RuntimeFallbackSummary = state.RuntimeFallbackSummary ?? "",
                WorkspaceSummary = state.WorkspaceSummary ?? "",
                WorkspaceArtifactDetails = state.WorkspaceArtifactDetails?.ToList() ?? new List<string>(),
                WorkspaceArtifacts = state.WorkspaceArtifacts?.ToList() ?? new List<AgentWorkspaceArtifactRecord>(),
                Success = state.Success,
                ErrorMessage = state.ErrorMessage ?? "",

                FallbackAttempts = state.FallbackAttempts?.ToList() ?? new List<AiFallbackAttempt>(),

                InputTokens = state.InputTokens,
                OutputTokens = state.OutputTokens,
                CostDisplay = state.CostDisplay ?? "",
                UsageRecords = state.UsageRecords ?? new List<UsageRecord>()
            };
        }

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
                DecisionPanelToggleButton.ToolTip = _isDecisionPanelExpanded ? "縮回決策窗" : "放大決策窗";
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
                resolverText = "Responses API（預估）";
            }
            else
            {
                modeText = "Auto";
                statusText = "Rule Auto";
                resolverText = "Rules（預估）";
            }

            string previewSummary = $"{previewTaskName} / 尚未執行";

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
            Detail = $"{previewTaskName} / 預估",
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
            Detail = "尚未觸發",
            State = NodeDecisionStepState.Info,
            DetailLines = new[]
            {
                "Preview 階段尚未實際執行 capability"
            }
        },
        new NodeDecisionStepViewData
        {
            Title = "Delegation",
            Detail = "尚未觸發",
            State = NodeDecisionStepState.Info,
            DetailLines = new[]
            {
                "Preview 階段尚未實際執行 agent delegation"
            }
        },
        new NodeDecisionStepViewData
        {
            Title = "Execution",
            Detail = "尚未執行",
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

        public PerplexityService GetPerplexityToolService()
            => _aiRouter.GetPerplexityToolService();

        private string GetDefaultNodeModelId()
        {
            return AiModelRegistry.Default.Id;
        }

        private string GetDefaultAgentId()
        {
            return AgentRegistry.Default.Id;
        }

        private string NormalizeOrDefaultAgentId(string? agentId)
        {
            return AgentRegistry.IsKnown(agentId)
                ? AgentRegistry.Get(agentId).Id
                : GetDefaultAgentId();
        }

        public string GetNodeSelectedAgent(NodeControl node)
        {
            if (node == null)
                return GetDefaultAgentId();

            if (_nodeAgentsById.TryGetValue(node.Id, out var agentId))
                return NormalizeOrDefaultAgentId(agentId);

            var fallback = GetDefaultAgentId();
            _nodeAgentsById[node.Id] = fallback;
            return fallback;
        }

        public string GetEffectiveNodeAgent(NodeControl node, string? topText = null)
        {
            if (node == null)
                return GetDefaultAgentId();

            string selectedAgentId = GetNodeSelectedAgent(node);

            if (!_isAutoModelSelectionEnabled)
                return selectedAgentId;

            string text = topText ?? node.GetTopText() ?? "";
            var taskResolution = NodeTaskModeResolver.Resolve(text);
            var taskMode = NodeTaskModeHelper.Normalize(taskResolution.Mode);

            var resolver = new AgentSelectionResolver();
            var selection = resolver.Resolve(
                text,
                taskMode,
                GetAttachmentsForNode(node),
                selectedAgentId);

            return AgentRegistry.Get(selection.AgentId).Id;
        }

        public AgentDefinition GetNodeAgentDefinition(NodeControl node)
        {
            return AgentRegistry.Get(GetNodeSelectedAgent(node));
        }

        public void SetNodeSelectedAgent(NodeControl node, string agentId)
        {
            if (node == null)
                return;

            string normalized = NormalizeOrDefaultAgentId(agentId);
            _nodeAgentsById[node.Id] = normalized;

            var agent = AgentRegistry.Get(normalized);

            // Phase 1 相容模式：agent 改變時，同步預設 model/task
            _nodeModelsById[node.Id] = AiModelHelper.NormalizeNodeModel(agent.DefaultModelId);
            _nodeTaskModesById[node.Id] = NodeTaskModeHelper.Normalize(agent.DefaultTaskMode);

            SaveState();
        }

        private string NormalizeOrDefaultNodeModel(string? model)
        {
            return _aiRouter.NormalizeNodeModel(string.IsNullOrWhiteSpace(model)
                ? GetDefaultNodeModelId()
                : model);
        }

        private static NodeTaskMode GetDefaultNodeTaskMode()
        {
            return NodeTaskModeHelper.Default;
        }

        private static NodeTaskMode NormalizeOrDefaultTaskMode(NodeTaskMode mode)
        {
            return NodeTaskModeHelper.Normalize(mode);
        }

        private static NodeTaskMode ParseNodeTaskMode(string? raw)
        {
            return NodeTaskModeHelper.ParseOrDefault(raw);
        }

        public IReadOnlyList<NodeTaskModeOption> GetAllNodeTaskModes()
        {
            return NodeTaskModeHelper.All;
        }

        public NodeTaskMode GetNodeTaskMode(NodeControl node)
        {
            if (node == null)
                return GetDefaultNodeTaskMode();

            if (_nodeTaskModesById.TryGetValue(node.Id, out var mode))
                return NormalizeOrDefaultTaskMode(mode);

            var fallback = GetDefaultNodeTaskMode();
            _nodeTaskModesById[node.Id] = fallback;
            return fallback;
        }

        public void SetNodeTaskMode(NodeControl node, NodeTaskMode mode)
        {
            if (node == null) return;

            _nodeTaskModesById[node.Id] = NormalizeOrDefaultTaskMode(mode);
            SaveState();
        }

        public string GetNodeTaskModeStorageValue(NodeControl node)
        {
            return NodeTaskModeHelper.ToStorageValue(GetNodeTaskMode(node));
        }

        public string GetNodeTaskModeDisplayName(NodeControl node)
        {
            return NodeTaskModeHelper.ToDisplayName(GetNodeTaskMode(node));
        }

        private NodeTaskMode ResolvePreviewTaskModeForNode(NodeControl node)
        {
            if (node == null)
                return GetDefaultNodeTaskMode();

            string text = node.GetTopText() ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return GetNodeTaskMode(node);

            var resolution = NodeTaskModeResolver.Resolve(text);
            return NodeTaskModeHelper.Normalize(resolution.Mode);
        }

        public string GetNodeSelectedModel(NodeControl node)
        {
            if (node == null)
                return GetDefaultNodeModelId();

            if (_nodeModelsById.TryGetValue(node.Id, out var model))
                return NormalizeOrDefaultNodeModel(model);

            var fallback = GetDefaultNodeModelId();
            _nodeModelsById[node.Id] = fallback;
            return fallback;
        }

        public IReadOnlyList<AiExecutionLogEntry> GetExecutionLogs(NodeControl node)
        {
            if (node == null)
                return Array.Empty<AiExecutionLogEntry>();

            return _executionLogService.GetLogs(node.Id.ToString());
        }

        public AiExecutionLogEntry? GetLatestExecutionLog(NodeControl node)
        {
            if (node == null)
                return null;

            return _executionLogService.GetLatest(node.Id.ToString());
        }

        public void RefreshDecisionForNode(NodeControl node)
        {
            if (node == null)
                return;

            ShowDecisionForNode(node);
        }

        public void ClearExecutionLogs(NodeControl node)
        {
            if (node == null)
                return;

            _executionLogService.ClearNode(node.Id.ToString());
        }

        public void SetNodeSelectedModel(NodeControl node, string model)
        {
            if (node == null) return;

            _nodeModelsById[node.Id] = NormalizeOrDefaultNodeModel(model);
            SaveState();
        }

        public bool IsAutoModelSelectionEnabled()
        {
            return _isAutoModelSelectionEnabled;
        }

        public bool IsAdvancedAutoResolverEnabled()
        {
            return _isAdvancedAutoResolverEnabled;
        }

        public void SetAutoModelSelectionEnabled(bool enabled, bool save = true)
        {
            _isAutoModelSelectionEnabled = enabled;

            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = enabled;

            if (!enabled)
                _isAdvancedAutoResolverEnabled = false;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();
            RefreshAllNodeModelSelectionUIs();

            if (save)
                SaveState();
        }

        public void SetAdvancedAutoResolverEnabled(bool enabled, bool save = true)
        {
            if (!_isAutoModelSelectionEnabled)
                enabled = false;

            _isAdvancedAutoResolverEnabled = enabled;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = enabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();
            RefreshAllNodeModelSelectionUIs();

            if (save)
                SaveState();
        }

        private void UpdateAdvancedAutoResolverVisibility()
        {
            if (AdvancedAutoResolverSwitch == null)
                return;

            AdvancedAutoResolverSwitch.Visibility =
                _isAutoModelSelectionEnabled
                    ? Visibility.Visible
                    : Visibility.Collapsed;
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
     model: "等待送出",
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
     model: "等待送出",
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

        public string GetEffectiveNodeModel(NodeControl node, string? topText = null)
        {
            var manualModel = GetNodeSelectedModel(node);

            if (!_isAutoModelSelectionEnabled)
                return manualModel;

            string text = topText ?? node?.GetTopText() ?? "";
            var resolution = NodeTaskModeResolver.Resolve(text);

            return _nodeModelSelection.ResolveRuleAutoModel(
                NodeTaskModeHelper.Normalize(resolution.Mode),
                manualModel);
        }

        public bool CanUserManuallySelectModel()
        {
            return !_isAutoModelSelectionEnabled;
        }

        public void RefreshAllNodeModelSelectionUIs()
        {
            foreach (var node in MainCanvas.Children.OfType<NodeControl>())
            {
                node.RefreshModelSelectionUI();
            }
        }

        public void RequestBeginEdit(NodeControl node, EditReason reason)
        {
            if (node == null) return;

            if (_editingNode != null && !ReferenceEquals(_editingNode, node))
            {
                _editingNode.ForceExitEditMode();
                _editingNode = null;
                _editingReason = EditReason.None;
            }

            _editingNode = node;
            _editingReason = reason;

            node.EnterEditMode();
        }

        public void NotifyEditEnded(NodeControl node)
        {
            if (node == null) return;

            if (_editingNode != null && ReferenceEquals(_editingNode, node))
            {
                _editingNode = null;
                _editingReason = EditReason.None;
            }
        }

        public void NotifyNodeSubmitted(NodeControl node)
        {
            if (node == null) return;
            if (!IsInitialNode(node)) return;

            ScheduleAutoRenameFromInitialNode(node);
        }

        private void ClearEditingIfDeleted(NodeControl node)
        {
            if (_editingNode != null && ReferenceEquals(_editingNode, node))
            {
                _editingNode = null;
                _editingReason = EditReason.None;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 商品級門面：標題列顯示版本（版本號單一真相＝csproj <Version>）。
            var ver = typeof(MainWindow).Assembly.GetName().Version;
            if (ver != null)
                Title = $"cat5201-pro v{ver.Major}.{ver.Minor}.{ver.Build}";

            Sidebar.Visibility = Visibility.Visible;
            StartUI.Visibility = Visibility.Visible;
            MainUI.Visibility = Visibility.Collapsed;
            _isSidebarCollapsed = false;

            // API 金鑰來源先初始化（在任何 AI 服務建構 / WarmupSafely 之前），讓使用者輸入的金鑰可被解析。
            ApiKeyStore.Initialize(System.IO.Path.GetDirectoryName(PreferencesPath) ?? SavesDir);

            // 花費帳本（花錢安全）：在任何執行之前載入，之後每筆 LLM/媒體成本都會累計。
            SpendLedger.Initialize(System.IO.Path.GetDirectoryName(PreferencesPath) ?? SavesDir);

            // #10 長任務日誌：影片 operation 落地/恢復用（與帳本同資料夾）。
            VideoJobJournal.Initialize(System.IO.Path.GetDirectoryName(PreferencesPath) ?? SavesDir);

            // 全域個人化偏好先載入（在同步開關之前），確保所有設定一律以個人化為準，跨專案、跨重啟一致。
            LoadPreferences();
            SyncDownstreamAutoModeRadios();
            SyncPresentationEngineRadios();
            SyncDocumentEngineRadios();
            EnsureApiStatusRows();

            // 個人化若已開啟手機鏡像，啟動時自動把唯讀 server 拉起來（fire-and-forget，不擋 UI）。
            _ = AutoStartMobileMirrorIfEnabledAsync();

            WireDrivePostWriteHook(); // §18：依偏好與憑證狀態掛上 Drive 自動上傳

            // P2-2 會話還原：偵測上次崩潰 → 提示開回最後的專案；並寫入本次 marker。
            bool prevCrashed = DetectCrashAndWriteSessionLock();
            OfferSessionRestoreIfCrashed(prevCrashed);

            // P2-1 首次啟動精靈：四把主金鑰全空 → 引導輸入與驗證。
            ShowOnboardingIfNoKeys();

            // #10：上次關程式前有已付費、未完成的影片任務 → 問使用者要不要接續取回（不重複付費）。
            OfferResumePendingVideoJobs();

            SetRandomStartMessage();
            RefreshFileList();

            _aiRouter.WarmupSafely();
            // UI 預覽路徑（GetEffectiveNodeModel）也套用同一份自訂路由表。
            _nodeModelSelection.UseOverrides(_taskRoutingOverrides);
            _nodeService = new NodeService(_aiRouter, this);
            RefreshMemoryPanel();

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
        }

        private void SetRandomStartMessage()
        {
            string[] messages =
            {
                "讓我們有個新的開始！",
                "或許我們該從頭來過…",
                "再來一次吧！忘掉過去的那些…",
                "來點新的？"
            };
            StartMessage.Text = messages[_random.Next(messages.Length)];
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartUI.Visibility = Visibility.Collapsed;
            MainUI.Visibility = Visibility.Visible;

            _currentFilePath = System.IO.Path.Combine(SavesDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";
            _hasStarted = true;

            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = _isAutoModelSelectionEnabled;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();

            _fileNameLockedByUser = false;
            _lastAppliedAutoKeyword = "";
            _lastInitialTopSnapshot = "";

            _attachmentsByNode.Clear();
            _nodeModelsById.Clear();
            _nodeTaskModesById.Clear();

            _editingNode = null;
            _editingReason = EditReason.None;

            Dispatcher.InvokeAsync(() =>
            {
                var node = new NodeControl();
                double x = MainCanvas.ActualWidth / 2 - node.Width / 2;
                double y = MainCanvas.ActualHeight / 2 - node.Height / 2;

                Canvas.SetLeft(node, SafeFinite(x, 0));
                Canvas.SetTop(node, SafeFinite(y, 0));
                Canvas.SetZIndex(node, GetNextZIndex());
                MainCanvas.Children.Add(node);
                HookNode(node);

                _nodeAgentsById[node.Id] = GetDefaultAgentId();

                var defaultAgent = AgentRegistry.Get(GetDefaultAgentId());
                _nodeModelsById[node.Id] = AiModelHelper.NormalizeNodeModel(defaultAgent.DefaultModelId);
                _nodeTaskModesById[node.Id] = NodeTaskModeHelper.Normalize(defaultAgent.DefaultTaskMode);
                _initialNode = node;

                _scale = 1.0;
                _scaleTransform.ScaleX = 1.0;
                _scaleTransform.ScaleY = 1.0;
                _translateTransform.X = 0.0;
                _translateTransform.Y = 0.0;

                RequestBeginEdit(node, EditReason.NewNode);

                SaveState();
                RefreshFileList();

            }, DispatcherPriority.Loaded);
        }

        private void NewFile_Click(object sender, RoutedEventArgs e)
        {
            StartUI.Visibility = Visibility.Visible;
            MainUI.Visibility = Visibility.Collapsed;
            SetRandomStartMessage();

            _hasStarted = false;
            _currentFilePath = null;
            CurrentFileLabel.Text = "目前檔案：尚未建立";
            ClearAll();

            _fileNameLockedByUser = false;
            _lastAppliedAutoKeyword = "";
            _lastInitialTopSnapshot = "";
            _attachmentsByNode.Clear();
            _nodeModelsById.Clear();
            _nodeTaskModesById.Clear();
            _nodeAgentsById.Clear();

            _editingNode = null;
            _editingReason = EditReason.None;

            _isAutoModelSelectionEnabled = false;
            _isAdvancedAutoResolverEnabled = false;

            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = false;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = false;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();
        }

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is FileItem item && File.Exists(item.FullPath))
            {
                LoadState(item.FullPath);
            }
        }

        private void FileList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TrySelectItemUnderMouse(FileList, e.GetPosition(FileList), out var item) == false)
                return;

            if (item == null || !File.Exists(item.FullPath))
                return;

            var cmStyle = (Style)FindResource("FileContextMenuStyle");
            var miStyle = (Style)FindResource("FileMenuItemStyle");
            var sepStyle = (Style)FindResource("FileSeparatorStyle");

            var menu = new ContextMenu
            {
                Style = cmStyle,
                PlacementTarget = FileList
            };

            var miRename = new MenuItem { Header = "重新命名", Style = miStyle };
            miRename.Click += (_, __) => BeginInlineRename(item);

            var miDelete = new MenuItem { Header = "刪除", Style = miStyle };
            miDelete.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F")!);
            miDelete.Click += (_, __) => DeleteFile(item.FullPath);

            var sep = new Separator { Style = sepStyle };

            menu.Items.Add(miRename);
            menu.Items.Add(sep);
            menu.Items.Add(miDelete);

            menu.IsOpen = true;
            e.Handled = true;
        }

        private static bool TrySelectItemUnderMouse(ListBox listBox, Point pos, out FileItem? selectedItem)
        {
            selectedItem = null;

            var element = listBox.InputHitTest(pos) as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = VisualTreeHelper.GetParent(element);

            if (element is not ListBoxItem item)
                return false;

            listBox.SelectedItem = item.DataContext;
            selectedItem = listBox.SelectedItem as FileItem;
            return selectedItem != null;
        }

        private void DeleteFile(string path)
        {
            var name = DisplayNameFromPath(path);

            bool ok = MenuConfirmDialog.ShowDeleteConfirm(
                owner: this,
                title: "刪除確認",
                message: $"確定要刪除檔案？\n{name}",
                resourceHost: this);

            if (!ok) return;

            try
            {
                Store.Delete(path); // 主檔+.bak+附件資料夾一次清（磁碟事務在 ProjectStore）

                if (!string.IsNullOrEmpty(_currentFilePath) &&
                    string.Equals(_currentFilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFilePath = null;
                    _hasStarted = false;
                    CurrentFileLabel.Text = "目前檔案：尚未建立";
                    _fileNameLockedByUser = false;
                    _lastAppliedAutoKeyword = "";
                    _lastInitialTopSnapshot = "";
                    _attachmentsByNode.Clear();
                    _nodeModelsById.Clear();
                    _nodeTaskModesById.Clear();
                    _isAutoModelSelectionEnabled = false;
                    _isAdvancedAutoResolverEnabled = false;

                    if (AutoModelSwitch != null)
                        AutoModelSwitch.IsChecked = false;

                    if (AdvancedAutoResolverSwitch != null)
                        AdvancedAutoResolverSwitch.IsChecked = false;

                    UpdateAdvancedAutoResolverVisibility();
                    UpdateDecisionPanelForCurrentMode();
                }

                RefreshFileList();
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, "錯誤", $"刪除失敗：{ex.Message}", this);
            }
        }


        // ===== #10 長任務恢復：上次未完成的影片 operation（已付費）續輪詢取回 =====

        private void OfferResumePendingVideoJobs()
        {
            try
            {
                var pending = VideoJobJournal.GetResumable();
                if (pending.Count == 0)
                    return;

                string preview = string.Join("\n", pending.Take(3).Select(j =>
                    "・" + VideoJobJournal.Summarize(j.Prompt)));

                bool ok = MenuConfirmDialog.ShowConfirm(
                    owner: this,
                    title: "未完成的影片任務",
                    message: $"偵測到 {pending.Count} 個上次未完成的影片生成（費用已支付）：\n{preview}\n\n" +
                             "要接續查詢並取回嗎？只是查詢雲端進度，不會重新付費。",
                    resourceHost: this,
                    confirmText: "取回",
                    cancelText: "略過");

                if (ok)
                    _ = ResumePendingVideoJobsAsync(pending);
            }
            catch (Exception ex) { AppLog.Warn("VideoJobs", "影片任務恢復提示失敗", ex); }
        }

        private async Task ResumePendingVideoJobsAsync(IReadOnlyList<VideoJobJournal.VideoJob> jobs)
        {
            int okCount = 0;
            var failMsgs = new List<string>();

            foreach (var job in jobs)
            {
                try
                {
                    var veo = new VeoVideoService();
                    var result = await veo.ResumeAsync(job.OperationName, onProgress: null, CancellationToken.None);

                    if (result.Success && result.Mp4Bytes.Length > 0)
                    {
                        var payload = GeneratedFileWriter.WriteVideo(
                            GetGeneratedFilesDir(),
                            title: string.IsNullOrWhiteSpace(job.Prompt)
                                ? "恢復的影片"
                                : "恢復的影片_" + VideoJobJournal.Summarize(job.Prompt, 24).TrimEnd('…'),
                            content: result.Mp4Bytes,
                            sourceSummary: "程式重啟後接續取回（Veo operation 續輪詢，未重新付費）");

                        if (payload.Success)
                        {
                            okCount++;
                            // 檔案真正寫好才結案；寫檔失敗就保持 pending，下次啟動還能再取回。
                            VideoJobJournal.MarkDone(job.OperationName);
                        }
                        else
                        {
                            failMsgs.Add(payload.ErrorMessage);
                        }
                    }
                    else
                    {
                        failMsgs.Add(string.IsNullOrWhiteSpace(result.ErrorMessage) ? "未知原因" : result.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    failMsgs.Add(ex.Message);
                    AppLog.Warn("VideoJobs", $"影片任務恢復失敗：{job.OperationName}", ex);
                }
            }

            string summary = okCount > 0 ? $"已取回 {okCount} 支影片到產出資料夾（_generated）。" : "";
            if (failMsgs.Count > 0)
                summary += $"\n{failMsgs.Count} 個未能取回：{failMsgs[0]}";

            MenuConfirmDialog.ShowMessage(this, "影片任務恢復",
                string.IsNullOrWhiteSpace(summary) ? "沒有可取回的影片。" : summary.Trim(), this);
        }

        // ===== 就地改名（右鍵→重新命名）：名稱原地變輸入框，Enter 確認 / Esc 取消 / 失焦確認 =====

        private void BeginInlineRename(FileItem item)
        {
            if (item == null)
                return;

            // 一次只編輯一個：先收掉其他項的編輯狀態。
            if (FileList.ItemsSource is IEnumerable<FileItem> list)
            {
                foreach (var other in list)
                    other.IsEditing = false;
            }

            item.EditText = item.DisplayName;
            item.IsEditing = true;
        }

        private void FileRenameEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // DataTrigger 切 Visible 時把焦點與全選帶進輸入框（Loaded 只發一次，不能用）。
            if (sender is TextBox tb && e.NewValue is bool visible && visible)
            {
                tb.Dispatcher.BeginInvoke(new Action(() =>
                {
                    tb.Focus();
                    tb.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void FileRenameEditor_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb)
                return;

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitInlineRename(tb);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (tb.DataContext is FileItem item)
                    item.IsEditing = false; // 丟棄輸入，回顯示狀態
            }
        }

        private void FileRenameEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                CommitInlineRename(tb); // 失焦＝確認（ChatGPT 行為）；內部有 IsEditing guard 防重入
        }

        private void CommitInlineRename(TextBox tb)
        {
            if (tb.DataContext is not FileItem item || !item.IsEditing)
                return;

            item.IsEditing = false; // 先收編輯狀態（Enter 後的 LostFocus 不會重複提交）

            string newName = (item.EditText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, item.DisplayName, StringComparison.Ordinal))
                return; // 空白或沒改 → 視為取消

            PerformRename(item.FullPath, newName);
        }

        private void PerformRename(string oldPath, string requestedName)
        {
            var newName = NormalizeKeywordForFileName(requestedName);
            if (string.IsNullOrWhiteSpace(newName))
                return;

            try
            {
                var oldBaseName = DisplayNameFromPath(oldPath);

                // 磁碟事務（Move+.bak+附件資料夾+路徑改寫+鎖定旗標+失敗回滾）整包在 ProjectStore。
                var result = Store.Rename(oldPath, newName);
                if (!result.Ok)
                {
                    if (!string.IsNullOrWhiteSpace(result.Error))
                        MenuConfirmDialog.ShowMessage(this, "重新命名失敗", result.Error, this);
                    return;
                }

                if (!string.IsNullOrEmpty(_currentFilePath) &&
                    string.Equals(_currentFilePath, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFilePath = result.NewPath;
                    _fileNameLockedByUser = true;

                    UpdateAttachmentRelativePathsInMemory(oldBaseName, DisplayNameFromPath(result.NewPath));
                    RefreshAllNodeAttachmentUIs();

                    CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";

                    SaveState();
                }

                RefreshFileList();
                SelectFileInList(result.NewPath);
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, "錯誤", $"重新命名失敗：{ex.Message}", this);
            }
        }

        // MoveAttachmentFolderSafely / .bak sidecar / 附件路徑改寫 → 已抽出到 ProjectStore（第 1 刀）。

        private void RefreshAllNodeAttachmentUIs()
        {
            foreach (var node in MainCanvas.Children.OfType<NodeControl>())
            {
                node.RefreshAttachmentsUI();
            }
        }

        private void SelectFileInList(string fullPath)
        {
            if (FileList.ItemsSource is not IEnumerable<FileItem> list) return;

            var target = list.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (target == null) return;

            FileList.SelectedItem = target;
            FileList.ScrollIntoView(target);
        }

        public void HookNode(NodeControl node)
        {
            if (!_nodeModelsById.ContainsKey(node.Id))
                _nodeModelsById[node.Id] = GetDefaultNodeModelId();

            if (!_nodeTaskModesById.ContainsKey(node.Id))
                _nodeTaskModesById[node.Id] = GetDefaultNodeTaskMode();

            if (!_autoFlowPoliciesByNode.ContainsKey(node.Id))
                _autoFlowPoliciesByNode[node.Id] = NodeAutoFlowPolicy.Default;
            node.Moved -= Node_Moved;
            node.Moved += Node_Moved;

            node.ContentChanged -= Node_ContentChanged;
            node.ContentChanged += Node_ContentChanged;
        }

        private void Node_Moved(object? sender, EventArgs e)
        {
            if (sender is NodeControl node)
                UpdateConnectionsFor(node);

            SaveState();
        }

        private void Node_ContentChanged(object? sender, EventArgs e)
        {
            SaveState();
        }

        private Point GetThumbCenterOnCanvas(NodeControl node, string thumbName)
        {
            if (node == null)
                return new Point(0, 0);

            return node.GetThumbCenterIgnoringHoverTransform(thumbName);
        }

        public NodeControl AddNode(double x, double y, bool beginEdit = true)
        {
            var node = new NodeControl();

            Canvas.SetLeft(node, SafeFinite(x - node.Width / 2, 0));
            Canvas.SetTop(node, SafeFinite(y - node.Height / 2, 0));
            Canvas.SetZIndex(node, GetNextZIndex());

            HookNode(node);

            _nodeAgentsById[node.Id] = GetDefaultAgentId();

            var defaultAgent = AgentRegistry.Get(GetDefaultAgentId());
            _nodeModelsById[node.Id] = AiModelHelper.NormalizeNodeModel(defaultAgent.DefaultModelId);
            _nodeTaskModesById[node.Id] = NodeTaskModeHelper.Normalize(defaultAgent.DefaultTaskMode);

            MainCanvas.Children.Add(node);

            if (beginEdit)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainCanvas.Children.Contains(node))
                    {
                        RequestBeginEdit(node, EditReason.NewNode);
                    }
                }), DispatcherPriority.Loaded);
            }

            SaveState();
            return node;
        }

        public void CreateCurve(NodeControl startNode, string startThumbName, NodeControl endNode, string endThumbName, bool flowMode = false)
        {
            var path = new PathShape
            {
                Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#ADADAD")!,
                StrokeThickness = 18,
                // #4：連接線需可被右鍵切換流動模式，故開啟命中測試。
                IsHitTestVisible = true,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var conn = new Connection
            {
                Path = path,
                StartNode = startNode,
                StartThumb = startThumbName,
                EndNode = endNode,
                EndThumb = endThumbName,
                FlowMode = flowMode
            };

            // 右鍵連接線：切換流動模式（含動畫）。Handled 避免冒泡到畫布平移。
            path.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                _ = ToggleConnectionFlowModeAsync(conn);
            };

            UpdateConnectionGeometry(conn);
            Canvas.SetZIndex(path, GetNextZIndex());
            MainCanvas.Children.Add(path);
            _connections.Add(conn);
            ApplyConnectionVisual(conn);

            HookNode(startNode);
            HookNode(endNode);

            SaveState();
        }

        // #4：依流動模式套用連接線外觀——流動時藍色虛線 + 上游→下游的行進動畫；否則回灰色實線。
        private void ApplyConnectionVisual(Connection conn)
        {
            if (conn?.Path == null)
                return;

            if (conn.FlowMode)
            {
                conn.Path.Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#2E6FE0")!;
                conn.Path.StrokeDashArray = new DoubleCollection { 1.1, 1.1 };

                var anim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 2.2,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromSeconds(0.7)),
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                conn.Path.BeginAnimation(PathShape.StrokeDashOffsetProperty, anim);
            }
            else
            {
                conn.Path.BeginAnimation(PathShape.StrokeDashOffsetProperty, null);
                conn.Path.StrokeDashOffset = 0;
                conn.Path.StrokeDashArray = null;
                conn.Path.Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#ADADAD")!;
            }
        }

        // #4：右鍵切換某條連接線的流動模式。「何時開始跑」交由自動化設定（個人化）決定：
        //  完全自動 → 切到流動就立刻跑這條分支（會先等母節點有輸出）；一鍵/關閉 → 只標記+動畫，由使用者從上游手動執行。
        private async Task ToggleConnectionFlowModeAsync(Connection conn)
        {
            if (conn == null)
                return;

            conn.FlowMode = !conn.FlowMode;
            ApplyConnectionVisual(conn);
            SaveState();

            if (conn.FlowMode &&
                _downstreamAutoMode == DownstreamAutoMode.FullyAuto &&
                !IsWorkflowChainRunning &&
                conn.StartNode != null)
            {
                try
                {
                    bool parentHasOutput = !string.IsNullOrWhiteSpace(conn.StartNode.GetBottomText());
                    await RunFlowWorkflowAsync(conn.StartNode, runStartNode: !parentHasOutput);
                }
                catch (OperationCanceledException)
                {
                    // 逾時 / 停止：安靜結束。
                }
            }
        }

        public IReadOnlyList<NodeControl> MaterializeDownstreamNodePlan(
            NodeControl sourceNode,
            DownstreamNodePlanPayload plan)
        {
            if (sourceNode == null || plan?.ProposedNodes == null || plan.ProposedNodes.Count == 0)
                return Array.Empty<NodeControl>();

            var created = new List<NodeControl>();
            double sourceLeft = Canvas.GetLeft(sourceNode);
            double sourceTop = Canvas.GetTop(sourceNode);

            if (double.IsNaN(sourceLeft))
                sourceLeft = 0;

            if (double.IsNaN(sourceTop))
                sourceTop = 0;

            const double downstreamNodeSpacingX = 340;
            const double downstreamNodeOffsetY = 80;
            const double downstreamNodeRowGap = 360;
            double x = sourceLeft + sourceNode.Width + 130;
            double y = ResolveAvailableDownstreamRowY(
                sourceNode,
                x,
                sourceTop + downstreamNodeOffsetY,
                plan.ProposedNodes.Count,
                downstreamNodeSpacingX,
                downstreamNodeRowGap);
            NodeControl previousNode = sourceNode;
            int index = 0;

            foreach (var proposal in plan.ProposedNodes.Where(x => x != null))
            {
                var node = new NodeControl();
                Canvas.SetLeft(node, SafeFinite(x + index * downstreamNodeSpacingX, 0));
                Canvas.SetTop(node, SafeFinite(y, 0));
                Canvas.SetZIndex(node, GetNextZIndex());

                HookNode(node);
                MainCanvas.Children.Add(node);

                string requestedAgent = proposal.AgentId ?? "";
                string effectiveAgent = AgentRegistry.IsKnown(requestedAgent)
                    ? requestedAgent
                    : GetDefaultAgentId();
                bool requiresFutureAgent = !string.Equals(requestedAgent, effectiveAgent, StringComparison.OrdinalIgnoreCase);

                SetNodeSelectedAgent(node, effectiveAgent);

                var agent = AgentRegistry.Get(effectiveAgent);
                _nodeModelsById[node.Id] = AiModelHelper.NormalizeNodeModel(agent.DefaultModelId);
                _nodeTaskModesById[node.Id] = ResolveTaskModeForDownstreamProposal(proposal, agent);

                if (requiresFutureAgent)
                    _unsupportedDownstreamNodeIds.Add(node.Id);
                else
                    _unsupportedDownstreamNodeIds.Remove(node.Id);

                // 標記為「自動生成的下游節點」，避免之後在它身上又自動展開（Mode 2 遞迴防護）。
                _generatedDownstreamNodeIds.Add(node.Id);

                node.SetTopText(BuildDownstreamNodePrompt(proposal));
                node.SetBottomText("");
                node.SetTopLocked(false);
                SetNodeAutoFlowPolicy(node, new NodeAutoFlowPolicy
                {
                    AutoRunEnabled = false,
                    WaitForAllInputs = false,
                    StopFlowOnError = true,
                    AllowPartialInput = false
                });
                SyncAutoFlowTemplate(node, node.GetTopText());

                // #4：自動展開的子節點與上游的連接線預設為「流動模式」（動畫 + 屬於執行路徑）。
                CreateCurve(previousNode, "ThumbTR", node, "ThumbTL", flowMode: true);

                created.Add(node);
                previousNode = node;
                index++;
            }

            RefreshConnectionsAfterLayout(new[] { sourceNode }.Concat(created).ToList());
            SaveState();
            return created;
        }

        // ===== §4 自動下游節點：偵測 / 一鍵展開並執行 / 完全自動 =====

        public DownstreamAutoMode GetDownstreamAutoMode() => _downstreamAutoMode;

        // 簡報生成器：AgentRuntime 撰寫簡報時讀這個決定交給哪個 AI。
        public PresentationEngine GetPresentationEngine() => _presentationEngine;

        // 文件產出引擎（個人化）：預設 Claude 文件技能（與 Claude App 同級），可切回內建快速版。
        private DocumentEngine _documentEngine = DocumentEngine.ClaudeSkills;
        public DocumentEngine GetDocumentEngine() => _documentEngine;

        // §7.2：重生簡報中的單一張投影片 → 重建 .pptx、換掉舊檔與 chip、更新節點的投影片清單。
        // §7.2：簡報預覽視窗（WebView2）按「重生這張」時，前端 postMessage 進來這裡。
        private async void OnPreviewWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg;
            try { msg = e.TryGetWebMessageAsString() ?? ""; }
            catch { return; }

            if (string.IsNullOrWhiteSpace(msg))
                return;

            int order;
            string action;
            string heading = "";
            var bullets = new List<string>();

            try
            {
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                action = root.TryGetProperty("action", out var a) ? (a.GetString() ?? "") : "";
                if (!root.TryGetProperty("order", out var o) || !o.TryGetInt32(out order))
                    return;

                if (root.TryGetProperty("heading", out var h))
                    heading = h.GetString() ?? "";

                if (root.TryGetProperty("bullets", out var bs) && bs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in bs.EnumerateArray())
                    {
                        string text = b.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                            bullets.Add(text.Trim());
                    }
                }
            }
            catch { return; }

            if (string.Equals(action, "regen", StringComparison.OrdinalIgnoreCase))
                await RegenerateSlideFromPreviewAsync(order, "");
            else if (string.Equals(action, "edit", StringComparison.OrdinalIgnoreCase))
                await ApplySlideEditFromPreviewAsync(order, heading, bullets);
        }

        /// <summary>
        /// 預覽裡就地編輯某一頁的文字 → 更新大綱、重建 .pptx，並讓對照 PDF 跟著更新（PDF 永遠跟著 pptx）。
        /// 覆蓋前留一版，可用「回到上一版」退回。
        /// </summary>
        private async Task ApplySlideEditFromPreviewAsync(int order, string heading, List<string> bullets)
        {
            string path = _previewPath ?? "";
            var node = _previewOwnerNode;

            if (_previewSlideRegenBusy || node == null ||
                string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _previewSlideRegenBusy = true;
            try
            {
                var native = NativeDeckStorage.Read(path);
                if (native == null) return; // Never flatten an external designer's slide structure.
                var outline = native.Outline;

                var slides = outline.Slides.Select(s => s.Order == order
                    ? new PresentationSlidePayload
                    {
                        Order = s.Order,
                        Kind = s.Kind,
                        Heading = string.IsNullOrWhiteSpace(heading) ? s.Heading : heading,
                        Bullets = bullets,
                        ImageBytes = s.ImageBytes
                    }
                    : s).ToList();

                var updated = new PresentationOutlinePayload
                {
                    Title = outline.Title,
                    Topic = outline.Topic,
                    Slides = slides,
                    SlideCount = slides.Count,
                    RequestedSlideCount = outline.RequestedSlideCount,
                    PipelineId = outline.PipelineId,
                    ModelId = outline.ModelId,
                    AgentId = outline.AgentId,
                    SourceSummary = outline.SourceSummary
                };

                WriteDeckFiles(path, updated, native.Cover);

                node.UpdatePresentationOutline(updated);
                SaveState();
                AppLog.Info("Preview", $"就地編輯第 {order} 頁並存回");

                if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                    OpenPreview(path, node);
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, "儲存修改", "存回簡報失敗：" + ex.Message, this);
            }
            finally
            {
                _previewSlideRegenBusy = false;
            }

            await Task.CompletedTask;
        }

        /// <summary>覆蓋 .pptx 並讓同一次產出的對照 PDF 跟著重建；兩者都先留上一版。</summary>
        private void WriteDeckFiles(string pptxPath, PresentationOutlinePayload outline, byte[]? coverPng)
        {
            // Build both before replacing either; layout failure must not destroy a working deck.
            byte[] pptx = PptxBuilder.Build(outline, coverPng);
            byte[] pdf = DeckPdfBuilder.Build(outline, coverPng);
            string companionPdf = GeneratedFileCompanion.FindCompanionPdf(pptxPath)
                ?? System.IO.Path.ChangeExtension(pptxPath, ".pdf");
            byte[] oldPptx = File.ReadAllBytes(pptxPath);
            byte[]? oldPdf = File.Exists(companionPdf) ? File.ReadAllBytes(companionPdf) : null;
            GeneratedFileHistory.SaveVersion(pptxPath);
            GeneratedFileHistory.SaveVersion(companionPdf);
            try
            {
                File.WriteAllBytes(pptxPath, pptx);
                File.WriteAllBytes(companionPdf, pdf);
                GeneratedFileCompanion.Register(pptxPath, companionPdf);
            }
            catch
            {
                File.WriteAllBytes(pptxPath, oldPptx);
                if (oldPdf != null) File.WriteAllBytes(companionPdf, oldPdf);
                throw;
            }
        }

        // 在預覽視窗就地重生第 order 張：重建大綱 → 重生那張 → 覆蓋同一個 .pptx → 重新渲染預覽。
        private async Task RegenerateSlideFromPreviewAsync(int order, string instruction)
        {
            string path = _previewPath ?? "";
            var node = _previewOwnerNode;

            if (_previewSlideRegenBusy || node == null || _nodeService == null ||
                string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _previewSlideRegenBusy = true;
            try
            {
                var native = NativeDeckStorage.Read(path);
                if (native == null)
                {
                    MenuConfirmDialog.ShowMessage(this, "保留簡報設計", "這份簡報未包含可安全編輯的原始大綱。請用 PowerPoint 修改，或整份重新生成；不會用內建版型覆蓋原設計。", this);
                    return;
                }
                if (!CheckDailyBudgetAllows(out string budgetMessage))
                {
                    MenuConfirmDialog.ShowMessage(this, "已達今日花費上限", budgetMessage, this);
                    return;
                }
                var sourceOrder = NativeDeckStorage.SourceOrderForPage(native, order);
                if (!sourceOrder.HasValue) return;
                order = sourceOrder.Value;
                var outline = native.Outline;

                string userInput = !string.IsNullOrWhiteSpace(node.GetPresentationUserInput())
                    ? node.GetPresentationUserInput()
                    : outline.Title;

                if (!string.IsNullOrWhiteSpace(instruction))
                    userInput = userInput.TrimEnd() + "\n\n【這一頁的修改要求】\n" + instruction.Trim();

                using var cts = new CancellationTokenSource();
                var updated = await _nodeService.RegeneratePresentationSlideAsync(node, outline, order, userInput, cts.Token);

                if (updated == null)
                {
                    // 重生失敗：重新渲染原檔（恢復按鈕可用狀態）。
                    if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                        OpenPreview(path, node);
                    return;
                }

                // 重建前先保留既有封面圖，避免重生一張內容頁後封面圖被洗掉。
                byte[]? coverPng = native.Cover;

                // 覆蓋同一個檔（路徑不變 → chip / 預覽都還指向同一份）；先留上一版，PDF 跟著重建。
                WriteDeckFiles(path, updated, coverPng);

                node.UpdatePresentationOutline(updated);
                SaveState();

                // 重新渲染預覽（若使用者還停在這份）。
                if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                    OpenPreview(path, node);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, "重生投影片", "重生投影片失敗：" + ex.Message, this);
            }
            finally
            {
                _previewSlideRegenBusy = false;
            }
        }

        /// <summary>
        /// 缺少 PDF 時以原生場景預覽；外部簡報不抽文字重建版面。
        /// </summary>
        private string BuildDeckPreviewHtml(string path)
        {
            var native = NativeDeckStorage.Read(path);
            if (native != null)
                return DeckHtmlRenderer.Build(native.Outline, new[] { native.Cover }, allowEdit: true);
            return "<!doctype html><html lang='zh-TW'><meta charset='utf-8'><body style='font-family:Microsoft JhengHei;padding:48px'><h2>請開啟原始簡報查看設計</h2><p>這份檔案沒有對照 PDF，無法在此忠實呈現版面。請使用「外部開啟」查看 PPTX。</p></body></html>";
        }

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

            return _connections.Any(c => ReferenceEquals(c.EndNode, node));
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

        // ===== 簡報生成器（個人化）=====

        public void SetPresentationEngine(PresentationEngine engine, bool save = true)
        {
            // Gamma 已開放。沒設 GAMMA_API_KEY 時執行期自動跳過（NotConfigured → fallback 內建 PptxBuilder），
            // 所以這裡不再攔——使用者的選擇如實保存。
            _presentationEngine = engine;
            SyncPresentationEngineRadios();
            if (save)
                SaveState();
        }

        // ===== 個人化「任務 → AI 模型」自訂路由 =====

        /// <summary>
        /// 設定某任務模式固定使用的模型；modelId 為 null / 空 → 清除該模式的 override。
        /// 模型未知或未啟用回 false（不變更）。成功會刷新各節點顯示並存檔。
        /// </summary>
        public bool SetTaskRoutingOverride(NodeTaskMode mode, string? modelId, bool save = true)
        {
            bool ok = _taskRoutingOverrides.Set(mode, modelId);
            if (!ok)
                return false;

            // 自動模式下，路由結果改變 → 刷新各節點選單顯示的模型。
            if (_isAutoModelSelectionEnabled)
                RefreshAllNodeModelSelectionUIs();

            if (save)
                SaveState();

            return true;
        }

        public void ClearTaskRoutingOverride(NodeTaskMode mode, bool save = true)
        {
            _taskRoutingOverrides.Clear(mode);

            if (_isAutoModelSelectionEnabled)
                RefreshAllNodeModelSelectionUIs();

            if (save)
                SaveState();
        }

        /// <summary>取得某任務模式目前的 override 模型 id；沒有設定（或已不可用）回 null。</summary>
        public string? GetTaskRoutingOverride(NodeTaskMode mode)
            => _taskRoutingOverrides.TryGet(mode, out var modelId) ? modelId : null;

        /// <summary>目前所有自訂路由的快照（給個人化面板顯示用）。</summary>
        public IReadOnlyDictionary<NodeTaskMode, string> GetTaskRoutingOverrides()
            => _taskRoutingOverrides.Snapshot();

        private bool _suppressPresentationEngineRadioEvents = false;

        private void SyncPresentationEngineRadios()
        {
            _suppressPresentationEngineRadioEvents = true;
            try
            {
                if (PresentationEngineClaude != null)
                    PresentationEngineClaude.IsChecked = _presentationEngine == PresentationEngine.Claude;
                if (PresentationEngineGpt != null)
                    PresentationEngineGpt.IsChecked = _presentationEngine == PresentationEngine.Gpt;
                if (PresentationEngineGamma != null)
                    PresentationEngineGamma.IsChecked = _presentationEngine == PresentationEngine.Gamma;
            }
            finally
            {
                _suppressPresentationEngineRadioEvents = false;
            }
        }

        private void PresentationEngine_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressPresentationEngineRadioEvents)
                return;

            if (ReferenceEquals(sender, PresentationEngineGpt))
                SetPresentationEngine(PresentationEngine.Gpt);
            else if (ReferenceEquals(sender, PresentationEngineGamma))
                SetPresentationEngine(PresentationEngine.Gamma);
            else
                SetPresentationEngine(PresentationEngine.Claude);
        }

        private bool _suppressDocumentEngineRadioEvents;

        private void SyncDocumentEngineRadios()
        {
            _suppressDocumentEngineRadioEvents = true;
            try
            {
                if (DocumentEngineSkills != null)
                    DocumentEngineSkills.IsChecked = _documentEngine == DocumentEngine.ClaudeSkills;
                if (DocumentEngineBuiltin != null)
                    DocumentEngineBuiltin.IsChecked = _documentEngine == DocumentEngine.Builtin;
            }
            finally
            {
                _suppressDocumentEngineRadioEvents = false;
            }
        }

        private void DocumentEngine_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressDocumentEngineRadioEvents)
                return;

            _documentEngine = ReferenceEquals(sender, DocumentEngineBuiltin)
                ? DocumentEngine.Builtin
                : DocumentEngine.ClaudeSkills;
            SavePreferences();
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

        private async void TryMaterializeDownstreamNodePlanFromText(string artifactText)
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

        private void UpdateConnectionsFor(NodeControl node)
        {
            foreach (var c in _connections)
                if (ReferenceEquals(c.StartNode, node) || ReferenceEquals(c.EndNode, node))
                    UpdateConnectionGeometry(c);
        }

        private void UpdateConnectionGeometry(Connection c)
        {
            var start = GetThumbCenterOnCanvas(c.StartNode, c.StartThumb);
            var end = GetThumbCenterOnCanvas(c.EndNode, c.EndThumb);

            var geometry = new System.Windows.Media.PathGeometry();
            var figure = new System.Windows.Media.PathFigure { StartPoint = start };
            var segment = new System.Windows.Media.BezierSegment
            {
                Point1 = new Point((start.X + end.X) / 2, start.Y),
                Point2 = new Point((start.X + end.X) / 2, end.Y),
                Point3 = end
            };
            figure.Segments.Add(segment);
            geometry.Figures.Add(figure);
            c.Path.Data = geometry;
        }

        public bool HasOutgoingConnections(NodeControl node)
        {
            foreach (var c in _connections)
                if (ReferenceEquals(c.StartNode, node))
                    return true;
            return false;
        }

        // ===== 右鍵：將此節點加入記憶 =====
        // 存成一筆使用者明確標記的共享記憶（user_marked，重要性 0.97、SourceNodeId 留空 → 不受跨鏈隔離、全畫布可召回）。
        // 同時會出現在「個人化 → 當前記憶」清單（NodeMemoryService 把 user_marked 併入該清單）。
        public void AddNodeToMemory(NodeControl node)
        {
            if (node == null || _nodeService == null)
                return;

            string topText = node.GetTopText() ?? "";
            string bottomText = node.GetBottomText() ?? "";

            if (string.IsNullOrWhiteSpace(bottomText))
            {
                MenuConfirmDialog.ShowMessage(this, "加入記憶",
                    "這個節點還沒有產出內容，無法加入記憶。", this);
                return;
            }

            string agentId = GetNodeSelectedAgent(node);
            string modelId = GetNodeSelectedModel(node);
            NodeTaskMode taskMode = GetNodeTaskMode(node);

            string title = _nodeService.RememberNodeManually(node, agentId, topText, bottomText, taskMode, modelId);

            // 加入後立即刷新並展開「當前記憶」面板，讓使用者馬上看到（需求：標記的節點要出現在個人化當前記憶中）。
            RefreshMemoryPanel();
            if (MemoryListPanel != null)
            {
                MemoryListPanel.Visibility = Visibility.Visible;
                RefreshMemoryPanel();
            }

            MenuConfirmDialog.ShowMessage(this, "加入記憶",
                $"已加入記憶：\n⭐ {title}", this);
        }

        // ===== 右鍵：更換連接方向 =====
        // 把「此節點這一端」的連線從上面兩個連接孔（ThumbTL ↔ ThumbTR）互換，純視覺移動接點。
        // 不改 StartNode / EndNode（上下游關係不變），也完全不碰記憶。
        public void SwapNodeConnectionSide(NodeControl node)
        {
            if (node == null)
                return;

            bool changed = false;
            foreach (var c in _connections)
            {
                if (ReferenceEquals(c.EndNode, node))
                {
                    c.EndThumb = c.EndThumb == "ThumbTL" ? "ThumbTR" : "ThumbTL";
                    UpdateConnectionGeometry(c);
                    changed = true;
                }
                if (ReferenceEquals(c.StartNode, node))
                {
                    c.StartThumb = c.StartThumb == "ThumbTL" ? "ThumbTR" : "ThumbTL";
                    UpdateConnectionGeometry(c);
                    changed = true;
                }
            }

            if (changed)
                SaveState();
        }

        // 此節點有沒有任何連線相連（給右鍵「更換連接方向」決定是否啟用）。
        public bool NodeHasAnyConnection(NodeControl node)
        {
            if (node == null) return false;
            foreach (var c in _connections)
                if (ReferenceEquals(c.StartNode, node) || ReferenceEquals(c.EndNode, node))
                    return true;
            return false;
        }

        // ===== 變更上游節點（rewire）流程 =====
        // 1) 右鍵「變更上游節點」→ BeginRewireUpstream：找出主上游連線，隱藏它、改用 ghost 跟滑鼠。
        // 2) 滑鼠移動 → UpdateRewireGhost。
        // 3) 左鍵點到合法節點 → TryCompleteRewire：依滑鼠在新節點中線左右決定接左孔/右孔，改 StartNode。
        // 4) ESC / 失敗 → CancelRewire：原連線資料未被改過，直接還原顯示。
        public void BeginRewireUpstream(NodeControl child)
        {
            if (child == null || _rewireMode)
                return;
            if (IsWorkflowChainRunning)
                return;

            var conn = _connections.FirstOrDefault(c => ReferenceEquals(c.EndNode, child));
            if (conn == null)
                return;

            _rewireMode = true;
            _rewireConn = conn;
            _rewireChild = child;

            if (conn.Path != null)
                conn.Path.Visibility = Visibility.Collapsed;

            _rewireGhost = new PathShape
            {
                Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#5B6CFF")!,
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(_rewireGhost, GetNextZIndex());
            MainCanvas.Children.Add(_rewireGhost);

            Mouse.OverrideCursor = Cursors.Cross;

            // 初始 ghost 起點畫在原上游孔位，避免閃一下。
            UpdateRewireGhost(GetThumbCenterOnCanvas(conn.StartNode, conn.StartThumb));
        }

        public void UpdateRewireGhost(Point mouseCanvas)
        {
            if (!_rewireMode || _rewireGhost == null || _rewireChild == null || _rewireConn == null)
                return;

            var end = GetThumbCenterOnCanvas(_rewireChild, _rewireConn.EndThumb);
            var start = mouseCanvas;

            var geometry = new System.Windows.Media.PathGeometry();
            var figure = new System.Windows.Media.PathFigure { StartPoint = start };
            figure.Segments.Add(new System.Windows.Media.BezierSegment
            {
                Point1 = new Point((start.X + end.X) / 2, start.Y),
                Point2 = new Point((start.X + end.X) / 2, end.Y),
                Point3 = end
            });
            geometry.Figures.Add(figure);
            _rewireGhost.Data = geometry;
        }

        // 嘗試把主上游接到 target。不合法（自己 / 自己的下游後代）→ 擋下、提示音、保持 rewire 狀態等待重選。
        public void TryCompleteRewire(NodeControl target)
        {
            if (!_rewireMode || _rewireConn == null || _rewireChild == null || target == null)
                return;

            if (ReferenceEquals(target, _rewireChild))
            {
                System.Media.SystemSounds.Asterisk.Play();   // 不能接自己
                return;
            }

            // 不能接到自己的下游後代（會形成循環）。
            if (GetDescendantIds(_rewireChild).Contains(target.Id.ToString()))
            {
                System.Media.SystemSounds.Asterisk.Play();
                return;
            }

            // 需求：依滑鼠在新節點「中線」的左右，決定接左孔(ThumbTL)或右孔(ThumbTR)。
            double centerX = SafeFinite(Canvas.GetLeft(target), 0) + SafePositiveFinite(target.Width, 300) / 2.0;
            Point mouse = Mouse.GetPosition(MainCanvas);
            string startThumb = mouse.X < centerX ? "ThumbTL" : "ThumbTR";

            // 成功——此刻才動連線資料：改上游節點與其接孔，下游端(EndNode/EndThumb)維持不變。
            var conn = _rewireConn;
            conn.StartNode = target;
            conn.StartThumb = startThumb;

            EndRewire();
            UpdateConnectionGeometry(conn);
            HookNode(target);
            SaveState();
        }

        public void CancelRewire() => EndRewire();

        // 收尾：移除 ghost、把原連線顯示回來、清狀態。成功與取消共用——
        // 取消時連線資料根本沒被改過所以等同還原；成功時資料已指向新上游，顯示回來即生效。
        private void EndRewire()
        {
            if (_rewireGhost != null)
            {
                MainCanvas.Children.Remove(_rewireGhost);
                _rewireGhost = null;
            }

            if (_rewireConn?.Path != null)
                _rewireConn.Path.Visibility = Visibility.Visible;

            Mouse.OverrideCursor = null;

            _rewireMode = false;
            _rewireConn = null;
            _rewireChild = null;
        }

        // 此節點的所有下游後代 ID（沿出邊 BFS，不含自己）。用於 rewire 防循環。
        private HashSet<string> GetDescendantIds(NodeControl node)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node == null)
                return ids;

            var visited = new HashSet<Guid> { node.Id };
            var queue = new Queue<NodeControl>();
            queue.Enqueue(node);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var c in _connections)
                {
                    if (ReferenceEquals(c.StartNode, cur) && c.EndNode != null && visited.Add(c.EndNode.Id))
                    {
                        ids.Add(c.EndNode.Id.ToString());
                        queue.Enqueue(c.EndNode);
                    }
                }
            }

            return ids;
        }

        public bool IsInitialNode(NodeControl node)
            => ReferenceEquals(_initialNode, node);

        private string GetAttachmentFolderForFile(string filePath)
            => Store.GetAttachmentFolder(filePath);

        private string? GetCurrentAttachmentFolder()
        {
            if (string.IsNullOrWhiteSpace(_currentFilePath)) return null;
            var folder = GetAttachmentFolderForFile(_currentFilePath);
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static bool IsImageExt(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
        }

        private static string MimeFromExt(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".md" => "text/markdown",
                ".csv" => "text/csv",
                ".json" => "application/json",
                _ => "application/octet-stream"
            };
        }

        public void AddAttachmentsForNode(NodeControl node, IEnumerable<string> filePaths)
        {
            var folder = GetCurrentAttachmentFolder();
            if (folder == null)
            {
                MenuConfirmDialog.ShowMessage(this, "提示", "目前尚未建立檔案，無法上傳附件。", this);
                return;
            }

            if (!_attachmentsByNode.TryGetValue(node.Id, out var list))
            {
                list = new List<AttachmentInfo>();
                _attachmentsByNode[node.Id] = list;
            }

            foreach (var src in filePaths ?? Array.Empty<string>())
            {
                try
                {
                    if (!File.Exists(src)) continue;

                    var ext = System.IO.Path.GetExtension(src);
                    var mime = MimeFromExt(ext);
                    var kind = IsImageExt(ext) ? "image"
                        : ext.ToLowerInvariant() == ".pdf" ? "pdf"
                        : ext.ToLowerInvariant() is ".html" or ".htm" ? "html"
                        : "file";

                    var safeName = System.IO.Path.GetFileName(src);
                    var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
                    var dest = System.IO.Path.Combine(folder, uniqueName);

                    File.Copy(src, dest, overwrite: false);

                    var rel = System.IO.Path.Combine(DisplayNameFromPath(_currentFilePath!), uniqueName);

                    list.Add(new AttachmentInfo
                    {
                        FileName = safeName,
                        RelativePath = rel,
                        MimeType = mime,
                        Kind = kind
                    });
                }
                catch (Exception ex) { AppLog.Warn("Attachments", "附件複製進專案失敗（該附件被略過）", ex); }
            }

            SaveState();
        }

        public IReadOnlyList<AttachmentInfo> GetAttachmentsForNode(INodeContext node)
        {
            if (node == null)
                return Array.Empty<AttachmentInfo>();

            if (_attachmentsByNode.TryGetValue(node.Id, out var list))
                return list.ToList();

            return Array.Empty<AttachmentInfo>();
        }

        // 執行用「有效附件」＝此節點自己的附件 + 沿上游鏈所有祖先的附件（按 RelativePath 去重）。
        // 為什麼：附件按 node.Id 隔離儲存，使用者通常只在源頭節點掛檔案；下游節點（{{input}} 只帶上游
        // 輸出文字）原本拿不到上游附件。工作流鏈執行時，下游應「繼承」上游掛的檔案，才看得到原始輸入。
        // 順序：最遠祖先在前、自己在最後，讓 LLM 把上游附件當輸入材料。visited 防環、多層鏈安全。
        public IReadOnlyList<AttachmentInfo> GetEffectiveAttachmentsForNode(INodeContext nodeContext)
        {
            // Slice B1 cast 橋：介面契約收 INodeContext，但上游鏈走訪需要具體控制項（runtime 流動的本來就是 NodeControl）。
            var node = nodeContext as NodeControl;
            if (node == null)
                return Array.Empty<AttachmentInfo>();

            var chain = new List<NodeControl>();
            var visited = new HashSet<Guid>();
            var up = GetFirstUpstreamNode(node);
            while (up != null && visited.Add(up.Id))
            {
                chain.Add(up);
                up = GetFirstUpstreamNode(up);
            }
            chain.Reverse();   // 最遠祖先排前面
            chain.Add(node);   // 自己最後

            var ordered = new List<AttachmentInfo>();
            var seenRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in chain)
            {
                foreach (var a in GetAttachmentsForNode(n))
                {
                    // RelativePath 非空且已收錄過 → 跳過重複；空路徑不去重，照收。
                    if (!string.IsNullOrEmpty(a.RelativePath) && !seenRel.Add(a.RelativePath))
                        continue;
                    ordered.Add(a);
                }
            }

            return ordered;
        }

        // 「同一條有向鏈」的節點 ID 集合＝此節點 + 所有上游祖先 + 所有下游後代（沿連線方向）。
        // 用途：記憶召回的跨鏈隔離——畫布上不同分支（兄弟/旁系，例如另一個掛附件的節點）的執行記憶
        // 不應互相注入污染；只有同一條鏈上的節點才彼此「詳細可見」，旁支只靠 NodeContextService 的
        // 「支線摘要」大概知道。沿有向邊 BFS，所以共同母節點的不同子分支彼此不在對方的鏈集合內。
        public HashSet<string> GetSameChainNodeIds(NodeControl node)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node == null)
                return ids;

            var conns = GetConnectionsForContext().ToList();
            ids.Add(node.Id.ToString());

            // 祖先：沿入邊（EndNode==cursor → 取 StartNode）向上 BFS。
            var visited = new HashSet<Guid> { node.Id };
            var queue = new Queue<NodeControl>();
            queue.Enqueue(node);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var c in conns)
                {
                    if (ReferenceEquals(c.EndNode, cur) && c.StartNode != null && visited.Add(c.StartNode.Id))
                    {
                        ids.Add(c.StartNode.Id.ToString());
                        queue.Enqueue(c.StartNode);
                    }
                }
            }

            // 後代：沿出邊（StartNode==cursor → 取 EndNode）向下 BFS。
            visited = new HashSet<Guid> { node.Id };
            queue.Enqueue(node);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var c in conns)
                {
                    if (ReferenceEquals(c.StartNode, cur) && c.EndNode != null && visited.Add(c.EndNode.Id))
                    {
                        ids.Add(c.EndNode.Id.ToString());
                        queue.Enqueue(c.EndNode);
                    }
                }
            }

            return ids;
        }

        public void OpenAttachment(string relativePath)
        {
            try
            {
                var abs = System.IO.Path.Combine(AttachmentsRootDir, relativePath);
                if (!File.Exists(abs))
                {
                    MenuConfirmDialog.ShowMessage(this, "開啟失敗", "找不到附件檔案（可能已被移動或刪除）。", this);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = abs,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, "錯誤", $"開啟附件失敗：{ex.Message}", this);
            }
        }

        /// <summary>用系統預設程式開啟生成的檔案（報告 / 簡報 deck）。限制只能開啟 _generated 資料夾內的檔案。</summary>
        public void OpenGeneratedFile(string fullPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                {
                    MenuConfirmDialog.ShowMessage(this, "開啟失敗", "找不到生成的檔案（可能已被移動或刪除）。", this);
                    return;
                }

                // 安全檢查：只允許開啟 _generated 資料夾內的檔案。
                string rootFull = System.IO.Path.GetFullPath(GeneratedFilesDir);
                string targetFull = System.IO.Path.GetFullPath(fullPath);
                if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    MenuConfirmDialog.ShowMessage(this, "開啟失敗", "檔案不在允許開啟的範圍內。", this);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = targetFull,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, "錯誤", $"開啟檔案失敗：{ex.Message}", this);
            }
        }

        // ===== Artifact 即時預覽（大型 overlay；點檔案 chip / 圖片開啟）=====

        private string? _previewPath;
        private NodeControl? _previewOwnerNode;
        private bool _previewWebMsgHooked;
        private bool _previewSlideRegenBusy;
        private bool _previewMediaPlaying;

        /// <summary>在 app 內預覽生成的檔案；無法內嵌渲染的格式給 fallback + 用系統程式開啟。</summary>
        public void OpenPreview(string? fullPath) => OpenPreview(fullPath, null);

        public async void OpenPreview(string? fullPath, NodeControl? owner)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                MenuConfirmDialog.ShowMessage(this, "預覽失敗", "找不到生成的檔案（可能已被移動或刪除）。", this);
                return;
            }

            _previewOwnerNode = owner;

            // 安全檢查：只允許預覽 _generated 資料夾內的檔案（與 OpenGeneratedFile 一致）。
            string rootFull = System.IO.Path.GetFullPath(GeneratedFilesDir);
            string targetFull = System.IO.Path.GetFullPath(fullPath);
            if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                MenuConfirmDialog.ShowMessage(this, "預覽失敗", "檔案不在允許預覽的範圍內。", this);
                return;
            }

            _previewPath = targetFull;
            PreviewFileName.Text = System.IO.Path.GetFileName(targetFull);

            HideAllPreviewRenderers();
            PreviewOverlay.Visibility = Visibility.Visible;

            // 產出檔＋知道是哪個節點做的 → 可以就地重新生成（並補修改指示）。
            bool canRegen = _previewOwnerNode != null &&
                            System.IO.Path.GetExtension(targetFull).ToLowerInvariant()
                                is ".pptx" or ".docx" or ".xlsx" or ".pdf" or ".png" or ".mp4";
            if (PreviewRegenBar != null)
            {
                PreviewRegenBar.Visibility = canRegen ? Visibility.Visible : Visibility.Collapsed;
                if (canRegen && PreviewRegenButton != null)
                    PreviewRegenButton.IsEnabled = !_previewOwnerNode!.IsGenerating;

                // 只有簡報能指定頁碼重生（其他檔案類型整份重做）。
                if (PreviewPageBox != null)
                    PreviewPageBox.Visibility = canRegen && targetFull.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                RefreshPreviewUndoState();
            }

            string ext = System.IO.Path.GetExtension(targetFull).TrimStart('.').ToLowerInvariant();

            try
            {
                switch (ext)
                {
                    case "png": case "jpg": case "jpeg": case "gif": case "bmp": case "webp":
                        ShowImagePreview(targetFull);
                        break;

                    case "mp4": case "mov": case "webm": case "m4v":
                        ShowMediaPreview(targetFull);
                        break;

                    case "html": case "htm": case "pdf":
                        await ShowWebPreviewAsync(targetFull);
                        break;

                    case "docx":
                        await ShowHtmlContentAsync(
                            ArtifactHtmlRenderer.BuildDocxHtml(ArtifactTextExtractor.ExtractDocx(targetFull)),
                            targetFull);
                        break;

                    case "pptx":
                        string? deckPdf = GeneratedFileCompanion.FindCompanionPdf(targetFull);
                        if (NativeDeckStorage.Read(targetFull) != null)
                            await ShowHtmlContentAsync(BuildDeckPreviewHtml(targetFull), targetFull);
                        else if (deckPdf != null)
                            await ShowWebPreviewAsync(deckPdf, targetFull);
                        else
                            await ShowHtmlContentAsync(BuildDeckPreviewHtml(targetFull), targetFull);
                        if (PreviewPageBox != null)
                            PreviewPageBox.Visibility = canRegen && NativeDeckStorage.Read(targetFull) != null
                                ? Visibility.Visible : Visibility.Collapsed;
                        break;

                    case "xlsx":
                        await ShowHtmlContentAsync(
                            ArtifactHtmlRenderer.BuildXlsxHtml(ArtifactTextExtractor.ExtractXlsxRows(targetFull)),
                            targetFull);
                        break;

                    case "txt": case "md": case "csv": case "json":
                        ShowTextPreview(File.ReadAllText(targetFull));
                        break;

                    default:
                        ShowFallback($"「.{ext}」格式無法在這裡預覽，可用系統程式開啟。");
                        break;
                }
            }
            catch (Exception ex)
            {
                ShowFallback($"預覽失敗：{ex.Message}");
            }
        }

        private void ShowImagePreview(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();

                PreviewImage.Source = bmp;
                PreviewImageScroll.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowFallback($"圖片載入失敗：{ex.Message}");
            }
        }

        private void ShowMediaPreview(string path)
        {
            PreviewMedia.Visibility = Visibility.Visible;
            PreviewMedia.Source = new Uri(path);
            PreviewMedia.Play();
            _previewMediaPlaying = true;
        }

        private async Task ShowWebPreviewAsync(string path, string? sourcePath = null)
        {
            PreviewLoading.Visibility = Visibility.Visible;
            PreviewWeb.Visibility = Visibility.Visible;

            try
            {
                await PreviewWeb.EnsureCoreWebView2Async();

                // 等待初始化期間若已關閉或切換到別的檔案，放棄這次導覽。
                if (PreviewOverlay.Visibility != Visibility.Visible ||
                    !string.Equals(_previewPath, sourcePath ?? path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                PreviewWeb.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
            }
            catch (Exception ex)
            {
                PreviewWeb.Visibility = Visibility.Collapsed;
                ShowFallback($"無法載入預覽器（WebView2）：{ex.Message}");
            }
            finally
            {
                PreviewLoading.Visibility = Visibility.Collapsed;
            }
        }

        // 把渲染好的 HTML 字串丟進 WebView2 顯示（DOCX → markdown 排版、PPTX → 投影片卡片）。
        private async Task ShowHtmlContentAsync(string html, string sourcePath)
        {
            PreviewLoading.Visibility = Visibility.Visible;
            PreviewWeb.Visibility = Visibility.Visible;

            try
            {
                await PreviewWeb.EnsureCoreWebView2Async();

                if (!_previewWebMsgHooked)
                {
                    PreviewWeb.CoreWebView2.WebMessageReceived += OnPreviewWebMessage;
                    _previewWebMsgHooked = true;
                }

                if (PreviewOverlay.Visibility != Visibility.Visible ||
                    !string.Equals(_previewPath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // NavigateToString 有約 2MB 字串上限：含封面圖（base64）的簡報會超過，導致
                // 「Value does not fall within the expected range」。改寫成暫存 HTML 檔再用 file:// 導覽，無大小限制。
                string tempHtml = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cat5201_preview.html");
                File.WriteAllText(tempHtml, html, new System.Text.UTF8Encoding(false));
                string uri = new Uri(tempHtml).AbsoluteUri + "?t=" + DateTime.Now.Ticks;
                PreviewWeb.CoreWebView2.Navigate(uri);
            }
            catch (Exception ex)
            {
                PreviewWeb.Visibility = Visibility.Collapsed;
                ShowFallback($"無法載入預覽器（WebView2）：{ex.Message}");
            }
            finally
            {
                PreviewLoading.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowTextPreview(string? text)
        {
            PreviewText.Text = string.IsNullOrWhiteSpace(text) ? "（沒有可顯示的文字內容）" : text;
            PreviewTextScroll.Visibility = Visibility.Visible;
        }

        private void ShowFallback(string message)
        {
            PreviewFallbackText.Text = message;
            PreviewFallback.Visibility = Visibility.Visible;
        }

        private void HideAllPreviewRenderers()
        {
            PreviewImageScroll.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;

            PreviewTextScroll.Visibility = Visibility.Collapsed;
            PreviewFallback.Visibility = Visibility.Collapsed;
            PreviewLoading.Visibility = Visibility.Collapsed;

            PreviewMedia.Visibility = Visibility.Collapsed;
            try { PreviewMedia.Stop(); PreviewMedia.Close(); } catch { }
            PreviewMedia.Source = null;
            _previewMediaPlaying = false;

            PreviewWeb.Visibility = Visibility.Collapsed;
            try { PreviewWeb.CoreWebView2?.Navigate("about:blank"); } catch { }
        }

        private void ClosePreview()
        {
            HideAllPreviewRenderers();
            PreviewOverlay.Visibility = Visibility.Collapsed;
            _previewPath = null;
            _previewOwnerNode = null;
        }

        private void ClosePreview_Click(object sender, RoutedEventArgs e) => ClosePreview();

        private void PreviewRegenInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (PreviewRegenHint != null && PreviewRegenInput != null)
                PreviewRegenHint.Visibility = string.IsNullOrEmpty(PreviewRegenInput.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        /// <summary>「回到上一版」按鈕的可用狀態：這個檔案（或它的對照 PDF）有備份才亮。</summary>
        private void RefreshPreviewUndoState()
        {
            if (PreviewUndoButton == null)
                return;

            PreviewUndoButton.IsEnabled =
                !string.IsNullOrWhiteSpace(_previewPath) && GeneratedFileHistory.HasPrevious(_previewPath);
        }

        // 新版不如舊版時退回：原檔與對照 PDF 一起退，預覽立刻重新整理。
        private async void PreviewUndo_Click(object sender, RoutedEventArgs e)
        {
            string path = _previewPath ?? "";
            if (string.IsNullOrWhiteSpace(path) || !GeneratedFileHistory.HasPrevious(path))
                return;

            bool ok = GeneratedFileHistory.RestorePrevious(path);
            if (ok)
            {
                string? companion = GeneratedFileCompanion.FindCompanionPdf(path);
                if (companion != null)
                    GeneratedFileHistory.RestorePrevious(companion);

                // 節點保存的大綱要跟著回到舊版，否則下次單頁重生會以新版為底本。
                if (_previewOwnerNode != null && path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
                {
                    var restored = NativeDeckStorage.Read(path)?.Outline;
                    if (restored != null)
                        _previewOwnerNode.UpdatePresentationOutline(restored);
                    SaveState();
                }

                AppLog.Info("Preview", "已退回上一版：" + System.IO.Path.GetFileName(path));
                OpenPreview(path, _previewOwnerNode);
            }
            else
            {
                MenuConfirmDialog.ShowMessage(this, "回到上一版", "沒有可以退回的版本。", this);
            }

            RefreshPreviewUndoState();
        }

        // 預覽視窗「重新生成」：用原需求 + 這裡輸入的修改指示，重跑產生這個檔案的節點。
        // 有填頁碼且是簡報 → 只重做那一頁（覆蓋同一個檔，先留上一版）。
        private async void PreviewRegenerate_Click(object sender, RoutedEventArgs e)
        {
            var node = _previewOwnerNode;
            if (node == null)
                return;

            if (node.IsGenerating)
            {
                MenuConfirmDialog.ShowMessage(this, "重新生成", "這個節點正在執行中，請等它跑完再重新生成。", this);
                return;
            }

            if (!CheckDailyBudgetAllows(out string budgetMessage))
            {
                MenuConfirmDialog.ShowMessage(this, "已達今日花費上限", budgetMessage, this);
                return;
            }

            string instruction = PreviewRegenInput?.Text?.Trim() ?? "";
            string pageText = PreviewRegenPage?.Text?.Trim() ?? "";

            // 單頁重生：留在預覽裡就地更新，不必重跑整個節點。
            if (!string.IsNullOrWhiteSpace(pageText) &&
                int.TryParse(pageText, out int pageNumber) &&
                pageNumber > 0 &&
                (_previewPath ?? "").EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                await RegenerateSlideFromPreviewAsync(pageNumber, instruction);
                if (PreviewRegenPage != null) PreviewRegenPage.Text = "";
                if (PreviewRegenInput != null) PreviewRegenInput.Text = "";
                RefreshPreviewUndoState();
                return;
            }

            ClosePreview();
            if (PreviewRegenInput != null)
                PreviewRegenInput.Text = "";

            AppLog.Info("Preview", string.IsNullOrWhiteSpace(instruction)
                ? "預覽重新生成（無額外指示）"
                : "預覽重新生成，附修改指示");

            await node.RegenerateWithInstructionAsync(instruction);
        }

        private void PreviewOverlay_BackgroundClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => ClosePreview();

        // 點卡片本身不關閉（阻止冒泡到背景）。
        private void PreviewCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => e.Handled = true;

        private void PreviewOpenExternal_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_previewPath))
                OpenGeneratedFile(_previewPath);
        }

        private void PreviewMedia_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_previewMediaPlaying)
                PreviewMedia.Pause();
            else
                PreviewMedia.Play();
            _previewMediaPlaying = !_previewMediaPlaying;
        }

        // 唯讀 TextBox 會吃掉滾輪事件，這裡在 tunnel 階段先攔下來捲外層 ScrollViewer。
        private void PreviewTextScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void PreviewMedia_MediaOpened(object sender, RoutedEventArgs e) { }

        private void PreviewMedia_MediaEnded(object sender, RoutedEventArgs e)
        {
            PreviewMedia.Position = TimeSpan.Zero;
            PreviewMedia.Pause();
            _previewMediaPlaying = false;
        }

        public void RemoveAttachment(NodeControl node, string relativePath)
        {
            if (!_attachmentsByNode.TryGetValue(node.Id, out var list))
                return;

            var hit = list.FirstOrDefault(a => string.Equals(a.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return;

            try
            {
                var abs = System.IO.Path.Combine(AttachmentsRootDir, hit.RelativePath);
                if (File.Exists(abs))
                {
                    File.Delete(abs);
                }
            }
            catch (Exception ex) { AppLog.Warn("Attachments", "附件實體檔刪除失敗（清單已移除，檔案殘留）", ex); }

            list.Remove(hit);
            if (list.Count == 0)
                _attachmentsByNode.Remove(node.Id);

            SaveState();
        }

        // 附件相對路徑改寫的磁碟版已在 ProjectStore;這裡只剩「已載入節點」的記憶體同步。
        private void UpdateAttachmentRelativePathsInMemory(string oldBaseName, string newBaseName)
        {
            foreach (var kv in _attachmentsByNode)
            {
                foreach (var a in kv.Value)
                {
                    a.RelativePath = ProjectStore.ReplaceAttachmentRelativeBase(a.RelativePath, oldBaseName, newBaseName);
                }
            }
        }

        private void SaveState()
        {
            if (!_hasStarted) return;
            if (_suppressSave) return;

            var nodes = new List<NodeState>();
            foreach (var child in MainCanvas.Children.OfType<NodeControl>())
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
    child.GetOutputImagePath()
));
            }

            var conns = new List<ConnState>();
            foreach (var c in _connections)
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
            foreach (var kv in _attachmentsByNode)
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

            foreach (var child in MainCanvas.Children.OfType<NodeControl>())
            {
                var nodeLogs = GetExecutionLogs(child);
                foreach (var log in nodeLogs)
                {
                    logs.Add(ToExecutionLogState(log));
                }
            }

            var state = new AppState(
    DateTime.Now,
    _initialNode?.Id.ToString(),
    nodes,
    conns,
    atts,
    logs,
    FileNameLocked: _fileNameLockedByUser,
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

            if (string.IsNullOrEmpty(_currentFilePath))
                _currentFilePath = Store.NewProjectPath();

            Store.Save(state, _currentFilePath!);

            // 個人化偏好與專案檔分離，但任何一次存檔都順手把全域偏好也寫回，確保隨時最新。
            SavePreferences();

            CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";
        }

        private void LoadState(string path)
        {
            if (!File.Exists(path)) return;

            // Store.Load 含 .bak fallback:主檔毀損時自動退上一版備份(原本直接 ReadAllText 沒這層救)。
            var state = Store.Load(path);
            if (state == null) return;

            StartUI.Visibility = Visibility.Collapsed;
            MainUI.Visibility = Visibility.Visible;
            _hasStarted = true;
            _currentFilePath = path;
            CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";

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

            _attachmentsByNode.Clear();
            _nodeModelsById.Clear();
            _nodeTaskModesById.Clear();
            _executionLogService.ClearAll();
            _nodeAgentsById.Clear();
            _unsupportedDownstreamNodeIds.Clear();
            _generatedDownstreamNodeIds.Clear();

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
            _nodeModelsById.Clear();
            _nodeTaskModesById.Clear();
            _executionLogService.ClearAll();
            _autoFlowTemplatesByNode.Clear();
            _autoFlowPoliciesByNode.Clear();
            _unsupportedDownstreamNodeIds.Clear();
            _nodeAgentsById.Clear();
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
                SaveState();

                _lastAppliedAutoKeyword = keyword;

                CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";
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
$@"請將下面內容，取一個像 ChatGPT 自動命名筆記那樣的「短標題/關鍵字」：
- 使用繁體中文
- 盡量 6~16 字
- 只輸出標題本身，不要加引號、不要加編號、不要加任何解釋
- 不要包含檔案副檔名

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

        private static string NormalizeKeywordForFileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            var s = raw.Trim();

            s = s.Trim('\"', '“', '”', '\'', '『', '』', '「', '」');
            s = Regex.Replace(s, @"\s+", " ");

            foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');

            s = s.Trim().TrimEnd('.');
            s = s.Trim('_');

            const int maxLen = 28;
            if (s.Length > maxLen)
                s = s.Substring(0, maxLen).Trim();

            return s;
        }

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

        private void CenterOnInitialButton_Click(object sender, RoutedEventArgs e)
        {
            if (_initialNode == null) return;

            double nodeX = Canvas.GetLeft(_initialNode) + _initialNode.Width / 2;
            double nodeY = Canvas.GetTop(_initialNode) + _initialNode.Height / 2;

            double viewportCenterX = Viewport.ActualWidth / 2;
            double viewportCenterY = Viewport.ActualHeight / 2;

            double targetX = viewportCenterX - nodeX * 1.0;
            double targetY = viewportCenterY - nodeY * 1.0;

            var animScale = new DoubleAnimation
            {
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            var animX = new DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            var animY = new DoubleAnimation
            {
                To = targetY,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            animY.Completed += (s, _) =>
            {
                _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _translateTransform.BeginAnimation(TranslateTransform.XProperty, null);
                _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);

                _scaleTransform.ScaleX = 1.0;
                _scaleTransform.ScaleY = 1.0;
                _translateTransform.X = targetX;
                _translateTransform.Y = targetY;

                _scale = 1.0;
            };

            _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            _translateTransform.BeginAnimation(TranslateTransform.XProperty, animX);
            _translateTransform.BeginAnimation(TranslateTransform.YProperty, animY);
        }

        private void CollapseSidebar(object sender, RoutedEventArgs e)
        {
            var animSidebar = new DoubleAnimation { From = Sidebar.ActualWidth, To = 0, Duration = TimeSpan.FromMilliseconds(300) };
            var animViewport = new DoubleAnimation { From = 140, To = 0, Duration = TimeSpan.FromMilliseconds(300) };

            animSidebar.Completed += (s, _) =>
            {
                Sidebar.Visibility = Visibility.Collapsed;
                HotZoneContainer.Visibility = Visibility.Visible;
                _isSidebarCollapsed = true;
            };

            Sidebar.BeginAnimation(WidthProperty, animSidebar);
            ViewportTranslate.BeginAnimation(TranslateTransform.XProperty, animViewport);
        }

        private void ExpandSidebar(object sender, RoutedEventArgs e)
        {
            Sidebar.Visibility = Visibility.Visible;
            var animSidebar = new DoubleAnimation { From = 0, To = 280, Duration = TimeSpan.FromMilliseconds(300) };
            var animViewport = new DoubleAnimation { From = 0, To = 140, Duration = TimeSpan.FromMilliseconds(300) };

            animSidebar.Completed += (s, _) =>
            {
                HotZoneContainer.Visibility = Visibility.Collapsed;
                _isSidebarCollapsed = false;
            };

            Sidebar.BeginAnimation(WidthProperty, animSidebar);
            ViewportTranslate.BeginAnimation(TranslateTransform.XProperty, animViewport);
        }

        private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 變更上游進行中：左鍵點擊節點 = 嘗試接上新上游；點空白不處理（用 ESC 取消）。
            if (_rewireMode && e.ChangedButton == MouseButton.Left)
            {
                var target = FindAncestor<NodeControl>(e.OriginalSource as DependencyObject);
                if (target != null)
                {
                    TryCompleteRewire(target);
                    e.Handled = true;
                }
                return;
            }

            if (e.ChangedButton == MouseButton.Middle)
            {
                _isPanning = true;
                _lastMousePos = e.GetPosition(this);
                Viewport.CaptureMouse();
            }
            else if (!_isSidebarCollapsed)
            {
                CollapseSidebar(sender, e);
            }
        }

        private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _isPanning)
            {
                _isPanning = false;
                Viewport.ReleaseMouseCapture();
            }
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (_rewireMode)
            {
                UpdateRewireGhost(e.GetPosition(MainCanvas));
                return;
            }

            if (_isPanning)
            {
                var pos = e.GetPosition(this);
                var delta = pos - _lastMousePos;
                _translateTransform.X += delta.X;
                _translateTransform.Y += delta.Y;
                _lastMousePos = pos;
            }
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T hit)
                    return hit;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var original = e.OriginalSource as DependencyObject;

            if (FindAncestor<NodeControl>(original) != null)
                return;

            double zoom = e.Delta > 0 ? 1.1 : 1 / 1.1;
            double newScale = _scale * zoom;
            if (newScale < 0.1 || newScale > 6.0) return;

            var pos = e.GetPosition(MainCanvas);
            _translateTransform.X -= (pos.X * (newScale - _scale));
            _translateTransform.Y -= (pos.Y * (newScale - _scale));

            _scaleTransform.ScaleX = newScale;
            _scaleTransform.ScaleY = newScale;
            _scale = newScale;

            e.Handled = true;
        }

        private void HotZoneContainer_MouseEnter(object sender, MouseEventArgs e) { }

        private void AutoModelSwitch_Checked(object sender, RoutedEventArgs e)
        {
            SetAutoModelSelectionEnabled(true);
        }

        private void AutoModelSwitch_Unchecked(object sender, RoutedEventArgs e)
        {
            SetAutoModelSelectionEnabled(false);
        }

        private void AdvancedAutoResolverSwitch_Checked(object sender, RoutedEventArgs e)
        {
            SetAdvancedAutoResolverEnabled(true);
        }

        private void AdvancedAutoResolverSwitch_Unchecked(object sender, RoutedEventArgs e)
        {
            SetAdvancedAutoResolverEnabled(false);
        }

        public void DeleteNodeAndDescendants(NodeControl root)
        {
            var nodesToDelete = new HashSet<NodeControl>();
            var connsToDelete = new HashSet<Connection>();

            var stack = new Stack<NodeControl>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!nodesToDelete.Add(n)) continue;

                foreach (var c in _connections)
                {
                    if (ReferenceEquals(c.StartNode, n))
                    {
                        connsToDelete.Add(c);
                        stack.Push(c.EndNode);
                    }
                }

                foreach (var c in _connections)
                {
                    if (ReferenceEquals(c.EndNode, n))
                    {
                        connsToDelete.Add(c);
                    }
                }
            }

            foreach (var c in connsToDelete)
            {
                if (c.Path != null)
                    MainCanvas.Children.Remove(c.Path);
            }
            _connections.RemoveAll(c => connsToDelete.Contains(c));

            foreach (var n in nodesToDelete)
            {
                n.ClearOutputFiles();
                ClearEditingIfDeleted(n);
                MainCanvas.Children.Remove(n);
                _attachmentsByNode.Remove(n.Id);
                _nodeModelsById.Remove(n.Id);
                _nodeTaskModesById.Remove(n.Id);
                _autoFlowTemplatesByNode.Remove(n.Id);
                _autoFlowPoliciesByNode.Remove(n.Id);
                _unsupportedDownstreamNodeIds.Remove(n.Id);
                ClearExecutionLogs(n);
            }

            if (_initialNode != null && nodesToDelete.Contains(_initialNode))
                _initialNode = null;

            SaveState();
        }

        internal Canvas GetMainCanvasRef() => MainCanvas;

        public string GetAttachmentsRootDir() => AttachmentsRootDir;

        internal IEnumerable<NodeControl> GetAllNodesInCanvas()
            => MainCanvas.Children.OfType<NodeControl>();

        internal IEnumerable<(NodeControl StartNode, string StartThumb, NodeControl EndNode, string EndThumb)> GetAllConnections()
        {
            foreach (var c in _connections)
                yield return (c.StartNode, c.StartThumb, c.EndNode, c.EndThumb);
        }

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
                capabilityDetail = decision.CapabilityAdjusted ? "已調整" : "OK";
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
                delegationDetail = "尚未觸發";
                delegationLines.Add("目前尚未進入 agent delegation");
            }

            // ===== Fallback =====
            var fallbackLines = new List<string>();

            if (isFallbackAttempt)
            {
                fallbackLines.Add($"目前為第 {attemptIndex} 次嘗試");
                fallbackLines.Add($"候選模型：{plannedLabel}");
                fallbackLines.Add($"原因：{(string.IsNullOrWhiteSpace(reason) ? "-" : reason)}");
            }
            else
            {
                fallbackLines.Add("目前使用 primary candidate 執行");
                fallbackLines.Add($"模型：{plannedLabel}");
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
        "Execution 狀態：執行中",
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
            Detail = isFallbackAttempt ? $"已進入第 {attemptIndex} 次嘗試" : "尚未觸發",
            State = isFallbackAttempt ? NodeDecisionStepState.Warning : NodeDecisionStepState.Info,
            Highlight = isFallbackAttempt,
            IsActive = isFallbackAttempt,
            DetailLines = fallbackLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Execution",
            Detail = "執行中",
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
                $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / 執行中",
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
                capabilityDetail = decision.CapabilityAdjusted ? "已調整" : "OK";
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
                delegationDetail = "尚未觸發";
                delegationLines.Add("目前尚未進入 agent delegation");
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
            Detail = decision.RuntimeFallbackUsed ? "已觸發" : "無",
            State = decision.RuntimeFallbackUsed ? NodeDecisionStepState.Warning : NodeDecisionStepState.Success,
            DetailLines = BuildFallbackLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Execution",
            Detail = "失敗",
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
                $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / 失敗",
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
                ? $"產出物 {artifactCount} 項（可見 {safeArtifacts.Count(x => x != null && x.IsUserVisible)}）"
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

                var ok = new Button { Content = "確定", Width = 72, Margin = new Thickness(0, 0, 8, 0) };
                ok.Click += (_, __) =>
                {
                    _result = _tb.Text;
                    DialogResult = true;
                    Close();
                };

                var cancel = new Button { Content = "取消", Width = 72 };
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

        public void SyncAutoFlowTemplate(NodeControl node, string? currentTopText)
        {
            if (node == null)
                return;

            string text = currentTopText ?? "";
            const string placeholder = "{{input}}";

            if (text.Contains(placeholder, StringComparison.Ordinal))
            {
                _autoFlowTemplatesByNode[node.Id] = text;
            }
        }

        public string GetAutoFlowTemplate(NodeControl node)
        {
            if (node == null)
                return "";

            if (_autoFlowTemplatesByNode.TryGetValue(node.Id, out var stored) &&
                !string.IsNullOrWhiteSpace(stored))
            {
                return stored;
            }

            string current = node.GetTopText() ?? "";
            if (current.Contains("{{input}}", StringComparison.Ordinal))
            {
                _autoFlowTemplatesByNode[node.Id] = current;
                return current;
            }

            return "";
        }

        public string CurrentFileDisplayKey()
        {
            if (string.IsNullOrWhiteSpace(_currentFilePath))
                return "default";

            try
            {
                return System.IO.Path.GetFileNameWithoutExtension(_currentFilePath);
            }
            catch
            {
                return "default";
            }
        }

        // ===== Memory v1：個人化設定對話框（齒輪開啟）=====

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            RefreshMemoryPanel();
            SyncDownstreamAutoModeRadios();
            SyncPresentationEngineRadios();
            SyncDocumentEngineRadios();
            EnsureApiStatusRows();
            SyncVideoStyleUI();
            BuildTaskRoutingPanel();
            SyncCostControls();
            ShowPersonalizationTab_Click(this, new RoutedEventArgs()); // 每次開啟預設停在「個人化」分頁
            RefreshApiPanel();
            if (SettingsOverlay != null)
                SettingsOverlay.Visibility = Visibility.Visible;
            MemoryInput?.Focus();
        }

        // ===== 設定分頁切換：個人化 / API / 技能 =====

        private void ShowPersonalizationTab_Click(object sender, RoutedEventArgs e)
        {
            if (PersonalizationScroll != null) PersonalizationScroll.Visibility = Visibility.Visible;
            if (ApiScroll != null) ApiScroll.Visibility = Visibility.Collapsed;
            if (SkillsScroll != null) SkillsScroll.Visibility = Visibility.Collapsed;
            SetTabSelected(TabPersonalizationBtn, true);
            SetTabSelected(TabApiBtn, false);
            SetTabSelected(TabSkillsBtn, false);
        }

        private void ShowApiTab_Click(object sender, RoutedEventArgs e)
        {
            if (PersonalizationScroll != null) PersonalizationScroll.Visibility = Visibility.Collapsed;
            if (ApiScroll != null) ApiScroll.Visibility = Visibility.Visible;
            if (SkillsScroll != null) SkillsScroll.Visibility = Visibility.Collapsed;
            SetTabSelected(TabPersonalizationBtn, false);
            SetTabSelected(TabApiBtn, true);
            SetTabSelected(TabSkillsBtn, false);
            RefreshApiPanel();
        }

        private void ShowSkillsTab_Click(object sender, RoutedEventArgs e)
        {
            if (PersonalizationScroll != null) PersonalizationScroll.Visibility = Visibility.Collapsed;
            if (ApiScroll != null) ApiScroll.Visibility = Visibility.Collapsed;
            if (SkillsScroll != null) SkillsScroll.Visibility = Visibility.Visible;
            SetTabSelected(TabPersonalizationBtn, false);
            SetTabSelected(TabApiBtn, false);
            SetTabSelected(TabSkillsBtn, true);
            RefreshSkillsList();
        }

        private static void SetTabSelected(System.Windows.Controls.Button? btn, bool selected)
        {
            if (btn == null) return;
            btn.Background = selected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x11, 0x11))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF1, 0xF3));
            btn.Foreground = selected
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44));
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlay != null)
                SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        // 點對話框外的暗色區域 → 關閉。
        private void SettingsOverlay_BackgroundClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SettingsOverlay != null)
                SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        // 點卡片本身不關閉（阻止冒泡到背景）。
        private void SettingsCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        // ===== 任務 → AI 自訂路由（個人化設定中的下拉清單）=====

        // 只列出真正會被路由特化的任務模式（Chat 為自由對話、沿用當前選定模型，不在此處覆蓋）。
        private static readonly (NodeTaskMode Mode, string Label)[] _routingModes = new[]
        {
            (NodeTaskMode.Research, "研究 / 查資料"),
            (NodeTaskMode.Code, "程式碼"),
            (NodeTaskMode.Translate, "翻譯"),
            (NodeTaskMode.Summarize, "摘要"),
            (NodeTaskMode.Rewrite, "改寫 / 潤稿"),
            (NodeTaskMode.Extract, "資訊擷取"),
        };

        // 每種任務真正需要的能力；只有具備該能力的已啟用模型才會出現在該任務的下拉中。
        // null = 純文字任務（翻譯 / 摘要 / 改寫 / 擷取），不特別限制，列出全部已啟用模型。
        private static AiModelCapability? RequiredCapabilityFor(NodeTaskMode mode) => mode switch
        {
            NodeTaskMode.Research => AiModelCapability.Search, // 需即時搜尋（目前只有 Perplexity 具備）
            NodeTaskMode.Code => AiModelCapability.Code,       // 需寫程式能力（排除無 Code 的 Perplexity）
            _ => null
        };

        // 程式化建立每列時暫時靜音 SelectionChanged，避免初始化就寫入 override。
        private bool _buildingTaskRouting;

        private void BuildTaskRoutingPanel()
        {
            if (TaskRoutingList == null)
                return;

            _buildingTaskRouting = true;
            try
            {
                TaskRoutingList.Children.Clear();
                var labelBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                var comboStyle = TryFindResource("RoutingComboBoxStyle") as Style;

                foreach (var (mode, label) in _routingModes)
                {
                    // 依任務能力過濾：不適用該任務的模型不列入（例：Perplexity 不會出現在「程式碼」）。
                    // 用 Available 保留 registry 的公司分組順序（GPT → Claude → Perplexity → Gemini），
                    // 不用 WithCapability（那會按價格排序，把同公司的拆散）。
                    var cap = RequiredCapabilityFor(mode);
                    var models = AiModelRegistry.Available
                        .Where(m => !cap.HasValue || (m.Capabilities & cap.Value) == cap.Value)
                        .ToList();

                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var caption = new TextBlock
                    {
                        Text = label,
                        FontSize = 12.5,
                        Foreground = labelBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(caption, 0);
                    row.Children.Add(caption);

                    var combo = new ComboBox { Tag = mode };
                    if (comboStyle != null)
                        combo.Style = comboStyle;

                    // 第一項固定為「自動」（= 清除 override，回退內建建議）。
                    var autoItem = new ComboBoxItem { Content = "自動（建議）", Tag = null };
                    combo.Items.Add(autoItem);

                    string? current = GetTaskRoutingOverride(mode);
                    ComboBoxItem selected = autoItem;

                    foreach (var m in models)
                    {
                        var item = new ComboBoxItem { Content = m.DisplayName, Tag = m.Id };
                        combo.Items.Add(item);
                        if (!string.IsNullOrEmpty(current) &&
                            string.Equals(current, m.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            selected = item;
                        }
                    }

                    combo.SelectedItem = selected;
                    combo.SelectionChanged += TaskRoutingCombo_SelectionChanged;

                    Grid.SetColumn(combo, 1);
                    row.Children.Add(combo);

                    TaskRoutingList.Children.Add(row);
                }
            }
            finally
            {
                _buildingTaskRouting = false;
            }
        }

        // ===== §15 個人化：高成本模型開關 + 手動逾時 =====

        // 把目前狀態反映到設定面板的控制項（開啟設定時呼叫）。
        private void SyncCostControls()
        {
            _syncingCostControls = true;
            try
            {
                if (BlockOpusSwitch != null)
                    BlockOpusSwitch.IsChecked = AiAutoCostPolicy.BlockOpus;
                if (BlockDeepResearchSwitch != null)
                    BlockDeepResearchSwitch.IsChecked = AiAutoCostPolicy.BlockDeepResearch;
                if (TimeoutInput != null)
                    TimeoutInput.Text = _manualTimeoutSeconds > 0
                        ? _manualTimeoutSeconds.ToString()
                        : "";
                if (ReadUpstreamAttachmentsSwitch != null)
                    ReadUpstreamAttachmentsSwitch.IsChecked = _readUpstreamAttachments;
                if (MobileMirrorSwitch != null)
                    MobileMirrorSwitch.IsChecked = _mobileMirrorEnabled;
                if (DailyBudgetInput != null)
                    DailyBudgetInput.Text = _dailyBudgetTwd > 0 ? _dailyBudgetTwd.ToString() : "";
                if (AutoConfirmSecondsInput != null)
                    AutoConfirmSecondsInput.Text = _autoConfirmSeconds > 0 ? _autoConfirmSeconds.ToString() : "0";
                if (GoogleAutoUploadSwitch != null)
                    GoogleAutoUploadSwitch.IsChecked = _googleAutoUploadDrive;
                if (DriveTypePdf != null)   DriveTypePdf.IsChecked   = _driveUploadTypes.Contains("pdf");
                if (DriveTypeWord != null)  DriveTypeWord.IsChecked  = _driveUploadTypes.Contains("word");
                if (DriveTypePpt != null)   DriveTypePpt.IsChecked   = _driveUploadTypes.Contains("ppt");
                if (DriveTypeExcel != null) DriveTypeExcel.IsChecked = _driveUploadTypes.Contains("excel");
                if (DriveTypeImage != null) DriveTypeImage.IsChecked = _driveUploadTypes.Contains("image");
                if (DriveTypeVideo != null) DriveTypeVideo.IsChecked = _driveUploadTypes.Contains("video");
                if (DriveTypeText != null)  DriveTypeText.IsChecked  = _driveUploadTypes.Contains("text");
                if (DriveConvertSwitch != null)
                    DriveConvertSwitch.IsChecked = _driveConvertGoogle;
                if (SpendTodayText != null)
                    SpendTodayText.Text = $"今日已花：{SpendLedger.TodayDisplay()}";
                RefreshSkillsList();   // §19：開設定面板時同步技能清單
            }
            finally
            {
                _syncingCostControls = false;
            }
        }

        private void ReadUpstreamAttachmentsSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            _readUpstreamAttachments = ReadUpstreamAttachmentsSwitch?.IsChecked == true;
            SavePreferences();
        }

        private void BlockOpusSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            AiAutoCostPolicy.BlockOpus = BlockOpusSwitch?.IsChecked == true;
            SaveState();
        }

        private void BlockDeepResearchSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            AiAutoCostPolicy.BlockDeepResearch = BlockDeepResearchSwitch?.IsChecked == true;
            SaveState();
        }

        // 逾時輸入：空白 = 自動；否則限制在 30～1800 秒（30 分鐘）的合理範圍。
        private void TimeoutInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (_syncingCostControls || TimeoutInput == null)
                return;

            string raw = (TimeoutInput.Text ?? "").Trim();

            if (string.IsNullOrEmpty(raw))
            {
                _manualTimeoutSeconds = 0;
            }
            else if (int.TryParse(raw, out int secs))
            {
                _manualTimeoutSeconds = Math.Clamp(secs, 30, 1800);
            }
            else
            {
                return; // 非數字：忽略，不覆寫既有值
            }

            SaveState();
        }

        private void TaskRoutingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_buildingTaskRouting)
                return;
            if (sender is not ComboBox combo || combo.Tag is not NodeTaskMode mode)
                return;

            string? modelId = (combo.SelectedItem as ComboBoxItem)?.Tag as string;

            if (string.IsNullOrEmpty(modelId))
                ClearTaskRoutingOverride(mode);
            else
                SetTaskRoutingOverride(mode, modelId);
        }

        private void RememberPreference_Click(object sender, RoutedEventArgs e)
        {
            if (_nodeService == null || MemoryInput == null)
                return;

            string text = (MemoryInput.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            _nodeService.AddManualPreference(text);
            MemoryInput.Clear();
            RefreshMemoryPanel();
            // 不彈確認視窗；新偏好會直接出現在下方清單。
        }

        // 單獨刪除一條偏好（× 按鈕）。
        private void DeletePreference_Click(object sender, RoutedEventArgs e)
        {
            if (_nodeService == null)
                return;

            if (sender is FrameworkElement fe && fe.Tag is string key && !string.IsNullOrWhiteSpace(key))
            {
                _nodeService.DeletePreference(key);
                RefreshMemoryPanel();
            }
        }

        // 「當前記憶」按鈕：展開／收合記憶清單（展開時即時刷新）。
        private void ToggleMemoryList_Click(object sender, RoutedEventArgs e)
        {
            if (MemoryListPanel == null)
                return;

            bool show = MemoryListPanel.Visibility != Visibility.Visible;
            MemoryListPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
                RefreshMemoryPanel();
        }

        private void ClearMemory_Click(object sender, RoutedEventArgs e)
        {
            if (_nodeService == null)
                return;

            bool ok = MenuConfirmDialog.ShowConfirm(
                this,
                "清除記憶",
                "確定要清除全部記憶嗎？此動作無法復原。",
                this,
                confirmText: "清除",
                cancelText: "取消",
                danger: true);

            if (!ok)
                return;

            _nodeService.ClearShownMemory();
            RefreshMemoryPanel();
        }

        public void RefreshMemoryPanel()
        {
            if (_nodeService == null)
                return;

            try
            {
                var items = _nodeService.GetPreferenceItems();
                if (PreferenceList != null)
                    PreferenceList.ItemsSource = items;

                // 空狀態提示 + 清除鈕只在有資料時顯示。
                bool hasItems = items != null && items.Count > 0;
                if (MemoryEmptyHint != null)
                    MemoryEmptyHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
                if (ClearAllMemoryButton != null)
                    ClearAllMemoryButton.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                // 面板刷新失敗不影響主流程
            }
        }

        // 把「目前生效的影片風格」填進輸入框：使用者自訂時顯示自訂內容，否則顯示原廠完整 prompt
        // （讓使用者看得到格式才能照著改寫）。
        private void SyncVideoStyleUI()
        {
            if (VideoStyleInput == null)
                return;

            VideoStyleInput.Text = GetEffectiveVideoStylePrompt();

            // 影片模型檔位下拉同步（Tag 對 Standard / Fast / Lite）。
            if (VideoModelTierCombo != null)
            {
                string want = VeoModels.ToStorageValue(_videoModelTier);
                foreach (var obj in VideoModelTierCombo.Items)
                {
                    if (obj is ComboBoxItem ci &&
                        string.Equals(ci.Tag?.ToString(), want, StringComparison.OrdinalIgnoreCase))
                    {
                        VideoModelTierCombo.SelectedItem = ci;
                        break;
                    }
                }
            }
        }

        // 影片模型檔位變更：落地個人化（前期測試建議 Lite）。
        private void VideoModelTierCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoModelTierCombo?.SelectedItem is not ComboBoxItem ci)
                return;

            _videoModelTier = VeoModels.ParseTier(ci.Tag?.ToString());
            SavePreferences();
        }

        // 失焦時落地影片風格：等於原廠預設（或空）就清成空字串（=用預設），否則存為自訂覆寫。
        private void VideoStyleInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (VideoStyleInput == null)
                return;

            string text = (VideoStyleInput.Text ?? "").Trim();

            _videoStyleOverride =
                (text.Length == 0 ||
                 string.Equals(text, VideoStyle.DefaultCinematicPrompt.Trim(), StringComparison.Ordinal))
                    ? ""
                    : text;

            SavePreferences();
        }

        private void ResetVideoStyle_Click(object sender, RoutedEventArgs e)
        {
            _videoStyleOverride = "";
            SavePreferences();
            SyncVideoStyleUI();
        }

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
            if (!TryBuildAutoFlowInjectedPrompt(fromNode, toNode, out var injected))
                return false;

            toNode.SetTopText(injected);
            toNode.RefreshModelSelectionUI();
            RefreshDecisionForNode(toNode);
            return true;
        }

        public bool TryBuildInputFromFirstUpstream(NodeControl node, out string injectedPrompt)
        {
            injectedPrompt = "";

            if (node == null)
                return false;

            var incoming = GetConnectionsForContext()
                .Where(c => ReferenceEquals(c.EndNode, node))
                .Select(c => c.StartNode)
                .Where(n => n != null)
                .FirstOrDefault();

            return incoming != null && TryBuildAutoFlowInjectedPrompt(incoming, node, out injectedPrompt);
        }

        private bool TryBuildAutoFlowInjectedPrompt(
            NodeControl fromNode,
            NodeControl toNode,
            out string injectedPrompt)
        {
            injectedPrompt = "";

            if (fromNode == null || toNode == null)
                return false;

            string sourceText = (fromNode.GetBottomText() ?? "").Trim();
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
                .Where(n => n != null)
                .FirstOrDefault();

            return incoming != null && TryPrepareAutoFlowInput(incoming, node);
        }

        public void FocusDecisionNode(NodeControl node)
        {
            if (node == null)
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
            try
            {
                var visited = new HashSet<Guid>();
                var current = startNode;

                for (int step = 0; step < 12; step++)
                {
                    if (current == null || !MainCanvas.Children.Contains(current))
                        return;

                    if (cts.IsCancellationRequested)
                        return;

                    if (!visited.Add(current.Id))
                        return;

                    FocusDecisionNode(current);

                    if (ShouldStopBeforeUnsupportedDownstreamNode(current))
                    {
                        current.SetBottomText(
                            "（此下游節點需要尚未接上的專用代理，已停止以避免不必要的 token 消耗。）\n" +
                            "目前可先使用前一個節點的結果；等 presentation/file/media/workflow agent 完成後再啟用此步。");
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
                    current.SetBottomText("（Dry run 停止：偵測到循環連線。）");
                    FocusDecisionNode(current);
                    return;
                }

                FocusDecisionNode(current);

                if (ShouldStopBeforeUnsupportedDownstreamNode(current))
                {
                    current.SetBottomText(
                        "（Dry run 停止：此節點需要尚未接上的專用代理。）\n" +
                        "真實執行時也會在此停止，以避免不必要的 token 消耗。");
                    return;
                }

                string label = ExtractWorkflowStepLabel(current.GetTopText());
                string inputSummary = string.IsNullOrWhiteSpace(upstreamText)
                    ? "無上游輸出或由本節點原始輸入開始。"
                    : SummarizeForDryRun(upstreamText);

                current.SetBottomText(
                    $"（Dry run，未呼叫模型）\n" +
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

            return _connections
                .Where(c => ReferenceEquals(c.StartNode, node) && c.FlowMode && c.EndNode != null)
                .Select(c => c.EndNode)
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
            // 下游先全部標記「等待中」（琥珀框 + 手機顯示），輪到誰執行時 Running 會蓋過；
            // startNode 自己不標（runStartNode 時它馬上就跑、否則它是剛跑完的上游）。
            MarkFlowSubtreeWaiting(startNode, includeRoot: false);
            try
            {
                var visited = new HashSet<Guid>();
                await RunFlowSubtreeAsync(startNode, runStartNode, visited, cts);
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

        private async Task RunFlowSubtreeAsync(
            NodeControl node,
            bool runThis,
            HashSet<Guid> visited,
            CancellationTokenSource cts)
        {
            if (node == null || !MainCanvas.Children.Contains(node))
                return;

            if (cts.IsCancellationRequested)
                return;

            // 防環：同一節點只跑一次。
            if (!visited.Add(node.Id))
                return;

            if (runThis)
            {
                FocusDecisionNode(node);

                if (ShouldStopBeforeUnsupportedDownstreamNode(node))
                {
                    node.SetBottomText(
                        "（此下游節點需要尚未接上的專用代理，已停止以避免不必要的 token 消耗。）\n" +
                        "目前可先使用前一個節點的結果；等 presentation/file/media/workflow agent 完成後再啟用此步。");
                    return;
                }

                _runningChainNode = node;
                bool success = await node.RunCurrentTopTextAsync(cts.Token);
                _runningChainNode = null;

                if (cts.IsCancellationRequested)
                    return;

                // 父失敗 → 不往下跑這條分支（避免把錯誤內容往下游灌）。
                if (!success)
                    return;
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

                branchTasks.Add(RunFlowSubtreeAsync(child, runThis: true, visited, cts));
            }

            await Task.WhenAll(branchTasks);
        }
        // MenuConfirmDialog / MenuConfirmWindow → 已搬到 MenuConfirmDialog.cs(第 3 刀)。
    }
}
