using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Cat5201
{
    /// <summary>
    /// 會「流光」的文字：IsShimmering 為 true 時，一道亮帶從左到右反覆掃過文字（像 Claude 對話進行中的標題）。
    /// 用在存檔清單：存檔裡有節點在執行時，名稱一直流光，直到全部跑完（2026-11-04 使用者要求）。
    ///
    /// 筆刷在程式裡建立、直接對它的 TranslateTransform 播放動畫——寫在 XAML 範本或 Setter 裡的筆刷會被凍結，
    /// 用 Storyboard 動它會丟出「無法在不可變的物件上播放動畫」。
    /// </summary>
    public sealed class ShimmerTextBlock : TextBlock
    {
        public static readonly DependencyProperty IsShimmeringProperty = DependencyProperty.Register(
            nameof(IsShimmering), typeof(bool), typeof(ShimmerTextBlock),
            new PropertyMetadata(false, (d, e) => ((ShimmerTextBlock)d).ApplyShimmer((bool)e.NewValue)));

        public bool IsShimmering
        {
            get => (bool)GetValue(IsShimmeringProperty);
            set => SetValue(IsShimmeringProperty, value);
        }

        /// <summary>一次掃過的時間。</summary>
        private static readonly Duration SweepDuration = new(TimeSpan.FromSeconds(1.6));

        private TranslateTransform? _shift;

        public ShimmerTextBlock()
        {
            // 從畫面上移除（清單重建、捲出可視範圍被回收）時停掉動畫；放回來時依目前狀態重新開始。
            Unloaded += (_, __) => StopAnimation();
            Loaded += (_, __) => { if (IsShimmering) ApplyShimmer(true); };
        }

        private void ApplyShimmer(bool on)
        {
            if (!on)
            {
                StopAnimation();
                ClearValue(ForegroundProperty);   // 回到原本的文字顏色
                return;
            }

            // 底色是平常的深灰，中間一道淺色亮帶；亮帶的位置靠 TranslateTransform 從左移到右。
            _shift = new TranslateTransform(-1, 0);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                RelativeTransform = _shift,
            };
            var baseColor = Color.FromRgb(0x5F, 0x63, 0x68);
            var glow = Color.FromRgb(0xD6, 0xD9, 0xDD);
            brush.GradientStops.Add(new GradientStop(baseColor, 0.0));
            brush.GradientStops.Add(new GradientStop(baseColor, 0.38));
            brush.GradientStops.Add(new GradientStop(glow, 0.5));
            brush.GradientStops.Add(new GradientStop(baseColor, 0.62));
            brush.GradientStops.Add(new GradientStop(baseColor, 1.0));
            Foreground = brush;

            var sweep = new DoubleAnimation(-1, 1, SweepDuration) { RepeatBehavior = RepeatBehavior.Forever };
            _shift.BeginAnimation(TranslateTransform.XProperty, sweep);
        }

        private void StopAnimation()
        {
            _shift?.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }
}
