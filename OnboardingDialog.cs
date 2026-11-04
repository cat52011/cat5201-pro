using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Cat5201
{
    /// <summary>
    /// P2-1 首次啟動精靈：偵測到四把主金鑰全空時引導輸入。
    /// 每家附「測試」鈕做即時驗證（打各家最便宜的端點），綠勾/紅叉立即回饋——
    /// 非工程師不用等到第一次跑任務失敗才知道金鑰貼錯。
    /// 本窗只收集輸入（EnteredKeys），儲存由 MainWindow 走既有 ApiKeyStore 加密路徑。
    /// </summary>
    public sealed class OnboardingDialog : Window
    {
        /// <summary>
        /// 使用說明：左邊是功能，右邊是怎麼操作。
        /// 文字與 README 的「基本操作」同一套說法（平實、陳述句、選單路徑寫成 設定 →「…」），
        /// 改其中一邊時另一邊也要對齊。
        /// </summary>
        // 用屬性（=>）而不是欄位：切換語言後重開這個視窗要拿到新語言的字。
        private static (string Step, string Detail)[] Tutorial =>
        new[]
        {
            (UiLanguage.P("提問", "Asking"),
             UiLanguage.P("在畫布空白處點兩下新增節點，於上半部輸入問題並送出，回答會顯示在下半部。需要簡報、報告、表格、圖片或影片時，直接在要求裡寫明即可。",
                          "Double-click an empty spot on the canvas to add a node. Type the question in the top half and send it; the answer appears in the bottom half. To get a deck, report, spreadsheet, image or video, state it in the request.")),
            (UiLanguage.P("選擇模型", "Models"),
             UiLanguage.P("畫布右下角為 Auto 開關。開啟時依任務內容自動選用模型；關閉後以節點上的模型選單指定。",
                          "The Auto switch is at the bottom right of the canvas. When it is on, a model is chosen based on the task; with it off, choose one from the node's model menu.")),
            (UiLanguage.P("串聯流程", "Flows"),
             UiLanguage.P("拖曳節點左上或右上的小圓點：拖到空白處會新增下游節點，拖到另一個節點上則將其接為下游。在連接線上按右鍵可切換為藍色虛線，代表上游完成後自動執行下游。",
                          "Drag the small dot at the top left or top right of a node. Dropping it on empty space creates a downstream node; dropping it on another node connects that node downstream. Right-click a line to turn it into a blue dashed line, which runs the downstream node automatically when the upstream one finishes.")),
            (UiLanguage.P("匯流", "Merging"),
             UiLanguage.P("兩條以上的藍線接到同一個節點時，會等所有上游完成才開始，每個上游的回答皆完整放入輸入。",
                          "When two or more blue lines lead into one node, it waits until every upstream node has finished. All upstream answers are included in full.")),
            (UiLanguage.P("決策窗", "Decision panel"),
             UiLanguage.P("點選節點後，右上角的決策窗會顯示該次執行的細節：任務類型、選用的模型與原因、搜尋來源、token 數與費用。",
                          "Click a node and the decision panel at the top right shows the details of that run: task type, the chosen model and why, search sources, tokens and cost.")),
            (UiLanguage.P("控制費用", "Cost"),
             UiLanguage.P("設定 →「個人化」→「每日上限」，預設 NT$100，該次執行將超過上限時會先行詢問。「API」分頁可填寫各服務的儲值餘額，之後每筆花費會即時扣除。",
                          "Gear icon, \"Personal\" tab, \"Daily limit\". The default is NT$100; if a run would go over it, you are asked first. On the \"API\" tab you can enter each provider's balance, and spending is deducted as you go.")),
            (UiLanguage.P("記憶", "Memory"),
             UiLanguage.P("在節點上按右鍵 →「將此節點加入記憶」，保存長期事實。回答的風格於設定 →「個人化」→「記憶與偏好」調整。",
                          "Right-click a node and choose \"Add this node to memory\" to keep a long-term fact. Answer style is set under the gear icon, \"Personal\" tab, \"Memory & preferences\".")),
            // 金鑰存在哪、怎麼清除，是使用者隨時該查得到的，所以收進這份說明。
            (UiLanguage.P("金鑰與隱私", "Keys"),
             UiLanguage.P("金鑰以 Windows DPAPI 加密存放於本機，僅限該 Windows 帳號解密，不會上傳。「API」分頁的輸入框留空表示不變更，按該列「清除」才會移除。亦可改用系統環境變數，其優先順序高於程式內填寫的金鑰。",
                          "Keys are encrypted with Windows DPAPI and stored locally; only this Windows account can decrypt them, and they are never uploaded. On the \"API\" tab, an empty box leaves that key unchanged; press \"Clear\" on its row to remove it. Environment variables can be used instead and take priority over keys entered in the app.")),
        };

        /// <summary>視窗高度（DIP）。螢幕比這矮時改用螢幕可用高度。</summary>
        private const double DialogHeight = 760;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

        private readonly Dictionary<string, PasswordBox> _boxes = new();
        private readonly Dictionary<string, TextBlock> _statuses = new();

        /// <summary>按「儲存並開始」時,使用者有輸入的金鑰（env 名 → 值）。</summary>
        public Dictionary<string, string> EnteredKeys { get; } = new();

        // 用途說明與 README「設定 API 金鑰」的表格一致。
        private static (string Env, string Name, string Hint, string SignupUrl)[] Providers =>
        new[]
        {
            ("OPENAI_API_KEY",     "OpenAI",     UiLanguage.P("GPT 模型、圖片生成", "GPT models, image generation"),                                     "https://platform.openai.com/api-keys"),
            ("ANTHROPIC_API_KEY",  "Claude",     UiLanguage.P("Claude 模型、「最佳品質」模式、資料分析", "Claude models, best quality mode, data analysis"), "https://console.anthropic.com/settings/keys"),
            ("GEMINI_API_KEY",     "Gemini",     UiLanguage.P("Gemini 模型、Veo 影片（選填）", "Gemini models, Veo video (optional)"),                 "https://aistudio.google.com/apikey"),
            ("PERPLEXITY_API_KEY", "Perplexity", UiLanguage.P("即時搜尋（選填，測試會送出一個極小請求）", "Live search (optional; Test sends one tiny request)"), "https://www.perplexity.ai/settings/api"),
        };

        public OnboardingDialog(Window owner)
        {
            Owner = owner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            // 固定大小（中英文相同）。內容比視窗長時在中間捲動，標題與底部按鈕固定——
            // 以前是依內容撐高，英文字較多時整個視窗超出螢幕，連「略過」都按不到。
            Width = 560;
            Height = Math.Min(DialogHeight, SystemParameters.WorkArea.Height - 20);

            var card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(26, 22, 26, 20),
                Margin = new Thickness(14),
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.20, BlurRadius = 26, ShadowDepth = 4 },
            };
            card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 標題
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 可捲動內容
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 按鈕

            var title = new TextBlock
            {
                Text = UiLanguage.P("歡迎使用 cat5201-pro", "Welcome to cat5201-pro"),
                FontSize = 19, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetRow(title, 0); layout.Children.Add(title);

            var root = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };   // 右邊留給捲軸
            var scroll = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
            root.Children.Add(new TextBlock
            {
                Text = UiLanguage.P("本程式不附金鑰，呼叫費用由各自帳號支付。內容會直接傳送至所選的模型供應商，不經過開發者的伺服器。金鑰之後可由左下角齒輪 →「API」分頁修改。", "No keys are included; calls are billed to your own accounts. Your content goes directly to the model providers you choose, not through the developer's servers. Keys can be changed later under the gear icon (bottom left), \"API\" tab."),
                FontSize = 12, LineHeight = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
            // onboarding 是最大流失點（#17）：把「一把就能用」講到不可能漏看，
            // 並在每家旁給「取得金鑰」直達連結，省去使用者自己找申請頁。
            root.Children.Add(new TextBlock
            {
                Text = UiLanguage.P("至少一把即可使用，建議備有 OpenAI 或 Anthropic 其中一把。未設定金鑰的功能會自動略過。", "One key is enough to start; at least one of OpenAI or Anthropic is recommended. Features without a key are skipped."),
                FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x4D)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            });

            foreach (var (env, name, hint, signupUrl) in Providers)
            {
                // 標題列：名稱 + 用途 + 狀態
                var head = new Grid { Margin = new Thickness(0, 4, 0, 3) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var nm = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)) };
                Grid.SetColumn(nm, 0); head.Children.Add(nm);
                var hb = new TextBlock { Text = "　" + hint, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xAC)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = hint };
                Grid.SetColumn(hb, 1); head.Children.Add(hb);

                // 「取得金鑰 ↗」：點了直接開該家申請頁 —— 別讓使用者自己 Google 去哪申請。
                var link = new TextBlock
                {
                    Text = UiLanguage.P("取得金鑰 ↗", "Get a key ↗"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x6C, 0xF7)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, 10, 0),
                    ToolTip = signupUrl,
                };
                string urlCopy = signupUrl;
                link.MouseLeftButtonDown += (_, __) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(urlCopy) { UseShellExecute = true });
                    }
                    catch (Exception ex) { AppLog.Warn("Onboarding", "開啟金鑰申請頁失敗", ex); }
                };
                Grid.SetColumn(link, 2); head.Children.Add(link);

                var st = new TextBlock { Text = "", FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(st, 3); head.Children.Add(st);
                _statuses[env] = st;
                root.Children.Add(head);

                // 輸入列：密碼框 + 測試鈕
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var boxBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF8)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(10, 2, 10, 2),
                };
                var pb = new PasswordBox { Height = 30, FontSize = 12.5, Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
                boxBorder.Child = pb;
                Grid.SetColumn(boxBorder, 0); row.Children.Add(boxBorder);
                _boxes[env] = pb;

                var testBtn = MakePill(UiLanguage.P("測試", "Test"), Color.FromRgb(0xF1, 0xF1, 0xF3), Color.FromRgb(0x44, 0x44, 0x44));
                testBtn.Margin = new Thickness(8, 0, 0, 0);
                string envCopy = env;
                testBtn.Click += async (_, __) => await ValidateAsync(envCopy);
                Grid.SetColumn(testBtn, 1); row.Children.Add(testBtn);
                root.Children.Add(row);
            }

            // 使用教學
            //
            // 原本只有一句「雙擊畫布新增節點 → 輸入任務 → 送出」，但這個程式真正的賣點
            // （節點接成流程、決策窗、成本控制）在那句話裡完全看不到。
            // 第一次打開的人需要知道的不只是「怎麼送出第一個問題」，而是「這個東西能做什麼」。
            root.Children.Add(new TextBlock
            {
                Text = UiLanguage.P("基本操作", "Basic use"),
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)),
                Margin = new Thickness(0, 16, 0, 6),
            });

            foreach (var (step, detail) in Tutorial)
            {
                var line = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UiLanguage.IsEnglish ? 112 : 86) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var left = new TextBlock
                {
                    Text = step,
                    FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)),
                    TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetColumn(left, 0); line.Children.Add(left);

                var right = new TextBlock
                {
                    Text = detail,
                    FontSize = 11.5, LineHeight = 17,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x82)),
                    TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetColumn(right, 1); line.Children.Add(right);

                root.Children.Add(line);
            }

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var skip = MakePill(UiLanguage.P("略過，稍後再設", "Skip for now"), Color.FromRgb(0xF1, 0xF1, 0xF3), Color.FromRgb(0x66, 0x66, 0x66));
            skip.Margin = new Thickness(0, 0, 8, 0);
            skip.Click += (_, __) => { DialogResult = false; Close(); };
            btnRow.Children.Add(skip);

            var ok = MakePill(UiLanguage.P("儲存並開始", "Save and start"), Color.FromRgb(0x11, 0x11, 0x11), Colors.White);
            ok.Click += (_, __) =>
            {
                foreach (var (env, box) in _boxes)
                {
                    string v = (box.Password ?? "").Trim();
                    if (!string.IsNullOrEmpty(v)) EnteredKeys[env] = v;
                }
                DialogResult = EnteredKeys.Count > 0;
                Close();
            };
            btnRow.Children.Add(ok);
            Grid.SetRow(btnRow, 2); layout.Children.Add(btnRow);

            card.Child = layout;
            Content = card;

            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; } };
        }

        // ===== 即時驗證：打各家最便宜的端點 =====
        private async Task ValidateAsync(string env)
        {
            var st = _statuses[env];
            string key = (_boxes[env].Password ?? "").Trim();
            if (string.IsNullOrEmpty(key))
            {
                SetStatus(st, UiLanguage.P("先貼上金鑰", "Paste a key first"), ok: null);
                return;
            }
            SetStatus(st, UiLanguage.P("驗證中…", "Checking…"), ok: null);
            try
            {
                using var req = BuildProbe(env, key);
                using var resp = await _http.SendAsync(req);
                int code = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                    SetStatus(st, UiLanguage.P("✓ 有效", "✓ Valid"), ok: true);
                else if (code == 401 || code == 403)
                    SetStatus(st, UiLanguage.P("✗ 金鑰無效", "✗ Invalid key"), ok: false);
                else
                    SetStatus(st, UiLanguage.P($"✗ 無法驗證（HTTP {code}）", $"✗ Couldn't verify (HTTP {code})"), ok: false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Onboarding", $"{env} 驗證失敗", ex);
                SetStatus(st, UiLanguage.P("✗ 連線失敗", "✗ Connection failed"), ok: false);
            }
        }

        private static HttpRequestMessage BuildProbe(string env, string key)
        {
            switch (env)
            {
                case "OPENAI_API_KEY":
                {
                    var r = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    return r;
                }
                case "ANTHROPIC_API_KEY":
                {
                    var r = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
                    r.Headers.Add("x-api-key", key);
                    r.Headers.Add("anthropic-version", "2023-06-01");
                    return r;
                }
                case "GEMINI_API_KEY":
                    return new HttpRequestMessage(HttpMethod.Get,
                        $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key)}");
                case "PERPLEXITY_API_KEY":
                {
                    // Perplexity 沒有免費驗證端點：發一個 max_tokens=1 的極小請求（成本趨近 0）。
                    var r = new HttpRequestMessage(HttpMethod.Post, "https://api.perplexity.ai/chat/completions")
                    {
                        Content = new StringContent(
                            "{\"model\":\"sonar\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],\"max_tokens\":1}",
                            Encoding.UTF8, "application/json"),
                    };
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    return r;
                }
                default:
                    throw new InvalidOperationException("unknown provider " + env);
            }
        }

        private static void SetStatus(TextBlock st, string text, bool? ok)
        {
            st.Text = text;
            st.Foreground = new SolidColorBrush(ok switch
            {
                true => Color.FromRgb(0x1F, 0x9D, 0x55),
                false => Color.FromRgb(0xC0, 0x3A, 0x4B),
                null => Color.FromRgb(0x8A, 0x8A, 0x90),
            });
        }

        private static Button MakePill(string caption, Color bg, Color fg)
        {
            var btn = new Button
            {
                Content = caption, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg), Background = new SolidColorBrush(bg),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, MinWidth = 76, Height = 32,
            };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.PaddingProperty, new Thickness(14, 0, 14, 0));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            btn.Template = template;
            return btn;
        }
    }
}
