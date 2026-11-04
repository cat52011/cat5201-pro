using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Cat5201
{
    /// <summary>
    /// 「這個節點這次由哪幾個模型一起回答」的選擇框（協作模式）。
    ///
    /// 2026-10-29 使用者的產品構想：讓不同 AI 在相同背景下回答同一件事、互看後交叉檢視，
    /// 分歧留給使用者決定。選兩到四個；少於兩個等於關閉協作。
    /// 版面比照 GenerationConfirmDialog：白卡、標題、清單、膠囊按鈕，不用 XAML。
    /// </summary>
    public sealed class CollaborationModelsDialog : Window
    {
        /// <summary>按下「確定」後的選擇（按取消為 null）。空清單＝關閉協作。</summary>
        public IReadOnlyList<string>? SelectedModels { get; private set; }

        public CollaborationModelsDialog(Window owner, IReadOnlyList<string> current)
        {
            Owner = owner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 380;
            SizeToContent = SizeToContent.Height;

            var ink = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E));
            var muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90));

            var card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(24, 22, 24, 18),
                Margin = new Thickness(14),
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.16, BlurRadius = 28, ShadowDepth = 5 },
            };
            card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
            };

            var root = new StackPanel();
            root.Children.Add(new TextBlock
            {
                Text = UiLanguage.P("這個節點由哪幾個模型一起回答？", "Which models should answer this node together?"),
                FontSize = 16, FontWeight = FontWeights.Bold, Foreground = ink,
                Margin = new Thickness(0, 0, 0, 8),
            });
            root.Children.Add(new TextBlock
            {
                Text = UiLanguage.P("每個模型會各自回答，再看過彼此的回答後交叉檢視，最後列出共識與分歧——分歧由你決定。", "Each model answers on its own, then reviews the others' answers, and finally the agreements and disagreements are listed. You decide on the disagreements.") +
                       UiLanguage.P($"選 {ModelCollaboration.MinModels}～{ModelCollaboration.MaxModels} 個；費用約為單一模型的兩倍乘以模型數。", $"Pick {ModelCollaboration.MinModels}–{ModelCollaboration.MaxModels}. The cost is roughly twice a single model, times the number of models."),
                FontSize = 11.5, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            var chosen = new HashSet<string>(ModelCollaboration.Normalize(current), StringComparer.OrdinalIgnoreCase);
            var boxes = new List<(CheckBox Box, string Id)>();

            var hint = new TextBlock
            {
                FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x4A, 0x00)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            };

            void Refresh()
            {
                int n = boxes.Count(b => b.Box.IsChecked == true);
                foreach (var (box, _) in boxes)
                    box.IsEnabled = box.IsChecked == true || n < ModelCollaboration.MaxModels;
                hint.Text = n == 1 ? UiLanguage.P("只選一個不會啟用協作——那跟一般模式一樣。", "Picking only one doesn't turn on collaboration; that's the same as normal mode.") : "";
            }

            // 深度研究不列：一次以美元計、跑好幾分鐘，乘上兩輪很容易失控。
            foreach (var def in AiModelRegistry.Available.Where(m => !m.IsDeepResearch))
            {
                var box = new CheckBox
                {
                    Content = def.DisplayName,
                    FontSize = 13, Foreground = ink,
                    IsChecked = chosen.Contains(def.Id),
                    Margin = new Thickness(0, 5, 0, 5),
                };
                box.Checked += (_, __) => Refresh();
                box.Unchecked += (_, __) => Refresh();
                boxes.Add((box, def.Id));
                root.Children.Add(box);
            }
            root.Children.Add(hint);
            Refresh();

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };

            Button Pill(string caption, Color bg, Color fg)
            {
                var b = new Button
                {
                    Content = caption, FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(fg), Background = new SolidColorBrush(bg),
                    BorderThickness = new Thickness(0), Cursor = Cursors.Hand, MinWidth = 80, Height = 34,
                    Margin = new Thickness(8, 0, 0, 0),
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
                b.Template = template;
                return b;
            }

            var clear = Pill(UiLanguage.P("關閉協作", "Turn off collaboration"), Color.FromRgb(0xF1, 0xF1, 0xF3), Color.FromRgb(0x44, 0x44, 0x44));
            clear.Click += (_, __) => { SelectedModels = Array.Empty<string>(); DialogResult = true; Close(); };
            var cancel = Pill(UiLanguage.P("取消", "Cancel"), Color.FromRgb(0xF1, 0xF1, 0xF3), Color.FromRgb(0x44, 0x44, 0x44));
            cancel.IsCancel = true;
            cancel.Click += (_, __) => { DialogResult = false; Close(); };
            var ok = Pill(UiLanguage.P("確定", "OK"), Color.FromRgb(0x11, 0x11, 0x11), Colors.White);
            ok.IsDefault = true;
            ok.Click += (_, __) =>
            {
                SelectedModels = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList();
                DialogResult = true;
                Close();
            };

            row.Children.Add(clear);
            row.Children.Add(cancel);
            row.Children.Add(ok);
            root.Children.Add(row);

            card.Child = root;
            Content = card;
        }
    }
}
