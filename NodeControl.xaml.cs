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
        private string _loadingBaseText = UiLanguage.P("AI 正在生成", "AI is generating");
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

        // ===== 成本可稽查：本次執行的逐筆帳目 =====
        // 節點開始執行時開一個 UsageScope（AsyncLocal），這次執行裡每個 AI 服務呼叫完成就記一筆
        // （含輸出意圖判斷等輔助呼叫、圖片、影片、搜尋）。決策窗與手機鏡像都從這裡加總——
        // 不再是「整個節點的 token 總數 × 最後那個模型的單價」（混用模型時會失真）。
        private UsageScope? _usageScope;

        /// <summary>必須在執行節點的 async 方法本體內呼叫，帳目才會流進它 await 的所有子呼叫。</summary>
        private void BeginUsageScope() => _usageScope = UsageMeter.BeginScope();

        /// <summary>本次（或最近一次）執行的逐筆帳目。</summary>
        public IReadOnlyList<UsageRecord> GetUsageRecords()
            => _usageScope?.Snapshot() ?? (IReadOnlyList<UsageRecord>)Array.Empty<UsageRecord>();

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
            NodeRunStatus.Running => UiLanguage.P("執行中", "Running"),
            NodeRunStatus.Success => UiLanguage.P("成功", "Done"),
            NodeRunStatus.Failed => UiLanguage.P("失敗", "Failed"),
            NodeRunStatus.Waiting => UiLanguage.P("等待中", "Waiting"),
            _ => UiLanguage.P("閒置", "Idle"),
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

            /// <summary>「程式碼：」「報告：」…依副檔名分類（OutputFileCategory），取代一律「檔案：」。</summary>
            public string CategoryLabel => OutputFileCategory.Of(FileName).Label + "：";
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
                MenuConfirmDialog.ShowMessage(Application.Current.MainWindow, UiLanguage.P("錯誤", "Error"), UiLanguage.P("（找不到 MainWindow，無法上傳）", "(Main window not found, can't upload)"), Application.Current.MainWindow);
                return;
            }

            // §18：已連結 Google Drive 時提供雙來源選單；未連結行為與以前完全相同（直開本機選檔）。
            if (_parent.IsGoogleDriveLinked() && sender is FrameworkElement anchorEl)
            {
                // 這個選單原本是裸的 ContextMenu，也就是 Windows 的預設外觀——
                // 整個畫布上就它一個長得像 1990 年代的控制項。節點右鍵選單早就有自己的樣式，
                // 直接沿用同一套（圓角、淺底、無陰影、hover 變淡）。
                var menu = new ContextMenu
                {
                    Style = TryFindResource("NodeContextMenuStyle") as Style,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    HorizontalOffset = 0,
                    VerticalOffset = 4,
                };

                var itemStyle = TryFindResource("NodeMenuItemStyle") as Style;

                var local = new MenuItem { Header = UiLanguage.P("本機檔案…", "Local file…"), Style = itemStyle };
                local.Click += (_, __) => PickLocalAttachments();
                menu.Items.Add(local);

                var drive = new MenuItem { Header = "Google Drive…", Style = itemStyle };
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
                Title = UiLanguage.P("選擇要上傳給 AI 的檔案/照片", "Choose files/photos to upload to the AI"),
                Multiselect = true,
                Filter =
                    UiLanguage.P("所有檔案 (*.*)|*.*|", "All files (*.*)|*.*|") +
                    UiLanguage.P("圖片 (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|", "Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|") +
                    UiLanguage.P("文件 (*.pdf;*.txt;*.md;*.csv;*.json)|*.pdf;*.txt;*.md;*.csv;*.json", "Documents (*.pdf;*.txt;*.md;*.csv;*.json)|*.pdf;*.txt;*.md;*.csv;*.json")
            };

            if (dlg.ShowDialog(Window.GetWindow(this)) == true && _parent != null)
            {
                _parent.AddAttachmentsForNode(this, dlg.FileNames);
                RefreshAttachmentsUI();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
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
