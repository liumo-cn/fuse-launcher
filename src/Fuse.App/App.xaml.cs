using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Fuse.Core.Config;
using Fuse.Core.Download;

namespace Fuse.App;

public partial class App : Application
{
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Threading.Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例:双开会出现两个托盘/组件,还会互相争抢 config 与下载队列
        _singleInstance = new System.Threading.Mutex(true, @"Local\FuseLauncher.SingleInstance", out var isNew);
        if (!isNew)
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        Boot("startup begin v" + AppServices.AppVersion);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog("后台线程未处理异常", (Exception)args.ExceptionObject);
            ErrorReporter.Report("后台线程未处理异常", args.ExceptionObject as Exception);
        };

        try
        {
            AppServices.Initialize();
            Boot("services initialized");
        }
        catch (Exception ex)
        {
            WriteCrashLog("初始化失败", ex);
            MessageBox.Show($"初始化失败：{ex.Message}\n\n详细信息已写入 crash.log", AppServices.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // 开屏动画(FR-12):在组件出现的位置播放,结晶后原地变成启动大圆
        double wx, wy;
        if (AppServices.Config.WidgetLeft >= 0 && AppServices.Config.WidgetTop >= 0)
        {
            wx = AppServices.Config.WidgetLeft;
            wy = AppServices.Config.WidgetTop;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            wx = area.Right - 460 - 40;
            wy = area.Bottom - 380 - 40;
        }
        // 开屏动画(FR-12):在组件出现的位置播放,结晶后原地变成启动大圆。
        // 圆心用组件自己声明的布局常量(不是窗口中心:大圆在 SafeArea 内偏移了 98,98)。
        var orbLocal = WidgetWindow.OrbCenterInWindow;
        var splash = new SplashWindow(new System.Windows.Point(wx + orbLocal.X, wy + orbLocal.Y));
        splash.Finished += () => Dispatcher.Invoke(ShowWidget);
        splash.Show();
        Boot("splash shown");

        InitTray();
        Boot("tray initialized");
    }

    private WidgetWindow? _widget;
    private StatsOverlayWindow? _statsOverlay;

    /// <summary>数据浮窗统一入口(设置开关与启动恢复共用):on = 创建/显示,off = 关闭。</summary>
    public static void ApplyStatsOverlay(bool on)
    {
        var app = Current as App;
        if (app == null) return;
        app.ApplyStatsOverlayCore(on);
    }

    private void ApplyStatsOverlayCore(bool on)
    {
        if (on)
        {
            try
            {
                _statsOverlay ??= new StatsOverlayWindow();
                _statsOverlay.Start();
                Boot("[overlay] shown");
            }
            catch (Exception ex)
            {
                Boot("[overlay] show failed: " + ex.Message);
                _statsOverlay = null;
            }
        }
        else
        {
            _statsOverlay?.Hide();
            _statsOverlay?.Close();
            _statsOverlay = null;
            Boot("[overlay] hidden");
        }
    }

    private void ShowWidget()
    {
        _widget = new WidgetWindow();
        AppServices.Widget = _widget;
        _widget.Show();
        if (!AppServices.Config.ShowWidget) _widget.Hide();
        Boot("widget shown");
        // 数据浮窗:配置开启时随启动器出现(位置记忆)
        if (AppServices.Config.ShowStatsOverlay)
        {
            try
            {
                _statsOverlay = new StatsOverlayWindow();
                _statsOverlay.Start();
                Boot("[overlay] restored from config");
            }
            catch (Exception ex)
            {
                Boot("[overlay] restore failed: " + ex.Message);
                _statsOverlay = null;
            }
        }

        // 自动更新:开启时启动后台检查,发现新版本弹提示(失败静默,不打扰)
        _ = AutoCheckUpdateAsync();
        // 诊断:开屏圆心(期望) vs 大圆真实圆心(实测),diff 应为 0,0
        try
        {
            var px = _widget.OrbCenterOnScreen; // PointToScreen 给的是物理像素
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_widget);
            var actualDip = new System.Windows.Point(px.X / dpi.DpiScaleX, px.Y / dpi.DpiScaleY);
            var expectDip = new System.Windows.Point(
                _widget.Left + WidgetWindow.OrbCenterInWindow.X,
                _widget.Top + WidgetWindow.OrbCenterInWindow.Y);
            Boot($"orb center actual={actualDip.X:F1},{actualDip.Y:F1} " +
                 $"expected={expectDip.X:F1},{expectDip.Y:F1} " +
                 $"diff={actualDip.X - expectDip.X:F1},{actualDip.Y - expectDip.Y:F1}");
        }
        catch (Exception ex)
        {
            Boot("orb center probe failed: " + ex.Message);
        }
    }

    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            // 悬停提示:名称 + 版本号(NotifyIcon.Text 上限 63 字符,绰绰有余)
            Text = "Fuse Launcher V" + AppServices.AppVersion,
            Visible = true,
            Icon = AppIcon.BuildTrayIcon(),
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏悬浮组件", null, (_, _) => ToggleWidget());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left) ToggleWidget();
        };
    }

    private void ToggleWidget()
    {
        if (_widget == null) return;
        if (_widget.IsVisible)
        {
            _widget.Hide();
            AppServices.Config.ShowWidget = false;
        }
        else
        {
            _widget.Show();
            AppServices.Config.ShowWidget = true;
        }
        AppServices.SaveConfig();
    }

    /// <summary>启动后台检查更新:开启「自动更新」且配置了更新源时,发现新版弹提示(OK=下次再说)。</summary>
    private async System.Threading.Tasks.Task AutoCheckUpdateAsync()
    {
        try
        {
            if (!AppServices.Config.AutoUpdate) { Boot("[update] auto off"); return; }
            if (Updater.ConfiguredUrl == null) { Boot("[update] no source"); return; }
            var m = await Updater.CheckAsync();
            Boot("[update] remote " + m.Version + " current " + AppServices.AppVersion);
            if (!Updater.IsNewer(m.Version, AppServices.AppVersion)) { Boot("[update] up to date"); return; }
            Boot("[update] new version found, showing dialog");
            await Dispatcher.InvokeAsync(() =>
            {
                NoticeDialog.Show(
                    "发现新版本 V" + m.Version,
                    (string.IsNullOrEmpty(m.Notes) ? "" : m.Notes + "\n\n") +
                    "将下载并静默安装,完成后自动重启启动器。",
                    "立即更新",
                    () => _ = ApplyUpdateAsync(m));
            });
        }
        catch (Exception ex)
        {
            Boot("[update] auto check failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static async System.Threading.Tasks.Task ApplyUpdateAsync(Updater.Manifest m)
    {
        try
        {
            var path = await Updater.DownloadAsync(m, null);
            Updater.ApplyAndRestart(path);
        }
        catch (Exception ex)
        {
            NoticeDialog.Show("更新失败", ex.Message, null, null);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UI 线程未处理异常", e.Exception);
        MessageBox.Show($"发生未处理的异常：{e.Exception.Message}\n\n完整堆栈已写入 crash.log（%APPDATA%\\MCDownloader）",
            AppServices.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void Boot(string message)
    {
        var dir = Path.GetDirectoryName(AppServices.ConfigPath)!;
        Directory.CreateDirectory(dir);
        Fuse.Core.Logging.LogSink.Append(Path.Combine(dir, "boot.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
    }

    private static void WriteCrashLog(string title, Exception ex)
    {
        var dir = Path.GetDirectoryName(AppServices.ConfigPath)!;
        Directory.CreateDirectory(dir);
        Fuse.Core.Logging.LogSink.Append(Path.Combine(dir, "crash.log"),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}\n{ex}\n\n");
    }
}
