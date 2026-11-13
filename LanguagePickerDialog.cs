using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Cat5201
{
    /// <summary>
    /// 第一次啟動時問介面語言。之後在 個人化 → 語言 隨時可以改。
    ///
    /// 這個視窗出現的時候還不知道使用者看得懂哪種語言，所以每一行都中英並列，
    /// 兩個選項也各用自己的語言寫。關掉視窗（Esc / 不選）＝依系統語言猜一個並存下來，之後不再問（要改請到個人化）。
    /// </summary>
    public sealed class LanguagePickerDialog : Window
    {
        /// <summary>使用者選的語言代碼（<see cref="UiLanguage.Chinese"/> / <see cref="UiLanguage.English"/>）。</summary>
        public string SelectedCode { get; private set; } = GuessFromSystem();

        public LanguagePickerDialog(Window owner)
        {
            Owner = owner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 440;
            SizeToContent = SizeToContent.Height;

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFD, 0xF9)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xD6, 0xC6)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(26, 22, 26, 22),
                Margin = new Thickness(14),
                Effect = new DropShadowEffect { Color = Color.FromRgb(0x2A, 0x20, 0x19), Opacity = 0.15, BlurRadius = 26, ShadowDepth = 4 },
            };
            card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };

            var root = new StackPanel();
            root.Children.Add(new TextBlock
            {
                Text = "選擇語言 · Choose your language",
                FontSize = 21, FontWeight = FontWeights.Normal, FontFamily = new System.Windows.Media.FontFamily("Noto Serif TC, Georgia, PMingLiU"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x1D, 0x18)),
                Margin = new Thickness(0, 0, 0, 6),
            });
            root.Children.Add(new TextBlock
            {
                Text = "之後可以在 設定 → 個人化 → 語言 更改。\nYou can change this later in Settings → Personal → Language.",
                FontSize = 12, LineHeight = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x90, 0x80)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18),
            });

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var zh = MakeChoice("繁體中文", "Traditional Chinese", UiLanguage.Chinese);
            Grid.SetColumn(zh, 0); row.Children.Add(zh);
            var en = MakeChoice("English", "英文", UiLanguage.English);
            Grid.SetColumn(en, 2); row.Children.Add(en);
            root.Children.Add(row);

            card.Child = root;
            Content = card;

            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; } };
        }

        private Button MakeChoice(string title, string subtitle, string code)
        {
            bool suggested = code == SelectedCode;
            var bg = suggested ? Color.FromRgb(0x2A, 0x20, 0x19) : Color.FromRgb(0xF5, 0xF3, 0xEF);
            var fg = suggested ? Colors.White : Color.FromRgb(0x26, 0x23, 0x1E);
            var sub = suggested ? Color.FromRgb(0xC2, 0xBD, 0xB4) : Color.FromRgb(0x9A, 0x90, 0x80);

            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg), HorizontalAlignment = HorizontalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 11.5,
                Foreground = new SolidColorBrush(sub), HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });

            var btn = new Button
            {
                Content = content,
                Background = new SolidColorBrush(bg),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Height = 64,
            };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            btn.Template = template;

            btn.Click += (_, __) => { SelectedCode = code; DialogResult = true; Close(); };
            return btn;
        }

        /// <summary>系統是中文就預設中文，其他一律英文。只用來決定哪顆按鈕是深色。</summary>
        private static string GuessFromSystem()
        {
            string name = CultureInfo.CurrentUICulture.Name;
            return name.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.Chinese
                : UiLanguage.English;
        }
    }
}
