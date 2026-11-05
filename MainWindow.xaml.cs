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

        // 簡報生成器（個人化設定可切換；預設 Claude）。
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
            /// <summary>看得到的那條線（流動時是藍色虛線）。不接受滑鼠，見 HitPath。</summary>
            public PathShape Path = null!;

            /// <summary>
            /// 專門用來被點的那條線：同樣的曲線、同樣的粗細，但永遠是實心的透明筆畫。
            ///
            /// 為什麼要分成兩條：流動線是虛線，而 WPF 的命中測試只認「真的畫出來的那幾段」，
            /// 虛線的空隙完全點不到。右鍵切換流動模式是這條線唯一的操作方式，
            /// 使用者卻有一半的機率點了沒反應，而且看不出為什麼。
            /// （Transparent 的筆畫接受命中測試，null 的不接受——這正是這條線存在的理由。）
            /// </summary>
            public PathShape HitPath = null!;

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
            // 含背景執行中存檔的連線：呼叫端都是依節點找上下游，背景節點要找得到自己的上游（見 MainWindow.Sessions.cs）。
            return LiveConnections()
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

        // 目前畫面的存檔路徑。存在 ProjectRef 裡（見 MainWindow.Sessions.cs）：切換存檔時，執行中的節點
        // 保有原本那個 ProjectRef，改名也會跟著更新，產出才寫得回對的存檔。
        private string? _currentFilePath
        {
            get => _activeProject.Path;
            set => _activeProject.Path = value;
        }
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

            // 這個存檔有節點在執行（目前畫面或背景）：名稱顯示流光，直到全部跑完（見 MainWindow.Sessions.cs）。
            private bool _isRunning;
            public bool IsRunning
            {
                get => _isRunning;
                set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(nameof(IsRunning)); } }
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

        // 最近刪除：刪掉的存檔連同附件與該存檔的記憶整包留 7 天，可完整復原。
        private RecycleBin? _trashInstance;
        private RecycleBin Trash => _trashInstance ??= new RecycleBin(SavesDir);

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
                UsageRecords: entry.UsageRecords?.ToList(),

                MemoryRecall: entry.MemoryRecall,
                EffectivePreferences: entry.EffectivePreferences ?? "",
                OutputIntentSummary: entry.OutputIntentSummary ?? "",
                StepInputs: entry.StepInputs?.ToList(),
                StepInputLogPath: entry.StepInputLogPath ?? "",
                CapabilityPlanSummary: entry.CapabilityPlanSummary ?? ""
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

                // 執行紀錄是「當時發生了什麼」，不是「現在該路由到哪裡」。
                //
                // NormalizeNodeModel 會把舊世代 ID 轉成後繼模型（gpt-5.5 → gpt-5.6-sol、
                // claude-opus-5 → claude-opus-5-5），那是路由用的正規化；套在歷史紀錄上
                // 等於竄改稽查資料——而且逐筆 UsageRecords 仍保留原 ID，同一份紀錄會自相矛盾
                //（2026-10-27 第三方審查）。這裡一律原值讀回。
                RequestedModelId = state.RequestedModelId ?? "",
                PlannedModelId = state.PlannedModelId ?? "",
                ActualModelId = state.ActualModelId ?? "",

                TaskMode = state.TaskMode ?? "",
                Confidence = state.Confidence,

                ResolverReason = state.ResolverReason ?? "",
                ResolverKeywords = state.ResolverKeywords?.ToList() ?? new List<string>(),

                CapabilityAdjusted = state.CapabilityAdjusted,
                CapabilityReason = state.CapabilityReason ?? "",

                CapabilityRequestedModelId = state.CapabilityRequestedModelId ?? "",
                CapabilityResolvedModelId = state.CapabilityResolvedModelId ?? "",
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
                MemoryRecall = state.MemoryRecall ?? MemoryRecallStats.Empty,
                EffectivePreferences = state.EffectivePreferences ?? "",
                OutputIntentSummary = state.OutputIntentSummary ?? "",
                CapabilityPlanSummary = state.CapabilityPlanSummary ?? "",
                StepInputs = state.StepInputs ?? new List<StepInputRecord>(),
                StepInputLogPath = state.StepInputLogPath ?? "",
                UsageRecords = state.UsageRecords ?? new List<UsageRecord>()
            };
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

        /// <summary>把 Auto / 進階解析兩個開關的畫面狀態同步成目前（全域）設定。</summary>
        private void SyncAutoModeSwitches()
        {
            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = _isAutoModelSelectionEnabled;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();
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
            {
                SaveState();
                // 也要落地成全域偏好：只寫進存檔的話，SaveState 在還沒建檔時直接 return，
                // 重開程式後又從偏好讀回舊值——使用者會看到自己剛切的模式自己彈回去。
                SavePreferences();
            }
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
            {
                SaveState();
                // 也要落地成全域偏好：只寫進存檔的話，SaveState 在還沒建檔時直接 return，
                // 重開程式後又從偏好讀回舊值——使用者會看到自己剛切的模式自己彈回去。
                SavePreferences();
            }
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

        // 協作模式：節點 → 一起回答的模型（2026-10-29）。少於兩個就是一般模式。
        private readonly Dictionary<Guid, List<string>> _collaborationModelsByNode = new();

        public IReadOnlyList<string> GetNodeCollaborationModels(NodeControl node)
            => node != null && _collaborationModelsByNode.TryGetValue(node.Id, out var list)
                ? list
                : Array.Empty<string>();

        public void SetNodeCollaborationModels(NodeControl node, IEnumerable<string>? models)
        {
            if (node == null) return;

            var normalized = ModelCollaboration.Normalize(models).ToList();
            if (normalized.Count >= ModelCollaboration.MinModels)
                _collaborationModelsByNode[node.Id] = normalized;
            else
                _collaborationModelsByNode.Remove(node.Id);

            RefreshDecisionForNode(node);
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
            ProviderBalance.Initialize(System.IO.Path.GetDirectoryName(PreferencesPath) ?? SavesDir);

            // #10 長任務日誌：影片 operation 落地/恢復用（與帳本同資料夾）。
            VideoJobJournal.Initialize(System.IO.Path.GetDirectoryName(PreferencesPath) ?? SavesDir);

            // 全域個人化偏好先載入（在同步開關之前），確保所有設定一律以個人化為準，跨專案、跨重啟一致。
            LoadPreferences();
            // 介面語言：沒選過就先問（後面的金鑰精靈、開始畫面都要用選好的語言顯示）。
            AskInterfaceLanguageIfNeeded();
            SyncUiLanguageRadios();
            SyncDownstreamAutoModeRadios();
            SyncPresentationEngineRadios();
            SyncDocumentEngineRadios();
            SyncIllustrationQualityRadios();
            EnsureApiStatusRows();
            ApplyApiBalancePanelVisibility();   // 右下角餘額面板（依個人化開關）
            OrganizeGeneratedFilesOnce();       // 舊的散檔歸到各自的存檔資料夾
            AlignGeneratedFoldersWithSaveNames(); // 改過名的存檔：產出資料夾對齊目前的名稱，收掉空資料夾
            EnsureSessionTimer();               // 存檔清單的流光、背景執行中存檔的釋放（MainWindow.Sessions.cs）
            ApplyDecisionPanelVisibility();     // 右上角決策視窗（依個人化開關）
            InitializeTrash();                  // 最近刪除：清掉過期的，決定入口要不要顯示

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
                UiLanguage.P("讓我們有個新的開始！", "Let's make a fresh start!"),
                UiLanguage.P("或許我們該從頭來過…", "Maybe we should start over…"),
                UiLanguage.P("再來一次吧！忘掉過去的那些…", "One more time! Forget what came before…"),
                UiLanguage.P("來點新的？", "Something new?")
            };
            StartMessage.Text = messages[_random.Next(messages.Length)];
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartUI.Visibility = Visibility.Collapsed;
            MainUI.Visibility = Visibility.Visible;

            _currentFilePath = System.IO.Path.Combine(SavesDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            RefreshCurrentFileLabel();
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
                    title: UiLanguage.P("未完成的影片任務", "Unfinished video jobs"),
                    message: UiLanguage.P($"偵測到 {pending.Count} 個上次未完成的影片生成（費用已支付）：\n{preview}\n\n", $"Found {pending.Count} unfinished video generation(s) from last time (already paid):\n{preview}\n\n") +
                             UiLanguage.P("要接續查詢並取回嗎？只是查詢雲端進度，不會重新付費。", "Check their progress and retrieve them? This only checks the cloud status; you won't be charged again."),
                    resourceHost: this,
                    confirmText: UiLanguage.P("取回", "Retrieve"),
                    cancelText: UiLanguage.P("略過", "Skip"));

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
                                ? UiLanguage.P("恢復的影片", "Recovered video")
                                : UiLanguage.P("恢復的影片_", "Recovered video_") + VideoJobJournal.Summarize(job.Prompt, 24).TrimEnd('…'),
                            content: result.Mp4Bytes,
                            sourceSummary: UiLanguage.P("程式重啟後接續取回（Veo operation 續輪詢，未重新付費）", "Retrieved after restart (Veo operation polled again, no new charge)"));

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
                        failMsgs.Add(string.IsNullOrWhiteSpace(result.ErrorMessage) ? UiLanguage.P("未知原因", "Unknown reason") : result.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    failMsgs.Add(ex.Message);
                    AppLog.Warn("VideoJobs", $"影片任務恢復失敗：{job.OperationName}", ex);
                }
            }

            string summary = okCount > 0 ? UiLanguage.P($"已取回 {okCount} 支影片到產出資料夾（_generated）。", $"Retrieved {okCount} video(s) to the output folder (_generated).") : "";
            if (failMsgs.Count > 0)
                summary += UiLanguage.P($"\n{failMsgs.Count} 個未能取回：{failMsgs[0]}", $"\n{failMsgs.Count} couldn't be retrieved: {failMsgs[0]}");

            MenuConfirmDialog.ShowMessage(this, UiLanguage.P("影片任務恢復", "Video job recovery"),
                string.IsNullOrWhiteSpace(summary) ? UiLanguage.P("沒有可取回的影片。", "No videos to retrieve.") : summary.Trim(), this);
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

            SaveStateFor(sender as NodeControl);
        }

        private void Node_ContentChanged(object? sender, EventArgs e)
        {
            SaveStateFor(sender as NodeControl);
        }

        /// <summary>節點在背景執行中的存檔裡：存回那個存檔；否則存目前畫面。</summary>
        private void SaveStateFor(NodeControl? node)
        {
            var session = SessionOf(node);
            if (session != null)
                SaveBackgroundSession(session);
            else
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

        /// <summary>
        /// 畫布上這個座標落在哪個節點上（排除指定的那一個）。重疊時取 Z 序最高的那一個，
        /// 跟使用者眼睛看到的「最上面那張卡」一致。
        /// </summary>
        public NodeControl? FindNodeAtCanvasPoint(Point canvasPoint, NodeControl? exclude = null)
        {
            NodeControl? best = null;
            int bestZ = int.MinValue;

            foreach (var node in MainCanvas.Children.OfType<NodeControl>())
            {
                if (ReferenceEquals(node, exclude))
                    continue;

                double left = Canvas.GetLeft(node);
                double top = Canvas.GetTop(node);
                if (double.IsNaN(left) || double.IsNaN(top))
                    continue;

                double w = node.ActualWidth > 0 ? node.ActualWidth : node.Width;
                double h = node.ActualHeight > 0 ? node.ActualHeight : node.Height;
                if (!new Rect(left, top, w, h).Contains(canvasPoint))
                    continue;

                int z = Canvas.GetZIndex(node);
                if (z >= bestZ)
                {
                    bestZ = z;
                    best = node;
                }
            }

            return best;
        }

        /// <summary>
        /// 這條新連線接得起來嗎（不碰任何資料，只回答能不能）。
        /// 拖曳途中用它決定要不要把虛線變成藍色，放開時再問一次。
        /// </summary>
        public bool CanConnectNodes(NodeControl? source, NodeControl? target)
            => source != null && target != null &&
               ConnectionRules.Evaluate(CurrentEdges(), source.Id, target.Id) == ConnectionRules.Verdict.Ok;

        /// <summary>目前畫布上的所有連線，化成單純的 (上游, 下游) 配對給 ConnectionRules 用。</summary>
        private IEnumerable<(Guid From, Guid To)> CurrentEdges()
            => _connections
                .Where(c => c.StartNode != null && c.EndNode != null)
                .Select(c => (c.StartNode.Id, c.EndNode.Id));

        /// <summary>
        /// 把來源節點接到一個**已經存在**的節點上——匯流（兩條以上的上游匯進同一個節點）
        /// 就是這樣做出來的。
        ///
        /// 在這之前，從連接點拖出去一律會生出一個**新**節點，而「變更上游」是把既有的那條線
        /// 搬到別處、不是多加一條。也就是說排程器早就看得懂「等所有上游都跑完再開始」，
        /// 畫面上卻永遠做不出那個形狀。使用者 2026-10-13 回報「我沒有設定如 C 的連接方式，
        /// 目前也沒有連接方式」講的就是這件事。
        ///
        /// 新線跟「拖到空白處生新節點」一樣預設是**一般連線（灰線）**：
        /// 同一個手勢在兩個地方給出不同種類的線，使用者沒有辦法預期自己做出了什麼。
        /// 要讓它自動往下跑，右鍵那條線改成流動連線。
        /// </summary>
        public bool TryConnectToExistingNode(NodeControl source, string sourceThumb, NodeControl target, Point dropPoint)
        {
            if (!CanConnectNodes(source, target))
                return false;

            // 接左孔還是右孔：看放開的位置在目標節點中線的哪一邊，線才不會橫跨整張卡。
            double left = Canvas.GetLeft(target);
            double width = target.ActualWidth > 0 ? target.ActualWidth : target.Width;
            string endThumb = dropPoint.X < left + width / 2 ? "ThumbTL" : "ThumbTR";

            CreateCurve(source, sourceThumb, target, endThumb, flowMode: false);
            return true;
        }

        public void CreateCurve(NodeControl startNode, string startThumbName, NodeControl endNode, string endThumbName, bool flowMode = false)
        {
            var path = new PathShape
            {
                Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#ADADAD")!,
                StrokeThickness = 18,
                // 看得到的那條線不收滑鼠事件：流動時它是虛線，空隙點不到。
                IsHitTestVisible = false
            };

            // 收滑鼠的是這條：一樣的曲線、一樣粗，實心透明，所以整條都點得到。
            var hitPath = new PathShape
            {
                Stroke = System.Windows.Media.Brushes.Transparent,
                StrokeThickness = 18,
                IsHitTestVisible = true,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var conn = new Connection
            {
                Path = path,
                HitPath = hitPath,
                StartNode = startNode,
                StartThumb = startThumbName,
                EndNode = endNode,
                EndThumb = endThumbName,
                FlowMode = flowMode
            };

            // 右鍵連接線：切換流動模式（含動畫）。Handled 避免冒泡到畫布平移。
            hitPath.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                _ = ToggleConnectionFlowModeAsync(conn);
            };

            // 流動模式只能用「右鍵點線」切換，沒有任何地方寫著這件事——
            // 畫面上就只是一條灰線，使用者不會去右鍵一條線。至少讓滑鼠停著時說得出來。
            UpdateConnectionTooltip(conn);

            UpdateConnectionGeometry(conn);

            int z = GetNextZIndex();
            Canvas.SetZIndex(path, z);
            Canvas.SetZIndex(hitPath, z);       // 疊在看得到的那條線上，但仍在節點之下
            MainCanvas.Children.Add(path);
            MainCanvas.Children.Add(hitPath);
            _connections.Add(conn);
            ApplyConnectionVisual(conn);

            HookNode(startNode);
            HookNode(endNode);

            SaveState();
        }

        /// <summary>連接線的滑鼠提示：現在是哪種線、右鍵會變成什麼。</summary>
        private static void UpdateConnectionTooltip(Connection conn)
        {
            if (conn?.HitPath == null) return;

            conn.HitPath.ToolTip = conn.FlowMode
                ? UiLanguage.P("流動連線（藍色虛線）\n上游跑完會自動帶著結果往下游跑；匯流節點會等所有上游都完成。\n右鍵：改回一般連線", "Flow link (blue dashed)\nWhen the upstream finishes, its result flows downstream automatically; merge nodes wait for every upstream.\nRight-click: switch back to a normal link")
                : UiLanguage.P("一般連線（灰線）\n只把上游內容當背景，不會自動往下跑。\n右鍵：改成流動連線", "Normal link (gray)\nUpstream content is only used as background and doesn't run downstream automatically.\nRight-click: switch to a flow link");
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
            UpdateConnectionTooltip(conn);
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

        // ===== 簡報生成器（個人化）=====

        public void SetPresentationEngine(PresentationEngine engine, bool save = true)
        {
            // Gamma 選項已從介面下架（2026-10-15）；舊的存檔值會在 Parse 時轉成 Claude。
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
            else
                SetPresentationEngine(PresentationEngine.Claude);
        }

        private bool _suppressIllustrationQualityRadioEvents;

        /// <summary>
        /// 配圖品質三個選項。文字裡的單價直接問價目表，不寫死在 XAML——
        /// 不然哪天官方調價，介面上就會長期掛著一個騙人的數字。
        /// </summary>
        private void SyncIllustrationQualityRadios()
        {
            _suppressIllustrationQualityRadioEvents = true;
            try
            {
                string current = ImageQuality.Normalize(DeliveryOptions.IllustrationQuality);

                void Fill(System.Windows.Controls.RadioButton? btn, string quality, string label)
                {
                    if (btn == null) return;
                    btn.Content = UiLanguage.P($"{label}（約 {ModelCostEstimator.ImageUnitCostText(AgentRuntime.DeckImageSize, quality)}）", $"{label} (about {ModelCostEstimator.ImageUnitCostText(AgentRuntime.DeckImageSize, quality)})");
                    btn.IsChecked = current == quality;
                }

                Fill(IllustrationQualityHigh, ImageQuality.High, UiLanguage.P("高品質", "High quality"));
                Fill(IllustrationQualityMedium, ImageQuality.Medium, UiLanguage.P("標準（建議）", "Standard (recommended)"));
                Fill(IllustrationQualityLow, ImageQuality.Low, UiLanguage.P("最省", "Cheapest"));
            }
            finally
            {
                _suppressIllustrationQualityRadioEvents = false;
            }
        }

        private void IllustrationQuality_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressIllustrationQualityRadioEvents)
                return;

            DeliveryOptions.IllustrationQuality =
                ReferenceEquals(sender, IllustrationQualityHigh) ? ImageQuality.High :
                ReferenceEquals(sender, IllustrationQualityLow) ? ImageQuality.Low :
                ImageQuality.Medium;

            SaveState();
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
            if (c.HitPath != null)
                c.HitPath.Data = geometry;
        }

        public bool HasOutgoingConnections(NodeControl node)
        {
            foreach (var c in LiveConnections())
                if (ReferenceEquals(c.StartNode, node))
                    return true;
            return false;
        }

        // ===== 右鍵：將此節點加入記憶 =====
        // 存成一筆使用者明確標記的共享記憶（user_marked，重要性 0.97、SourceNodeId 留空 → 不受跨鏈隔離、全畫布可召回）。
        // 同時會出現在「個人化 → 當前記憶」清單（NodeMemoryService 把 user_marked 併入該清單）。
        public async void AddNodeToMemory(NodeControl node)
        {
            if (node == null || _nodeService == null)
                return;

            string topText = node.GetTopText() ?? "";
            string bottomText = node.GetBottomText() ?? "";

            if (string.IsNullOrWhiteSpace(bottomText))
            {
                MenuConfirmDialog.ShowMessage(this, UiLanguage.P("加入記憶", "Add to memory"),
                    UiLanguage.P("這個節點還沒有產出內容，無法加入記憶。", "This node hasn't produced anything yet, so it can't be added to memory."), this);
                return;
            }

            string agentId = GetNodeSelectedAgent(node);
            string modelId = GetNodeSelectedModel(node);
            NodeTaskMode taskMode = GetNodeTaskMode(node);

            // async void：例外沒人接的話會變成未處理例外。這裡會呼叫模型做重點萃取，
            // 離線或額度用完都可能丟出來，必須在這一層收掉並告訴使用者。
            string title;
            try
            {
                title = await _nodeService.RememberNodeManuallyAsync(node, agentId, topText, bottomText, taskMode, modelId);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Memory", "加入記憶失敗", ex);
                MenuConfirmDialog.ShowMessage(this, UiLanguage.P("加入記憶", "Add to memory"), UiLanguage.P("加入記憶失敗：", "Couldn't add to memory:") + ex.Message, this);
                return;
            }

            // 加入後立即刷新並展開「當前記憶」面板，讓使用者馬上看到（需求：標記的節點要出現在個人化當前記憶中）。
            RefreshMemoryPanel();
            if (MemoryListPanel != null)
            {
                MemoryListPanel.Visibility = Visibility.Visible;
                RefreshMemoryPanel();
            }

            MenuConfirmDialog.ShowMessage(this, UiLanguage.P("加入記憶", "Add to memory"),
                UiLanguage.P($"已加入記憶：\n⭐ {title}", $"Added to memory:\n⭐ {title}"), this);
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
                if (c.HitPath != null)
                    MainCanvas.Children.Remove(c.HitPath);
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
            foreach (var c in LiveConnections())
                yield return (c.StartNode, c.StartThumb, c.EndNode, c.EndThumb);
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

        // MenuConfirmDialog / MenuConfirmWindow → 已搬到 MenuConfirmDialog.cs(第 3 刀)。
    }
}
