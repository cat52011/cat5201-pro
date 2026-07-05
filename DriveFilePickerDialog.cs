using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace test
{
    /// <summary>
    /// §18：從 Google Drive 選檔當附件的選擇器。
    /// 呼叫端先用 ListFilesAsync 拿清單再開此窗（此窗不打 API，保持簡單可測）。
    /// Google 原生格式（Docs/Sheets/Slides）不能直接下載位元組，列出但停用並註明。
    /// </summary>
    public sealed class DriveFilePickerDialog : Window
    {
        private readonly List<CheckBox> _boxes = new();

        /// <summary>使用者勾選的 (Id, Name)。按「附加」才有值。</summary>
        public List<(string Id, string Name)> Selected { get; } = new();

        public DriveFilePickerDialog(Window owner, IReadOnlyList<(string Id, string Name, string Mime)> files)
        {
            Owner = owner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 560;

            var card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(20, 18, 20, 16),
                Margin = new Thickness(14),
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.18, BlurRadius = 24, ShadowDepth = 4 },
            };
            card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };

            var root = new StackPanel();
            root.Children.Add(new TextBlock
            {
                Text = "☁️ 從 Google Drive 選擇附件",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock
            {
                Text = "最近的檔案（此程式可見範圍）。Google 原生文件（Docs/Sheets）無法直接當附件，已停用。",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9E)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var scroll = new ScrollViewer { MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var list = new StackPanel();

            if (files.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "（Drive 裡沒有此程式可見的檔案）",
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9E)),
                    Margin = new Thickness(2, 8, 0, 8),
                });
            }

            foreach (var (id, name, mime) in files)
            {
                bool googleNative = mime.StartsWith("application/vnd.google-apps", StringComparison.OrdinalIgnoreCase);
                var cb = new CheckBox
                {
                    Content = (googleNative ? "📄 " : "📎 ") + name + (googleNative ? "（Google 原生格式）" : ""),
                    Tag = (id, name),
                    FontSize = 12.5,
                    Margin = new Thickness(2, 3, 0, 3),
                    IsEnabled = !googleNative,
                    Foreground = new SolidColorBrush(googleNative
                        ? Color.FromRgb(0xB0, 0xB0, 0xB4)
                        : Color.FromRgb(0x33, 0x33, 0x33)),
                };
                _boxes.Add(cb);
                list.Children.Add(cb);
            }

            scroll.Content = list;
            root.Children.Add(scroll);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };

            var cancel = MakePill("取消", Color.FromRgb(0xF1, 0xF1, 0xF3), Color.FromRgb(0x44, 0x44, 0x44));
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (_, __) => { DialogResult = false; Close(); };
            btnRow.Children.Add(cancel);

            var ok = MakePill("附加所選檔案", Color.FromRgb(0x11, 0x11, 0x11), Colors.White);
            ok.IsDefault = true;
            ok.Click += (_, __) =>
            {
                foreach (var cb in _boxes.Where(b => b.IsChecked == true))
                    if (cb.Tag is ValueTuple<string, string> t)
                        Selected.Add((t.Item1, t.Item2));
                DialogResult = Selected.Count > 0;
                Close();
            };
            btnRow.Children.Add(ok);

            root.Children.Add(btnRow);
            card.Child = root;
            Content = card;

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
            };
        }

        private static Button MakePill(string caption, Color bg, Color fg)
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
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
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
