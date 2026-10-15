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
    /// 產檔／媒體生成的確認框——這是「AI 即將花你的錢」的那一刻，所以它必須講清楚
    /// 要做什麼、大概多少錢、按「否」會怎樣，然後讓人一眼看完。
    ///
    /// 2026-10-13 重做版面：拿掉 ✨ 與每一列的 emoji 圖示，改用排版層次（標題 / 清單 / 註腳）。
    /// 一堆表情符號配上「偵測到這次輸入可能需要產生：」這種句子，看起來像機器在自我介紹，
    /// 而不是一個軟體在問你一個明確的問題。
    ///
    /// 行為：回傳 true=執行；支援手機遠端設 DialogResult；autoConfirmSeconds 逾時自動同意。
    /// </summary>
    public sealed class GenerationConfirmDialog : Window
    {
        /// <summary>超支警告（null＝這次不會超過今日上限）。</summary>
        public sealed record BudgetWarning(string CommittedDisplay, int BudgetTwd, double EstimatedUsd);

        public GenerationConfirmDialog(
            Window owner,
            IReadOnlyList<(string Icon, string Name, string Detail)> items,
            int autoConfirmSeconds,
            BudgetWarning? budget = null)
        {
            Owner = owner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 400;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 640;

            var ink = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E));
            var muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90));
            var hairline = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));

            var card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(24, 22, 24, 18),
                Margin = new Thickness(14), // 留空間給陰影
                SnapsToDevicePixels = true,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    Opacity = 0.16,
                    BlurRadius = 28,
                    ShadowDepth = 5,
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

            // 標題：直接問要不要做，不講「偵測到」。
            root.Children.Add(new TextBlock
            {
                Text = budget == null ? "要產生這些檔案嗎？" : "這次會超過今日花費上限",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = ink,
                Margin = new Thickness(0, 0, 0, 14),
            });

            // 清單：一行一項，名稱在左、說明靠右，中間用細線分隔。
            for (int i = 0; i < items.Count; i++)
            {
                var (_, name, detail) = items[i];

                var grid = new Grid { Margin = new Thickness(0, 9, 0, 9) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var nameText = new TextBlock
                {
                    Text = name,
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ink,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 14, 0),
                };
                Grid.SetColumn(nameText, 0);
                grid.Children.Add(nameText);

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    var detailText = new TextBlock
                    {
                        Text = detail,
                        FontSize = 11.5,
                        Foreground = muted,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(detailText, 1);
                    grid.Children.Add(detailText);
                }

                root.Children.Add(grid);

                if (i < items.Count - 1)
                    root.Children.Add(new Border { Height = 1, Background = hairline });
            }

            // 超支：講清楚現在花到哪、上限多少、這次估多少，而不是只說「超過了」。
            if (budget != null)
            {
                root.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5)),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(13, 10, 13, 10),
                    Margin = new Thickness(0, 14, 0, 0),
                    Child = new TextBlock
                    {
                        Text =
                            $"今日已花 {budget.CommittedDisplay}，上限 NT${budget.BudgetTwd}；" +
                            $"這次估計再花 {ModelCostEstimator.FormatTwd(budget.EstimatedUsd)}。\n" +
                            "仍要產生的話，這一次會放行超過上限；之後的任務照常受限制。",
                        FontSize = 11.5,
                        LineHeight = 18,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x4B, 0x00)),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }

            root.Children.Add(new TextBlock
            {
                Text = "選「不產生」會改成純文字回答。",
                FontSize = 11.5,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0),
            });

            // 倒數提示（活的）＋按鈕列
            var countdownText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x54, 0xFF)),
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = autoConfirmSeconds > 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            root.Children.Add(countdownText);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };

            var noBtn = MakePillButton("不產生", bg: Color.FromRgb(0xF1, 0xF1, 0xF3), fg: Color.FromRgb(0x44, 0x44, 0x44));
            noBtn.IsCancel = true;
            noBtn.Margin = new Thickness(0, 0, 8, 0);
            noBtn.Click += (_, __) => { DialogResult = false; Close(); };
            btnRow.Children.Add(noBtn);

            var yesBtn = MakePillButton(
                budget == null ? "產生" : "仍要產生",
                bg: Color.FromRgb(0x11, 0x11, 0x11),
                fg: Colors.White);
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
            // 會超過上限時不自動同意——那是要花超出預算的錢，必須有人真的按下去。
            if (autoConfirmSeconds > 0 && budget == null)
            {
                int remaining = autoConfirmSeconds;
                countdownText.Text = $"{remaining} 秒後自動產生";
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
                    countdownText.Text = $"{remaining} 秒後自動產生";
                };
                Closed += (_, __) => timer.Stop(); // 任一方式關閉（含手機遠端回答）都停表
                timer.Start();
            }
            else
            {
                countdownText.Visibility = Visibility.Collapsed;
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
                MinWidth = 88,
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
