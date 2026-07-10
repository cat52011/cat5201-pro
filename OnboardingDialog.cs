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
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

        private readonly Dictionary<string, PasswordBox> _boxes = new();
        private readonly Dictionary<string, TextBlock> _statuses = new();

        /// <summary>按「儲存並開始」時,使用者有輸入的金鑰（env 名 → 值）。</summary>
        public Dictionary<string, string> EnteredKeys { get; } = new();

        private static readonly (string Env, string Name, string Hint)[] Providers =
        {
            ("OPENAI_API_KEY",     "OpenAI",     "GPT 文字 · 圖片生成"),
            ("ANTHROPIC_API_KEY",  "Claude",     "寫作 · 程式 · 簡報內容"),
            ("GEMINI_API_KEY",     "Gemini",     "Google 生態 · Veo 影片"),
            ("PERPLEXITY_API_KEY", "Perplexity", "即時聯網研究（驗證會發送一個極小請求）"),
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
            Width = 560;
            SizeToContent = SizeToContent.Height;

            var card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(26, 22, 26, 20),
                Margin = new Thickness(14),
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.20, BlurRadius = 26, ShadowDepth = 4 },
            };
            card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };

            var root = new StackPanel();

            root.Children.Add(new TextBlock
            {
                Text = "👋 歡迎使用 cat5201-pro",
                FontSize = 19, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock
            {
                Text = "本程式採 BYO-key：使用你自己的 API 金鑰，成本透明、資料不經第三方。\n填入至少一把即可開始（之後隨時可在 設定 → API 補齊）。金鑰以 Windows DPAPI 加密存本機。",
                FontSize = 12, LineHeight = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            });

            foreach (var (env, name, hint) in Providers)
            {
                // 標題列：名稱 + 用途 + 狀態
                var head = new Grid { Margin = new Thickness(0, 4, 0, 3) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var nm = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)) };
                Grid.SetColumn(nm, 0); head.Children.Add(nm);
                var hb = new TextBlock { Text = "　" + hint, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xAC)), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(hb, 1); head.Children.Add(hb);
                var st = new TextBlock { Text = "", FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(st, 2); head.Children.Add(st);
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

                var testBtn = MakePill("測試", Color.FromRgb(0xF1, 0xF1, 0xF3), Color.FromRgb(0x44, 0x44, 0x44));
                testBtn.Margin = new Thickness(8, 0, 0, 0);
                string envCopy = env;
                testBtn.Click += async (_, __) => await ValidateAsync(envCopy);
                Grid.SetColumn(testBtn, 1); row.Children.Add(testBtn);
                root.Children.Add(row);
            }

            // 快速上手
            root.Children.Add(new TextBlock
            {
                Text = "快速上手：雙擊畫布新增節點 → 輸入任務（問答／報告／簡報／圖片…）→ 送出。\n每次執行的模型選擇、成本與來源都會攤開在決策窗。",
                FontSize = 11.5, LineHeight = 17,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x7A, 0x9C)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var skip = MakePill("略過，稍後再設", Color.FromRgb(0xF1, 0xF1, 0xF3), Color.FromRgb(0x66, 0x66, 0x66));
            skip.Margin = new Thickness(0, 0, 8, 0);
            skip.Click += (_, __) => { DialogResult = false; Close(); };
            btnRow.Children.Add(skip);

            var ok = MakePill("儲存並開始", Color.FromRgb(0x11, 0x11, 0x11), Colors.White);
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
            root.Children.Add(btnRow);

            card.Child = root;
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
                SetStatus(st, "先貼上金鑰", ok: null);
                return;
            }
            SetStatus(st, "驗證中…", ok: null);
            try
            {
                using var req = BuildProbe(env, key);
                using var resp = await _http.SendAsync(req);
                int code = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                    SetStatus(st, "✓ 有效", ok: true);
                else if (code == 401 || code == 403)
                    SetStatus(st, "✗ 金鑰無效", ok: false);
                else
                    SetStatus(st, $"✗ 無法驗證（HTTP {code}）", ok: false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Onboarding", $"{env} 驗證失敗", ex);
                SetStatus(st, "✗ 連線失敗", ok: false);
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
