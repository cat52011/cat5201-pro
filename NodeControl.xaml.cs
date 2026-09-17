using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Cat5201
{
    public partial class NodeControl : UserControl, INodeContext
    {
        private Path? _tempPath;
        private Point _startPoint;
        private MainWindow? _parent;

        private bool _isDraggingNode = false;
        private Point _dragStartOnCanvas;
        private Point _nodeStartPos;

        private bool _isTopLocked = false;
        private bool _isGenerating = false;
        // 手機鏡像（§17 階段二）遠端「停止」用：每次執行建立、外部可 Cancel；手動停止與逾時要分開顯示。
        private CancellationTokenSource? _activeManualStop;
        private bool _isShowingLoadingText = false;

        // 等待計時：讓使用者在長任務（特別是生成圖片）時看到已等待秒數，而不是空轉。
        private DispatcherTimer? _loadingTimer;
        private DateTime _loadingStartUtc;
        private string _loadingBaseText = "AI 正在生成";
        private bool _loadingIsImageTask = false;

        // 長任務（影片生成等）的即時進度提示，附加在 loading 文字後面，由執行端動態更新。
        private string _loadingExtraHint = "";

        // Product UX：節點執行狀態（邊框顏色 + 狀態列）。
        private enum NodeRunStatus { Idle, Running, Success, Failed, Waiting }
        private NodeRunStatus _runStatus = NodeRunStatus.Idle;
        private DispatcherTimer? _statusRevertTimer;
        // 失敗後「重新執行」要用的上一次實際送出 prompt。
        private string _lastRunPrompt = "";

        private double _fontSize = 20;

        private bool _isSyncingModelSelector = false;
        private bool _modelsLoaded = false;

        // ===== 新增：模型正式值 / 編輯草稿值 =====
        // 上一次真正送出內容時所使用的模型
        private string _committedModelId = AiModels.DefaultNodeModel;

        // 目前編輯中的暫時模型
        private string _editingModelId = AiModels.DefaultNodeModel;

        // ===== #1 真實 token 用量載體 =====
        // 本次主回覆執行從 API usage 累積的真實 token 數（跨 continuation 多輪相加）。
        // 以「節點」為載體：不同節點各自獨立，多節點並行執行也安全。null/0 = 該模型尚未接真實 usage。
        private int _lastInputTokens;
        private int _lastOutputTokens;
        private bool _hasRealTokenUsage;

        // ===== #1b 媒體生成成本累加器 =====
        // 圖片/影片以「張數」或「秒數」計價，無法用 token 計算。
        // 每次生成後呼叫 AddMediaCostUsd() 累加；ResetTokenUsage() 時一併清零。
        private double _mediaGenerationCostUsd;
        private string _mediaGenerationCostLabel = "";

        /// <summary>每次主回覆執行開始時清零（之後同一次執行的多輪累加）。</summary>
        public void ResetTokenUsage()
        {
            _lastInputTokens = 0;
            _lastOutputTokens = 0;
            _hasRealTokenUsage = false;
            _mediaGenerationCostUsd = 0;
            _mediaGenerationCostLabel = "";
        }

        /// <summary>累加一次 API 呼叫回傳的真實用量；任一為 null 表示該 provider 未提供，略過。</summary>
        public void RecordTokenUsage(int? inputTokens, int? outputTokens)
        {
            if (inputTokens.HasValue && inputTokens.Value > 0)
            {
                _lastInputTokens += inputTokens.Value;
                _hasRealTokenUsage = true;
            }
            if (outputTokens.HasValue && outputTokens.Value > 0)
            {
                _lastOutputTokens += outputTokens.Value;
                _hasRealTokenUsage = true;
            }
        }

        /// <summary>取本次執行的真實用量；無真實資料回 false（呼叫端退回估算）。</summary>
        public bool TryGetRealTokenUsage(out int inputTokens, out int outputTokens)
        {
            inputTokens = _lastInputTokens;
            outputTokens = _lastOutputTokens;
            return _hasRealTokenUsage;
        }

        /// <summary>累加一次媒體生成費用（圖片/影片以張或秒計價，不用 token）。</summary>
        public void AddMediaCostUsd(double usd, string label)
        {
            if (usd <= 0) return;
            _mediaGenerationCostUsd += usd;
            if (!string.IsNullOrWhiteSpace(label))
                _mediaGenerationCostLabel += (string.IsNullOrEmpty(_mediaGenerationCostLabel) ? "" : " + ") + label;

            // 花錢安全：媒體成本也進全域帳本（LLM 文字成本在 MainWindow.AddExecutionLog 記）。
            SpendLedger.Add(usd, string.IsNullOrWhiteSpace(label) ? "media" : label);
        }

        /// <summary>取本次執行累積的媒體生成費用（0 = 無圖片/影片生成）。</summary>
        public (double usd, string label) GetMediaCostUsd() => (_mediaGenerationCostUsd, _mediaGenerationCostLabel);

        // ===== 手機鏡像（§17 階段一）唯讀狀態存取器：供 WorkspaceSnapshot 讀取，不依賴 UI。 =====

        /// <summary>執行狀態鍵：idle / running / success / failed。</summary>
        public string GetRunStatusKey() => _runStatus switch
        {
            NodeRunStatus.Running => "running",
            NodeRunStatus.Success => "success",
            NodeRunStatus.Failed => "failed",
            NodeRunStatus.Waiting => "waiting",
            _ => "idle",
        };

        /// <summary>中文狀態標籤。</summary>
        public string GetRunStatusLabel() => _runStatus switch
        {
            NodeRunStatus.Running => "執行中",
            NodeRunStatus.Success => "成功",
            NodeRunStatus.Failed => "失敗",
            NodeRunStatus.Waiting => "等待中",
            _ => "閒置",
        };

        /// <summary>節點輸入的第一行（當標題），截斷到 maxLen 字。</summary>
        public string GetInputTitle(int maxLen = 48)
        {
            string top = GetTopText() ?? "";
            foreach (var raw in top.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                return line.Length > maxLen ? line.Substring(0, maxLen) + "…" : line;
            }
            return "";
        }

        /// <summary>輸出文字預覽（手機鏡像等顯示用），截斷到 maxLen 字；顯示用途一律清掉 Markdown 記號。</summary>
        public string GetOutputPreview(int maxLen = 240)
        {
            string text = MarkdownDisplayText.Clean(GetBottomText()).Trim();
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "…";
        }

        /// <summary>執行中的進度提示（例如「影片生成中 N%」）；非執行中回空字串。</summary>
        public string GetLoadingHintText() => _loadingExtraHint ?? "";

        public event EventHandler? Moved;
        public event EventHandler? ContentChanged;

        public Guid Id { get; }

        private readonly ObservableCollection<AttachmentVm> _attachments = new();

        private bool _isAttachmentMouseDown = false;
        private bool _isAttachmentDragging = false;
        private Point _attachDragStart;
        private double _attachScrollStartX;
        private const double AttachmentDragThreshold = 4.0;

        private bool _isHoveredVisual = false;

        private TransformGroup? _hoverTransformGroup;
        private ScaleTransform? _hoverScaleTransform;
        private TranslateTransform? _hoverTranslateTransform;
        private DropShadowEffect? _hoverShadowEffect;

        private const double HoverScaleValue = 1.03;
        private const double HoverLiftY = -2.5;
        private const double HoverShadowBlur = 26.0;
        private const double HoverShadowOpacity = 0.26;

        private static readonly Duration HoverEnterDuration = new Duration(TimeSpan.FromMilliseconds(180));
        private static readonly Duration HoverLeaveDuration = new Duration(TimeSpan.FromMilliseconds(220));

        public List<AiExecutionLogEntry> ExecutionLogs { get; } = new();
        private sealed class AttachmentVm
        {
            public string FileName { get; set; } = "";
            public string RelativePath { get; set; } = "";
            public string Kind { get; set; } = "file";
            public string KindGlyph => Kind switch
            {
                "image" => "🖼",
                "pdf"   => "📄",
                "html"  => "🌐",
                _       => "📎"
            };
        }

        // 本次執行產生的可開啟檔案（報告 / 簡報 deck 等），顯示在輸出區下方。
        private readonly ObservableCollection<OutputFileVm> _outputFiles = new();
        // 完整絕對路徑，用於 ClearOutputFiles 時刪除磁碟實體檔案。
        private readonly List<string> _pendingFilePaths = new();
        // 本次執行在輸出區直接顯示的圖片（生成圖片任務），點擊可開啟原圖。
        private string? _outputImagePath;

        private sealed class OutputFileVm
        {
            public string FileName { get; set; } = "";
            public string FullPath { get; set; } = "";
        }

        // §7.2 簡報單張重生：保存本次簡報的大綱 + .pptx 路徑 + 原始請求，供預覽視窗單張重生時用。
        private PresentationDeckContext? _presentationDeck;

        private sealed class PresentationDeckContext
        {
            public PresentationOutlinePayload Outline { get; set; } = new();
            public string PptxPath { get; set; } = "";
            public string UserInput { get; set; } = "";
            public string SourceSummary { get; set; } = "";
        }

        public NodeControl() : this(Guid.NewGuid().ToString()) { }

        private void InitializeHoverVisualObjects()
        {
            if (_hoverScaleTransform == null)
                _hoverScaleTransform = new ScaleTransform(1.0, 1.0);

            if (_hoverTranslateTransform == null)
                _hoverTranslateTransform = new TranslateTransform(0.0, 0.0);

            if (_hoverTransformGroup == null)
            {
                _hoverTransformGroup = new TransformGroup();
                _hoverTransformGroup.Children.Add(_hoverScaleTransform);
                _hoverTransformGroup.Children.Add(_hoverTranslateTransform);
            }

            if (_hoverShadowEffect == null)
            {
                _hoverShadowEffect = new DropShadowEffect
                {
                    BlurRadius = 0,
                    ShadowDepth = 0,
                    Opacity = 0,
                    Color = Color.FromRgb(80, 120, 255)
                };
            }

            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = _hoverTransformGroup;
            Effect = _hoverShadowEffect;
        }

        private void ApplyHoveredVisualState(bool hovered)
        {
            if (_isHoveredVisual == hovered)
                return;

            _isHoveredVisual = hovered;

            InitializeHoverVisualObjects();

            if (_parent != null && hovered)
                Panel.SetZIndex(this, _parent.GetNextZIndex());

            double targetScale = hovered ? HoverScaleValue : 1.0;
            double targetLiftY = hovered ? HoverLiftY : 0.0;
            double targetBlur = hovered ? HoverShadowBlur : 0.0;
            double targetShadowOpacity = hovered ? HoverShadowOpacity : 0.0;

            var duration = hovered ? HoverEnterDuration : HoverLeaveDuration;

            IEasingFunction easing = hovered
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var scaleXAnim = new DoubleAnimation
            {
                To = targetScale,
                Duration = duration,
                EasingFunction = easing
            };

            var scaleYAnim = new DoubleAnimation
            {
                To = targetScale,
                Duration = duration,
                EasingFunction = easing
            };

            var liftAnim = new DoubleAnimation
            {
                To = targetLiftY,
                Duration = duration,
                EasingFunction = easing
            };

            var blurAnim = new DoubleAnimation
            {
                To = targetBlur,
                Duration = duration,
                EasingFunction = easing
            };

            var shadowOpacityAnim = new DoubleAnimation
            {
                To = targetShadowOpacity,
                Duration = duration,
                EasingFunction = easing
            };

            // 動畫結束後再刷新一次曲線，避免端點停在舊位置
            scaleYAnim.Completed += (_, __) => NotifyConnectionLayoutChanged();
            liftAnim.Completed += (_, __) => NotifyConnectionLayoutChanged();

            _hoverScaleTransform!.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                scaleXAnim,
                HandoffBehavior.SnapshotAndReplace);

            _hoverScaleTransform.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                scaleYAnim,
                HandoffBehavior.SnapshotAndReplace);

            _hoverTranslateTransform!.BeginAnimation(
                TranslateTransform.YProperty,
                liftAnim,
                HandoffBehavior.SnapshotAndReplace);

            _hoverShadowEffect!.BeginAnimation(
                DropShadowEffect.BlurRadiusProperty,
                blurAnim,
                HandoffBehavior.SnapshotAndReplace);

            _hoverShadowEffect.BeginAnimation(
                DropShadowEffect.OpacityProperty,
                shadowOpacityAnim,
                HandoffBehavior.SnapshotAndReplace);

            // 先即時刷新一次，讓拖曳中看起來也比較同步
            NotifyConnectionLayoutChanged();
        }

        private void ResetHoverVisualStateImmediately()
        {
            InitializeHoverVisualObjects();

            _hoverScaleTransform!.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _hoverScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _hoverTranslateTransform!.BeginAnimation(TranslateTransform.YProperty, null);
            _hoverShadowEffect!.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            _hoverShadowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, null);

            _hoverScaleTransform.ScaleX = 1.0;
            _hoverScaleTransform.ScaleY = 1.0;
            _hoverTranslateTransform.Y = 0.0;
            _hoverShadowEffect.BlurRadius = 0.0;
            _hoverShadowEffect.Opacity = 0.0;

            _isHoveredVisual = false;

            NotifyConnectionLayoutChanged();
        }

        public Point GetThumbCenterIgnoringHoverTransform(string thumbName)
        {
            if (string.IsNullOrWhiteSpace(thumbName))
                return new Point(
                    Canvas.GetLeft(this) + Width / 2.0,
                    Canvas.GetTop(this) + Height / 2.0);

            if (FindName(thumbName) is not FrameworkElement thumb)
            {
                return new Point(
                    Canvas.GetLeft(this) + Width / 2.0,
                    Canvas.GetTop(this) + Height / 2.0);
            }

            // 這裡是關鍵：
            // 只把 thumb 的中心點換算到 NodeControl 自己的座標系
            // 不往 MainCanvas 直接轉，避免把 NodeControl 的 RenderTransform 算進去
            Point localCenter = thumb.TranslatePoint(
                new Point(thumb.ActualWidth / 2.0, thumb.ActualHeight / 2.0),
                this);

            double nodeLeft = Canvas.GetLeft(this);
            double nodeTop = Canvas.GetTop(this);

            if (double.IsNaN(nodeLeft)) nodeLeft = 0;
            if (double.IsNaN(nodeTop)) nodeTop = 0;

            return new Point(
                nodeLeft + localCenter.X,
                nodeTop + localCenter.Y);
        }
        private void NodeControl_MouseEnter(object sender, MouseEventArgs e)
        {
            ApplyHoveredVisualState(true);
            _parent?.NotifyNodeHoverEntered(this);
        }

        private void NodeControl_MouseLeave(object sender, MouseEventArgs e)
        {
            ApplyHoveredVisualState(false);
            _parent?.NotifyNodeHoverLeft(this);
        }

        public NodeControl(string idString)
        {
            InitializeComponent();

            LostMouseCapture += (_, __) =>
            {
                _isDraggingNode = false;
            };

            AttachmentItems.ItemsSource = _attachments;
            OutputFileItems.ItemsSource = _outputFiles;

            Loaded += (s, e) =>
            {
                _parent = Window.GetWindow(this) as MainWindow;

                InitializeHoverVisualObjects();

                EnsureModelSelectorLoaded();
                ApplyFontSize(_fontSize);
                RefreshAttachmentsUI();

                InitializeCommittedModelIfNeeded();
                RefreshModelSelectionUI();

                UpdateAutoTaskPreview();
                UpdateEditButtons();
            };

            if (!Guid.TryParse(idString, out var gid))
                gid = Guid.NewGuid();
            Id = gid;

            TopEditor.LostKeyboardFocus -= TopEditor_LostKeyboardFocus;
            TopEditor.KeyDown -= TopEditor_KeyDown;

            TopEditor.TextChanged += TopEditor_TextChanged;
            TopEditor.PreviewMouseLeftButtonDown += TopEditor_PreviewMouseLeftButtonDown;
            MouseEnter += NodeControl_MouseEnter;
            MouseLeave += NodeControl_MouseLeave;
            Unloaded += (_, __) => ResetHoverVisualStateImmediately();
        }

        public bool IsEditing => TopEditor != null && TopEditor.IsReadOnly == false;

        // ===== 新增：對外可取目前已提交模型 =====
        public string GetCommittedModelId()
            => AiModelHelper.NormalizeNodeModel(_committedModelId);

        // ===== 新增：外部可設定已提交模型（例如載入專案時）=====
        public void SetCommittedModelId(string modelId, bool syncEditingModel = true)
        {
            string normalized = NormalizeSafeModelId(modelId);

            _committedModelId = normalized;
            if (syncEditingModel)
                _editingModelId = normalized;

            RefreshModelSelectionUI();
        }

        internal void EnterEditMode()
        {
            _isTopLocked = false;

            InitializeCommittedModelIfNeeded();

            // 進入編輯時，草稿模型 = 上次正式送出的模型
            _editingModelId = _committedModelId;

            TopEditor.IsReadOnly = false;
            TopEditor.Focus();
            TopEditor.CaretIndex = TopEditor.Text?.Length ?? 0;

            EnsureModelSelectorLoaded();
            RefreshModelSelectionUI();
            UpdateAutoTaskPreview();
            UpdateEditButtons();
        }

        internal void ForceExitEditMode()
        {
            RevertEditingModelToCommitted();

            TopEditor.IsReadOnly = true;
            RefreshModelSelectionUI();
            UpdateAutoTaskPreview();
            UpdateEditButtons();

            if (!IsMouseOver)
                ResetHoverVisualStateImmediately();
        }

        internal void EndEditBecauseSent()
        {
            TopEditor.IsReadOnly = true;
            RefreshModelSelectionUI();
            UpdateAutoTaskPreview();
            UpdateEditButtons();
            _parent?.NotifyEditEnded(this);
        }

        private void EnsureModelSelectorLoaded()
        {
            if (_modelsLoaded || ModelSelector == null)
                return;

            LoadModelsFromRegistry();
            _modelsLoaded = true;
        }

        private void NotifyConnectionLayoutChanged()
        {
            Moved?.Invoke(this, EventArgs.Empty);
        }

        private void LoadModelsFromRegistry()
        {
            if (ModelSelector == null)
                return;

            string currentSelectedId = GetSelectedModelIdFromComboBox();

            _isSyncingModelSelector = true;
            try
            {
                ModelSelector.ItemsSource = null;
                // Multi-Model v1：只列已啟用模型（休眠擴充點如 Gemini 不顯示）。
                ModelSelector.ItemsSource = AiModelRegistry.Available;
            }
            finally
            {
                _isSyncingModelSelector = false;
            }

            SelectModelInComboBox(currentSelectedId);
        }

        private string GetSelectedModelIdFromComboBox()
        {
            if (ModelSelector?.SelectedItem is AiModelDefinition model &&
                !string.IsNullOrWhiteSpace(model.Id))
            {
                return model.Id.Trim();
            }

            return AiModels.DefaultNodeModel;
        }

        private void SelectModelInComboBox(string modelId)
        {
            if (ModelSelector == null)
                return;

            modelId = NormalizeSafeModelId(modelId);

            _isSyncingModelSelector = true;
            try
            {
                var available = AiModelRegistry.Available;

                var match = available.FirstOrDefault(x =>
                    string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    ModelSelector.SelectedItem = match;
                    return;
                }

                if (available.Count > 0)
                    ModelSelector.SelectedItem = available[0];
            }
            finally
            {
                _isSyncingModelSelector = false;
            }
        }

        // ===== 新增：安全正規化 =====
        private static string NormalizeSafeModelId(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return AiModels.DefaultNodeModel;

            return AiModelHelper.NormalizeNodeModel(modelId);
        }

        // ===== 新增：初始化正式模型 =====
        private void InitializeCommittedModelIfNeeded()
        {
            if (_parent == null)
            {
                _committedModelId = NormalizeSafeModelId(_committedModelId);
                _editingModelId = NormalizeSafeModelId(_editingModelId);
                return;
            }

            bool committedMissing = string.IsNullOrWhiteSpace(_committedModelId);
            bool editingMissing = string.IsNullOrWhiteSpace(_editingModelId);

            if (committedMissing)
            {
                string stored = _parent.GetNodeSelectedModel(this);
                _committedModelId = NormalizeSafeModelId(stored);
            }
            else
            {
                _committedModelId = NormalizeSafeModelId(_committedModelId);
            }

            if (editingMissing)
            {
                _editingModelId = _committedModelId;
            }
            else
            {
                _editingModelId = NormalizeSafeModelId(_editingModelId);
            }
        }

        // ===== 新增：未送出離開編輯時回復 =====
        private void RevertEditingModelToCommitted()
        {
            _editingModelId = _committedModelId;
        }

        // ===== 新增：真正送出時提交模型 =====
        private void CommitEditingModel()
        {
            _committedModelId = NormalizeSafeModelId(_editingModelId);

            if (_parent != null)
            {
                _parent.SetNodeSelectedModel(this, _committedModelId);
            }
        }

        internal void RefreshModelSelectionUI()
        {
            EnsureModelSelectorLoaded();
            InitializeCommittedModelIfNeeded();

            if (_parent != null && !_parent.IsAutoModelSelectionEnabled() && IsEditing)
                RevertEditingModelToCommitted();

            // 編輯狀態顯示草稿模型；非編輯狀態顯示正式模型
            string displayModel = IsEditing ? _editingModelId : _committedModelId;
            SelectModelInComboBox(displayModel);

            UpdateEditButtons();
        }

        private void UpdateEditButtons()
        {
            bool editable = (TopEditor != null && TopEditor.IsReadOnly == false);

            if (PlusButton != null)
            {
                PlusButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
                PlusButton.IsEnabled = editable;
            }

            if (SendButton != null)
            {
                SendButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
                SendButton.IsEnabled = editable && !_isGenerating;
            }

            if (ModelSelector != null)
            {
                ModelSelector.Visibility = Visibility.Visible;

                bool canManualSelect =
                    _parent != null &&
                    _parent.CanUserManuallySelectModel();

                ModelSelector.IsEnabled = editable && !_isGenerating && canManualSelect;

                // §13 Manual/Auto 提示：清楚說明此節點的模型是「自己選的」還是「系統自動挑的」。
                ModelSelector.ToolTip = canManualSelect
                    ? "🛠 手動模式：可自行選擇此區塊使用的模型"
                    : "🤖 自動模式：系統依內容自動挑模型（要手動選請到個人化關閉自動）";
            }

            RefreshAttachmentsUI();
        }

        private void SyncModelSelectorFromParent()
        {
            EnsureModelSelectorLoaded();

            if (_parent == null || ModelSelector == null)
                return;

            // 自動模式下：
            // 編輯狀態顯示依當前文字推算的暫時模型，但不覆蓋 committed
            // 非編輯狀態永遠顯示 committed model
            if (IsEditing)
            {
                string model = _parent.GetEffectiveNodeModel(this, TopEditor?.Text ?? "");
                _editingModelId = NormalizeSafeModelId(model);
                SelectModelInComboBox(_editingModelId);
            }
            else
            {
                SelectModelInComboBox(_committedModelId);
            }
        }

        private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingModelSelector) return;
            if (_parent == null) return;
            if (ModelSelector.SelectedItem is not AiModelDefinition model) return;
            if (string.IsNullOrWhiteSpace(model.Id)) return;

            string selectedId = NormalizeSafeModelId(model.Id);

            if (!_parent.CanUserManuallySelectModel())
            {
                // 自動模式下不允許手動改正式值，直接回到目前應顯示的值
                SyncModelSelectorFromParent();
                return;
            }

            // 只改編輯草稿模型，不改正式模型
            if (IsEditing)
            {
                _editingModelId = selectedId;
            }
            else
            {
                // 非編輯狀態通常不應該改，但保險起見仍維持正式值
                SelectModelInComboBox(_committedModelId);
                return;
            }
        }

        public bool GetTopLocked() => _isTopLocked;

        public void SetTopLocked(bool locked)
        {
            _isTopLocked = locked;

            if (locked && !IsEditing)
            {
                RevertEditingModelToCommitted();
            }

            TopEditor.IsReadOnly = true;
            RefreshModelSelectionUI();
            UpdateAutoTaskPreview();
            UpdateEditButtons();
        }

        public double GetFontSize() => _fontSize;

        public void SetFontSize(double size)
        {
            if (size < 5) size = 5;
            if (size > 200) size = 200;

            _fontSize = size;
            ApplyFontSize(_fontSize);
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyFontSize(double size)
        {
            if (TopEditor != null) TopEditor.FontSize = size;
            if (BottomDisplay != null) BottomDisplay.FontSize = size;
            if (BottomRich != null)
            {
                BottomRich.FontSize = size;
                // 渲染中：標題字級是相對倍率算的，重建 Document 才會一起縮放。
                if (BottomRich.Visibility == Visibility.Visible && _bottomRawText != null)
                    ShowRenderedBottom(_bottomRawText);
            }
        }

        private void UpdateAutoTaskPreview()
        {
            NodeTaskMode mode;

            var raw = TopEditor?.Text ?? "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                mode = _parent?.GetNodeTaskMode(this) ?? NodeTaskMode.Chat;
            }
            else
            {
                mode = ResolvePreviewTaskMode(raw);
            }

            // 保留 task preview 計算邏輯，但不再顯示在 UI 上
            _ = mode;
        }
        private static NodeTaskMode ResolvePreviewTaskMode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return NodeTaskMode.Chat;

            string raw = text.Trim();
            string normalized = raw.ToLowerInvariant();

            if (ContainsAny(raw, normalized,
                "翻譯", "譯成", "翻成", "中文", "英文", "日文", "韓文", "對照", "中英對照",
                "完整中文菜單", "translate", "translation", "menu translation", "traditional chinese", "繁體中文"))
            {
                return NodeTaskMode.Translate;
            }

            if (ContainsAny(raw, normalized,
                "程式", "程式碼", "code", "bug", "錯誤", "修正", "debug", "exception", "class", "method",
                "c#", "xaml", ".net", "wpf", "visual studio", "compile", "build", "namespace",
                "完整程式", "完整程式碼", "可直接貼上", "貼上即用"))
            {
                return NodeTaskMode.Code;
            }

            if (ContainsAny(raw, normalized,
                "查詢", "搜尋", "查證", "最新", "最近", "新聞", "資料來源", "來源", "比較", "分析",
                "research", "search", "latest", "news", "current", "today", "compare", "source", "citation"))
            {
                return NodeTaskMode.Research;
            }

            if (ContainsAny(raw, normalized,
                "摘要", "總結", "整理重點", "重點整理", "濃縮", "簡述", "懶人包",
                "summarize", "summary", "key points", "tldr"))
            {
                return NodeTaskMode.Summarize;
            }

            if (ContainsAny(raw, normalized,
                "改寫", "重寫", "潤稿", "修飾", "順一下", "口語化", "正式一點", "換個說法",
                "rewrite", "rephrase", "polish", "refine"))
            {
                return NodeTaskMode.Rewrite;
            }

            if (ContainsAny(raw, normalized,
                "擷取", "抽取", "提取", "整理成表格", "欄位", "抓出", "抽出", "列出所有",
                "extract", "parse", "fields", "structured data"))
            {
                return NodeTaskMode.Extract;
            }

            return NodeTaskMode.Chat;
        }

        private static bool ContainsAny(string raw, string normalized, params string[] keywords)
        {
            foreach (var keyword in keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                    continue;

                var k = keyword.Trim();
                if (raw.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains(k.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetTaskModeDisplayName(NodeTaskMode mode)
        {
            return mode switch
            {
                NodeTaskMode.Research => "Research",
                NodeTaskMode.Translate => "Translate",
                NodeTaskMode.Summarize => "Summarize",
                NodeTaskMode.Rewrite => "Rewrite",
                NodeTaskMode.Extract => "Extract",
                NodeTaskMode.Code => "Code",
                _ => "Chat"
            };
        }

        internal void RefreshAttachmentsUI()
        {
            if (_parent == null)
            {
                _attachments.Clear();

                if (AttachmentListHost != null)
                    AttachmentListHost.Visibility = Visibility.Collapsed;

                if (ModelSelector != null &&
                    AttachmentColumn != null &&
                    AttachmentSplitterColumn != null &&
                    ModelColumn != null)
                {
                    AttachmentColumn.Width = new GridLength(0);
                    AttachmentSplitterColumn.Width = new GridLength(0);
                    ModelColumn.Width = new GridLength(1, GridUnitType.Star);

                    Grid.SetColumn(ModelSelector, 0);
                    Grid.SetColumnSpan(ModelSelector, 3);
                }

                return;
            }

            var list = _parent.GetAttachmentsForNode(this);

            _attachments.Clear();
            foreach (var a in list)
            {
                _attachments.Add(new AttachmentVm
                {
                    FileName = a.FileName,
                    RelativePath = a.RelativePath,
                    Kind = a.Kind
                });
            }

            if (AttachmentListHost != null &&
                ModelSelector != null &&
                AttachmentColumn != null &&
                AttachmentSplitterColumn != null &&
                ModelColumn != null)
            {
                bool hasAttachments = _attachments.Count > 0;

                if (hasAttachments)
                {
                    AttachmentListHost.Visibility = Visibility.Visible;

                    AttachmentColumn.Width = new GridLength(68, GridUnitType.Star);
                    AttachmentSplitterColumn.Width = new GridLength(2);
                    ModelColumn.Width = new GridLength(32, GridUnitType.Star);

                    Grid.SetColumn(ModelSelector, 2);
                    Grid.SetColumnSpan(ModelSelector, 1);
                }
                else
                {
                    AttachmentListHost.Visibility = Visibility.Collapsed;

                    AttachmentColumn.Width = new GridLength(0);
                    AttachmentSplitterColumn.Width = new GridLength(0);
                    ModelColumn.Width = new GridLength(1, GridUnitType.Star);

                    Grid.SetColumn(ModelSelector, 0);
                    Grid.SetColumnSpan(ModelSelector, 3);
                }
            }

            if (AttachmentScroll != null)
                AttachmentScroll.ScrollToHorizontalOffset(0);
        }

        private void AttachmentItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent == null) return;
            if (_isAttachmentDragging) return;

            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement fe && fe.Tag is AttachmentVm vm)
                {
                    _parent.OpenAttachment(vm.RelativePath);
                    e.Handled = true;
                }
            }
        }

        private void OutputFileItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent == null) return;

            if (sender is FrameworkElement fe && fe.Tag is OutputFileVm vm)
            {
                _parent.OpenPreview(vm.FullPath, this);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 清掉輸出檔案連結，並刪除磁碟上對應的實體檔案。
        /// 重跑 / 清除輸出文字 / 刪除節點時呼叫。可從背景執行緒呼叫。
        /// </summary>
        public void ClearOutputFiles()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearOutputFiles);
                return;
            }

            foreach (var path in _pendingFilePaths)
            {
                try
                {
                    if (System.IO.File.Exists(path))
                        System.IO.File.Delete(path);
                }
                catch (Exception ex) { AppLog.Warn("Node", $"暫存檔清理失敗（磁碟會殘檔）：{path}", ex); }
            }
            _pendingFilePaths.Clear();

            _outputFiles.Clear();
            if (OutputFileHost != null)
                OutputFileHost.Visibility = Visibility.Collapsed;

            _outputImagePath = null;
            if (OutputImage != null)
                OutputImage.Source = null;
            if (OutputImageHost != null)
                OutputImageHost.Visibility = Visibility.Collapsed;
        }

        /// <summary>設定本次執行產生、可在輸出區點擊開啟的檔案。可從背景執行緒呼叫。</summary>
        public void SetOutputFiles(IReadOnlyList<GeneratedFilePayload> files)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetOutputFiles(files));
                return;
            }

            _outputFiles.Clear();
            _pendingFilePaths.Clear();

            if (files != null)
            {
                foreach (var f in files)
                {
                    if (f == null || !f.Success || string.IsNullOrWhiteSpace(f.FilePath))
                        continue;

                    _outputFiles.Add(new OutputFileVm
                    {
                        FileName = f.FileName,
                        FullPath = f.FilePath
                    });
                    _pendingFilePaths.Add(f.FilePath);
                }
            }

            if (OutputFileHost != null)
                OutputFileHost.Visibility = _outputFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===== §7.2 簡報單張重生（重生 UI 在預覽視窗，這裡只保存重生需要的脈絡） =====

        /// <summary>本次簡報產生後，把大綱 + .pptx 路徑 + 原始請求存起來，供預覽視窗單張重生時用。可從背景執行緒呼叫。</summary>
        public void SetPresentationDeck(
            PresentationOutlinePayload outline, string pptxPath, string userInput, string sourceSummary)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetPresentationDeck(outline, pptxPath, userInput, sourceSummary));
                return;
            }

            if (outline?.Slides == null || outline.Slides.Count == 0)
            {
                ClearPresentationDeck();
                return;
            }

            _presentationDeck = new PresentationDeckContext
            {
                Outline = outline,
                PptxPath = pptxPath ?? "",
                UserInput = userInput ?? "",
                SourceSummary = sourceSummary ?? ""
            };
        }

        public void ClearPresentationDeck()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearPresentationDeck);
                return;
            }

            _presentationDeck = null;
        }

        public PresentationOutlinePayload? GetPresentationOutline() => _presentationDeck?.Outline;
        public string GetPresentationUserInput() => _presentationDeck?.UserInput ?? "";
        public string GetPresentationPptxPath() => _presentationDeck?.PptxPath ?? "";
        public string GetPresentationSourceSummary() => _presentationDeck?.SourceSummary ?? "";

        /// <summary>預覽視窗單張重生成功、覆蓋了同一個 .pptx 後，更新節點保存的大綱（路徑不變）。</summary>
        public void UpdatePresentationOutline(PresentationOutlinePayload updatedOutline)
        {
            if (_presentationDeck != null && updatedOutline != null)
                _presentationDeck.Outline = updatedOutline;
        }

        /// <summary>取得目前輸出區的可開啟檔案完整路徑，用於存檔持久化。</summary>
        public IReadOnlyList<string> GetOutputFilePaths() => _pendingFilePaths.ToList();

        /// <summary>取得目前輸出區 inline 顯示的圖片路徑，用於存檔持久化。</summary>
        public string? GetOutputImagePath() => _outputImagePath;

        /// <summary>從存檔還原輸出區檔案 chip（路徑指向的檔案需仍存在）。可從背景執行緒呼叫。</summary>
        public void RestoreOutputFiles(IEnumerable<string>? paths)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => RestoreOutputFiles(paths));
                return;
            }

            _outputFiles.Clear();
            _pendingFilePaths.Clear();

            if (paths != null)
            {
                foreach (var p in paths)
                {
                    if (string.IsNullOrWhiteSpace(p) || !System.IO.File.Exists(p))
                        continue;

                    _outputFiles.Add(new OutputFileVm
                    {
                        FileName = System.IO.Path.GetFileName(p),
                        FullPath = p
                    });
                    _pendingFilePaths.Add(p);
                }
            }

            if (OutputFileHost != null)
                OutputFileHost.Visibility = _outputFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>設定本次執行產生、要在輸出區直接顯示的圖片（生成圖片任務）。可從背景執行緒呼叫。</summary>
        public void SetOutputImage(string? path)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetOutputImage(path));
                return;
            }

            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                _outputImagePath = null;
                if (OutputImage != null)
                    OutputImage.Source = null;
                if (OutputImageHost != null)
                    OutputImageHost.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                // OnLoad：完整讀進記憶體，避免鎖住磁碟檔案（之後仍可刪除 / 開啟）。
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();

                if (OutputImage != null)
                    OutputImage.Source = bmp;
                _outputImagePath = path;
                if (OutputImageHost != null)
                    OutputImageHost.Visibility = Visibility.Visible;
            }
            catch
            {
                _outputImagePath = null;
                if (OutputImage != null)
                    OutputImage.Source = null;
                if (OutputImageHost != null)
                    OutputImageHost.Visibility = Visibility.Collapsed;
            }
        }

        private void OutputImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent != null && !string.IsNullOrWhiteSpace(_outputImagePath))
            {
                _parent.OpenPreview(_outputImagePath);
                e.Handled = true;
            }
        }

        // 輸出區（文字+圖片+檔案）統一捲動：唯讀 TextBox 會吃掉滾輪事件，這裡在 tunnel 階段先攔下來捲外層。
        private void OutputScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is System.Windows.Controls.ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void AttachmentDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;

            if (sender is Button btn && btn.Tag is AttachmentVm vm)
            {
                bool ok = MenuConfirmDialog.ShowDeleteConfirm(
                    owner: _parent,
                    title: "刪除確認",
                    message: $"確定要刪除附件？\n{vm.FileName}",
                    resourceHost: _parent);

                if (!ok) return;

                _parent.RemoveAttachment(this, vm.RelativePath);
                RefreshAttachmentsUI();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void AttachmentScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (AttachmentScroll == null) return;

            _isAttachmentMouseDown = true;
            _isAttachmentDragging = false;
            _attachDragStart = e.GetPosition(AttachmentScroll);
            _attachScrollStartX = AttachmentScroll.HorizontalOffset;
        }

        private void AttachmentScroll_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isAttachmentMouseDown) return;
            if (AttachmentScroll == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var cur = e.GetPosition(AttachmentScroll);
            var dx = cur.X - _attachDragStart.X;

            if (!_isAttachmentDragging)
            {
                if (Math.Abs(dx) < AttachmentDragThreshold) return;

                _isAttachmentDragging = true;
                AttachmentScroll.CaptureMouse();
                e.Handled = true;
            }

            AttachmentScroll.ScrollToHorizontalOffset(_attachScrollStartX - dx);
            e.Handled = true;
        }

        private void AttachmentScroll_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (AttachmentScroll == null) return;

            _isAttachmentMouseDown = false;

            if (_isAttachmentDragging)
            {
                _isAttachmentDragging = false;
                AttachmentScroll.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;
            bool busy = _isGenerating || _parent.IsWorkflowChainRunning;
            DeleteMenuItem.IsEnabled = !_parent.IsInitialNode(this);
            EditMenuItem.IsEnabled = true;
            FontSizeMenuItem.IsEnabled = true;
            // 不可用時直接隱藏（與 ⚡展開 / ⏭略過 / ⏹停止 一致），避免「灰著佔位」。
            // 一鍵執行下游:本節點要「已執行成功(有產出)」且「有自動狀態(流動模式)的下游連線」才顯示。
            // 沿流動邊扇出、不重跑本節點;自動流僅限 FlowMode 連線(GetFlowDownstreamNodes 已過濾)。
            bool canRunChain = !busy
                && !string.IsNullOrWhiteSpace(GetBottomText())
                && _parent.NodeHasFlowDownstream(this);
            RunWorkflowMenuItem.IsEnabled = canRunChain;
            RunWorkflowMenuItem.Visibility = canRunChain ? Visibility.Visible : Visibility.Collapsed;

            // §4：只有「可拆成多階段工作流」的節點才啟用一鍵展開（一般對話不顯示啟用）。
            bool canExpand = !busy &&
                             !string.IsNullOrWhiteSpace(GetTopText()) &&
                             _parent.NodeCanExpandToWorkflow(this);
            ExpandWorkflowMenuItem.IsEnabled = canExpand;
            ExpandWorkflowMenuItem.Visibility = canExpand
                ? Visibility.Visible
                : Visibility.Collapsed;

            // §4 stop：只有「鏈正在跑」時才顯示「停止工作流」。
            bool chainRunning = _parent.IsWorkflowChainRunning;
            StopWorkflowMenuItem.IsEnabled = chainRunning;
            StopWorkflowMenuItem.Visibility = chainRunning ? Visibility.Visible : Visibility.Collapsed;

            // §4 skip：鏈未在跑、此節點未在生成、且有下游節點時，才提供「略過此步」。
            bool canSkip = !chainRunning && !_isGenerating && _parent.NodeHasDownstream(this);
            SkipStepMenuItem.IsEnabled = canSkip;
            SkipStepMenuItem.Visibility = canSkip ? Visibility.Visible : Visibility.Collapsed;

            // 加入記憶：只要此節點有產出內容就能手動標記為重要記憶。
            bool canRemember = !busy && !string.IsNullOrWhiteSpace(GetBottomText());
            AddToMemoryMenuItem.IsEnabled = canRemember;
            AddToMemoryMenuItem.Visibility = canRemember ? Visibility.Visible : Visibility.Collapsed;

            // 重新生成答案：節點已有輸出且不在忙碌時才提供（沿用既有 research/分析成果，只重生最終答案）。
            // 原本是輸出區右下角的按鈕，已移到此右鍵選單。
            bool canRegenerate = !busy && !string.IsNullOrWhiteSpace(GetBottomText());
            RegenerateMenuItem.IsEnabled = canRegenerate;
            RegenerateMenuItem.Visibility = canRegenerate ? Visibility.Visible : Visibility.Collapsed;

            // 變更上游：只有「有上游連線」且非初始節點、鏈未在跑時才提供。
            bool canRewire = !busy && !_parent.IsInitialNode(this) && _parent.GetFirstUpstreamNode(this) != null;
            ChangeUpstreamMenuItem.IsEnabled = canRewire;
            ChangeUpstreamMenuItem.Visibility = canRewire ? Visibility.Visible : Visibility.Collapsed;

            // 更換連接方向：只要此節點有任何連線相連即可（純視覺互換接孔，不影響上下游/記憶）。
            bool canSwapSide = !busy && _parent.NodeHasAnyConnection(this);
            SwapConnectionSideMenuItem.IsEnabled = canSwapSide;
            SwapConnectionSideMenuItem.Visibility = canSwapSide ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddToMemoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.AddNodeToMemory(this);
        }

        private void ChangeUpstreamMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.BeginRewireUpstream(this);
        }

        private void SwapConnectionSideMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.SwapNodeConnectionSide(this);
        }

        private void EditMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.RequestBeginEdit(this, MainWindow.EditReason.UserEdit);
        }

        private void FontSizeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FontSizeSliderDialog(
                owner: Window.GetWindow(this),
                title: "調整字體大小",
                initialValue: _fontSize,
                min: 5,
                max: 200,
                onPreviewValueChanged: v => ApplyFontSize(v)
            );

            bool? ok = dlg.ShowDialog();
            if (ok == true) SetFontSize(dlg.SelectedValue);
            else ApplyFontSize(_fontSize);
        }

        private async void ExpandWorkflowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null || _isGenerating)
                return;

            await _parent.ExpandAndRunDownstreamWorkflowAsync(this);
        }

        private async void RunWorkflowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null || _isGenerating)
                return;

            // #4：一鍵執行下游——本節點已執行成功(有產出),不重跑,直接沿「流動模式」邊扇出跑各自動下游。
            await _parent.RunFlowWorkflowAsync(this, runStartNode: false);
        }

        // §4 stop：停止整條正在跑的工作流鏈。
        private void StopWorkflowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.StopWorkflowChain();
        }

        // §4 skip：略過此步，從下一步繼續整條鏈。
        private async void SkipStepMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null || _isGenerating)
                return;

            await _parent.SkipStepAndContinueAsync(this);
        }

        // §4 skip：把此節點標成「已略過」（琥珀框 + 狀態列），但保留 bottom 文字作為下游 passthrough。
        public void MarkChainStepSkipped()
        {
            _runStatus = NodeRunStatus.Idle;
            StopStatusRevertTimer();

            var amber = new SolidColorBrush(Color.FromRgb(0xB9, 0x7B, 0x16));
            if (NodeBorder != null)
                NodeBorder.BorderBrush = amber;
            if (StatusText != null)
            {
                StatusText.Text = "已略過此步（沿用上一步結果）";
                StatusText.Foreground = amber;
            }
            if (StatusFooter != null) StatusFooter.Visibility = Visibility.Visible;
        }

        private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;
            if (_parent.IsInitialNode(this)) return;

            if (_parent.HasOutgoingConnections(this))
            {
                var ok = MenuConfirmDialog.ShowDeleteConfirm(
                    owner: _parent,
                    title: "確認刪除",
                    message: "當前區塊不是末端區塊，是否確認刪除？",
                    resourceHost: _parent);

                if (!ok) return;
            }

            _parent.DeleteNodeAndDescendants(this);
        }

        private void TopEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TopEditor.Text))
                _isTopLocked = false;

            UpdateAutoTaskPreview();

            if (_parent != null &&
    _parent.IsAutoModelSelectionEnabled() &&
    !string.IsNullOrWhiteSpace(TopEditor.Text))
            {
                if (IsEditing)
                {
                    string autoModel = _parent.GetEffectiveNodeModel(this, TopEditor.Text);
                    _editingModelId = NormalizeSafeModelId(autoModel);
                }

                RefreshModelSelectionUI();
            }

            if (_parent != null && IsEditing)
            {
                _parent.SyncAutoFlowTemplate(this, TopEditor.Text ?? "");
            }

            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void TopEditor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsEditing)
            {
                e.Handled = true;
            }
        }

        private void TopEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { }
        private void TopEditor_KeyDown(object sender, KeyEventArgs e) { }

        private void PlusButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsEditing) return;

            if (_parent == null)
            {
                MenuConfirmDialog.ShowMessage(Application.Current.MainWindow, "錯誤", "（找不到 MainWindow，無法上傳）", Application.Current.MainWindow);
                return;
            }

            // §18：已連結 Google Drive 時提供雙來源選單；未連結行為與以前完全相同（直開本機選檔）。
            if (_parent.IsGoogleDriveLinked() && sender is FrameworkElement anchorEl)
            {
                var menu = new ContextMenu();

                var local = new MenuItem { Header = "📁 本機檔案…" };
                local.Click += (_, __) => PickLocalAttachments();
                menu.Items.Add(local);

                var drive = new MenuItem { Header = "☁️ Google Drive…" };
                drive.Click += async (_, __) =>
                {
                    if (await _parent.AttachFromDriveAsync(this))
                    {
                        RefreshAttachmentsUI();
                        ContentChanged?.Invoke(this, EventArgs.Empty);
                    }
                };
                menu.Items.Add(drive);

                menu.PlacementTarget = anchorEl;
                menu.IsOpen = true;
                return;
            }

            PickLocalAttachments();
        }

        private void PickLocalAttachments()
        {
            var dlg = new OpenFileDialog
            {
                Title = "選擇要上傳給 AI 的檔案/照片",
                Multiselect = true,
                Filter =
                    "所有檔案 (*.*)|*.*|" +
                    "圖片 (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|" +
                    "文件 (*.pdf;*.txt;*.md;*.csv;*.json)|*.pdf;*.txt;*.md;*.csv;*.json"
            };

            if (dlg.ShowDialog(Window.GetWindow(this)) == true && _parent != null)
            {
                _parent.AddAttachmentsForNode(this, dlg.FileNames);
                RefreshAttachmentsUI();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsEditing) return;
            if (_isGenerating) return;

            var top = TopEditor.Text ?? "";
            if (string.IsNullOrWhiteSpace(top)) return;

            string runTop = BuildPromptForCurrentRun(top);

            // 送出前，將目前草稿模型正式提交為本次送出模型
            // 自動模式下取當前文字實際推算出的模型；手動模式下取 editingModel
            if (_parent != null && _parent.IsAutoModelSelectionEnabled())
            {
                _editingModelId = NormalizeSafeModelId(_parent.GetEffectiveNodeModel(this, runTop));
            }

            CommitEditingModel();

            _isTopLocked = true;
            EndEditBecauseSent();

            ContentChanged?.Invoke(this, EventArgs.Empty);
            _parent?.NotifyNodeSubmitted(this);

            // 藍虛線下游：送出「當下」就把整條執行路徑標成等待中（琥珀框＋手機顯示），
            // 不等母節點跑完——RunFlowWorkflowAsync 開頭那次標記對手動送出來說太晚了。
            bool hasFlowDownstream = _parent != null && _parent.NodeHasFlowDownstream(this);
            if (hasFlowDownstream)
                _parent!.MarkFlowSubtreeWaiting(this, includeRoot: false);

            await GenerateBottomReplyFromTopAsync(runTop);

            if (_parent != null && RunProducedUsableOutput())
            {
                // 藍色虛線（流動模式）下游：上游一完成就自動沿線往下跑（下游各自仍會檢查自己輸入端是否有內容，
                // 空的節點會被 RunCurrentTopTextAsync 略過）。這是明確畫的執行路徑，不受「自動展開」個人化開關影響。
                if (hasFlowDownstream)
                    await _parent.RunFlowWorkflowAsync(this, runStartNode: false);
                // 否則維持 §4 Mode 2（完全自動）：多階段任務且策略為 FullyAuto 時，AI 自動展開下游並依序執行。
                else
                    await _parent.MaybeAutoExpandAfterSubmitAsync(this);
            }

            // 兜底：母節點失敗、或工作流鏈忙碌被擋而沒真的往下跑時，把仍停在等待中的下游復位
            //（只動 Waiting 狀態，成功路徑上已由 RunFlowWorkflowAsync 的 finally 清過，重複呼叫無害）。
            if (hasFlowDownstream)
                _parent!.ClearFlowSubtreeWaiting(this);
        }

        // 本次執行是否產出可作為下游輸入的有效內容（排除錯誤 / 逾時 / 無回應訊息）。
        private bool RunProducedUsableOutput()
        {
            string bottom = (GetBottomText() ?? "").TrimStart();
            if (string.IsNullOrWhiteSpace(bottom))
                return false;

            return !bottom.StartsWith("（AI 產生失敗）", StringComparison.Ordinal)
                && !bottom.StartsWith("（AI 產生逾時）", StringComparison.Ordinal)
                && !bottom.StartsWith("（AI 沒有回傳文字）", StringComparison.Ordinal)
                && !bottom.StartsWith("AI 這次沒有回傳內容", StringComparison.Ordinal)
                && !bottom.StartsWith("AI 回應逾時", StringComparison.Ordinal)
                && !bottom.StartsWith("找不到主視窗", StringComparison.Ordinal)
                && !bottom.StartsWith("AI 服務尚未準備好", StringComparison.Ordinal);
        }

        private async Task GenerateBottomReplyFromTopAsync(
            string topText,
            Func<Action<string>, CancellationToken, Task<string>>? executor = null,
            CancellationToken externalToken = default)
        {
            _isGenerating = true;
            topText ??= "";
            _lastRunPrompt = topText;
            // #1 真實用量：頂層執行開始時清零一次；本次所有子步驟 / continuation 輪次都累加到同一個節點，
            //  讓多代理編排也能算出「整個節點」的總 token，而非只剩最後一步。
            ResetTokenUsage();
            bool isImageTask = IsImageTask(topText);
            UpdateEditButtons();
            ClearOutputFiles();
            ApplyRunStatus(NodeRunStatus.Running);
            TimeSpan executionTimeout = ResolveExecutionTimeout(topText);

            try
            {
                StartBottomLoadingAnimation(isImageTask);
                await Dispatcher.Yield(DispatcherPriority.Render);

                if (_parent == null)
                {
                    StopBottomLoadingAnimation(clearIfLoading: true);
                    MarkBottomPlain();
                    BottomDisplay.Text = "找不到主視窗，目前無法呼叫 AI。請重新開啟節點再試。";
                    ApplyRunStatus(NodeRunStatus.Failed, "主視窗未連接");
                    return;
                }

                if (_parent.NodeService == null)
                {
                    StopBottomLoadingAnimation(clearIfLoading: true);
                    MarkBottomPlain();
                    BottomDisplay.Text = "AI 服務尚未準備好，請稍候幾秒再試一次。";
                    ApplyRunStatus(NodeRunStatus.Failed, "服務初始化中");
                    return;
                }

                // 花錢安全：已達每日花費上限 → 不執行（所有執行路徑都經過這裡：送出/重跑/工作流/手機指令）。
                if (!_parent.CheckDailyBudgetAllows(out string budgetMessage))
                {
                    StopBottomLoadingAnimation(clearIfLoading: true);
                    MarkBottomPlain();
                    BottomDisplay.Text = budgetMessage;
                    ApplyRunStatus(NodeRunStatus.Failed, "已達每日花費上限");
                    return;
                }

                // 外部（工作流鏈「停止」/ 手機遠端「停止」）token 與本步逾時 token 連動：任一觸發都會取消這次執行。
                _activeManualStop?.Dispose();
                _activeManualStop = new CancellationTokenSource();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _activeManualStop.Token);
                cts.CancelAfter(executionTimeout);

                Action<string> deltaHandler = delta =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_isShowingLoadingText)
                            StopBottomLoadingAnimation(clearIfLoading: true);

                        MarkBottomPlain(); // 串流中：顯示=資料（結束後才換成清過記號的版本）
                        BottomDisplay.AppendText(delta);
                        BottomDisplay.ScrollToEnd();
                    });
                };

                string finalReply = await (executor != null
                    ? executor(deltaHandler, cts.Token)
                    : _parent.NodeService.GenerateStreamAsync(this, topText, deltaHandler, cts.Token));

                StopBottomLoadingAnimation(clearIfLoading: string.IsNullOrWhiteSpace(finalReply));

                if (string.IsNullOrWhiteSpace(finalReply))
                {
                    // 有 chip（檔案型任務成功）：不視為失敗，晶片本身就是輸出。
                    if (_outputFiles.Count > 0)
                    {
                        ApplyRunStatus(NodeRunStatus.Success);
                    }
                    else
                    {
                        MarkBottomPlain();
                        BottomDisplay.Text = "AI 這次沒有回傳內容，可能是請求被中斷或模型無回應。請再試一次。";
                        ApplyRunStatus(NodeRunStatus.Failed, "沒有回傳內容");
                    }
                }
                else
                {
                    // 資料留原文（下游/存檔/builder 用）；TextBox 存清過記號的版本保底，畫面切到 FlowDocument 真渲染。
                    _bottomRawText = finalReply.Trim();
                    string display = MarkdownDisplayText.Clean(_bottomRawText);
                    if (!string.Equals(BottomDisplay.Text, display, StringComparison.Ordinal))
                    {
                        BottomDisplay.Text = display;
                    }
                    ShowRenderedBottom(_bottomRawText);

                    ApplyRunStatus(NodeRunStatus.Success);
                }

                UpdateAutoTaskPreview();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                StopBottomLoadingAnimation(clearIfLoading: true);

                if (externalToken.IsCancellationRequested || _activeManualStop?.IsCancellationRequested == true)
                {
                    // 使用者主動「停止」（工作流鏈，或手機遠端停止）：不是逾時，給對應訊息與狀態。
                    MarkBottomPlain();
                    BottomDisplay.Text =
                        "已手動停止。這一步尚未完成。\n" +
                        "可右鍵「執行此節點與下游」從這裡重跑，或「略過此步、從下一步續跑」。";
                    ApplyRunStatus(NodeRunStatus.Failed, "已停止");
                }
                else
                {
                    MarkBottomPlain();
                    BottomDisplay.Text =
                        $"AI 回應逾時。這次任務超過 {FormatTimeout(executionTimeout)} 仍未完成，已自動取消。\n" +
                        "可以試著縮短問題、減少附件，或換一個較快的模型再試。";
                    ApplyRunStatus(NodeRunStatus.Failed, $"逾時（超過 {FormatTimeout(executionTimeout)}）");
                }
            }
            catch (Exception ex)
            {
                StopBottomLoadingAnimation(clearIfLoading: true);
                string friendly = BuildFriendlyError(ex);
                MarkBottomPlain();
                BottomDisplay.Text = friendly;
                ApplyRunStatus(NodeRunStatus.Failed, friendly);
            }
            finally
            {
                StopBottomLoadingAnimation(clearIfLoading: false);
                _isGenerating = false;
                _activeManualStop?.Dispose();
                _activeManualStop = null;
                UpdateEditButtons();
            }
        }

        // ===== 手機鏡像（§17 階段二）遠端輕操控入口：皆須在 UI 執行緒呼叫（由 MainWindow 的 Dispatcher 保證）。 =====

        /// <summary>此節點是否正在執行（供快照 CanStop / CanRerun 判斷）。</summary>
        public bool IsGenerating => _isGenerating;

        /// <summary>遠端「停止」：取消目前執行。回傳是否確實有在跑而送出取消。</summary>
        public bool StopActiveRun()
        {
            if (!_isGenerating || _activeManualStop == null)
                return false;
            try { _activeManualStop.Cancel(); return true; }
            catch { return false; }
        }

        /// <summary>遠端「重跑」：沿用上一次 prompt（或現有輸入）重新生成；fire-and-forget，不阻塞呼叫端。</summary>
        public async Task RerunFromMirrorAsync()
        {
            if (_isGenerating)
                return;
            string prompt = string.IsNullOrWhiteSpace(_lastRunPrompt)
                ? BuildPromptForCurrentRun(GetTopText())
                : _lastRunPrompt;
            if (string.IsNullOrWhiteSpace(prompt))
                return;
            await GenerateBottomReplyFromTopAsync(prompt);
        }

        // ===== Product UX：節點狀態（邊框顏色）+ 狀態列 =====

        private void ApplyRunStatus(NodeRunStatus status, string? detail = null)
        {
            _runStatus = status;

            StopStatusRevertTimer();

            if (NodeBorder != null)
            {
                NodeBorder.BorderBrush = status switch
                {
                    NodeRunStatus.Running => new SolidColorBrush(Color.FromRgb(0x1E, 0x73, 0xE6)), // 藍：執行中
                    NodeRunStatus.Success => new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B)), // 綠：成功
                    NodeRunStatus.Failed => new SolidColorBrush(Color.FromRgb(0xD1, 0x43, 0x43)),  // 紅：失敗
                    NodeRunStatus.Waiting => new SolidColorBrush(Color.FromRgb(0xE0, 0x92, 0x2B)), // 琥珀：等待上游
                    _ => new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)),                      // 黑：閒置
                };
            }

            switch (status)
            {
                case NodeRunStatus.Running:
                    if (StatusText != null) StatusText.Text = "";
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Collapsed;
                    break;

                case NodeRunStatus.Success:
                    // §3：成功後可從右鍵選單「重新生成答案」(沿用 research)。footer 已無內容，收起避免空白條。
                    if (StatusText != null)
                    {
                        StatusText.Text = "";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                    }
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Collapsed;
                    // 成功的綠框只短暫提示，之後回到黑框，避免畫布上一片綠。
                    StartStatusRevertTimer();
                    break;

                case NodeRunStatus.Failed:
                    if (StatusText != null)
                    {
                        StatusText.Text = string.IsNullOrWhiteSpace(detail) ? "執行失敗" : detail;
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    }
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Visible;
                    break;

                case NodeRunStatus.Waiting:
                    // 排入流動工作流、等上游跑完前的過渡狀態；只換琥珀邊框，不顯示 footer。
                    if (StatusText != null) StatusText.Text = "";
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Collapsed;
                    break;

                default: // Idle
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        /// <summary>被排入流動工作流、等待上游完成前的「等待中」狀態（節點琥珀框 + 手機鏡像顯示）。</summary>
        public void MarkWaiting()
        {
            if (_isGenerating || _runStatus == NodeRunStatus.Running)
                return; // 正在跑的節點不覆蓋
            ApplyRunStatus(NodeRunStatus.Waiting);
        }

        /// <summary>若仍停在等待中（上游失敗或鏈結束而未輪到它執行），復位為閒置。</summary>
        public void ClearWaitingStatus()
        {
            if (_runStatus == NodeRunStatus.Waiting)
                ApplyRunStatus(NodeRunStatus.Idle);
        }

        private void StartStatusRevertTimer()
        {
            StopStatusRevertTimer();

            _statusRevertTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _statusRevertTimer.Tick += (_, _) =>
            {
                StopStatusRevertTimer();
                // 仍維持在成功狀態才回復黑框（避免覆蓋後續的新執行 / 失敗）。
                if (_runStatus == NodeRunStatus.Success && NodeBorder != null)
                    NodeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
            };
            _statusRevertTimer.Start();
        }

        private void StopStatusRevertTimer()
        {
            if (_statusRevertTimer != null)
            {
                _statusRevertTimer.Stop();
                _statusRevertTimer = null;
            }
        }

        // 把底層拋出的技術性錯誤轉成使用者看得懂的訊息。
        private static string BuildFriendlyError(Exception ex)
        {
            string raw = ex?.Message ?? "";
            string lower = raw.ToLowerInvariant();

            if (lower.Contains("401") || lower.Contains("unauthorized") || lower.Contains("api key") || lower.Contains("api 金鑰"))
                return "API 金鑰無效或尚未設定，請檢查模型設定後再試。";

            if (lower.Contains("429") || lower.Contains("rate limit") || lower.Contains("quota") || lower.Contains("insufficient"))
                return "請求太頻繁或額度不足，請稍候再試，或改用其他模型。";

            if (ex is System.Net.Http.HttpRequestException ||
                lower.Contains("network") || lower.Contains("connection") || lower.Contains("socket") ||
                lower.Contains("name resolution") || lower.Contains("無法連線") || lower.Contains("timed out"))
                return "無法連線到 AI 服務，請確認網路後再試一次。";

            if (lower.Contains("500") || lower.Contains("502") || lower.Contains("503") || lower.Contains("server error") || lower.Contains("overloaded"))
                return "AI 服務暫時無法使用，請稍後再試。";

            if ((lower.Contains("content") && (lower.Contains("policy") || lower.Contains("blocked"))) ||
                lower.Contains("refus") || lower.Contains("safety"))
                return "這個請求被模型拒絕，可能涉及受限制的內容。";

            string trimmed = raw.Length > 140 ? raw.Substring(0, 140).TrimEnd() + "…" : raw;
            return string.IsNullOrWhiteSpace(trimmed)
                ? "發生未預期的錯誤，請重試一次。"
                : $"執行時發生錯誤：{trimmed}";
        }

        // §3：只重新生成最終答案，沿用上一次的 research / capability 成果（較快、不重跑搜尋）。
        private async void RegenerateMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isGenerating)
                return;

            if (_parent?.NodeService == null)
                return;

            // 有可沿用的成功 workspace（同一 session 內剛跑過）→ 只重生答案、省去 research（快）。
            // 沒有（例如重啟 app 後記憶體快取已失效、或此節點從沒在本 session 成功跑過）→ 直接完整重跑，
            // 避免落到 replay 找不到輸入而回空字串、跳出「沒有回傳內容」。
            if (!_parent.NodeService.CanRegenerateAnswer(this))
            {
                await RunCurrentTopTextAsync();
                return;
            }

            string prompt = string.IsNullOrWhiteSpace(_lastRunPrompt)
                ? BuildPromptForCurrentRun(GetTopText())
                : _lastRunPrompt;

            if (string.IsNullOrWhiteSpace(prompt))
                return;

            await GenerateBottomReplyFromTopAsync(
                prompt,
                (delta, token) => _parent.NodeService.RegenerateAnswerStreamAsync(this, delta, token));
        }

        // §3：用相同輸入整段重播工作流。
        public async Task<bool> RunCurrentTopTextAsync(CancellationToken externalToken = default)
        {
            if (_isGenerating)
                return false;

            string top = GetTopText() ?? "";
            if (string.IsNullOrWhiteSpace(top))
                return false;

            string runTop = BuildPromptForCurrentRun(top);

            if (_parent != null && _parent.IsAutoModelSelectionEnabled())
                _editingModelId = NormalizeSafeModelId(_parent.GetEffectiveNodeModel(this, runTop));

            CommitEditingModel();
            _isTopLocked = true;
            EndEditBecauseSent();
            ContentChanged?.Invoke(this, EventArgs.Empty);
            _parent?.NotifyNodeSubmitted(this);

            await GenerateBottomReplyFromTopAsync(runTop, externalToken: externalToken);

            string bottom = GetBottomText() ?? "";
            return !string.IsNullOrWhiteSpace(bottom) &&
                   !bottom.TrimStart().StartsWith("（AI 產生失敗）", StringComparison.Ordinal) &&
                   !bottom.TrimStart().StartsWith("（AI 產生逾時）", StringComparison.Ordinal) &&
                   !bottom.TrimStart().StartsWith("（AI 沒有回傳文字）", StringComparison.Ordinal);
        }

        private string BuildPromptForCurrentRun(string topText)
        {
            string prompt = topText ?? "";

            if (_parent != null &&
                _parent.TryBuildInputFromFirstUpstream(this, out var injectedPrompt) &&
                !string.IsNullOrWhiteSpace(injectedPrompt))
            {
                prompt = injectedPrompt;
            }

            return prompt;
        }

        private TimeSpan ResolveExecutionTimeout(string? topText)
        {
            // 影片生成：原生延伸接長片可能長達數十分鐘，且每段需輪詢——一律「無逾時上限」，
            // 只由使用者主動取消 / 工作流停止來中止（不受手動逾時設定限制）。
            if (IsVideoTask(topText))
                return Timeout.InfiniteTimeSpan;

            // §15 個人化：使用者若手動設定逾時上限（>0 秒），一律以它為準，覆蓋以下自動判斷。
            int manualSecs = _parent?.GetManualTimeoutSeconds() ?? 0;
            if (manualSecs > 0)
                return TimeSpan.FromSeconds(manualSecs);

            bool hasAttachments = _attachments.Count > 0;
            string text = topText ?? "";
            string lower = text.ToLowerInvariant();

            bool codePatchTask =
                ContainsAny(text, lower,
                    "bug", "debug", "修正", "修改", "修好", "patch", "diff", "重構", "程式", "程式碼");

            bool reportTask =
                ContainsAny(text, lower,
                    "報告", "report", "分析", "研究", "財報", "匯出", "export", "生成檔", "生成報", "generate",
                    "簡報", "投影片", "ppt", "pptx", "slides", "slide", "presentation");

            // 生成圖片：gpt-image-2 出圖較慢（可能 1～3 分鐘），需要更寬鬆的逾時。
            bool imageTask = IsImageTask(text);

            if (hasAttachments && codePatchTask)
                return TimeSpan.FromMinutes(10);

            if (hasAttachments)
                return TimeSpan.FromMinutes(6);

            if (imageTask)
                return TimeSpan.FromMinutes(8);

            if (reportTask)
                return TimeSpan.FromMinutes(8);

            return TimeSpan.FromMinutes(3);
        }

        // 生成圖片偵測（逾時策略用）：與 OrchestrationPlanner 共用 MediaTaskKeywords 單一真相，不再人工同步清單。
        private static bool IsImageTask(string? topText)
            => MediaTaskKeywords.IsImageGenerationCommand(topText);

        // 影片偵測（逾時策略用）：刻意用「寬鬆」清單——只是多給時間，寧誤勿漏；
        // 觸發實際生成的嚴格清單在 OrchestrationPlanner（走 MediaTaskKeywords.VideoGenerationCommands）。
        private static bool IsVideoTask(string? topText)
            => MediaTaskKeywords.MentionsVideoForTimeout(topText);

        private static string FormatTimeout(TimeSpan timeout)
        {
            if (timeout.TotalMinutes >= 1)
                return $"{(int)Math.Round(timeout.TotalMinutes)} 分鐘";

            return $"{(int)Math.Round(timeout.TotalSeconds)} 秒";
        }

        private void StartBottomLoadingAnimation() => StartBottomLoadingAnimation(false);

        private void StartBottomLoadingAnimation(bool imageTask)
        {
            _isShowingLoadingText = true;
            MarkBottomPlain();
            BottomDisplay.Text = "";

            _loadingIsImageTask = imageTask;
            _loadingBaseText = imageTask ? "正在生成圖片" : "AI 正在生成";
            _loadingExtraHint = "";
            _loadingStartUtc = DateTime.UtcNow;

            if (BottomLoadingText != null)
                BottomLoadingText.Text = _loadingBaseText + "…";

            if (BottomLoadingOverlay != null)
                BottomLoadingOverlay.Visibility = Visibility.Visible;

            StartSpinnerAnimation();
            StartLoadingTimer();
        }

        private void StartLoadingTimer()
        {
            StopLoadingTimer();

            _loadingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _loadingTimer.Tick += LoadingTimer_Tick;
            _loadingTimer.Start();
        }

        private void LoadingTimer_Tick(object? sender, EventArgs e)
        {
            if (BottomLoadingText == null)
                return;

            var elapsed = DateTime.UtcNow - _loadingStartUtc;

            string clock = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}"
                : $"{elapsed.Seconds} 秒";

            string hint = string.IsNullOrWhiteSpace(_loadingExtraHint)
                ? (_loadingIsImageTask ? "（gpt-image 通常需 1～3 分鐘）" : "")
                : $"（{_loadingExtraHint}）";

            BottomLoadingText.Text = $"{_loadingBaseText}… {clock}{hint}";
        }

        /// <summary>長任務（影片生成等）即時進度提示，可從背景執行緒呼叫。傳 null/空字串清除。</summary>
        public void SetLoadingHint(string? hint)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetLoadingHint(hint));
                return;
            }

            _loadingExtraHint = hint ?? "";
        }

        private void StopLoadingTimer()
        {
            if (_loadingTimer != null)
            {
                _loadingTimer.Stop();
                _loadingTimer.Tick -= LoadingTimer_Tick;
                _loadingTimer = null;
            }
        }

        private void StartSpinnerAnimation()
        {
            if (BottomLoadingSpinnerRotate == null)
                return;

            BottomLoadingSpinnerRotate.BeginAnimation(
                RotateTransform.AngleProperty,
                null);

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(0.85),
                RepeatBehavior = RepeatBehavior.Forever
            };

            BottomLoadingSpinnerRotate.BeginAnimation(
                RotateTransform.AngleProperty,
                animation);
        }

        private void StopBottomLoadingAnimation(bool clearIfLoading)
        {
            StopLoadingTimer();

            if (BottomLoadingSpinnerRotate != null)
            {
                BottomLoadingSpinnerRotate.BeginAnimation(
                    RotateTransform.AngleProperty,
                    null);
            }

            if (BottomLoadingOverlay != null)
                BottomLoadingOverlay.Visibility = Visibility.Collapsed;

            if (clearIfLoading && _isShowingLoadingText)
            {
                MarkBottomPlain();
                BottomDisplay.Text = "";
            }

            _isShowingLoadingText = false;
        }

        public string GetTopText() => TopEditor.Text ?? "";

        public void SetTopText(string text)
        {
            TopEditor.Text = text ?? "";
            UpdateAutoTaskPreview();
        }


        // 顯示與資料分離（P1-10 後半）：資料真相＝_bottomRawText（原始 Markdown，存檔/下游/builder 用）。
        // 顯示分兩態（#6）：最終輸出 → BottomRich(FlowDocument 真渲染粗體/標題/清單)；
        // 串流中/錯誤訊息/清空 → BottomDisplay(TextBox 純文字)。null = 顯示內容就是原文。
        private string? _bottomRawText;

        public string GetBottomText() => _bottomRawText ?? (BottomDisplay.Text ?? "");

        public void SetBottomText(string text)
        {
            _bottomRawText = text ?? "";
            BottomDisplay.Text = MarkdownDisplayText.Clean(_bottomRawText); // TextBox 同步保底（邏輯/量測仍讀它）
            if (string.IsNullOrWhiteSpace(_bottomRawText))
                ShowPlainBottom();
            else
                ShowRenderedBottom(_bottomRawText);
        }

        // 回到純文字顯示狀態（串流/錯誤/清空），並標記「顯示即資料」。
        private void MarkBottomPlain()
        {
            _bottomRawText = null;
            ShowPlainBottom();
        }

        private void ShowPlainBottom()
        {
            if (BottomRich != null) BottomRich.Visibility = Visibility.Collapsed;
            if (BottomDisplay != null) BottomDisplay.Visibility = Visibility.Visible;
        }

        private void ShowRenderedBottom(string raw)
        {
            if (BottomRich == null || BottomDisplay == null)
                return;

            BottomRich.Document = MarkdownFlowDocumentBuilder.Build(raw, BottomDisplay.FontSize);
            BottomRich.Visibility = Visibility.Visible;
            BottomDisplay.Visibility = Visibility.Collapsed;
        }

        // 載入存檔時呼叫：節點已有輸出 → 維持閒置黑框外觀。
        // 「重新生成答案」已改到右鍵選單（ContextMenu_Opened 依「有輸出」即啟用），不再需要還原右下角按鈕；
        // footer 無內容時收起，避免一開檔出現空白狀態條。
        public void RestoreRegenerateAffordance()
        {
            _runStatus = NodeRunStatus.Idle;
            if (StatusText != null) StatusText.Text = "";
            if (StatusFooter != null) StatusFooter.Visibility = Visibility.Collapsed;
        }

        public void ClearBottomText()
        {
            ClearOutputFiles();
            MarkBottomPlain();
            BottomDisplay.Text = "";
        }

        public void AppendBottomText(string delta)
        {
            if (string.IsNullOrEmpty(delta))
                return;

            MarkBottomPlain(); // 串流 append：顯示=資料
            BottomDisplay.AppendText(delta);
            BottomDisplay.ScrollToEnd();
        }

        private sealed class FontSizeSliderDialog : Window
        {
            private readonly Slider _slider;
            private readonly TextBlock _valueText;

            public double SelectedValue => _slider.Value;

            public FontSizeSliderDialog(Window? owner, string title, double initialValue, double min, double max, Action<double> onPreviewValueChanged)
            {
                Owner = owner;
                Title = title;

                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                ShowInTaskbar = false;
                Topmost = true;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;

                Width = 520;
                Height = 220;

                var bg = TryGetBrush("NodeMenuBg", Colors.White);
                var border = TryGetBrush("NodeMenuBorder", (Color)ColorConverter.ConvertFromString("#D6D6D6")!);
                var text = TryGetBrush("NodeMenuText", (Color)ColorConverter.ConvertFromString("#222222")!);

                var outer = new Border
                {
                    Background = bg,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(18),
                    SnapsToDevicePixels = true
                };

                Content = outer;

                outer.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ButtonState == MouseButtonState.Pressed)
                    {
                        try { DragMove(); } catch { }
                    }
                };

                var root = new Grid();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var titleText = new TextBlock
                {
                    Text = title,
                    Foreground = text,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                Grid.SetRow(titleText, 0);
                root.Children.Add(titleText);

                var centerPanel = new Grid
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                centerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                centerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                _valueText = new TextBlock
                {
                    Foreground = text,
                    FontSize = 14,
                    Text = ((int)Math.Round(initialValue)).ToString(CultureInfo.InvariantCulture),
                    Margin = new Thickness(0, 0, 0, 10),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                Grid.SetRow(_valueText, 0);
                centerPanel.Children.Add(_valueText);

                _slider = new Slider
                {
                    Minimum = min,
                    Maximum = max,
                    Value = Math.Max(min, Math.Min(max, initialValue)),
                    TickFrequency = 1,
                    IsSnapToTickEnabled = false,
                    AutoToolTipPlacement = AutoToolTipPlacement.None,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                _slider.ValueChanged += (_, __) =>
                {
                    var value = _slider.Value;
                    _valueText.Text = ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
                    onPreviewValueChanged?.Invoke(value);
                };
                Grid.SetRow(_slider, 1);
                centerPanel.Children.Add(_slider);

                Grid.SetRow(centerPanel, 1);
                root.Children.Add(centerPanel);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };

                var cancel = CreateDialogButton("取消", text);
                cancel.IsCancel = true;
                cancel.Margin = new Thickness(0, 0, 8, 0);
                cancel.Click += (_, __) =>
                {
                    DialogResult = false;
                    Close();
                };

                var ok = CreateDialogButton("確定", text);
                ok.IsDefault = true;
                ok.Click += (_, __) =>
                {
                    DialogResult = true;
                    Close();
                };

                btnPanel.Children.Add(cancel);
                btnPanel.Children.Add(ok);

                Grid.SetRow(btnPanel, 2);
                root.Children.Add(btnPanel);

                outer.Child = root;

                PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape)
                    {
                        DialogResult = false;
                        Close();
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Enter)
                    {
                        DialogResult = true;
                        Close();
                        e.Handled = true;
                    }
                };

                Loaded += (_, __) => _slider.Focus();
            }

            private static Button CreateDialogButton(string caption, Brush fg)
            {
                var btn = new Button
                {
                    Content = caption,
                    FontSize = 13,
                    Foreground = fg,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 6, 8, 6),
                    Cursor = Cursors.Hand
                };

                btn.MouseEnter += (_, __) => btn.Opacity = 0.85;
                btn.MouseLeave += (_, __) => btn.Opacity = 1.0;
                btn.PreviewMouseLeftButtonDown += (_, __) => btn.Opacity = 0.70;
                btn.PreviewMouseLeftButtonUp += (_, __) => btn.Opacity = 0.85;

                return btn;
            }

            private static Brush TryGetBrush(string key, Color fallback)
            {
                try
                {
                    if (Application.Current?.TryFindResource(key) is Brush b)
                        return b;
                }
                catch { }

                return new SolidColorBrush(fallback);
            }
        }

        private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent == null) return;
            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null) return;
            if (sender is not FrameworkElement dragHeader) return;

            _isDraggingNode = true;
            _dragStartOnCanvas = e.GetPosition(mw.MainCanvas);
            _nodeStartPos = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

            dragHeader.CaptureMouse();

            Panel.SetZIndex(this, _parent.GetNextZIndex());
            e.Handled = true;
        }

        private void DragHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingNode || _parent == null) return;
            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null) return;

            var current = e.GetPosition(mw.MainCanvas);
            var offset = current - _dragStartOnCanvas;

            Canvas.SetLeft(this, _nodeStartPos.X + offset.X);
            Canvas.SetTop(this, _nodeStartPos.Y + offset.Y);

            Moved?.Invoke(this, EventArgs.Empty);
        }

        private void DragHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingNode) return;

            _isDraggingNode = false;

            if (sender is UIElement element && element.IsMouseCaptured)
                element.ReleaseMouseCapture();

            e.Handled = true;
        }

        private void Corner_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (_parent == null) return;
            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null) return;
            if (sender is not Thumb thumb) return;

            // 變更上游進行中：點到連接點不可拉出新節點。不建立暫時連線（_tempPath 維持 null），
            // 真正的「接上新上游」交給 Corner_DragCompleted → TryCompleteRewire 處理。
            if (_parent.IsRewireActive)
                return;

            var center = GetThumbCenterOnCanvas(thumb, mw.MainCanvas);
            _startPoint = center;

            _tempPath = new Path
            {
                Stroke = Brushes.DimGray,
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                IsHitTestVisible = false
            };

            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = center };
            fig.Segments.Add(new BezierSegment(center, center, center, true));
            geo.Figures.Add(fig);
            _tempPath.Data = geo;

            Canvas.SetZIndex(_tempPath, _parent.GetNextZIndex());
            mw.MainCanvas.Children.Add(_tempPath);
        }

        private void Corner_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_tempPath?.Data is not PathGeometry geo) return;
            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null) return;

            Point current = Mouse.GetPosition(mw.MainCanvas);

            if (geo.Figures.Count == 0) return;
            if (geo.Figures[0].Segments.Count == 0) return;
            if (geo.Figures[0].Segments[0] is not BezierSegment seg) return;

            seg.Point1 = new Point((_startPoint.X + current.X) / 2, _startPoint.Y);
            seg.Point2 = new Point((_startPoint.X + current.X) / 2, current.Y);
            seg.Point3 = current;
        }

        private void Corner_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_parent == null) return;
            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null) return;
            if (sender is not Thumb thumb) return;

            // 變更上游進行中：點/放在這個節點的連接點 = 把上游改接到「本節點」，不生新下游節點。
            // 接左孔/右孔由 MainWindow 依滑鼠相對本節點中線判定。
            if (_parent.IsRewireActive)
            {
                _parent.TryCompleteRewire(this);
                return;
            }

            if (_tempPath != null)
            {
                mw.MainCanvas.Children.Remove(_tempPath);
                _tempPath = null;
            }

            Point current = Mouse.GetPosition(mw.MainCanvas);

            var newNode = new NodeControl();
            double newWidth = newNode.Width;
            double newHeight = newNode.Height;

            string sourceThumb = thumb.Name;
            string targetThumb = thumb.Name == "ThumbTL" ? "ThumbTR" : "ThumbTL";

            double left;
            double top = current.Y - 20.0;

            if (targetThumb == "ThumbTL")
            {
                left = current.X - 20.0;
            }
            else
            {
                left = current.X - (newWidth - 20.0);
            }

            Canvas.SetLeft(newNode, left);
            Canvas.SetTop(newNode, top);
            Canvas.SetZIndex(newNode, _parent.GetNextZIndex());

            _parent.HookNode(newNode);
            mw.MainCanvas.Children.Add(newNode);

            mw.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!mw.MainCanvas.Children.Contains(newNode))
                    return;

                newNode.UpdateLayout();
                mw.MainCanvas.UpdateLayout();

                _parent.CreateCurve(this, sourceThumb, newNode, targetThumb);
                _parent.RequestBeginEdit(newNode, MainWindow.EditReason.NewNode);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static Point GetThumbCenterOnCanvas(FrameworkElement thumb, Canvas canvas)
        {
            return thumb.TranslatePoint(
                new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2),
                canvas);
        }

        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb)
                return;

            if (thumb.Name == "ThumbBL")
            {
                ThumbBL_DragDelta(sender, e);
                return;
            }

            if (thumb.Name == "ThumbBR")
            {
                ThumbBR_DragDelta(sender, e);
                return;
            }
        }


        private void ThumbBL_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = Width - e.HorizontalChange;
            double newHeight = Height + e.VerticalChange;

            if (newWidth >= 150)
            {
                double left = Canvas.GetLeft(this);
                Canvas.SetLeft(this, left + e.HorizontalChange);
                Width = newWidth;
            }

            if (newHeight >= 200)
                Height = newHeight;

            Moved?.Invoke(this, EventArgs.Empty);
        }

        private void ThumbBR_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = Width + e.HorizontalChange;
            double newHeight = Height + e.VerticalChange;

            if (newWidth >= 150)
                Width = newWidth;

            if (newHeight >= 200)
                Height = newHeight;

            Moved?.Invoke(this, EventArgs.Empty);
        }
    }

    public class BottomOffsetConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double height = (double)value;
            double offset = parameter != null ? double.Parse(parameter.ToString()!) : 0;
            return height - offset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    public class RightOffsetConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = (double)value;
            double offset = parameter != null ? double.Parse(parameter.ToString()!) : 0;
            return width - offset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    public class HeaderLeftConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double offset = parameter != null ? double.Parse(parameter.ToString()!) : 0;
            return offset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    public class HeaderWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = (double)value;
            double offset = parameter != null ? double.Parse(parameter.ToString()!) : 0;
            return width - offset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    public class AttachmentPanelMarginConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                double btnH = values.Length > 0 && values[0] is double d0 ? d0 : 40.0;
                Thickness btnM = values.Length > 1 && values[1] is Thickness t ? t : new Thickness(0, 0, 0, 10);
                double panelH = values.Length > 2 && values[2] is double d2 ? d2 : 28.0;

                double lr = 70.0;
                if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                    lr = p;

                double targetCenterFromBottom = btnM.Bottom + (btnH / 2.0);
                double bottom = targetCenterFromBottom - (panelH / 2.0);

                if (double.IsNaN(bottom) || double.IsInfinity(bottom)) bottom = 16.0;
                if (bottom < 0) bottom = 0;

                return new Thickness(lr, 0, lr, bottom);
            }
            catch
            {
                return new Thickness(66, 0, 66, 16);
            }
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
