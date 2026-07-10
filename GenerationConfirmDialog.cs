using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Cat5201
{
    /// <summary>
    /// 產檔／媒體生成的專屬確認框——這是產品「AI 即將花你的錢」的招牌時刻，
    /// 不用通用文字牆（MenuConfirmDialog），改成逐項卡片＋成本徽章＋倒數提示的正式設計。
    /// 行為與舊版一致：回傳 true=執行；支援手機遠端設 DialogResult；autoConfirmSeconds 逾時自動同意。
    /// </summary>
    public sealed class GenerationConfirmDialog : Window
    {
        public GenerationConfirmDialog(
            Window owner,
            IReadOnlyList<(string Icon, string Name, string Detail)> items,
            int autoConfirmSeconds)
        {
            Owner = owner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 408;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 620;

            var card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(22, 20, 22, 18),
                Margin = new Thickness(14), // 留空間給陰影
                SnapsToDevicePixels = true,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    Opacity = 0.18,
                    BlurRadius = 24,
                    ShadowDepth = 4,
                },
            };
            card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };

            var root = new StackPanel();

            // 標題列：icon + 標題
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            titleRow.Children.Add(new TextBlock { Text = "✨", FontSize = 17, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            titleRow.Children.Add(new TextBlock
            {
                Text = "要產生檔案／媒體嗎？",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            root.Children.Add(titleRow);

            root.Children.Add(new TextBlock
            {
                Text = "偵測到這次輸入可能需要產生：",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90)),
                Margin = new Thickness(0, 0, 0, 12),
            });

            // 逐項卡片：icon + 名稱 + 成本/說明徽章
            foreach (var (icon, name, detail) in items)
            {
                var row = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xFA)),
                    CornerRadius = new CornerRadius(11),
                    Padding = new Thickness(14, 11, 14, 11),
                    Margin = new Thickness(0, 0, 0, 7),
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var iconText = new TextBlock { Text = icon, FontSize = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(iconText, 0);
                grid.Children.Add(iconText);

                var nameText = new TextBlock
                {
                    Text = name,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(nameText, 1);
                grid.Children.Add(nameText);

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    var badge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xF6)),
                        CornerRadius = new CornerRadius(7),
                        Padding = new Thickness(8, 3, 8, 3),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = detail,
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x63, 0x8F)),
                        },
                    };
                    Grid.SetColumn(badge, 2);
                    grid.Children.Add(badge);
                }

                row.Child = grid;
                root.Children.Add(row);
            }

            root.Children.Add(new TextBlock
            {
                Text = "選「否」只會給純文字回答，不產生任何檔案／媒體。",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9E)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 0, 0),
            });

            // 倒數提示（活的）＋按鈕列
            var countdownText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x54, 0xFF)),
                Margin = new Thickness(2, 3, 0, 0),
                Visibility = autoConfirmSeconds > 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            root.Children.Add(countdownText);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };

            var noBtn = MakePillButton("否", bg: Color.FromRgb(0xF1, 0xF1, 0xF3), fg: Color.FromRgb(0x44, 0x44, 0x44));
            noBtn.IsCancel = true;
            noBtn.Margin = new Thickness(0, 0, 8, 0);
            noBtn.Click += (_, __) => { DialogResult = false; Close(); };
            btnRow.Children.Add(noBtn);

            var yesBtn = MakePillButton("是，執行", bg: Color.FromRgb(0x11, 0x11, 0x11), fg: Colors.White);
            yesBtn.IsDefault = true;
            yesBtn.Click += (_, __) => { DialogResult = true; Close(); };
            btnRow.Children.Add(yesBtn);

            root.Children.Add(btnRow);
            card.Child = root;
            Content = card;

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
                else if (e.Key == Key.Enter) { DialogResult = true; Close(); e.Handled = true; }
            };

            // 逾時自動同意：倒數顯示在提示行（按鈕保持乾淨）。
            if (autoConfirmSeconds > 0)
            {
                int remaining = autoConfirmSeconds;
                countdownText.Text = $"{remaining} 秒未選擇將自動執行";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, __) =>
                {
                    remaining--;
                    if (remaining <= 0)
                    {
                        timer.Stop();
                        try { DialogResult = true; } catch { }
                        Close();
                        return;
                    }
                    countdownText.Text = $"{remaining} 秒未選擇將自動執行";
                };
                Closed += (_, __) => timer.Stop(); // 任一方式關閉（含手機遠端回答）都停表
                timer.Start();
            }
        }

        private static Button MakePillButton(string caption, Color bg, Color fg)
        {
            var btn = new Button
            {
                Content = caption,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg),
                Background = new SolidColorBrush(bg),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                MinWidth = 84,
                Height = 34,
            };
            // 圓角膠囊樣板（與 app 的黑色主按鈕同語彙）
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
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
