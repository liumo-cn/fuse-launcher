using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Fuse.App.Controls;

/// <summary>下载中的旋转指示器:14×14 圆弧,只在下载态启动,离开即停(不空转)。</summary>
public sealed class Spinner : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private readonly Brush _brush;
    private double _angle;

    public Spinner(Brush brush)
    {
        _brush = brush;
        Width = 14;
        Height = 14;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 9) % 360;
            InvalidateVisual();
        };
    }

    public void Start()
    {
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop() => _timer.Stop();

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(Width / 2, Height / 2);
        var radius = Math.Min(Width, Height) / 2 - 1.5;
        var start = _angle * Math.PI / 180.0;
        const double sweep = 260 * Math.PI / 180.0;
        var p1 = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
        var p2 = new Point(center.X + radius * Math.Cos(start + sweep), center.Y + radius * Math.Sin(start + sweep));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(p1, false, false);
            ctx.ArcTo(p2, new Size(radius, radius), 0, sweep > Math.PI,
                SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(_brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}
