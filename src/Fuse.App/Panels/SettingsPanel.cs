using System;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fuse.Core.Config;
using Fuse.Core.Java;
using Microsoft.Win32;

namespace Fuse.App.Panels;

/// <summary>设置面板(FR-12):启动器文件夹 / Java 更换 / 内存滑块 / 可见性 Toggle / 版本隔离 / 下载源,自动保存。</summary>
public class SettingsPanel : UserControl
{
    private readonly TextBlock?[] _javaPick;

    public SettingsPanel()
    {
        _javaPick = new TextBlock?[] { null };
        // 设置项较多:内容超出面板高度时内部滚动。
        // 上限必须小于「面板 MaxHeight(660) - 内边距/标题条」,否则 ScrollViewer 自己会被裁掉一截,
        // 导致滚到底也看不到最后一行的按钮。
        Content = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildRoot(),
        };
        LoadValues();
    }

    private UIElement BuildRoot()
    {
        var root = new StackPanel();

        // 1 下载路径
        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pathInfo = new StackPanel();
        pathInfo.Children.Add(new TextBlock { Text = "启动器文件夹", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        var pathValue = new TextBlock { Text = AppServices.InstallDirectory, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], FontFamily = new FontFamily("Consolas") };
        pathValue.TextTrimming = TextTrimming.CharacterEllipsis;
        pathInfo.Children.Add(pathValue);
        Grid.SetColumn(pathInfo, 0);
        pathRow.Children.Add(pathInfo);
        var browse = new Button { Content = "修改", Style = (Style)Application.Current.Resources["ChipButton"] };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择安装目录（选择该目录下任意位置）",
                FileName = "选择此目录",
                CheckFileExists = false,
            };
            if (dialog.ShowDialog() == true)
            {
                var dir = System.IO.Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(dir))
                {
                    AppServices.Config.InstallDirectory = dir;
                    Save();
                    pathValue.Text = dir;
                    // 主目录变了:已打开的版本面板里是旧目录的版本,必须立刻重建;
                    // 否则点旧行启动会按新目录找 version.json,报"版本文件不完整"
                    if (PanelWindow.OpenPanels.TryGetValue("versions", out var versionsPanel) &&
                        versionsPanel.PanelContent is VersionsPanel list)
                        list.Refresh();
                }
            }
        };
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);
        root.Children.Add(Wrap("pathRow", pathRow));

        // 2 Java 版本(手风琴列表:点击展开,列表底色同卡片、文字白色)
        var javaHeader = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        javaHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        javaHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var javaInfo = new StackPanel();
        javaInfo.Children.Add(new TextBlock { Text = "Java 版本", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        var javaValue = new TextBlock { FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], FontFamily = new FontFamily("Consolas") };
        _javaPick[0] = javaValue;
        javaInfo.Children.Add(javaValue);
        Grid.SetColumn(javaInfo, 0);
        javaHeader.Children.Add(javaInfo);
        var javaChev = new TextBlock { Text = "▾", FontSize = 12, Foreground = T2, VerticalAlignment = VerticalAlignment.Center };
        JavaChevron = javaChev;
        Grid.SetColumn(javaChev, 1);
        javaHeader.Children.Add(javaChev);
        javaHeader.MouseLeftButtonUp += (_, _) =>
        {
            _javaExpanded = !_javaExpanded;
            RefreshJavaSection();
        };
        root.Children.Add(Wrap("javaHeader", javaHeader, out _));
        _javaOptions = new StackPanel { Visibility = Visibility.Collapsed };
        root.Children.Add(_javaOptions);

        // 3 内存滑块
        var memRow = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var memTop = new Grid { Margin = new Thickness(14, 10, 14, 8) };
        memTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        memTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        memTop.Children.Add(new TextBlock { Text = "内存分配", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        var memValue = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var memChip = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 4, 10, 4),
            Child = memValue,
        };
        memChip.SetResourceReference(Border.BackgroundProperty, "AccentChipBrush");
        Grid.SetColumn(memChip, 1);
        memTop.Children.Add(memChip);
        memRow.Children.Add(memTop);
        var slider = new Slider
        {
            Style = (Style)Application.Current.Resources["GreenSlider"],
            Minimum = 1024,
            Maximum = 16384,
            TickFrequency = 512,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(14, 0, 14, 12),
        };
        slider.ValueChanged += (_, _) =>
        {
            // 自动模式下滑块是禁用的,这里再兜一层,避免程序化赋值把自动值写成手动
            if (AppServices.Config.AutoHeap) return;
            memValue.Text = $"{(int)slider.Value} MB";
            AppServices.Config.MaxHeapMb = (long)slider.Value;
            Save();
        };
        memRow.Children.Add(slider);

        // 自动分配开关:按物理内存 + 当前版本模组数计算(每版本详情页还能单独固定)
        var autoRow = new Grid { Margin = new Thickness(14, 0, 14, 12) };
        autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var autoInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        autoInfo.Children.Add(new TextBlock
        {
            Text = "自动分配内存", FontSize = 12.5, Foreground = T2,
        });
        Grid.SetColumn(autoInfo, 0);
        autoRow.Children.Add(autoInfo);
        var autoToggle = new CheckBox
        {
            Style = (Style)Application.Current.Resources["GlassToggle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(autoToggle, 1);
        autoRow.Children.Add(autoToggle);
        autoToggle.Checked += (_, _) => SetAutoHeap(true);
        autoToggle.Unchecked += (_, _) => SetAutoHeap(false);
        memRow.Children.Add(autoRow);

        root.Children.Add(memRow);
        _memSlider = slider;
        _memValue = memValue;
        _autoHeapToggle = autoToggle;

        // 4 启动器可见性 Toggle
        var visRow = new Grid();
        visRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        visRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var visInfo = new StackPanel();
        visInfo.Children.Add(new TextBlock { Text = "启动器可见性", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        Grid.SetColumn(visInfo, 0);
        visRow.Children.Add(visInfo);
        var visStack = new StackPanel();
        var toggle = new CheckBox { Style = (Style)Application.Current.Resources["GlassToggle"], HorizontalAlignment = HorizontalAlignment.Right };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "启动器可见性");
        var state = new TextBlock { Text = "已开启", FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        state.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        visStack.Children.Add(toggle);
        visStack.Children.Add(state);
        Grid.SetColumn(visStack, 1);
        visRow.Children.Add(visStack);
        // 只保存"启动后是否保持显示",不再立即显示/隐藏组件(手动显隐走托盘的「显示 / 隐藏悬浮组件」)
        toggle.Checked += (_, _) => SetKeepVisibleOnLaunch(true);
        toggle.Unchecked += (_, _) => SetKeepVisibleOnLaunch(false);
        root.Children.Add(Wrap("visRow", visRow, out _));
        _visToggle = toggle;
        _visState = state;

        // 4b 自动更新 Toggle
        var updRow = new Grid();
        updRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        updRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var updInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        updInfo.Children.Add(new TextBlock { Text = "自动更新", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        Grid.SetColumn(updInfo, 0);
        updRow.Children.Add(updInfo);
        var updToggle = new CheckBox
        {
            Style = (Style)Application.Current.Resources["GlassToggle"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        System.Windows.Automation.AutomationProperties.SetName(updToggle, "自动更新");
        Grid.SetColumn(updToggle, 1);
        updRow.Children.Add(updToggle);
        // 与"启动器可见性"同款:监听 Checked/Unchecked(UIA TogglePattern 不触发 Click)
        updToggle.Checked += (_, _) => SetAutoUpdate(true);
        updToggle.Unchecked += (_, _) => SetAutoUpdate(false);
        root.Children.Add(Wrap("updRow", updRow, out _));
        _autoUpdateToggle = updToggle;

        // 4c 开机自启动 Toggle(默认关;写/删 HKCU Run 注册表项,立即生效)
        var autoRunRow = new Grid();
        autoRunRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        autoRunRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var autoRunInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        autoRunInfo.Children.Add(new TextBlock { Text = "开机自启动", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        Grid.SetColumn(autoRunInfo, 0);
        autoRunRow.Children.Add(autoRunInfo);
        var autoRunToggle = new CheckBox
        {
            Style = (Style)Application.Current.Resources["GlassToggle"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        System.Windows.Automation.AutomationProperties.SetName(autoRunToggle, "开机自启动");
        Grid.SetColumn(autoRunToggle, 1);
        autoRunRow.Children.Add(autoRunToggle);
        autoRunToggle.Checked += (_, _) => SetLaunchOnStartup(true);
        autoRunToggle.Unchecked += (_, _) => SetLaunchOnStartup(false);
        root.Children.Add(Wrap("autoRunRow", autoRunRow, out _));
        _autoRunToggle = autoRunToggle;

        // 4c2 数据浮窗 Toggle(一行显示 FPS/GPU/CPU/内存/网速/延迟)
        var overlayRow = new Grid();
        overlayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        overlayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var overlayInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        overlayInfo.Children.Add(new TextBlock { Text = "数据浮窗", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        overlayInfo.Children.Add(new TextBlock
        {
            Text = "一行显示 FPS / GPU / CPU / 内存 / 网速 / 延迟，可拖动，位置自动记忆",
            FontSize = 10.5, Foreground = T2, Opacity = 0.66, TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(overlayInfo, 0);
        overlayRow.Children.Add(overlayInfo);
        var overlayToggle = new CheckBox
        {
            Style = (Style)Application.Current.Resources["GlassToggle"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        System.Windows.Automation.AutomationProperties.SetName(overlayToggle, "数据浮窗");
        Grid.SetColumn(overlayToggle, 1);
        overlayRow.Children.Add(overlayToggle);
        overlayToggle.Checked += (_, _) => SetStatsOverlay(true);
        overlayToggle.Unchecked += (_, _) => SetStatsOverlay(false);
        root.Children.Add(Wrap("overlayRow", overlayRow, out _));
        _overlayToggle = overlayToggle;

        // 4d 进程优先级手风琴(高/正常/低):启动后应用到 MC 进程,调整 CPU 资源分配
        // 注意:卡片本身(OptRow)已有 14,10 内边距,这里不能再加外边距,否则文字比别的卡片缩进一截
        var priHead = new Grid();
        priHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        priHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        priHead.Children.Add(new TextBlock { Text = "进程优先级", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        _priorityChevron = new TextBlock { Text = "▸", FontSize = 11, Foreground = T2, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        _priorityValue = new TextBlock
        {
            Text = PriorityDisplay(AppServices.Config.GamePriority),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        // 数值和箭头同在第 1 列,必须放进同一个横向栈里,否则会叠在一起
        var priRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        priRight.Children.Add(_priorityValue);
        priRight.Children.Add(_priorityChevron);
        Grid.SetColumn(priRight, 1);
        priHead.Children.Add(priRight);
        var priHeader = new Button { Style = (Style)Application.Current.Resources["FlatRowButton"], Content = priHead, Padding = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(priHeader, "进程优先级 展开");
        _priorityOptions = new StackPanel { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        foreach (var (label, key, desc) in new[]
        {
            ("高", "high", "游戏优先占用 CPU(机器空闲或追求流畅时用)"),
            ("正常", "normal", "系统标准调度(推荐,大多数情况够用)"),
            ("低", "low", "游戏让路给其他程序(挂后台挂机、边干活边玩)"),
        })
        {
            var capturedKey = key;
            var capturedLabel = label;
            var capturedDesc = desc;
            var optGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            optGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            optGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var optInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            optInfo.Children.Add(new TextBlock { Text = capturedLabel, FontSize = 12, Foreground = T2, FontWeight = FontWeights.SemiBold });
            optInfo.Children.Add(new TextBlock { Text = capturedDesc, FontSize = 10.5, Foreground = T2, Opacity = 0.66, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(optInfo, 0);
            optGrid.Children.Add(optInfo);
            var optRow = new Button
            {
                Style = (Style)Application.Current.Resources["FlatRowButton"],
                Content = optGrid,
                Padding = new Thickness(10, 6, 10, 6),
            };
            System.Windows.Automation.AutomationProperties.SetName(optRow, "优先级" + capturedLabel);
            optRow.Click += (_, _) =>
            {
                AppServices.Config.GamePriority = capturedKey;
                Save();
                RefreshPrioritySelection();
            };
            _priorityOptions!.Children.Add(optRow);
        }
        var priStack = new StackPanel();
        priStack.Children.Add(priHeader);
        priStack.Children.Add(_priorityOptions);
        priHeader.Click += (_, _) =>
        {
            _priorityExpanded = !_priorityExpanded;
            _priorityOptions.Visibility = _priorityExpanded ? Visibility.Visible : Visibility.Collapsed;
            _priorityChevron.Text = _priorityExpanded ? "▾" : "▸";
        };
        root.Children.Add(Wrap("priCard", priStack, out _));
        RefreshPrioritySelection();

        // 4c 版本更新:状态文字 + 检查按钮
        var verRow = new Grid();
        verRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        verRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var verInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        verInfo.Children.Add(new TextBlock { Text = "版本更新", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        _updateStatus = new TextBlock
        {
            Text = "当前 V" + AppServices.AppVersion,
            FontSize = 11, Foreground = T2, Opacity = 0.66, Margin = new Thickness(0, 3, 0, 0),
        };
        verInfo.Children.Add(_updateStatus);
        Grid.SetColumn(verInfo, 0);
        verRow.Children.Add(verInfo);
        _updateBtn = new Button
        {
            Content = "检查更新",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _updateBtn.Click += (_, _) => RunUpdateCheck();
        Grid.SetColumn(_updateBtn, 1);
        verRow.Children.Add(_updateBtn);
        root.Children.Add(Wrap("verRow", verRow, out _));

        // 5 版本隔离(只读)
        root.Children.Add(ReadOnlyRow("版本隔离", "完全隔离"));

        // 6 自定义(色环取色,FR-12)
        root.Children.Add(new TextBlock
        {
            Text = "自定义",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            Margin = new Thickness(0, 6, 0, 12),
        });
        _accentSwatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(AppServices.ParseHex(AppServices.Config.AccentColorHex, Color.FromRgb(0xA4, 0x16, 0xE5))),
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
        _bgSwatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(AppServices.ParseHex(AppServices.Config.PanelBgHex, Color.FromRgb(0x1E, 0x20, 0x26))),
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
        _hoverSwatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(AppServices.ParseHex(
                AppServices.Config.HoverColorHex,
                AppServices.ParseHex(AppServices.Config.AccentColorHex, Color.FromRgb(0xA4, 0x16, 0xE5)))),
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
        root.Children.Add(ColorRow("组件高亮颜色", _accentSwatch, ColorTarget.Accent));
        root.Children.Add(ColorRow("页面背景颜色", _bgSwatch, ColorTarget.Panel));
        root.Children.Add(ColorRow("点击高亮颜色", _hoverSwatch, ColorTarget.Hover));

        var resetBtn = new Button
        {
            Content = "重置默认配色",
            Style = (Style)Application.Current.Resources["ChipButton"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        resetBtn.Click += (_, _) =>
        {
            var c = AppServices.Config;
            c.AccentColorHex = null;
            c.PanelBgHex = null;
            c.HoverColorHex = null; // 回到"跟随组件高亮色"
            Save();
            AppServices.ApplyTheme();
            _accentSwatch!.Background = new SolidColorBrush(Color.FromRgb(0xA4, 0x16, 0xE5));
            _bgSwatch!.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x26));
            _hoverSwatch!.Background = new SolidColorBrush(Color.FromRgb(0xA4, 0x16, 0xE5));
        };
        root.Children.Add(resetBtn);

        // 8 自动补全文件
        root.Children.Add(BuildRepairSection());

        // 9 关于:产品名 / 版本 / 作者
        var about = new TextBlock
        {
            Text = AppServices.ProductName + "  v" + AppServices.AppVersion + "  ·  作者 " + AppServices.Author,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 14, 0, 2),
        };
        about.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        root.Children.Add(about);

        return root;
    }

    /// <summary>自动补全:体检所有已安装版本,缺失的文件按现有安装器重新拉取(已存在则跳过)。</summary>
    private UIElement BuildRepairSection()
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 14, 0, 0),
        };
        var host = new StackPanel();
        // 标题行:左标题 + 右按钮,按钮不必滚到最底部就能看到
        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = "自动补全文件", FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = T2, VerticalAlignment = VerticalAlignment.Center,
        });
        var run = new Button
        {
            Content = "立即补全",
            Style = (Style)Application.Current.Resources["ChipButton"],
        };
        System.Windows.Automation.AutomationProperties.SetName(run, "立即补全");
        run.Click += (_, _) => RunRepair(run);
        Grid.SetColumn(run, 1);
        titleRow.Children.Add(run);
        host.Children.Add(titleRow);

        _repairHint = new TextBlock
        {
            Text = "检查已安装版本的客户端、依赖库与资源文件是否缺失,有缺失自动补齐。",
            FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        host.Children.Add(_repairHint);

        card.Child = host;
        return card;
    }

    private TextBlock? _repairHint;

    private async void RunRepair(Button trigger)
    {
        if (trigger.Tag is "busy") return;
        trigger.Tag = "busy";
        trigger.IsEnabled = false;
        if (_repairHint != null) _repairHint.Text = "正在检查…";

        try
        {
            var audits = await Task.Run(() => Fuse.Core.Installer.VersionDoctor.Audit(
                AppServices.VersionsRoot, AppServices.LibrariesRoot, AppServices.AssetsRoot));

            var broken = audits.Where(a => a.NeedsRepair).ToList();
            if (broken.Count == 0)
            {
                if (_repairHint != null)
                    _repairHint.Text = $"检查完成:{audits.Count} 个版本文件齐全,无需补全。";
                return;
            }

            // 原版补全需要官方清单里的 url/sha1
            var manifest = await Fuse.Core.Sources.MojangSource
                .FetchManifestAsync(AppServices.Http, System.Threading.CancellationToken.None);

            var queued = 0;
            foreach (var a in broken)
            {
                if (a.Kind == "fabric" && !string.IsNullOrEmpty(a.LoaderVersion))
                {
                    var meta = Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        gameVersion = a.GameVersion,
                        loaderVersion = a.LoaderVersion,
                        versionsRoot = AppServices.VersionsRoot,
                        librariesRoot = AppServices.LibrariesRoot,
                        assetsRoot = AppServices.AssetsRoot,
                        gameDirectory = AppServices.InstanceDirectory(a.VersionId),
                    });
                    AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                    {
                        Name = $"{a.VersionId}(补全)",
                        Kind = "fabric-install",
                        Meta = meta,
                    });
                    queued++;
                    continue;
                }

                var entry = manifest.FirstOrDefault(v => v.Id == a.GameVersion);
                if (entry == null) continue;
                var vanillaMeta = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    versionId = a.GameVersion,
                    versionsRoot = AppServices.VersionsRoot,
                    librariesRoot = AppServices.LibrariesRoot,
                    assetsRoot = AppServices.AssetsRoot,
                    gameDirectory = AppServices.InstanceDirectory(a.VersionId),
                });
                AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                {
                    Name = $"{a.VersionId}(补全)",
                    Kind = "vanilla-install",
                    SourceUrl = entry.Url,
                    ExpectedHash = entry.Sha1,
                    Meta = vanillaMeta,
                });
                queued++;
            }

            if (_repairHint != null)
            {
                var detail = string.Join("\n", broken.Take(4).Select(a => $"· {a.VersionId}:{a.Summary}"));
                if (broken.Count > 4) detail += $"\n· 另有 {broken.Count - 4} 个版本";
                _repairHint.Text = $"发现 {broken.Count} 个版本缺文件,已加入 {queued} 个补全任务:\n{detail}";
            }
        }
        catch (Exception ex)
        {
            if (_repairHint != null) _repairHint.Text = "检查失败:" + ex.Message;
        }
        finally
        {
            trigger.Tag = null;
            trigger.IsEnabled = true;
        }
    }

    private StackPanel? _javaOptions;
    private bool _javaExpanded;
    private TextBlock? JavaChevron;
    private Slider? _memSlider;
    private TextBlock? _memValue;
    private CheckBox? _autoHeapToggle;
    private bool _syncingMemoryToggle;
    private bool _syncingVisToggle;
    private CheckBox? _autoUpdateToggle;
    private bool _syncingUpdateToggle;
    private CheckBox? _autoRunToggle;
    private bool _syncingAutoRunToggle;
    private TextBlock? _priorityValue;
    private TextBlock? _priorityChevron;
    private StackPanel? _priorityOptions;
    private bool _priorityExpanded;
    private TextBlock? _updateStatus;
    private Button? _updateBtn;
    private bool _updateBusy;
    private CheckBox? _visToggle;
    private TextBlock? _visState;
    private bool _loading;

    private static Border ReadOnlyRow(string label, string value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        var labelT = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 };
        var valueT = new TextBlock { Text = value, FontSize = 12, Foreground = Brushes.White };
        Grid.SetColumn(labelT, 0);
        Grid.SetColumn(valueT, 1);
        grid.Children.Add(labelT);
        grid.Children.Add(valueT);
        return Wrap(null, grid, out _);
    }

    private static Brush T2 => new SolidColorBrush(Color.FromArgb(166, 255, 255, 255));

    private static Border Wrap(string? name, UIElement child, out Border border)
    {
        border = new Border { Style = (Style)Application.Current.Resources["OptRow"], Child = child };
        return border;
    }

    private static Border Wrap(string name, UIElement child) => Wrap(name, child, out _);

    private void UpdateJavaText()
    {
        var c = AppServices.Config;
        var javaText = _javaPick[0];
        if (javaText == null) return;
        javaText.Text = c.JavaPolicy == "manual" && !string.IsNullOrEmpty(c.JavaHomeOverride)
            ? "手动指定：" + c.JavaHomeOverride
            : "Java 自动检测";
    }

    public void LoadValues()
    {
        _loading = true;
        var c = AppServices.Config;
        _memSlider!.Value = c.MaxHeapMb;
        _syncingVisToggle = true;
        try
        {
            _visToggle!.IsChecked = c.KeepVisibleOnLaunch;
            _syncingUpdateToggle = true;
            _syncingAutoRunToggle = true;
            _syncingOverlayToggle = true;
            try
            {
                _autoUpdateToggle!.IsChecked = c.AutoUpdate;
                _autoRunToggle!.IsChecked = c.LaunchOnStartup;
                _overlayToggle!.IsChecked = c.ShowStatsOverlay;
            }
            finally
            {
                _syncingUpdateToggle = false;
                _syncingAutoRunToggle = false;
                _syncingOverlayToggle = false;
            }
        }
        finally
        {
            _syncingVisToggle = false;
        }
        _visState!.Text = c.KeepVisibleOnLaunch ? "已开启" : "已关闭";
        UpdateJavaText();
        RefreshJavaSection();
        RefreshMemoryAuto();
        _loading = false;
    }

    /// <summary>自动内存开关 → 滑块禁用并显示推荐值;关闭则恢复手动滑块。</summary>
    private void RefreshMemoryAuto()
    {
        var auto = AppServices.Config.AutoHeap;
        _syncingMemoryToggle = true;
        try
        {
            if (_autoHeapToggle != null) _autoHeapToggle.IsChecked = auto;
        }
        finally
        {
            _syncingMemoryToggle = false;
        }
        if (_memSlider == null || _memValue == null) return;
        _memSlider.IsEnabled = !auto;
        _memSlider.Opacity = auto ? 0.45 : 1;
        _memValue.Text = auto
            ? "自动 · " + Fuse.Core.Config.MemoryAdvisor.Suggest(
                0, Fuse.Core.Config.MemoryAdvisor.TotalPhysicalMemoryMb()) + " MB"
            : (int)_memSlider.Value + " MB";
    }

    private void SetAutoHeap(bool on)
    {
        // 程序化同步 IsChecked 时不要反过来再写配置
        if (_syncingMemoryToggle) return;
        AppServices.Config.AutoHeap = on;
        Save();
        RefreshMemoryAuto();
    }

    private void SetKeepVisibleOnLaunch(bool on)
    {
        if (_syncingVisToggle) return;
        AppServices.Config.KeepVisibleOnLaunch = on;
        Save();
        if (_visState != null) _visState.Text = on ? "已开启" : "已关闭";
    }

    private void SetAutoUpdate(bool on)
    {
        if (_syncingUpdateToggle) return;
        AppServices.Config.AutoUpdate = on;
        Save();
    }

    private void SetLaunchOnStartup(bool on)
    {
        if (_syncingAutoRunToggle) return;
        AppServices.Config.LaunchOnStartup = on;
        Save();
        ApplyAutoRunRegistry(on);
    }

    private CheckBox? _overlayToggle;
    private bool _syncingOverlayToggle;

    /// <summary>数据浮窗开关:开启即创建浮窗(位置记忆),关闭销毁并停止采样。窗口由 App 层持有。</summary>
    private void SetStatsOverlay(bool on)
    {
        if (_syncingOverlayToggle) return;
        AppServices.Config.ShowStatsOverlay = on;
        Save();
        App.ApplyStatsOverlay(on);
    }

    /// <summary>开机自启动 = HKCU Run 注册表项(当前用户,无需管理员);exe 移动过也在下次保存时刷新。</summary>
    private static void ApplyAutoRunRegistry(bool on)
    {
        try
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "Fuse Launcher";
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey);
            if (on)
            {
                var exe = System.Reflection.Assembly.GetEntryAssembly()!.Location;
                key.SetValue(valueName, "\"" + exe + "\"", Microsoft.Win32.RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(valueName, false);
            }
        }
        catch (Exception)
        {
            // 注册表不可写(极罕见)时开关状态仍保存,下次启动再尝试
        }
    }

    private static string PriorityDisplay(string key) => key switch
    {
        "high" => "高",
        "low" => "低",
        _ => "正常",
    };

    /// <summary>按当前配置高亮优先级选项,并把选中值显示在折叠标题上。</summary>
    private void RefreshPrioritySelection()
    {
        var current = AppServices.Config.GamePriority;
        if (_priorityValue != null) _priorityValue.Text = PriorityDisplay(current);
        if (_priorityOptions == null) return;
        foreach (var child in _priorityOptions.Children)
        {
            if (child is Button row && row.Content is Grid g && g.Children.Count > 0 && g.Children[0] is StackPanel info)
            {
                var label = (info.Children[0] as TextBlock)?.Text;
                row.Background = PriorityDisplay(current) == label
                    ? (Brush)Application.Current.Resources["AccentChipSoftBrush"]
                    : Brushes.Transparent;
            }
        }
    }

    /// <summary>检查更新:拉清单 → 比版本 → 有新版直接下载并静默重装(状态都落在"版本更新"行上)。</summary>
    private void RunUpdateCheck()
    {
        if (_updateBusy) return;
        // 并发闸门:自动检查/手动检查/其他面板实例都不会叠加执行
        if (!Updater.TryBeginUpdate()) { SetUpdateStatus("已有更新流程在进行中"); return; }
        _updateBusy = true;
        if (_updateBtn != null) _updateBtn.IsEnabled = false;
        SetUpdateStatus("正在检查…");
        _ = Task.Run(async () =>
        {
            try
            {
                if (Updater.ConfiguredUrl == null)
                {
                    ToUi("未配置更新源");
                    return;
                }
                var m = await Updater.CheckAsync();
                if (!Updater.IsNewer(m.Version, AppServices.AppVersion))
                {
                    ToUi("已是最新版本");
                    return;
                }
                ToUi("发现新版本 V" + m.Version + ",下载中…");
                var path = await Updater.DownloadAsync(m,
                    pct => ToUi("下载中 " + pct + "%"));
                ToUi("安装更新…");
                await Dispatcher.InvokeAsync(() => Updater.ApplyAndRestart(path));
            }
            catch (Exception ex)
            {
                ToUi("更新失败:" + ex.Message);
            }
            finally
            {
                _updateBusy = false;
                Updater.EndUpdate();
                _ = Dispatcher.InvokeAsync(() => { if (_updateBtn != null) _updateBtn.IsEnabled = true; });
            }
        });

        void ToUi(string text) =>
            Dispatcher.BeginInvoke(new Action(() => SetUpdateStatus(text)));
    }

    private void SetUpdateStatus(string text)
    {
        if (_updateStatus != null) _updateStatus.Text = text;
    }

    /// <summary>重建 Java 手风琴选项列表(自动检测 + 本机运行时),选中项绿色高亮。</summary>
    private void RefreshJavaSection()
    {
        if (_javaOptions == null) return;
        _javaOptions.Children.Clear();
        _javaOptions.Visibility = _javaExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (JavaChevron != null) JavaChevron.Text = _javaExpanded ? "▴" : "▾";

        var c = AppServices.Config;
        AddJavaOption("自动检测（推荐）", "", c.JavaPolicy == "auto");
        foreach (var rt in JavaLocator.FindInstalled()
                     .Concat(JavaLocator.ScanRuntimesRoot(System.IO.Path.Combine(AppServices.InstallDirectory, "runtimes"))))
            AddJavaOption($"Java {rt.VersionString}（{rt.MajorVersion}）· {rt.Source}", rt.Home,
                c.JavaPolicy == "manual" && c.JavaHomeOverride == rt.Home);
    }

    private void AddJavaOption(string label, string home, bool selected)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
        };
        var baseFill = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));
        if (selected)
            row.SetResourceReference(Border.BackgroundProperty, "AccentChipBrush");
        else
            row.Background = baseFill;
        // 可点行也要有悬停反馈:全站统一为点击高亮色的实心填充
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverSolidBrush");
        row.MouseLeave += (_, _) =>
        {
            if (selected) row.SetResourceReference(Border.BackgroundProperty, "AccentChipBrush");
            else row.Background = baseFill;
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (selected)
        {
            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            grid.Children.Add(check);
        }
        row.Child = grid;
        row.MouseLeftButtonUp += (_, _) =>
        {
            var c = AppServices.Config;
            if (string.IsNullOrEmpty(home))
            {
                c.JavaPolicy = "auto";
                c.JavaHomeOverride = null;
            }
            else
            {
                c.JavaPolicy = "manual";
                c.JavaHomeOverride = home;
            }
            Save();
            UpdateJavaText();
            RefreshJavaSection();
        };
        _javaOptions!.Children.Add(row);
    }

    private enum ColorTarget { Accent, Panel, Hover }

    /// <summary>颜色设置行:色块 + 标签 + 更改按钮,点击跳出色环。</summary>
    private UIElement ColorRow(string label, Border swatch, ColorTarget target)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        swatch.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(swatch, 0);
        row.Children.Add(swatch);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2 });
        Grid.SetColumn(info, 1);
        info.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(info);

        var change = new Button { Content = "更改", Style = (Style)Application.Current.Resources["ChipButton"] };
        change.Click += (_, _) =>
        {
            // 色环叠加在设置面板中心
            var host = Window.GetWindow(this);
            var hostRect = host != null
                ? new Rect(host.Left, host.Top, host.ActualWidth, host.ActualHeight)
                : SystemParameters.WorkArea;
            var accent = AppServices.ParseHex(AppServices.Config.AccentColorHex, Color.FromRgb(0xA4, 0x16, 0xE5));
            Color current;
            switch (target)
            {
                case ColorTarget.Panel:
                    current = AppServices.ParseHex(AppServices.Config.PanelBgHex, Color.FromRgb(0x1E, 0x20, 0x26));
                    break;
                case ColorTarget.Hover:
                    current = AppServices.ParseHex(AppServices.Config.HoverColorHex, accent);
                    break;
                default:
                    current = accent;
                    break;
            }
            var wheel = new ColorWheelWindow(current, hostRect);
            if (wheel.ShowDialog() == true && wheel.Selected.HasValue)
            {
                var picked = wheel.Selected.Value;
                var hex = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
                switch (target)
                {
                    case ColorTarget.Panel:
                        AppServices.Config.PanelBgHex = hex;
                        break;
                    case ColorTarget.Hover:
                        AppServices.Config.HoverColorHex = hex;
                        break;
                    default:
                        AppServices.Config.AccentColorHex = hex;
                        break;
                }
                AppServices.SaveConfig();
                AppServices.ApplyTheme();
                swatch.Background = new SolidColorBrush(picked);
            }
        };
        Grid.SetColumn(change, 2);
        row.Children.Add(change);
        return row;
    }

    private Border? _accentSwatch;
    private Border? _bgSwatch;
    private Border? _hoverSwatch;

    private static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    private void Save()
    {
        if (_loading) return;
        AppServices.SaveConfig();
    }
}
