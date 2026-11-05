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
    // NodeControl 部分類別 — 滑鼠移入的放大與陰影效果（自 NodeControl.xaml.cs 原樣搬出）
    public partial class NodeControl
    {
        private void InitializeHoverVisualObjects()
        {
            if (_hoverScaleTransform == null)
                _hoverScaleTransform = new ScaleTransform(1.0, 1.0);

            if (_hoverTranslateTransform == null)
                _hoverTranslateTransform = new TranslateTransform(0.0, 0.0);

            if (_hoverTransformGroup == null)
            {
                _hoverTransformGroup = new TransformGroup();
                _hoverTransformGroup.Children.Add(_hoverScaleTransform);
                _hoverTransformGroup.Children.Add(_hoverTranslateTransform);
            }

            if (_hoverShadowEffect == null)
            {
                _hoverShadowEffect = new DropShadowEffect
                {
                    BlurRadius = 0,
                    ShadowDepth = 0,
                    Opacity = 0,
                    Color = Color.FromRgb(80, 120, 255)
                };
            }

            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = _hoverTransformGroup;
            Effect = _hoverShadowEffect;
        }

        private void ApplyHoveredVisualState(bool hovered)
        {
            if (_isHoveredVisual == hovered)
                return;

            _isHoveredVisual = hovered;

            InitializeHoverVisualObjects();

            if (_parent != null && hovered)
                Panel.SetZIndex(this, _parent.GetNextZIndex());

            double targetScale = hovered ? HoverScaleValue : 1.0;
            double targetLiftY = hovered ? HoverLiftY : 0.0;
            double targetBlur = hovered ? HoverShadowBlur : 0.0;
            double targetShadowOpacity = hovered ? HoverShadowOpacity : 0.0;

            var duration = hovered ? HoverEnterDuration : HoverLeaveDuration;

            IEasingFunction easing = hovered
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var scaleXAnim = new DoubleAnimation
            {
                To = targetScale,
                Duration = duration,
                EasingFunction = easing
            };

            var scaleYAnim = new DoubleAnimation
            {
                To = targetScale,
                Duration = duration,
                EasingFunction = easing
            };

            var liftAnim = new DoubleAnimation
            {
                To = targetLiftY,
                Duration = duration,
                EasingFunction = easing
            };

            var blurAnim = new DoubleAnimation
            {
                To = targetBlur,
                Duration = duration,
                EasingFunction = easing
            };

            var shadowOpacityAnim = new DoubleAnimation
            {
                To = targetShadowOpacity,
                Duration = duration,
                EasingFunction = easing
            };

            // 動畫結束後再刷新一次曲線，避免端點停在舊位置
            scaleYAnim.Completed += (_, __) => NotifyConnectionLayoutChanged();
            liftAnim.Completed += (_, __) => NotifyConnectionLayoutChanged();

            _hoverScaleTransform!.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                scaleXAnim,
                HandoffBehavior.SnapshotAndReplace);

            _hoverScaleTransform.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                scaleYAnim,
                HandoffBehavior.SnapshotAndReplace);

            _hoverTranslateTransform!.BeginAnimation(
                TranslateTransform.YProperty,
                liftAnim,
                HandoffBehavior.SnapshotAndReplace);

            _hoverShadowEffect!.BeginAnimation(
                DropShadowEffect.BlurRadiusProperty,
                blurAnim,
                HandoffBehavior.SnapshotAndReplace);

            _hoverShadowEffect.BeginAnimation(
                DropShadowEffect.OpacityProperty,
                shadowOpacityAnim,
                HandoffBehavior.SnapshotAndReplace);

            // 先即時刷新一次，讓拖曳中看起來也比較同步
            NotifyConnectionLayoutChanged();
        }

        private void ResetHoverVisualStateImmediately()
        {
            InitializeHoverVisualObjects();

            _hoverScaleTransform!.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _hoverScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _hoverTranslateTransform!.BeginAnimation(TranslateTransform.YProperty, null);
            _hoverShadowEffect!.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            _hoverShadowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, null);

            _hoverScaleTransform.ScaleX = 1.0;
            _hoverScaleTransform.ScaleY = 1.0;
            _hoverTranslateTransform.Y = 0.0;
            _hoverShadowEffect.BlurRadius = 0.0;
            _hoverShadowEffect.Opacity = 0.0;

            _isHoveredVisual = false;

            NotifyConnectionLayoutChanged();
        }

        public Point GetThumbCenterIgnoringHoverTransform(string thumbName)
        {
            if (string.IsNullOrWhiteSpace(thumbName))
                return new Point(
                    Canvas.GetLeft(this) + Width / 2.0,
                    Canvas.GetTop(this) + Height / 2.0);

            if (FindName(thumbName) is not FrameworkElement thumb)
            {
                return new Point(
                    Canvas.GetLeft(this) + Width / 2.0,
                    Canvas.GetTop(this) + Height / 2.0);
            }

            // 這裡是關鍵：
            // 只把 thumb 的中心點換算到 NodeControl 自己的座標系
            // 不往 MainCanvas 直接轉，避免把 NodeControl 的 RenderTransform 算進去
            Point localCenter = thumb.TranslatePoint(
                new Point(thumb.ActualWidth / 2.0, thumb.ActualHeight / 2.0),
                this);

            double nodeLeft = Canvas.GetLeft(this);
            double nodeTop = Canvas.GetTop(this);

            if (double.IsNaN(nodeLeft)) nodeLeft = 0;
            if (double.IsNaN(nodeTop)) nodeTop = 0;

            return new Point(
                nodeLeft + localCenter.X,
                nodeTop + localCenter.Y);
        }
        private void NodeControl_MouseEnter(object sender, MouseEventArgs e)
        {
            ApplyHoveredVisualState(true);
            _parent?.NotifyNodeHoverEntered(this);
        }

        private void NodeControl_MouseLeave(object sender, MouseEventArgs e)
        {
            ApplyHoveredVisualState(false);
            _parent?.NotifyNodeHoverLeft(this);
        }
    }
}
