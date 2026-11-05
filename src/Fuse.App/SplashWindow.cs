using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Fuse.App;

/// <summary>
/// 开屏动画(FR-12):深空星野 + 12 颗粒子径向汇聚 + 光核呼吸结晶 + 涟漪。
/// 时序对齐设计稿:0-0.5s 觉醒 / 0.5-2.0s 汇聚呼吸 / 2.0-2.5s 结晶,共 2.5s。
/// 结束后淡出并回调 Finished(主窗体此时才显示)。
/// </summary>
public class SplashWindow : Window
{
    public event Action? Finished;

    /// <summary>跟随「设置 → 自定义 → 组件高亮颜色」:开屏特效与组件大圆同色。</summary>
    private readonly Color _accent = ResolveAccent();

    private static Color ResolveAccent()
    {
        if (Application.Current != null &&
            Application.Current.Resources["GreenBrush"] is SolidColorBrush brush)
            return brush.Color;
        return Color.FromRgb(0xA4, 0x16, 0xE5);
    }

    public SplashWindow(Point orbCenter)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent; // 除特效外全透明(FR-12 用户确认)
        Width = 800;
        Height = 800;
        // 开屏动画播放位置 = 组件出现的位置:窗口中心对准大圆圆心
        Left = orbCenter.X - Width / 2;
        Top = orbCenter.Y - Height / 2;

        var root = new Canvas();

        var center = new Canvas();
        Canvas.SetLeft(center, 0);
        Canvas.SetTop(center, 0);
        root.Children.Add(center);
        BuildRipple(center);
        BuildCore(center);
        BuildParticles(center, _accent);

        Content = root;
        Loaded += (_, _) => StartTimeline();
    }

    private static void BuildParticles(Canvas center, Color accent)
    {
        const int count = 12;
        for (var i = 0; i < count; i++)
        {
            var angle = i / (double)count * Math.PI * 2;
            var r = 240 + Math.Sin(angle * 2) * 40;
            var dx = Math.Cos(angle) * r;
            var dy = Math.Sin(angle) * r * 0.78; // 椭圆压扁
            var size = 9 + i % 3 * 3;

            var particle = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new RadialGradientBrush(
                    Colors.White, Color.FromArgb(235, accent.R, accent.G, accent.B)),
                Effect = new BlurEffect { Radius = 8 },
                Opacity = 0,
                RenderTransform = new TranslateTransform(
                    400 + dx,
                    400 + dy),
            };

            // 汇聚:位移 ×1→×0.48→×0.12→0,透明度 0→.35→.7→.9→0
            var animX = ConvergeKeys(
                400 + dx,
                SystemParameters.PrimaryScreenWidth / 2);
            var animY = ConvergeKeys(
                400 + dy,
                SystemParameters.PrimaryScreenHeight / 2);
            var animScale = ScaleKeys();
            var animO = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromSeconds(0.5 + i * 0.05),
                Duration = TimeSpan.FromMilliseconds(1500),
            };
            AddKF(animO, 0, 0);
            AddKF(animO, 0.35, 0.2);
            AddKF(animO, 0.7, 0.6);
            AddKF(animO, 0.9, 0.85);
            AddKF(animO, 0, 1);

            var blur = (BlurEffect)particle.Effect;
            blur.BeginAnimation(BlurEffect.RadiusProperty, BlurKeys());
            particle.BeginAnimation(OpacityProperty, animO);

            center.Children.Add(particle);
        }
    }

    private static DoubleAnimationUsingKeyFrames ConvergeKeys(double from, double to)
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1500) };
        // 与 CSS 关键帧一致:0% / 60% / 85% / 100%(48% → 12% → 0)
        AddKF(anim, from, 0);
        AddKF(anim, from + (to - from) * 0.48, 0.6);
        AddKF(anim, from + (to - from) * 0.12, 0.85);
        AddKF(anim, to, 1);
        return anim;
    }

    private static DoubleAnimationUsingKeyFrames ScaleKeys()
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1500) };
        AddKF(anim, 0, 0);
        AddKF(anim, 1, 0.2);
        AddKF(anim, 0.6, 0.6);
        AddKF(anim, 0.2, 0.85);
        AddKF(anim, 0, 1);
        return anim;
    }

    private static DoubleAnimationUsingKeyFrames BlurKeys()
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1500) };
        AddKF(anim, 8, 0);
        AddKF(anim, 5, 0.2);
        AddKF(anim, 3, 0.6);
        AddKF(anim, 0, 0.85);
        AddKF(anim, 0, 1);
        return anim;
    }

    private static void AddKF(DoubleAnimationUsingKeyFrames anim, double value, double t)
    {
        var total = anim.Duration.HasTimeSpan ? anim.Duration.TimeSpan.TotalSeconds : 1.5;
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(value, TimeSpan.FromSeconds(t * total),
            new KeySpline(0.22, 0.61, 0.36, 1)));
    }

    private static SplineDoubleKeyFrame AddKF(double value, double seconds, double totalSeconds = 1.0)
    {
        // 统一使用设计稿的 cubic-bezier(.22,.61,.36,1)
        return new SplineDoubleKeyFrame(value, TimeSpan.FromSeconds(seconds),
            new KeySpline(0.22, 0.61, 0.36, 1));
    }

    private void BuildRipple(Canvas center)
    {
        var ripple = new Ellipse
        {
            Width = 80,
            Height = 80,
            Stroke = new SolidColorBrush(Color.FromArgb(128, _accent.R, _accent.G, _accent.B)),
            StrokeThickness = 1,
            Opacity = 0,
            RenderTransform = new ScaleTransform(1, 1, 40, 40),
        };
        Canvas.SetLeft(ripple, 360);
        Canvas.SetTop(ripple, 360);
        center.Children.Add(ripple);

        var scaleAnim = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromSeconds(2.0), Duration = TimeSpan.FromMilliseconds(500) };
        scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.Zero));
        scaleAnim.KeyFrames.Add(AddKF(2.6, 0.5, 0.5));
        ripple.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        ripple.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        var opacityAnim = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromSeconds(2.0), Duration = TimeSpan.FromMilliseconds(500) };
        opacityAnim.KeyFrames.Add(AddKF(0, 0, 0.5));
        opacityAnim.KeyFrames.Add(AddKF(0.5, 0.1, 0.5));
        opacityAnim.KeyFrames.Add(AddKF(0, 0.5, 0.5));
        ripple.BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void BuildCore(Canvas center)
    {
        // 外发光(独立椭圆,透明度按呼吸曲线驱动)
        var glow = new Ellipse
        {
            Width = 200,
            Height = 200,
            Fill = new RadialGradientBrush(
                Color.FromArgb(115, _accent.R, _accent.G, _accent.B),
                Color.FromArgb(0, _accent.R, _accent.G, _accent.B)),
            Opacity = 0,
        };
        Canvas.SetLeft(glow, 300); // 200×200 与 80×80 的主核同心于窗口中心 (400,400)
        Canvas.SetTop(glow, 300);
        center.Children.Add(glow);

        var scale = new ScaleTransform(0.3, 0.3, 40, 40);

        var coreGrid = new Grid
        {
            Width = 80,
            Height = 80,
            RenderTransform = scale,
            Effect = new BlurEffect { Radius = 24 },
        };
        var core = new Ellipse { Fill = new SolidColorBrush(_accent) };
        var highlightBrush = new RadialGradientBrush(
            Color.FromArgb(89, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
        {
            GradientOrigin = new Point(0.32, 0.28),
            Center = new Point(0.5, 0.5),
        };
        var highlight = new Ellipse { Margin = new Thickness(3), Fill = highlightBrush };
        coreGrid.Children.Add(core);
        coreGrid.Children.Add(highlight);
        Canvas.SetLeft(coreGrid, 360);
        Canvas.SetTop(coreGrid, 360);
        center.Children.Add(coreGrid);

        // 呼吸关键帧:0% 觉醒 → 20% → 40% 峰值 → 60% → 80% → 92% → 100% 结晶
        AnimateScale(scale);
        AnimateBlur((BlurEffect)coreGrid.Effect);
        AnimateGlow(glow);
    }

    private void AnimateScale(ScaleTransform scale)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(2500),
            FillBehavior = FillBehavior.HoldEnd,
        };
        AddKF(anim, 0.3, 0);
        AddKF(anim, 0.9, 0.2);
        AddKF(anim, 1.4, 0.4);
        AddKF(anim, 1.05, 0.6);
        AddKF(anim, 1.0, 0.8);
        AddKF(anim, 1.08, 0.92);
        AddKF(anim, 1.0, 1);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void AnimateBlur(BlurEffect blur)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(2500),
            FillBehavior = FillBehavior.HoldEnd,
        };
        AddKF(anim, 24, 0);
        AddKF(anim, 10, 0.2);
        AddKF(anim, 6, 0.4);
        AddKF(anim, 8, 0.6);
        AddKF(anim, 6, 0.8);
        AddKF(anim, 0, 0.92);
        AddKF(anim, 0, 1);
        blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
    }

    private void AnimateGlow(Ellipse glow)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(2500),
            FillBehavior = FillBehavior.HoldEnd,
        };
        AddKF(anim, 0, 0);
        AddKF(anim, 0.5, 0.2);
        AddKF(anim, 0.7, 0.4);
        AddKF(anim, 0.6, 0.6);
        AddKF(anim, 0.55, 0.8);
        AddKF(anim, 0.7, 0.92);
        AddKF(anim, 0.45, 1);
        glow.BeginAnimation(OpacityProperty, anim);
    }

    private void StartTimeline()
    {
        // 2.5s 动画结束 → 淡出 → 回调
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
        {
            BeginTime = TimeSpan.FromSeconds(2.55),
        };
        fade.Completed += (_, _) =>
        {
            Close();
            RaiseFinished();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private bool _finishedRaised;

    private void RaiseFinished()
    {
        if (_finishedRaised) return;
        _finishedRaised = true;
        Finished?.Invoke();
    }

    /// <summary>
    /// 兜底:开屏窗被提前关闭(外部 WM_CLOSE / 异常路径)时,动画时钟会停掉、
    /// Completed 不会再触发——这里仍然要把 Finished 发出去,否则主窗体永远不出现。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        RaiseFinished();
    }
}
