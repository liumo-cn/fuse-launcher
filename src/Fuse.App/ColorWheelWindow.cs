using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fuse.App;

/// <summary>
/// 色环取色窗(FR-12 自定义):外环选色相(点击/拖动),亮度滑块明暗,
/// 预览实时变化,确认后由调用方应用并持久化。
/// </summary>
public class ColorWheelWindow : Window
{
    public Color? Selected { get; private set; }

    private double _hue = 120;   // 默认微软绿色相
    private double _brightness = 0.55;
    private readonly Ellipse _preview;
    private readonly TextBlock _hexText;
    private bool _picking;

    private const int WheelSize = 220;
    private const double RingOuter = 104;
    private const double RingInner = 82;

    public ColorWheelWindow(Color initial, Rect hostRect)
    {
        // 定位:设置面板右缘 10px,放不下自动换到左侧,垂直与面板对齐,全部钳制在工作区内
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += (_, _) =>
        {
            try
            {
                var area = SystemParameters.WorkArea;
                var right = hostRect.Right + 10;
                var left = hostRect.Left - ActualWidth - 10;
                Left = right + ActualWidth <= area.Right - 8 ? right
                    : Math.Max(area.Left + 8, Math.Min(area.Right - ActualWidth - 8, left));
                Top = Math.Max(area.Top + 8, Math.Min(area.Bottom - ActualHeight - 8, hostRect.Top));
                // 出场动画:从色块(设置面板)位置由小到大
                PanelPopIn.Show(this, new Point(
                    hostRect.Left + hostRect.Width / 2,
                    hostRect.Top + hostRect.Height / 2));
            }
            finally
            {
                Opacity = 1;   // 构造时是 0(避免先闪一帧完整窗口),定位和动画就绪后恢复
            }
        };

        // 先隐形再显示:否则会先渲染一帧默认位置的完整窗口,再缩回去重播,表现为"闪一下再长大"
        Opacity = 0;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Width = 300;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        var hsv = ColorToHsv(initial);
        _hue = hsv.hue;
        _brightness = hsv.v;

        var chrome = new Border
        {
            Background = (Brush)Application.Current.Resources["PanelBgBrush"],
            CornerRadius = new CornerRadius(20),
            BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 40,
                ShadowDepth = 10,
                Opacity = 0.5,
            },
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "选择颜色",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // 色环
        var wheelHost = new Grid { Width = WheelSize, Height = WheelSize, Margin = new Thickness(0, 16, 0, 8), Cursor = Cursors.Hand };
        var wheel = new Image { Source = BuildHueWheel(WheelSize), IsHitTestVisible = true };
        wheelHost.Children.Add(wheel);
        wheelHost.MouseLeftButtonDown += (_, e) => { _picking = true; wheelHost.CaptureMouse(); Pick(e.GetPosition(wheelHost)); };
        wheelHost.MouseMove += (_, e) => { if (_picking) Pick(e.GetPosition(wheelHost)); };
        wheelHost.MouseLeftButtonUp += (_, _) => { _picking = false; wheelHost.ReleaseMouseCapture(); };
        stack.Children.Add(wheelHost);

        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack,
        };
        Content = chrome;
        chrome.Child = scroll;

        // 亮度滑块
        var brightRow = new Grid { Margin = new Thickness(4, 4, 4, 10) };
        brightRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brightRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brightRow.Children.Add(new TextBlock { Text = "亮度", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center });
        var brightText = new TextBlock { FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], FontFamily = new FontFamily("Consolas") };
        Grid.SetColumn(brightText, 1);
        brightRow.Children.Add(brightText);
        stack.Children.Add(brightRow);
        var slider = new Slider
        {
            Style = (Style)Application.Current.Resources["GreenSlider"],
            Minimum = 30,
            Maximum = 90,
            Value = _brightness * 100,
            TickFrequency = 5,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(4, 2, 4, 2),
        };
        slider.ValueChanged += (_, _) =>
        {
            _brightness = slider.Value / 100.0;
            brightText.Text = $"{(int)slider.Value}%";
            RefreshPreview();
        };
        // 初值在对象初始化器里就已赋好(且吸附到刻度),那次变更挂在回调之前接不到;
        // 这里用滑块实际值同步一次,否则标签永远停留在写死的初始文本上
        brightText.Text = $"{(int)slider.Value}%";
        stack.Children.Add(slider);

        // 预览行(三列:标签/色号/色块,不再重叠)
        var previewRow = new Grid { Margin = new Thickness(4, 10, 4, 10) };
        previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        previewRow.Children.Add(new TextBlock { Text = "预览", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center });
        _hexText = new TextBlock { FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(_hexText, 1);
        previewRow.Children.Add(_hexText);
        _preview = new Ellipse
        {
            Width = 34,
            Height = 34,
            Stroke = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            StrokeThickness = 1,
        };
        Grid.SetColumn(_preview, 2);
        previewRow.Children.Add(_preview);
        stack.Children.Add(previewRow);

        // 确认/取消(胶囊圆角,紧凑)
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button
        {
            Content = "确认更改",
            Style = (Style)Application.Current.Resources["PillPrimary"],
            Margin = new Thickness(0, 0, 10, 0),
        };
        ok.Click += (_, _) =>
        {
            Selected = CurrentColor();
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        var cancel = new Button
        {
            Content = "取消",
            Style = (Style)Application.Current.Resources["PillSecondary"],
        };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);

        Content = stack;
        RefreshPreview();
    }

    private static readonly Brush T2Text = new SolidColorBrush(Color.FromArgb(166, 255, 255, 255));

    private static WriteableBitmap BuildHueWheel(int size)
    {
        var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        var center = size / 2.0;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - center;
            var dy = y - center;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > RingOuter + 1 || dist < RingInner - 1) continue;

            var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            var rgb = HsvToRgb(hue, 1, 1);
            var alpha = 255;
            if (dist > RingOuter) alpha = (int)(255 * (1 - (dist - RingOuter)));
            if (dist < RingInner) alpha = (int)(255 * (1 - (RingInner - dist)));

            var idx = (y * size + x) * 4;
            pixels[idx] = rgb.b;
            pixels[idx + 1] = rgb.g;
            pixels[idx + 2] = rgb.r;
            pixels[idx + 3] = (byte)Math.Max(0, Math.Min(255, alpha));
        }
        wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        return wb;
    }

    private void Pick(Point pos)
    {
        var dx = pos.X - WheelSize / 2.0;
        var dy = pos.Y - WheelSize / 2.0;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < RingInner - 2 || dist > RingOuter + 2) return;
        _hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        RefreshPreview();
    }

    private Color CurrentColor()
    {
        var rgb = HsvToRgb(_hue, 0.9, _brightness);
        return Color.FromRgb(rgb.r, rgb.g, rgb.b);
    }

    private void RefreshPreview()
    {
        var c = CurrentColor();
        _preview.Fill = new SolidColorBrush(c);
        _hexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    // HSV → RGB(hue 0-360, s/v 0-1)
    internal static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    internal static (double hue, double s, double v) ColorToHsv(Color c)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

}
