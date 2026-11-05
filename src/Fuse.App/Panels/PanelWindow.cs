using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Fuse.App.Panels;

/// <summary>
/// 玻璃面板窗体基座：无边框透明 + 系统亚克力 + 失焦自动关闭。
/// 由悬浮组件的花瓣锚定弹出（FR-12）。
/// </summary>
public class PanelWindow : Window
{
    public static readonly Dictionary<string, PanelWindow> OpenPanels = new();

    public static bool AnyPanelOpen => OpenPanels.Count > 0;

    /// <summary>关闭所有已打开的面板(启动爆炸收进托盘前,让"所有页面消失")。</summary>
    public static void CloseAll()
    {
        foreach (var panel in new List<PanelWindow>(OpenPanels.Values))
        {
            try { panel.Close(); } catch (Exception) { }
        }
        OpenPanels.Clear();
    }

    /// <summary>面板内容控件(供外部刷新,如删除版本后刷新版本列表)。</summary>
    public object? PanelContent { get; private set; }

    /// <summary>窗口左侧留给下载圆环的宽度。</summary>
    private const double RingGutter = 0;

    /// <summary>弹出面板。petalPoint 为花瓣在屏幕上的位置;onLeft 表示花瓣在组件左侧。</summary>
    public static void ShowPanel(string kind, Point petalPoint, bool onLeft, WidgetWindow widget)
    {
        Fuse.Core.Logging.LogSink.Append(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuseLauncher", "boot.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] [panel] ShowPanel {kind}\n");
        // 同类面板单实例:已打开则仅激活,不重复创建(用户要求)
        if (OpenPanels.TryGetValue(kind, out var existing))
        {
            existing.Activate();
            return;
        }


        PanelWindow window = kind switch
        {
            "versions" => Create(kind, "本地已安装版本", new VersionsPanel(), widget),
            "download" => Create(kind, "下载", new DownloadPanel(), widget),
            "settings" => Create(kind, "设置", new SettingsPanel(), widget),
            "login" => Create(kind, "账号登录", new LoginPanel(), widget),
            "more" => Create(kind, "更多", new MorePanel(), widget),
            "version-detail" => Create(kind, VersionDetailPanel.PendingTitle ?? "详情",
                new VersionDetailPanel(VersionDetailPanel.PendingVersionId ?? ""), widget, width: 380),
            // 给已有原版版本装加载器:独立面板(可与下载页并存),标题为该版本显示名
            "loader-install" => Create(kind, DownloadPanel.PendingFixedTitle ?? "安装加载器",
                new DownloadPanel(DownloadPanel.PendingFixedVersionId), widget),
            _ => throw new ArgumentException("未知面板 " + kind),
        };

        // 先转成全透明再 Show:WPF 在 Show 之后会立刻渲染一帧"默认位置、完整尺寸"的窗口,
        // 之后我们才定位 + 缩回 0.35 重播动画——用户看到的就是"完整页面闪一下、消失、再长大"。
        // 透明度设为 0 后这一帧不可见,定位和动画都在同一帧内完成,再把窗口恢复不透明。
        window.Opacity = 0;
        window.Show();
        try
        {
            // SizeToContent 测量完成后才有 Actual 尺寸,定位必须在显示后做
            window.UpdateLayout();
            var area = SystemParameters.WorkArea;
            // 默认位置:面板中心 = 启动大圆圆心(用户指定),并钳制在工作区内
            var orbCenterX = widget.Left + widget.ActualWidth / 2;
            var orbCenterY = widget.Top + widget.ActualHeight / 2;
            // 多个不同类面板可同时打开:逐层错开,否则完全重叠、看不到下层还有东西
            var stack = (OpenPanels.Count - 1) * 26.0;
            // 圆环占掉了左侧 RingGutter,面板中心 = 窗口左 + RingGutter + 面板宽/2
            var panelWidth = window.ActualWidth - RingGutter;
            var finalLeft = orbCenterX - panelWidth / 2 - RingGutter + stack;
            var finalTop = orbCenterY - window.ActualHeight / 2 + stack;
            window.Left = Math.Max(area.Left + 8, Math.Min(area.Right - window.ActualWidth - 8, finalLeft));
            window.Top = Math.Max(area.Top + 8, Math.Min(area.Bottom - window.ActualHeight - 8, finalTop));
            // 面板生成在启动大圆前面(覆盖大圆/花瓣),✕ 关闭后组件恢复交互

            // 出场动画:从生成点(花瓣点击处)由小到大,0.3s。
            // petalPoint 是物理像素,这里转回 DIP 再换算成窗口内相对位置;(0,0) = 详情页等无锚点入口,退回窗口中心
            var dpi = VisualTreeHelper.GetDpi(window);
            var anchor = petalPoint.X == 0 && petalPoint.Y == 0
                ? new Point(window.Left + window.ActualWidth / 2, window.Top + window.ActualHeight / 2)
                : new Point(petalPoint.X / dpi.DpiScaleX, petalPoint.Y / dpi.DpiScaleY);
            PanelPopIn.Show(window, anchor);
        }
        finally
        {
            // 无论动画是否成功,窗口都必须恢复可见
            window.Opacity = 1;
        }
    }

    private static PanelWindow Create(string kind, string title, UserControl content, WidgetWindow widget, int width = 336)
    {
        var window = new PanelWindow
        {
            // 左侧多留一条 RingGutter:圆环画在面板卡片之外(用户要求"页面外显示")
            Width = width + RingGutter,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 660,
            PanelContent = content,
        };
        var chrome = BuildChrome(kind, title, content, out var titleBar, out var closeButton);
        window.Content = chrome;
        // ✕ 关闭符号:唯一关闭方式(用户要求,不再失焦自动关闭)
        closeButton.Cursor = System.Windows.Input.Cursors.Hand;
        // Preview 阶段拦截:阻止事件冒泡到标题条触发拖动捕获
        closeButton.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        closeButton.MouseLeftButtonUp += (_, _) => window.Close();
        // 标题栏手动拖拽(捕获鼠标 + 增量移动,比 DragMove 可靠)
        var dragging = false;
        var dragOrigin = new Point();
        titleBar.Cursor = System.Windows.Input.Cursors.SizeAll;
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            dragOrigin = e.GetPosition(window);
            titleBar.CaptureMouse();
        };
        titleBar.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetPosition(window);
            window.Left += p.X - dragOrigin.X;
            window.Top += p.Y - dragOrigin.Y;
        };
        titleBar.MouseLeftButtonUp += (_, _) =>
        {
            dragging = false;
            titleBar.ReleaseMouseCapture();
        };
        OpenPanels[kind] = window;
        window.Closed += (_, _) =>
        {
            Fuse.Core.Logging.LogSink.Append(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuseLauncher", "boot.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [panel] Closed {kind}\n");
            OpenPanels.Remove(kind);
        };
        return window;
    }

    private static UIElement BuildChrome(string kind, string title, UserControl content, out Grid titleBar, out Border closeButton)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            // 不挂 DropShadowEffect:分层窗口只在窗口矩形内绘制,圆角外的阴影会被窗口边缘
            // 硬切成一圈半透明灰方框(浅色背景上尤其明显)。层次感靠 1px 亮边 + 90% 深色底。
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBgBrush");
        var titleGrid = new Grid
        {
            Background = Brushes.Transparent, // 无分层色带,但整条可拖
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        var titleText = new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var closeText = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            Child = new TextBlock
            {
                Text = "✕", FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(closeText, 1);
        titleGrid.Children.Add(titleText);
        titleGrid.Children.Add(closeText);
        titleBar = titleGrid;
        closeButton = closeText;
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 12),
            Child = titleGrid,
        });
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        stack.Children.Add(content);
        border.Child = stack;

        // 进度圆环:在卡片**下方**的窗口外侧条带里,左侧与页面左边缘对齐(用户指定)
        var ring = new Controls.DownloadRing(kind);
        ring.HorizontalAlignment = HorizontalAlignment.Left;
        ring.VerticalAlignment = VerticalAlignment.Center;
        ring.Margin = new Thickness(0, 4, 0, 8);
        // 高度自动:没有下载任务时不留空白(圆环整体 Collapsed)
        var ringStrip = new Grid();
        ringStrip.Children.Add(ring);

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(border, 0);
        outer.Children.Add(border);
        Grid.SetRow(ringStrip, 1);
        outer.Children.Add(ringStrip);
        return outer;
    }

    public PanelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        ResizeMode = ResizeMode.NoResize;
        // 不启用窗口亚克力:面板底色 90% 不透明,亚克力收益趋近于零,
        // 却会在浅色背景上显出一圈灰色方框(整窗矩形调色,圆角外也覆盖)
    }
}
