using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Fuse.App;

/// <summary>
/// 把「形状 + DropShadowEffect」预渲染成位图,之后当普通图片用。
///
/// 分层窗口(AllowsTransparency)每帧要回传整块表面,大半径模糊还会在软件路径上
/// 逐帧重算;烘焙后动画只做位图缩放/透明度,观感不变但每帧成本只剩一次合成。
/// </summary>
internal static class GlowFactory
{
    public static BitmapSource Bake(
        Brush fill, double discSize, double blurRadius, Color color, double shadowOpacity,
        double canvas, double scale)
    {
        var disc = new Ellipse
        {
            Width = discSize,
            Height = discSize,
            Fill = fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                BlurRadius = blurRadius,
                ShadowDepth = 0,
                Color = color,
                Opacity = shadowOpacity,
            },
        };
        var host = new Grid { Width = canvas, Height = canvas, Background = Brushes.Transparent };
        host.Children.Add(disc);
        host.Measure(new Size(canvas, canvas));
        host.Arrange(new Rect(0, 0, canvas, canvas));
        host.UpdateLayout();

        var pixels = (int)Math.Ceiling(canvas * scale);
        var rtb = new RenderTargetBitmap(pixels, pixels, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();
        return rtb;
    }
}

/// <summary>
/// 主题相关的烘焙光晕缓存。换主题时整套重做;呼吸/脉冲只切换预渲染的模糊档位,
/// 不再每帧改 BlurRadius。
/// </summary>
internal static class GlowCache
{
    /// <summary>与旧动画一一对应的模糊档位(120 静息,210 成功脉冲峰值)。</summary>
    public static readonly double[] FrameRadii = { 120, 134, 152, 172, 192, 210 };

    private const double OrbCanvas = 500;   // 80 + 2×210
    private const double OrbScale = 0.5;    // 光晕很软,半分辨率放大看不出
    private const double OrbShadowOpacity = 0.9;

    private const double PetalDisc = 74;
    private const double PetalBlur = 56;
    private const double PetalCanvas = 190; // 74 + 2×56
    private const double PetalScale = 0.75;
    private const double PetalShadowOpacity = 0.6;

    public static BitmapSource? PetalGlow { get; private set; }
    public static IReadOnlyList<BitmapSource?> OrbFrames => _orbFrames;

    private static readonly BitmapSource?[] _orbFrames = new BitmapSource?[FrameRadii.Length];

    public static void Refresh(Brush orbFill, Brush petalGlowFill, Color accent)
    {
        // 换主题:旧颜色的帧全部作废
        for (var i = 0; i < _orbFrames.Length; i++) _orbFrames[i] = null;

        try
        {
            PetalGlow = GlowFactory.Bake(
                petalGlowFill, PetalDisc, PetalBlur, accent, PetalShadowOpacity, PetalCanvas, PetalScale);
        }
        catch (Exception ex)
        {
            WidgetWindow.Boot("[glow] petal bake failed: " + ex.GetType().Name + " " + ex.Message);
        }

        // 第一档同步烘(静息状态立刻要用),其余放到盘后:启动时不抢 UI 线程
        BakeOrbFrame(0, orbFill, accent);
        if (OrbFrames[0] == null) return;
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => BakeRemainingOrbFrames(orbFill, accent)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static void BakeRemainingOrbFrames(Brush orbFill, Color accent)
    {
        try
        {
            for (var i = 1; i < FrameRadii.Length; i++)
                if (_orbFrames[i] == null) BakeOrbFrame(i, orbFill, accent);
        }
        catch (Exception ex)
        {
            WidgetWindow.Boot("[glow] orb bake failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static void BakeOrbFrame(int index, Brush orbFill, Color accent)
    {
        try
        {
            _orbFrames[index] = GlowFactory.Bake(
                orbFill, 80, FrameRadii[index], accent, OrbShadowOpacity, OrbCanvas, OrbScale);
        }
        catch (Exception ex)
        {
            WidgetWindow.Boot("[glow] orbit frame " + index + " failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>取最接近目标模糊半径的已烘档位下标;全空时返回 0。</summary>
    public static int IndexFor(double blurRadius)
    {
        var best = 0;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < FrameRadii.Length; i++)
        {
            var diff = Math.Abs(FrameRadii[i] - blurRadius);
            if (diff >= bestDiff) continue;
            bestDiff = diff;
            best = i;
        }
        return best;
    }

    public static BitmapSource? FrameAt(int index)
        => index >= 0 && index < _orbFrames.Length ? _orbFrames[index] : null;

    public static BitmapSource? FrameFor(double blurRadius) => FrameAt(IndexFor(blurRadius));
}
