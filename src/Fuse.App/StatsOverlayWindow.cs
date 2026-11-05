using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Fuse.Core.Config;
using Fuse.Core.Monitoring;

namespace Fuse.App;

/// <summary>
/// 数据浮窗:一行显示 FPS/GPU/CPU/内存/网速/延迟,无边框可拖动,位置记忆。
/// 采样在 DispatcherTimer(1s)里做,数值计算已在 Core 层后台化,这里只贴字符串。
/// </summary>
public sealed class StatsOverlayWindow : Window
{
    private readonly TextBlock _fpsText;
    private readonly TextBlock _gpuText;
    private readonly TextBlock _cpuText;
    private readonly TextBlock _memText;
    private readonly TextBlock _netText;
    private readonly TextBlock _pingText;
    private readonly SystemMetrics _metrics = new();
    private readonly FpsMonitor _fps = new();
    private readonly DispatcherTimer _timer;
    private Process? _trackedGame;

    private const double PidWidth = 44;   // 每个分段前的小图标宽度

    public StatsOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;

        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fpsText = Segment(panel, "FPS");
        _gpuText = Segment(panel, "GPU");
        _cpuText = Segment(panel, "CPU");
        _memText = Segment(panel, "RAM");
        _netText = Segment(panel, "NET");
        _pingText = Segment(panel, "PING", last: true);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x11, 0x16)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 5, 12, 5),
            Child = panel,
            Cursor = Cursors.SizeAll,
        };
        Content = card;

        // 全窗拖动
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }
            SavePosition();
        };

        Width = 560;
        Height = 34;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => RefreshOnce();
    }

    private static TextBlock Segment(StackPanel host, string label, bool last = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };
        row.Children.Add(icon);
        var value = new TextBlock
        {
            Text = "--",
            FontSize = 12.5,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 52,
            TextAlignment = TextAlignment.Left,
        };
        row.Children.Add(value);
        if (!last)
        {
            row.Children.Add(new TextBlock
            {
                Text = "│",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
            });
        }
        host.Children.Add(row);
        return value;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RestorePosition();
    }

    public void Start()
    {
        WidgetWindow.Boot("[overlay] start: left=" + Left + " top=" + Top);
        Show();
        WidgetWindow.Boot("[overlay] shown, is_visible=" + IsVisible);
        _timer.Start();
        TrackGameProcess();
        RefreshOnce();
    }

    public new void Hide()
    {
        _timer.Stop();
        _fps.Stop();
        _trackedGame = null;
        base.Hide();
    }

    /// <summary>找当前运行的 MC 进程并让 FPS 监控跟上;游戏退出后自动停止 FPS 采样。</summary>
    private void TrackGameProcess()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("javaw"))
            {
                if (p.HasExited) continue;
                if (ReferenceEquals(p, _trackedGame)) return;
                _trackedGame?.Dispose();
                _trackedGame = p;
                _fps.Start(p.Id);
                return;
            }
            // 没找到游戏:FPS 置空
            if (_fps.Fps > 0 || _trackedGame != null)
            {
                _fps.Stop();
                _trackedGame?.Dispose();
                _trackedGame = null;
            }
        }
        catch (Exception)
        {
        }
    }

    private void RefreshOnce()
    {
        TrackGameProcess();
        var sample = _metrics.Sample();

        _fpsText.Text = _fps.Fps > 0 ? Math.Round(_fps.Fps) + "" : "--";
        _gpuText.Text = sample.GpuPercent > 0 ? Math.Round(sample.GpuPercent) + "%" : "--";
        _cpuText.Text = Math.Round(sample.CpuPercent) + "%";
        _memText.Text = sample.MemoryTotalMb > 0
            ? sample.MemoryUsedMb + "M"
            : "--";
        _netText.Text = FormatSpeed(sample.NetworkDownBps + sample.NetworkUpBps);

            RefreshPingAsync();
    }

    private bool _pingBusy;

    private async void RefreshPingAsync()
    {
        if (_pingBusy) return;
        _pingBusy = true;
        try
        {
            // 自动加入的服务器优先(与服务器列表页同一来源),没有就用 servers.dat 第一条
            var profileAddress = GetAutoJoinAddress();
            var address = string.IsNullOrWhiteSpace(profileAddress)
                ? Fuse.Core.Monitoring.PingProbe.PickAddress(
                    Fuse.Core.Versioning.ServersDat.Read(
                        System.IO.Path.Combine(AppServices.ActiveGameDirectory, "servers.dat")), null)
                : profileAddress;

            if (string.IsNullOrWhiteSpace(address))
            {
                _pingText.Text = "--";
                return;
            }
            var (host, port) = Fuse.Core.Monitoring.PingProbe.Split(address!);
            var ping = await Fuse.Core.Monitoring.PingProbe.MeasureAsync(host, port, 2000).ConfigureAwait(false);
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _pingText.Text = ping.HasValue ? ping.Value + "ms" : "--";
            }));
        }
        catch (Exception)
        {
        }
        finally
        {
            _pingBusy = false;
        }
    }

    /// <summary>最近启动版本的自动加入服务器地址(与服务器列表页同一来源)。</summary>
    private static string? GetAutoJoinAddress()
    {
        var last = AppServices.Config.LastLaunchedVersion;
        if (string.IsNullOrEmpty(last)) return null;
        return AppServices.VersionProfiles.Get(last!).AutoJoinServer;
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0K";
        if (bytesPerSecond < 1024 * 1024) return (bytesPerSecond / 1024.0).ToString("0.#") + "K";
        return (bytesPerSecond / 1024.0 / 1024.0).ToString("0.#") + "M";
    }

    // ---------- 位置记忆 ----------

    private void RestorePosition()
    {
        // Width 在构造函数里已赋值,但防御 NaN(布局尚未完成时 OnSourceInitialized 可能先到)
        var w = double.IsNaN(Width) || Width <= 0 ? 560 : Width;
        var config = AppServices.Config;
        if (config.StatsOverlayLeft >= 0 && config.StatsOverlayTop >= 0)
        {
            Left = config.StatsOverlayLeft;
            Top = config.StatsOverlayTop;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - w - 24;
            Top = area.Top + 60;
        }
        ClampToWorkArea(w);
    }

    private void SavePosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        var config = AppServices.Config;
        config.StatsOverlayLeft = Left;
        config.StatsOverlayTop = Top;
        AppServices.SaveConfig();
    }

    private void ClampToWorkArea(double width)
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, Math.Min(area.Right - width, Left));
        Top = Math.Max(area.Top, Math.Min(area.Bottom - ActualHeight, Top));
    }
}
