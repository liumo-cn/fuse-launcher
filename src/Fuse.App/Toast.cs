using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Fuse.App;

internal partial class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const int WsExTransparent = 0x20;
    internal const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

/// <summary>
/// 轻提示:贴在大圆上方淡入淡出,不抢焦点、不吃鼠标(WS_EX_TRANSPARENT),
/// 用于"该版本已经在运行了"这类点了没动作的反馈。
/// </summary>
internal static class Toast
{
    private static Window? _current;

    public static void ShowNearOrb(string message)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => ShowNearOrb(message)));
            return;
        }

        try { _current?.Close(); } catch (Exception) { }

        var text = new TextBlock
        {
            Text = message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
            TextAlignment = TextAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var border = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
            Padding = new Thickness(16, 10, 16, 10),
            Child = text,
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBgBrush");

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            Content = border,
            Opacity = 0,
        };
        win.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(win).Handle;
            var style = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLong(
                handle, NativeMethods.GwlExStyle,
                style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate);
        };
        win.Loaded += (_, _) =>
        {
            PlaceAboveOrb(win);
            win.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(win.Opacity, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, _) => { try { win.Close(); } catch (Exception) { } };
                win.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            timer.Start();
        };
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, win)) _current = null;
        };
        _current = win;
        win.Show();
    }

    private static void PlaceAboveOrb(Window win)
    {
        // 用组件窗口的 DIP 坐标推算(Window.Left/Top 与布局常量同为 DIP)。
        // 不用 PointToScreen:它给的是物理像素,125% 缩放下会算到屏幕外。
        double left = double.NaN, top = double.NaN;
        try
        {
            var widget = AppServices.Widget;
            if (widget != null && widget.IsVisible)
            {
                var center = WidgetWindow.OrbCenterInWindow;
                left = widget.Left + center.X - win.ActualWidth / 2;
                top = widget.Top + center.Y - win.ActualHeight - 48;
            }
        }
        catch (Exception)
        {
            // 组件还没显示/坐标失效:退回右下角
        }

        var area = SystemParameters.WorkArea;
        if (double.IsNaN(left))
        {
            left = area.Right - win.ActualWidth - 40;
            top = area.Bottom - win.ActualHeight - 40;
        }
        // 贴着屏幕边缘时要夹回工作区,否则整条提示会跑到屏幕外
        left = Math.Max(area.Left + 8, Math.Min(left, area.Right - win.ActualWidth - 8));
        top = Math.Max(area.Top + 8, Math.Min(top, area.Bottom - win.ActualHeight - 8));
        win.Left = left;
        win.Top = top;
        WidgetWindow.Boot("[toast] at " + left.ToString("F0") + "," + top.ToString("F0")
            + " size " + win.ActualWidth.ToString("F0") + "x" + win.ActualHeight.ToString("F0"));
    }
}
