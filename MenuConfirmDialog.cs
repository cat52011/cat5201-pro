using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Cat5201
{
    // 第 3 刀(拆上帝物件):自 MainWindow 巢狀類原樣搬出。全 App ~19 處共用的確認/訊息框。
    internal static class MenuConfirmDialog
    {
        // 通用確認框（兩顆鈕）。回傳 true = 按下確認鈕。取代原生 MessageBox 的 Yes/No、OKCancel。
        public static bool ShowConfirm(
            Window owner, string title, string message, FrameworkElement resourceHost,
            string confirmText = "確定", string cancelText = "取消", bool danger = false,
            Action<Window>? onShown = null, int autoConfirmSeconds = 0)
        {
            var dlg = new MenuConfirmWindow(owner, title, message, resourceHost, confirmText, cancelText, danger, autoConfirmSeconds);
            // 手機鏡像（§17 階段二）遠端回答用：把視窗參照交給呼叫端，讓手機也能設 DialogResult 關閉此框。
            // 其他呼叫者不傳 onShown / autoConfirmSeconds，行為完全不變。
            if (onShown != null)
                dlg.Loaded += (_, __) => onShown(dlg);
            return dlg.ShowDialog() == true;
        }

        // 通用訊息框（單顆鈕，純告知）。取代原生 MessageBox 的 OK 訊息/錯誤/警告。
        public static void ShowMessage(
            Window owner, string title, string message, FrameworkElement resourceHost,
            string okText = "確定")
        {
            var dlg = new MenuConfirmWindow(owner, title, message, resourceHost, okText, null, false, 0);
            dlg.ShowDialog();
        }

        // 刪除確認（紅色「刪除」鈕）。
        public static bool ShowDeleteConfirm(Window owner, string title, string message, FrameworkElement resourceHost)
        {
            return ShowConfirm(owner, title, message, resourceHost, confirmText: "刪除", cancelText: "取消", danger: true);
        }

        private sealed class MenuConfirmWindow : Window
        {
            public MenuConfirmWindow(Window owner, string title, string message, FrameworkElement resourceHost,
                string confirmText, string? cancelText, bool danger, int autoConfirmSeconds = 0)
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

                Width = 360;
                SizeToContent = SizeToContent.Height;   // 依內容自動長高，避免長訊息被截斷
                MaxHeight = 600;

                var bg = TryGetBrush(resourceHost, "FileMenuBg", "NodeMenuBg", Colors.White);
                var border = TryGetBrush(resourceHost, "FileMenuBorder", "NodeMenuBorder", (Color)ColorConverter.ConvertFromString("#D6D6D6")!);
                var text = TryGetBrush(resourceHost, "FileMenuText", "NodeMenuText", (Color)ColorConverter.ConvertFromString("#222222")!);

                var outer = new Border
                {
                    Background = bg,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12),
                    SnapsToDevicePixels = true
                };

                outer.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ButtonState == MouseButtonState.Pressed)
                    {
                        try { DragMove(); } catch { }
                    }
                };

                var root = new Grid();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var titleText = new TextBlock
                {
                    Text = title,
                    Foreground = text,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                Grid.SetRow(titleText, 0);
                root.Children.Add(titleText);

                var msgText = new TextBlock
                {
                    Text = message,
                    Foreground = text,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(msgText, 1);
                root.Children.Add(msgText);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };

                if (!string.IsNullOrEmpty(cancelText))
                {
                    var cancel = CreateMenuButton(cancelText, text);
                    cancel.IsCancel = true;
                    cancel.Margin = new Thickness(0, 0, 8, 0);
                    cancel.Click += (_, __) => { DialogResult = false; Close(); };
                    btnPanel.Children.Add(cancel);
                }

                var confirmBrush = danger
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F")!)
                    : text;
                var confirm = CreateMenuButton(confirmText, confirmBrush);
                confirm.IsDefault = true;
                confirm.Click += (_, __) => { DialogResult = true; Close(); };
                btnPanel.Children.Add(confirm);

                // 自動同意倒數（產檔確認框用）：按鈕顯示剩餘秒數，逾時未選擇＝按下確認。
                // 只有明確傳入 autoConfirmSeconds > 0 的呼叫者會啟用；刪除確認等其他對話框絕不自動同意。
                if (autoConfirmSeconds > 0)
                {
                    int remaining = autoConfirmSeconds;
                    confirm.Content = $"{confirmText}（{remaining}）";
                    var countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    countdown.Tick += (_, __) =>
                    {
                        remaining--;
                        if (remaining <= 0)
                        {
                            countdown.Stop();
                            try { DialogResult = true; } catch { }
                            Close();
                            return;
                        }
                        confirm.Content = $"{confirmText}（{remaining}）";
                    };
                    Closed += (_, __) => countdown.Stop(); // 任一方式關閉（含手機遠端回答）都停表
                    countdown.Start();
                }

                Grid.SetRow(btnPanel, 2);
                root.Children.Add(btnPanel);

                outer.Child = root;
                Content = outer;

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
            }

            private static Button CreateMenuButton(string caption, Brush fg)
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

            public bool NodeAcceptsAutoFlowInput(NodeControl node)
            {
                if (node == null)
                    return false;

                if (node.GetTopLocked())
                    return false;

                string template = node.GetTopText() ?? "";
                if (string.IsNullOrWhiteSpace(template))
                    return false;

                return template.Contains("{{input}}", StringComparison.Ordinal);
            }
            private static Brush TryGetBrush(FrameworkElement host, string key1, string key2, Color fallback)
            {
                try
                {
                    if (host.TryFindResource(key1) is Brush b1) return b1;
                    if (host.TryFindResource(key2) is Brush b2) return b2;
                    if (Application.Current?.TryFindResource(key1) is Brush b3) return b3;
                    if (Application.Current?.TryFindResource(key2) is Brush b4) return b4;
                }
                catch { }
                return new SolidColorBrush(fallback);
            }
        }
    }
}
