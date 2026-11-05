using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Fuse.App;

/// <summary>
/// 页面出场动画:从生成点(锚点)由小到大弹现,0.3s。
/// 锚点用窗口坐标系的 DIP 点(花瓣点击处 / 色块位置),作为缩放原点,
/// 面板看起来是"从被点的地方长出来"的。
///
/// 注意:动画挂在窗口的**根内容**上而不是 Window 本身——WPF 不允许给 Window
/// 挂非 Identity 的 RenderTransform("转换对于 Window 无效"),那是静默失败 +
/// 每次打开面板都进 crash.log 的根因。
/// </summary>
internal static class PanelPopIn
{
    private const double FromScale = 0.35;
    private static readonly TimeSpan GrowMs = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FadeMs = TimeSpan.FromMilliseconds(180);

    public static void Show(Window window, Point anchorDip)
    {
        // 动画失败绝不能影响面板打开:任何异常只记日志
        try
        {
            Animate(window, anchorDip);
        }
        catch (Exception ex)
        {
            WidgetWindow.Boot("[popin] failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static void Animate(Window window, Point anchorDip)
    {
        if (window.Content is not UIElement root) return;

        var w = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (w <= 0 || h <= 0 || double.IsNaN(w) || double.IsNaN(h) ||
            double.IsNaN(anchorDip.X) || double.IsNaN(anchorDip.Y) ||
            double.IsNaN(window.Left) || double.IsNaN(window.Top)) return;

        // 锚点相对窗口位置,钳制到边缘内一点,避免原点落在窗口外导致动画看着是从屏幕外长出来
        var rx = Math.Max(0.05, Math.Min(0.95, (anchorDip.X - window.Left) / w));
        var ry = Math.Max(0.05, Math.Min(0.95, (anchorDip.Y - window.Top) / h));
        root.RenderTransformOrigin = new Point(rx, ry);

        var scale = new ScaleTransform(FromScale, FromScale);
        root.RenderTransform = scale;

        var grow = new DoubleAnimation(FromScale, 1, GrowMs)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        grow.Completed += (_, _) =>
        {
            // 动画结束摘掉变换,后续拖动面板时不再带着缩放
            root.RenderTransform = null;
            root.BeginAnimation(UIElement.OpacityProperty, null);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
        root.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, FadeMs));
    }
}
