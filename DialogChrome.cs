using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Cat5201
{
    /// <summary>
    /// 彈窗共用的外觀零件（純外觀，不含任何流程邏輯）：
    /// 方正的主要／次要／危險按鈕，以及彈窗卡片的陰影。
    /// </summary>
    internal static class DialogChrome
    {
        public static readonly Color Ink = Color.FromRgb(0x2A, 0x20, 0x19);
        public static readonly Color Cream = Color.FromRgb(0xFF, 0xFD, 0xF9);
        public static readonly Color Oat = Color.FromRgb(0xF1, 0xEB, 0xE0);
        public static readonly Color Hairline = Color.FromRgb(0xE0, 0xD6, 0xC6);
        public static readonly Color Oxblood = Color.FromRgb(0x9B, 0x3B, 0x34);

        public const string SerifFamily = "Noto Serif TC, Georgia, PMingLiU";

        public static Button Primary(string caption) => Make(caption, Ink, Cream);
        public static Button Secondary(string caption) => Make(caption, Oat, Ink);
        public static Button Danger(string caption) => Make(caption, Oxblood, Cream);

        /// <summary>彈窗卡片的陰影；卡片外要留至少 16px 的 Margin 才不會被視窗邊緣裁掉。</summary>
        public static DropShadowEffect CardShadow() => new DropShadowEffect
        {
            Color = Ink,
            Opacity = 0.16,
            BlurRadius = 22,
            ShadowDepth = 5,
            Direction = 270,
        };

        private static Button Make(string caption, Color bg, Color fg)
        {
            var btn = new Button
            {
                Content = caption,
                FontSize = 13,
                Foreground = new SolidColorBrush(fg),
                Background = new SolidColorBrush(bg),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                MinWidth = 84,
                Height = 34,
            };

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background")
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            border.SetValue(Border.PaddingProperty, new Thickness(18, 0, 18, 0));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.86));
            var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));
            template.Triggers.Add(hover);
            template.Triggers.Add(pressed);

            btn.Template = template;
            return btn;
        }
    }
}
