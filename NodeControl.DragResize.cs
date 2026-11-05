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
    // NodeControl 部分類別 — 字級對話框、拖曳移動與縮放（自 NodeControl.xaml.cs 原樣搬出）
    public partial class NodeControl
    {
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

                var cancel = CreateDialogButton(UiLanguage.P("取消", "Cancel"), text);
                cancel.IsCancel = true;
                cancel.Margin = new Thickness(0, 0, 8, 0);
                cancel.Click += (_, __) =>
                {
                    DialogResult = false;
                    Close();
                };

                var ok = CreateDialogButton(UiLanguage.P("確定", "OK"), text);
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

            // 停在一個接得起來的節點上就轉成藍色——不然使用者沒有理由相信
            // 「放在別的節點上」跟「放在空白處」會有不同結果。
            // （這個藍色只是「放開會接起來」的預覽，接出來的是一般灰線。）
            bool overTarget = _parent != null &&
                              _parent.CanConnectNodes(this, _parent.FindNodeAtCanvasPoint(current, this));
            _tempPath.Stroke = overTarget
                ? (Brush)new BrushConverter().ConvertFromString("#5B6CFF")!
                : Brushes.DimGray;
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

            // 放在一個已經存在的節點上＝把它接成下游（匯流就是這樣做出來的）；
            // 放在空白處才生一個新節點。接不起來（自己、重複、會形成環）時出個聲就好，
            // 不要默默生出一個莫名其妙的新節點。
            var dropTarget = mw.FindNodeAtCanvasPoint(current, this);
            if (dropTarget != null)
            {
                if (!_parent.TryConnectToExistingNode(this, thumb.Name, dropTarget, current))
                    System.Media.SystemSounds.Asterisk.Play();
                return;
            }

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
}
