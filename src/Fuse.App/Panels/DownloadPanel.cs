using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fuse.Core.Download;
using Fuse.Core.Installer;
using Fuse.Core.Sources;
using Fuse.Core.Versioning;
using System.IO;
using Newtonsoft.Json;

namespace Fuse.App.Panels;

/// <summary>
/// 下载面板(FR-12,手风琴卡片结构,按用户确认的交互流):
/// 默认 5 张卡片(原版游戏版本/forge/neoforge/fabric/fabric api/optifine),点卡片向下展开;
/// 「原版游戏版本」展开 = 官方版本列表,点击 = 选中底座(高亮);
/// 选中底座后,加载器卡片展开只显示支持该版本的加载器版本;
/// 冲突规则:Forge/NeoForge/Fabric 互斥,Fabric API 依赖 Fabric;
/// 右下角统一下载按钮:仅原版 → 装原版;原版+加载器 → 统一配置安装(命名=原版号-加载器号)。
/// Forge/NeoForge/OptiFine 本期暂未支持(卡片置灰)。
/// </summary>
public class DownloadPanel : UserControl
{
    /// <summary>加载器安装页(loader-install)前设置:已有原版版本 id,标题为其显示名。</summary>
    public static string? PendingFixedVersionId;
    public static string? PendingFixedTitle;

    private List<MojangVersionEntry>? _manifest;
    private MojangVersionEntry? _selectedVanilla;
    private string? _selectedLoader;          // 已选加载器版本号
    private string? _selectedLoaderKind;      // "fabric" / "forge" / "neoforge",三者互斥
    private Core.Sources.OptiFineVersion? _selectedOptiFine;
    private bool _javaPromptDismissed;        // 用户点了"取消":底部常驻"去下载 Java"
    private string _javaHint = "";
    private bool _includeApi;                 // 是否连带 Fabric API
    private string? _expandedCard;            // 当前展开的卡片
    private string? _expandedCollection;      // 当前展开的版本集合
    private string _dlMode = "custom";        // custom=原版+加载器 / modpack=整合包 / tasks=下载任务
    private DispatcherTimer? _taskTimer;
    private StackPanel? _taskList;
    private readonly Dictionary<string, (TextBlock Status, ProgressBar Bar, TextBlock Pct, Button Action)> _taskRows = new();
    private string _taskSignature = "";
    private string _packQuery = "";
    private string _packCategory = "全部";
    private int _packOffset;
    private int _packTotal;
    private bool _packLoading;
    private int _packToken;
    private readonly List<Fuse.Core.Sources.ModrinthHit> _packHits = new();
    private string _packSource = "modrinth";
    private StackPanel? _packList;
    private ScrollViewer? _packScroll;
    private Border? _packTypeOverlay;
    private TextBlock? _packTypeValue;
    private TextBlock? _packTypeChevron;
    private bool _packTypeExpanded;
    private TextBlock? _packFoot;
    private readonly StackPanel _root = new();
    private readonly Grid _bottomBar = new();
    /// <summary>下载进度/完成提示(底栏左侧,圆环留白之外的面板内文字)。</summary>
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _statusTimer;
    private readonly HashSet<string> _activeSeen = new(StringComparer.Ordinal);
    private Button? _downloadButton;   // 底栏下载按钮(完成时临时改文案)
    private int _doneToken;            // 连续完成时让旧恢复计时器失效
    private string _panelKind => _loaderOnly ? "loader-install" : "download";
    private int _generation; // 异步回调代际:面板重建后旧回调自动失效
    private readonly bool _loaderOnly; // 只装加载器(原版已存在):不显示原版选择卡片

    private static Brush T2 => new SolidColorBrush(Color.FromArgb(166, 255, 255, 255));

    private void Toast(string message) => SetStatus(message, "AccentTextBrush");

    private static async void LoadIcon(Border host, string url, string title)
    {
        var letter = string.IsNullOrEmpty(title) ? "?" : title.Substring(0, 1).ToUpperInvariant();
        host.Child = new TextBlock
        {
            Text = letter, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var bytes = await AppServices.Http.GetByteArrayAsync(url);
            var image = BitmapFrame.Create(new MemoryStream(bytes),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            host.Child = new Border { Background = brush, CornerRadius = new CornerRadius(9) };
        }
        catch (Exception)
        {
        }
    }

    private static string FormatCount(long n)
    {
        if (n >= 100_000_000) return (n / 100_000_000.0).ToString("0.#") + "亿";
        if (n >= 10_000) return (n / 10_000.0).ToString("0.#") + "万";
        return n.ToString("N0");
    }

    public DownloadPanel() : this(null)
    {
    }

    /// <summary>
    /// fixedVanillaId 非空 = 「给已有版本装加载器」模式:不显示原版下载卡片,
    /// 底座为该版本,默认展开 Fabric 卡片(用户要求,避免与下载页混淆)。
    /// </summary>
    public DownloadPanel(string? fixedVanillaId)
    {
        _loaderOnly = !string.IsNullOrEmpty(fixedVanillaId);
        if (_loaderOnly)
        {
            _selectedVanilla = new MojangVersionEntry(fixedVanillaId!, "release", "", "", "");
            _expandedCard = "fabric";
        }
        // 卡片全部展开后内容远超面板高度:卡片区可滚,底部操作栏固定在外面,
        // 圆环也就落在"卡片区域之外"的左下角了。
        _statusText = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 220,
            Visibility = Visibility.Collapsed,
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(_statusText, 0);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _statusTimer.Tick += (_, _) => UpdateDownloadStatus();
        Loaded += (_, _) =>
        {
            if (!_statusTimer.IsEnabled) _statusTimer.Start();
            UpdateDownloadStatus();
        };
        Unloaded += (_, _) => _statusTimer.Stop();

        _bottomBar.Margin = new Thickness(0, 8, 0, 0);
        _bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 500,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _root,
        };
        var dock = new DockPanel();
        DockPanel.SetDock(_bottomBar, Dock.Bottom);
        dock.Children.Add(_bottomBar);
        dock.Children.Add(scroll);
        Content = dock;
        Rebuild();
        if (!_loaderOnly) _ = LoadManifestAsync();
    }

    // ---------- 整合包(Modrinth modpack)浏览与一键安装 ----------

    private static readonly string[] PackCategories =
        { "technology", "magic", "adventure", "skyblock", "lightweight", "multiplayer", "quests", "vanilla+" };

    private static string PackCategoryLabel(string slug) => slug switch
    {
        "technology" => "科技",
        "magic" => "魔法",
        "adventure" => "冒险",
        "skyblock" => "空岛",
        "lightweight" => "轻量",
        "multiplayer" => "多人",
        "quests" => "任务",
        "vanilla+" => "原版增强",
        _ => slug,
    };

    // ---------- 下载任务面板 ----------

    private static string TaskKindLabel(string kind) => kind switch
    {
        "vanilla-install" => "原版",
        "fabric-install" => "Fabric 加载器",
        "forge-install" => "Forge 加载器",
        "neoforge-install" => "NeoForge 加载器",
        "optifine-install" => "OptiFine",
        "optifine-assemble" => "OptiFine 组装",
        "modpack-install" => "整合包",
        "mod-update" => "模组更新",
        "user-file" => "高速下载",
        "fileset" => "资源文件",
        _ => "文件下载",
    };

    private static string TaskStatusText(DownloadTaskRecord t)
    {
        if (t.Pausing) return "暂停中…";
        switch (t.Status)
        {
            case DownloadTaskStatus.Queued: return "排队中";
            case DownloadTaskStatus.Downloading: return "下载中";
            case DownloadTaskStatus.Paused: return "已暂停";
            case DownloadTaskStatus.Completed: return "已完成";
            case DownloadTaskStatus.Failed:
                var msg = t.ErrorMessage ?? "失败";
                if (msg.Length > 70) msg = msg.Substring(0, 70) + "…";
                return "失败:" + msg;
            default: return t.Status.ToString();
        }
    }

    private static int TaskOrder(DownloadTaskRecord t) => t.Status switch
    {
        DownloadTaskStatus.Downloading => 0,
        DownloadTaskStatus.Queued => 1,
        DownloadTaskStatus.Paused => 2,
        DownloadTaskStatus.Failed => 3,
        DownloadTaskStatus.Completed => 4,
        _ => 5,
    };

    private static string TaskSignature(List<DownloadTaskRecord> tasks)
        => string.Join("|", tasks.Select(t => t.TaskId + ":" + (int)t.Status + (t.Pausing ? "p" : "")));

    private void BuildTaskList()
    {
        var tasks = (AppServices.Queue?.GetTasks() ?? Array.Empty<DownloadTaskRecord>()).ToList();
        var active = tasks.Count(t => t.Status == DownloadTaskStatus.Downloading ||
                                      t.Status == DownloadTaskStatus.Queued ||
                                      t.Status == DownloadTaskStatus.Paused);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = active + " 个进行中 · 共 " + tasks.Count + " 个任务",
            FontSize = 12,
            Foreground = T2,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var clear = new Button
        {
            Content = "清除已完成",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(10, 5, 10, 5),
            IsEnabled = tasks.Any(t => t.Status == DownloadTaskStatus.Completed),
        };
        System.Windows.Automation.AutomationProperties.SetName(clear, "清除已完成任务");
        clear.Click += (_, _) =>
        {
            foreach (var t in (AppServices.Queue?.GetTasks() ?? Array.Empty<DownloadTaskRecord>())
                         .Where(t => t.Status == DownloadTaskStatus.Completed).ToList())
                AppServices.Queue?.Remove(t.TaskId);
            Rebuild();
        };
        Grid.SetColumn(clear, 1);
        header.Children.Add(clear);
        _root.Children.Add(header);

        _taskList = new StackPanel();
        if (tasks.Count == 0)
        {
            _taskList.Children.Add(new TextBlock
            {
                Text = "还没有下载任务。",
                FontSize = 11.5,
                Foreground = T2,
                Opacity = 0.7,
                Margin = new Thickness(2, 6, 0, 0),
            });
        }
        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 470,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _taskList,
        };
        _root.Children.Add(scroll);
        RebuildTaskRows(tasks);
        _taskSignature = TaskSignature(tasks);

        _taskTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _taskTimer.Tick -= TaskTick;
        _taskTimer.Tick += TaskTick;
        _taskTimer.Start();
    }

    private void TaskTick(object? sender, EventArgs e)
    {
        if (_dlMode != "tasks" || !IsLoaded || _taskList == null) return;
        var tasks = (AppServices.Queue?.GetTasks() ?? Array.Empty<DownloadTaskRecord>()).ToList();
        var signature = TaskSignature(tasks);
        if (signature != _taskSignature)
        {
            _taskSignature = signature;
            RebuildTaskRows(tasks);
            return;
        }
        foreach (var t in tasks)
        {
            if (!_taskRows.TryGetValue(t.TaskId, out var row)) continue;
            row.Bar.Value = Math.Max(0, Math.Min(1, t.Progress)) * 100;
            row.Pct.Text = (int)(Math.Max(0, Math.Min(1, t.Progress)) * 100) + "%";
            row.Status.Text = TaskStatusText(t);
        }
    }

    private void RebuildTaskRows(List<DownloadTaskRecord> tasks)
    {
        if (_taskList == null) return;
        _taskList.Children.Clear();
        _taskRows.Clear();
        var ordered = tasks
            .OrderBy(TaskOrder)
            .ThenByDescending(t => t.FinishedAt ?? DateTime.MinValue)
            .ThenByDescending(t => t.CreatedAt)
            .Take(40)
            .ToList();
        if (ordered.Count == 0) return;
        foreach (var task in ordered)
            _taskList.Children.Add(BuildTaskRow(task));
    }

    private Border BuildTaskRow(DownloadTaskRecord task)
    {
        var border = new Border
        {
            Background = (Brush)Application.Current.Resources["RowBgBrush"],
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(task.Name) ? task.TaskId : task.Name,
            FontSize = 12.5,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var status = new TextBlock
        {
            Text = TaskStatusText(task),
            FontSize = 10.5,
            Foreground = T2,
            Opacity = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        info.Children.Add(status);
        var bar = new ProgressBar
        {
            Style = (Style)Application.Current.Resources["GlassProgress"],
            Height = 4,
            Margin = new Thickness(0, 5, 8, 0),
            Minimum = 0,
            Maximum = 100,
            Value = Math.Max(0, Math.Min(1, task.Progress)) * 100,
        };
        info.Children.Add(bar);
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var pct = new TextBlock
        {
            Text = (int)(Math.Max(0, Math.Min(1, task.Progress)) * 100) + "%",
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Foreground = T2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 38,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(pct, 1);
        grid.Children.Add(pct);

        Button? action = null;
        if (task.Status != DownloadTaskStatus.Completed)
        {
            var (label, handler) = task.Status switch
            {
                DownloadTaskStatus.Downloading => ("暂停", (Action)(() => AppServices.Queue?.Pause(task.TaskId))),
                DownloadTaskStatus.Queued => ("取消", (Action)(() => AppServices.Queue?.Remove(task.TaskId))),
                DownloadTaskStatus.Paused => ("继续", (Action)(() => AppServices.Queue?.Resume(task.TaskId))),
                DownloadTaskStatus.Failed => ("重试", (Action)(() => AppServices.Queue?.Retry(task.TaskId))),
                _ => ("", (Action)(() => { })),
            };
            if (task.Pausing) label = "暂停中";
            if (label.Length > 0)
            {
                action = new Button
                {
                    Content = label,
                    IsEnabled = !task.Pausing,
                    Style = (Style)Application.Current.Resources["ChipButton"],
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                System.Windows.Automation.AutomationProperties.SetName(action, label + "任务 " + task.Name);
                action.Click += (_, _) =>
                {
                    if (task.Pausing) return;
                    handler();
                    Rebuild();
                };
                Grid.SetColumn(action, 2);
                grid.Children.Add(action);
            }
        }

        var remove = new Button
        {
            Style = (Style)Application.Current.Resources["ChipButton"],
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = "✕",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, "删除任务 " + task.Name);
        remove.Click += (_, _) =>
        {
            AppServices.Queue?.Remove(task.TaskId);
            Rebuild();
        };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        border.Child = grid;
        _taskRows[task.TaskId] = (status, bar, pct, action!);
        return border;
    }

    private UIElement BuildModeChips()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (label, key) in new[] { ("自定义", "custom"), ("整合包", "modpack"), ("任务", "tasks") })
        {
            var chipBtn = new Button
            {
                Style = (Style)Application.Current.Resources["ChipButton"],
                Content = label,
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(0, 0, 8, 0),
            };
            if (_dlMode == key) chipBtn.SetResourceReference(Button.BackgroundProperty, "AccentChipBrush");
            System.Windows.Automation.AutomationProperties.SetName(chipBtn, "下载" + label);
            var captured = key;
            chipBtn.Click += (_, _) =>
            {
                if (_dlMode == captured) return;
                _dlMode = captured;
                Rebuild();
            };
            row.Children.Add(chipBtn);
        }
        return row;
    }

    private void BuildPackBrowse()
    {
        // 搜索框(与模组页同款)
        var search = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            _packQuery = search.Text.Trim();
            ReloadPacks();
        };
        search.TextChanged += (_, _) =>
        {
            debounce.Stop();
            debounce.Start();
        };
        _root.Children.Add(search);

        // 下载源:Modrinth / CurseForge(整合包)
        var srcRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };
        foreach (var (label, key) in new[] { ("Modrinth", "modrinth"), ("CurseForge", "curseforge") })
        {
            var chip = new Button
            {
                Content = label,
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = _packSource == key
                    ? null
                    : new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
            };
            if (_packSource == key) chip.SetResourceReference(Button.BackgroundProperty, "AccentChipBrush");
            if (key == "curseforge" && !CurseForgeSource.IsConfigured)
            {
                chip.Opacity = 0.45;
                chip.IsEnabled = false;
            }
            System.Windows.Automation.AutomationProperties.SetName(chip, "整合包源 " + label);
            chip.Click += (_, _) =>
            {
                if (_packSource == key) return;
                _packSource = key;
                _packCategory = "全部";
                Rebuild();
            };
            srcRow.Children.Add(chip);
        }
        _root.Children.Add(srcRow);

        // 类型行 + 浮层(与模组页同款交互)
        var typeRowOuter = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Padding = new Thickness(8, 6, 8, 6),
        };
        var typeGrid = new Grid();
        typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        typeGrid.Children.Add(new TextBlock
        {
            Text = "类型", FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = T2, VerticalAlignment = VerticalAlignment.Center,
        });
        _packTypeValue = new TextBlock
        {
            Text = _packCategory == "全部" ? "全部" : PackCategoryLabel(_packCategory),
            FontSize = 12, Margin = new Thickness(0, 0, 8, 0),
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_packTypeValue, 1);
        typeGrid.Children.Add(_packTypeValue);
        _packTypeChevron = new TextBlock { Text = "▾", FontSize = 12, Foreground = T2, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_packTypeChevron, 2);
        typeGrid.Children.Add(_packTypeChevron);
        var typeRow = new Button
        {
            Style = (Style)Application.Current.Resources["FlatRowButton"],
            Content = typeGrid,
            Padding = new Thickness(6, 4, 6, 4),
        };
        System.Windows.Automation.AutomationProperties.SetName(typeRow, "整合包类型 展开");
        typeRowOuter.Child = typeRow;
        typeRow.Click += (_, _) =>
        {
            _packTypeExpanded = !_packTypeExpanded;
            if (_packTypeOverlay != null)
                _packTypeOverlay.Visibility = _packTypeExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (_packTypeChevron != null) _packTypeChevron.Text = _packTypeExpanded ? "▴" : "▾";
        };
        _root.Children.Add(typeRowOuter);

        var listHost = new Grid();
        _packList = new StackPanel();
        _packScroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _packList,
        };
        _packScroll.ScrollChanged += (_, _) =>
        {
            if (_packScroll == null || _packLoading || _packHits.Count >= _packTotal) return;
            if (_packScroll.VerticalOffset >= _packScroll.ScrollableHeight - 32 && _packScroll.ScrollableHeight > 0)
                LoadMorePacks();
        };
        listHost.Children.Add(_packScroll);

        // 类型浮层(覆盖在列表上,不推挤布局)
        var options = new StackPanel();
        options.Children.Add(MakePackCategoryOption("全部", "全部"));
        foreach (var c in PackCategories)
            options.Children.Add(MakePackCategoryOption(PackCategoryLabel(c), c));
        _packTypeOverlay = new Border
        {
            Background = (Brush)Application.Current.Resources["PanelBgBrush"],
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 2, 0, 0),
            Width = 174,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer
            {
                Style = (Style)Application.Current.Resources["ModernScrollViewer"],
                Height = 4 * 40,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = options,
            },
        };
        _packTypeOverlay.Loaded += (_, _) =>
        {
            if (typeRowOuter.ActualWidth > 0) _packTypeOverlay.Width = Math.Round(typeRowOuter.ActualWidth / 2);
        };
        Panel.SetZIndex(_packTypeOverlay, 10);
        listHost.Children.Add(_packTypeOverlay);

        _root.Children.Add(listHost);
        ReloadPacks();
    }

    private UIElement MakePackCategoryOption(string label, string key)
    {
        var selected = _packCategory == key;
        var opt = new Button
        {
            Style = (Style)Application.Current.Resources["OptRowButton"],
            Margin = new Thickness(0, 0, 0, 6),
            Background = selected ? null : new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
            Content = new Grid { },
        };
        var grid = (Grid)((Button)opt).Content;
        grid.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (selected)
        {
            var check = new TextBlock { Text = "✓", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            check.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            Grid.SetColumn(check, 1);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(check);
        }
        opt.Click += (_, _) =>
        {
            if (_packCategory == key)
            {
                _packTypeExpanded = false;
                Rebuild();
                return;
            }
            _packCategory = key;
            _packTypeExpanded = false;
            Rebuild();
        };
        return opt;
    }

    private void ReloadPacks()
    {
        _packOffset = 0;
        _packTotal = 0;
        _packHits.Clear();
        _packLoading = false;
        _packToken++;
        if (_packList != null) _packList.Children.Clear();
        LoadMorePacks();
    }

    private void LoadMorePacks()
    {
        if (_packLoading || _packList == null) return;
        _packLoading = true;
        var gen = _generation;
        var token = _packToken;
        if (_packOffset == 0)
        {
            _packList.Children.Clear();
            _packList.Children.Add(HintRow("正在搜索…"));
        }
        var query = _packQuery;
        var category = _packCategory;
        var offset = _packOffset;
        _ = Task.Run(async () =>
        {
            try
            {
                List<Fuse.Core.Sources.ModrinthHit> hits;
                int total;
                if (_packSource == "curseforge")
                {
                    var cf = await CurseForgeSource.SearchAsync(
                        query, "", "", category, offset, 20, CancellationToken.None, "modpack").ConfigureAwait(false);
                    hits = cf.Hits.Select(h => new Fuse.Core.Sources.ModrinthHit(
                        h.Id.ToString(), h.Slug, h.Name, h.Summary, h.Downloads,
                        "modpack", h.IconUrl ?? "", "")).ToList();
                    total = cf.Total;
                }
                else
                {
                    var r = await Fuse.Core.Sources.ModrinthSource.SearchAsync(
                        AppServices.Http, query, "", "", category, offset, 20,
                        CancellationToken.None, "modpack").ConfigureAwait(false);
                    hits = r.Hits;
                    total = r.Total;
                }
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    if (gen != _generation || token != _packToken || _packList == null) return;
                    _packLoading = false;
                    _packTotal = total;
                    if (offset == 0) _packList.Children.Clear();
                    if (hits.Count == 0 && offset == 0)
                    {
                        _packList.Children.Add(HintRow("没有找到匹配的整合包"));
                        return;
                    }
                    foreach (var h in hits)
                    {
                        _packHits.Add(h);
                        _packList.Children.Add(BuildPackRow(h));
                    }
                    if (_packFoot != null) _packList.Children.Remove(_packFoot);
                    _packFoot = HintRow(_packHits.Count >= _packTotal ? "没有更多了" : "下拉加载更多…");
                    _packList.Children.Add(_packFoot);
                }));
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    if (gen != _generation || token != _packToken || _packList == null) return;
                    _packLoading = false;
                    _packList.Children.Clear();
                    _packList.Children.Add(RetryRow("整合包获取失败:" + ex.Message, ReloadPacks));
                }));
            }
        });
        _packOffset += 20;
    }

    private Border BuildPackRow(Fuse.Core.Sources.ModrinthHit hit)
    {
        var row = new Border
        {
            Style = (Style)Application.Current.Resources["GlassRowBorder"],
            Margin = new Thickness(0, 0, 0, 8),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 36, Height = 36,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Border.BackgroundProperty, "AccentChipSoftBrush");
        LoadIcon(icon, hit.IconUrl, hit.Title);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var info = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = hit.Title,
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var date = hit.DateModified.Length >= 10 ? hit.DateModified.Substring(0, 10) : hit.DateModified;
        info.Children.Add(new TextBlock
        {
            Text = date.Length > 0
                ? $"{FormatCount(hit.Downloads)} 下载 · {date} · {(_packSource == "curseforge" ? "CurseForge" : "Modrinth")} 整合包"
                : $"{FormatCount(hit.Downloads)} 下载 · {(_packSource == "curseforge" ? "CurseForge" : "Modrinth")} 整合包",
            FontSize = 10.5, Foreground = T2, Opacity = 0.66,
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var dl = new Button
        {
            Style = (Style)Application.Current.Resources["ChipButton"],
            Width = 28, Height = 28,
            Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = "⬇", FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(dl, "下载整合包 " + hit.Title);
        dl.Click += (_, _) => DownloadPack(hit, dl);
        Grid.SetColumn(dl, 2);
        grid.Children.Add(dl);

        row.Child = grid;
        return row;
    }

    private void DownloadPack(Fuse.Core.Sources.ModrinthHit hit, Button marker)
    {
        if (marker.Tag is "busy") return;
        marker.Tag = "busy";
        if (_packSource == "curseforge")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await DownloadCurseForgePackAsync(hit).ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(new Action(() =>
                    {
                        marker.Tag = null;
                        Toast("已加入下载任务:" + hit.Title);
                    }));
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(new Action(() =>
                    {
                        marker.Tag = null;
                        Toast("下载失败:" + ex.Message);
                    }));
                }
            });
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var (file, versionNumber, versionName) = await Fuse.Core.Sources.ModrinthSource.GetLatestPackFileAsync(
                    AppServices.Http, hit.ProjectId, CancellationToken.None).ConfigureAwait(false);
                var packVersionId = Fuse.Core.Download.DownloadQueue.SanitizePackVersionId(hit.Slug, versionNumber);
                var meta = new Newtonsoft.Json.Linq.JObject
                {
                    ["mrpackUrl"] = file.Url,
                    ["mrpackSha1"] = file.Sha1,
                    ["packSlug"] = hit.Slug,
                    ["versionNumber"] = versionNumber,
                    ["title"] = hit.Title,
                    ["versionsRoot"] = AppServices.VersionsRoot,
                    ["librariesRoot"] = AppServices.LibrariesRoot,
                    ["assetsRoot"] = AppServices.AssetsRoot,
                    ["instancesRoot"] = Path.Combine(AppServices.ActiveGameDirectory, "instances"),
                    // 外部目录(共享根布局)没有 instances 隔离:实例目录就是根目录本身
                    ["instanceDir"] = AppServices.NewPackInstanceDirectoryIn(AppServices.ActiveGameDirectory, packVersionId),
                }.ToString();
                var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                {
                    Name = hit.Title,
                    Kind = "modpack-install",
                    Meta = meta,
                });
                DownloadFocus.Register(task.TaskId, _panelKind);

                // 安装完成后把整合包名写进版本档案 → 版本列表显示具体名称。
                // 注意:这里在后台线程,不能用 DispatcherTimer(没有 Dispatcher 永远不触发)
                var displayName = hit.Title + " " + (versionName.Length > 0 ? versionName : versionNumber);
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                        var rec = AppServices.Queue?.GetTasks()?.FirstOrDefault(t => t.TaskId == task.TaskId);
                        if (rec == null) return;
                        if (rec.Status == Fuse.Core.Download.DownloadTaskStatus.Downloading ||
                            rec.Status == Fuse.Core.Download.DownloadTaskStatus.Queued) continue;
                        if (rec.Status == Fuse.Core.Download.DownloadTaskStatus.Completed)
                        {
                            var profile = AppServices.VersionProfiles.Get(packVersionId);
                            profile.DisplayName = displayName;
                            AppServices.VersionProfiles.Set(packVersionId, profile);
                        }
                        return;
                    }
                });

                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    marker.Tag = null;
                    if (marker.Content is TextBlock t)
                    {
                        t.Text = "✓";
                        var done = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                        done.Tick += (_, _) => { t.Text = "⬇"; done.Stop(); };
                        done.Start();
                    }
                    Toast("整合包安装已开始:" + hit.Title);
                }));
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    marker.Tag = null;
                    Toast("下载失败:" + ex.Message);
                }));
            }
        });
    }


    /// <summary>
    /// CurseForge 整合包:下载 pack zip → 按 manifest 解析每个文件的 downloadUrl →
    /// 合成等价 .mrpack(overrides 原样保留)→ 复用现有 modpack-install 全自动安装。
    /// </summary>
    private async Task DownloadCurseForgePackAsync(Fuse.Core.Sources.ModrinthHit hit)
    {
        if (!CurseForgeSource.IsConfigured)
            throw new InvalidOperationException("CurseForge 未启用");
        if (!int.TryParse(hit.ProjectId, out var projectId))
            throw new InvalidOperationException("整合包 id 无效");

        var files = await CurseForgeSource.GetFilesAsync(projectId, "", "", CancellationToken.None)
            .ConfigureAwait(false);
        var main = files.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.DownloadUrl));
        if (main == null)
            throw new InvalidOperationException("作者未授权第三方下载，请在网页中打开");

        var packVersionId = Fuse.Core.Download.DownloadQueue.SanitizePackVersionId(hit.Slug, main.DisplayName);
        var root = AppServices.ActiveGameDirectory;
        // 只做一次 API 列表查询，真正的 pack zip 下载和 manifest 逐文件解析都放进队列，
        // 这样任务面板有进度，暂停按钮也能真正取消。
        var metaJson = new Newtonsoft.Json.Linq.JObject
        {
            ["cfProjectId"] = projectId,
            ["cfFileId"] = main.Id,
            ["cfDownloadUrl"] = main.DownloadUrl,
            ["cfSha1"] = main.Sha1,
            ["packSlug"] = hit.Slug,
            ["versionNumber"] = main.DisplayName,
            ["title"] = hit.Title,
            ["versionsRoot"] = AppServices.VersionsRoot,
            ["librariesRoot"] = AppServices.LibrariesRoot,
            ["assetsRoot"] = AppServices.AssetsRoot,
            ["instancesRoot"] = Path.Combine(root, "instances"),
            ["instanceDir"] = AppServices.NewPackInstanceDirectoryIn(root, packVersionId),
        }.ToString();
        var task = AppServices.Queue!.Enqueue(new DownloadTaskRecord
        {
            Name = hit.Title,
            Kind = "modpack-install",
            Meta = metaJson,
        });
        DownloadFocus.Register(task.TaskId, _panelKind);
    }

    private void Rebuild()
    {
        _generation++;
        _root.Children.Clear();
        _taskTimer?.Stop();
        _taskRows.Clear();
        _taskList = null;
        // 顶部模式切换:自定义(原版+加载器) / 整合包 / 下载任务
        if (!_loaderOnly)
        {
            _root.Children.Add(BuildModeChips());
            if (_dlMode == "modpack")
            {
                BuildPackBrowse();
                if (_downloadButton != null) _downloadButton.Visibility = Visibility.Collapsed;
                return;
            }
            if (_dlMode == "tasks")
            {
                BuildTaskList();
                if (_downloadButton != null) _downloadButton.Visibility = Visibility.Collapsed;
                return;
            }
            if (_downloadButton != null) _downloadButton.Visibility = Visibility.Visible;
        }


        // 只装加载器模式:先说清结果,避免用户以为会改坏当前版本
        if (_loaderOnly && _selectedVanilla != null)
        {
            var gvName = _selectedVanilla.Id;
            var willBe = _selectedLoader != null
                ? $"{gvName}-fabric-{_selectedLoader}"
                : $"{gvName}-fabric-<加载器版本>";
            var note = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock
                {
                    Text = $"将生成独立版本 {willBe},原版 {gvName} 保持不动。",
                    FontSize = 11.5,
                    Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            note.SetResourceReference(Border.BackgroundProperty, "AccentChipSoftBrush");
            _root.Children.Add(note);
        }

        // ① 原版游戏版本(可展开:官方版本列表,点击 = 选中底座);只装加载器模式不需要
        if (!_loaderOnly)
            _root.Children.Add(MakeCard("原版游戏版本", "选择要下载的版本", "vanilla", BuildVanillaContent));
        // ② forge / neoforge(与 fabric 互斥,三选一)
        _root.Children.Add(MakeCard("forge 版本", LoaderSubtitle("forge", "Forge"),
            "forge", () => BuildModLoaderContent(ModLoaderInstaller.Forge),
            disabled: _selectedVanilla == null));
        _root.Children.Add(MakeCard("neoforge 版本", LoaderSubtitle("neoforge", "NeoForge"),
            "neoforge", () => BuildModLoaderContent(ModLoaderInstaller.NeoForge),
            disabled: _selectedVanilla == null));
        // ③ fabric(可展开:支持所选底座的加载器版本)
        _root.Children.Add(MakeCard("fabric 版本", LoaderSubtitle("fabric", "Fabric"),
            "fabric", BuildFabricContent, disabled: _selectedVanilla == null));
        // ④ fabric api(依赖 Fabric)
        _root.Children.Add(MakeCard("fabric api 版本",
            _includeApi ? "已勾选" : "依赖 Fabric", "fabricapi",
            BuildFabricApiContent, disabled: _selectedLoaderKind != "fabric" || _selectedLoader == null));
        // ⑤ optifine(需要本地 Java,缺失时提示下载)
        _root.Children.Add(MakeCard("optifine 版本", OptiFineSubtitle(),
            "optifine", BuildOptiFineContent, disabled: _selectedVanilla == null));

        // Java 缺失常驻提示(用户点了"取消"之后)
        if (_javaPromptDismissed && _selectedVanilla != null)
        {
            var javaMissing = FindJava(RequiredJavaMajor(_selectedVanilla.Id)) == null;
            if (javaMissing)
            {
                var goJava = new Button
                {
                    Content = "Java 缺失 —— 去下载",
                    Style = (Style)Application.Current.Resources["ChipButton"],
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                goJava.Click += (_, _) =>
                {
                    _javaPromptDismissed = false;
                    _expandedCard = "optifine";
                    Rebuild();
                };
                _root.Children.Add(goJava);
            }
        }
        if (_javaHint.Length > 0)
        {
            _root.Children.Add(new TextBlock
            {
                Text = _javaHint,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 2, 8),
            });
        }

        // 统一下载按钮(固定底栏右侧;左侧空出来,圆环画在面板卡片之外的窗口留白里)
        _bottomBar.Children.Clear();

        // 底栏左侧:下载状态文字(圆环在面板之外的左侧留白里,见 PanelWindow)
        Grid.SetColumn(_statusText, 0);
        _bottomBar.Children.Add(_statusText);

        var downloadButton = new Button { Style = (Style)Application.Current.Resources["GreenBigButton"] };
        // 加载器模式:必须先选加载器,不能点成"再装一遍原版"
        downloadButton.IsEnabled = _loaderOnly
            ? _selectedLoader != null
            : _selectedOptiFine != null
                ? FindJava(RequiredJavaMajor(_selectedVanilla!.Id)) != null
                : _selectedVanilla != null;
        downloadButton.Content = BuildButtonText();
        downloadButton.Click += (_, _) => EnqueueSelected();
        _downloadButton = downloadButton;
        Grid.SetColumn(downloadButton, 1);
        _bottomBar.Children.Add(downloadButton);

    }

    /// <summary>沿 inheritsFrom 找到真正的 MC 版本(OptiFine/Fabric 等派生版本返回父版本号)。</summary>
    internal static string ResolveMcVersion(string versionId)
    {
        try
        {
            return Core.Versioning.VersionJsonLoader.ResolveGameVersion(
                AppServices.VersionsRoot, versionId);
        }
        catch (Exception)
        {
            return versionId;
        }
    }

    /// <summary>
    /// 该版本能否找到可启动的客户端 jar。派生版本(OptiFine/Fabric 等)自己没有 jar,
    /// 要沿 inheritsFrom 找到父版本的 jar,否则会误判成"原版没装"而去排一个不存在的原版任务。
    /// </summary>
    private static bool HasClientJar(string versionId)
    {
        try
        {
            var merged = Core.Versioning.VersionJsonLoader.Load(AppServices.VersionsRoot, versionId);
            var jarId = string.IsNullOrEmpty(merged.Jar) ? versionId : merged.Jar!;
            return System.IO.File.Exists(
                System.IO.Path.Combine(AppServices.VersionsRoot, jarId, jarId + ".jar"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>加载器卡片的副标题:已选则显示版本,未选则提示。</summary>
    private string LoaderSubtitle(string kind, string display)
    {
        if (_selectedVanilla == null) return "需要先选择原版版本";
        if (_selectedLoaderKind == kind && _selectedLoader != null) return "已选 " + display + " " + _selectedLoader;
        return "选择 " + display + " 版本";
    }

    /// <summary>
    /// 底栏状态文字:下载中显示"正在下载 N 项 · 百分比";全部结束后显示"下载完成 ✓ 共 N 项",
    /// 直到下一次下载开始。失败的单独提示,避免"看起来成功了"。
    /// </summary>
    private void UpdateDownloadStatus()
    {
        var tasks = AppServices.Queue?.GetTasks();
        if (tasks == null) return;

        var active = tasks
            .Where(t => t.Status == DownloadTaskStatus.Queued || t.Status == DownloadTaskStatus.Downloading)
            .ToList();
        if (active.Count > 0)
        {
            foreach (var t in active) _activeSeen.Add(t.TaskId);
            // 新一轮下载开始时把按钮从"下载完成"恢复成正常文案,并取消待执行的"重置页面"
            _doneToken++;
            if (_downloadButton != null && (string?)_downloadButton.Content == DoneText)
                _downloadButton.Content = BuildButtonText();
            var progress = active.Average(t => Math.Max(0, Math.Min(1, t.Progress)));
            SetStatus($"正在下载 {active.Count} 项 · {progress:P0}", "TextSecondaryBrush");
            return;
        }

        if (_activeSeen.Count == 0) return;

        var done = tasks.Count(t => _activeSeen.Contains(t.TaskId) && t.Status == DownloadTaskStatus.Completed);
        var failed = tasks.Count(t => _activeSeen.Contains(t.TaskId) && t.Status == DownloadTaskStatus.Failed);
        _activeSeen.Clear();

        if (failed > 0)
        {
            SetStatus($"下载结束:完成 {done} 项,失败 {failed} 项", "TextSecondaryBrush");
            return;
        }

        // 完成提示按用户要求显示在下载按钮上:按钮文案临时变成「下载完成 ✓」,3 秒后恢复
        _statusText.Visibility = Visibility.Collapsed;
        if (done > 0) ShowDoneOnButton();
    }

    private const string DoneText = "下载完成 ✓";

    /// <summary>
    /// 完成提示直接改在下载按钮上("下载 1.21.11" → "下载完成 ✓"),
    /// 显示 3 秒后把整个页面恢复成初始状态(用户要求:连选择一起重置)。
    /// </summary>
    private void ShowDoneOnButton()
    {
        if (_downloadButton == null) return;
        var token = ++_doneToken;
        _downloadButton.Content = DoneText;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (token != _doneToken) return; // 期间又开始下载了,不要清掉用户新选的
            ResetDownloadSelection();
        };
        timer.Start();
    }

    /// <summary>恢复到初始下载选择状态:清空版本/加载器/OptiFine 选择与展开项。</summary>
    private void ResetDownloadSelection()
    {
        if (!_loaderOnly) _selectedVanilla = null;
        _selectedLoader = null;
        _selectedLoaderKind = null;
        _selectedOptiFine = null;
        _includeApi = false;
        _expandedCard = null;
        _expandedCollection = null;
        _javaHint = "";
        _javaPromptDismissed = false;
        _statusText.Visibility = Visibility.Collapsed;
        Rebuild();
    }

    private void SetStatus(string text, string brushKey)
    {
        _statusText.Text = text;
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        _statusText.Visibility = Visibility.Visible;
    }

    private string BuildButtonText()
    {
        if (_selectedVanilla == null) return "下载";
        if (_selectedOptiFine != null) return "安装 " + _selectedOptiFine.Display;
        if (_loaderOnly)
        {
            if (_selectedLoader == null) return "安装加载器";
            return _includeApi
                ? $"安装 Fabric {_selectedLoader} + API"
                : $"安装 Fabric {_selectedLoader}";
        }
        if (_selectedLoader == null || _selectedLoaderKind == null) return $"下载原版 {_selectedVanilla.Id}";
        var display = _selectedLoaderKind switch
        {
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            _ => "Fabric",
        };
        var text = $"下载 {_selectedVanilla.Id} + {display} {_selectedLoader}";
        return _includeApi && _selectedLoaderKind == "fabric" ? text + " + API" : text;
    }

    // ---------- 卡片构建 ----------

    private UIElement MakeCard(string title, string subtitle, string key,
        Func<UIElement>? expandedContent, bool disabled = false)
    {
        var expanded = _expandedCard == key && !disabled;
        // 卡片用 Button 而不是 Border:真实点击可靠,也让 UIA 能直接 Invoke
        var card = new Button
        {
            Style = (Style)Application.Current.Resources["OptRowButton"],
            Background = (Brush)Application.Current.Resources["RowBgBrush"],
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = disabled ? 0.45 : 1,
        };
        System.Windows.Automation.AutomationProperties.SetName(card, title);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        });
        head.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
        });
        Grid.SetColumn(head, 0);
        grid.Children.Add(head);

        if (!disabled)
        {
            card.Click += (_, _) =>
            {
                _expandedCard = _expandedCard == key ? null : key;
                Rebuild();
            };
        }
        card.Content = grid;

        var host = new StackPanel();
        host.Children.Add(card);
        if (expanded && expandedContent != null)
        {
            host.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = expandedContent?.Invoke(),
            });
        }
        return host;
    }

    // ---------- 原版版本列表 ----------

    private UIElement BuildVanillaContent()
    {
        var host = new StackPanel();
        var list = new StackPanel();
        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        host.Children.Add(scroll);

        if (_manifest == null)
        {
            list.Children.Add(HintRow("正在获取官方版本清单…"));
            _ = LoadManifestAsync(list);
        }
        else
        {
            FillVanillaCollections(list);
        }
        return host;
    }

    private async Task LoadManifestAsync(StackPanel? list = null)
    {
        var gen = _generation;
        try
        {
            _manifest = await MojangSource.FetchManifestAsync(AppServices.Http, System.Threading.CancellationToken.None);
            if (gen != _generation) return; // 面板已重建,旧回调失效
            if (list != null) FillVanillaCollections(list);
            Rebuild();
        }
        catch (Exception ex)
        {
            if (list != null)
            {
                list.Children.Clear();
                list.Children.Add(RetryRow("清单获取失败：" + ex.Message,
                    () => _ = LoadManifestAsync(list)));
            }
        }
    }

    /// <summary>版本集合键:26.x → "26";1.x → "1.21"/"1.20"(主次两段)。</summary>
    private static string CollectionKey(string id)
    {
        var parts = id.Split('.');
        return parts[0] == "1" && parts.Length > 1 ? parts[0] + "." + parts[1] : parts[0];
    }

    private void FillVanillaCollections(StackPanel list)
    {
        list.Children.Clear();
        var releases = MojangSource.Filter(_manifest!, "", "release");
        var groups = releases.GroupBy(v => CollectionKey(v.Id)).ToList(); // 保留新到旧顺序

        foreach (var g in groups)
        {
            var key = g.Key;
            var items = g.ToList();
            var expanded = _expandedCollection == key;

            // 集合行
            list.Children.Add(MakeSelectableRow(key, items.Count + " 个版本",
                expanded, () =>
                {
                    _expandedCollection = _expandedCollection == key ? null : key;
                    Rebuild();
                }));

            // 展开的版本列表(嵌套,同风格)
            if (expanded)
            {
                var nested = new StackPanel { Margin = new Thickness(16, 0, 0, 8) };
                foreach (var v in items)
                {
                    var entry = v;
                    var selected = _selectedVanilla?.Id == entry.Id;
                    nested.Children.Add(MakeSelectableRow(entry.Id, entry.ReleaseTime.Substring(0, 10),
                        selected, () =>
                        {
                            _selectedVanilla = entry;
                            _selectedLoader = null; // 换底座后加载器重选
                            _selectedLoaderKind = null;
                            _selectedOptiFine = null;
                            _includeApi = false;
                            Rebuild();
                        }));
                }
                list.Children.Add(nested);
            }
        }
    }

    // ---------- Fabric 加载器列表 ----------

    private UIElement BuildFabricContent()
    {
        var host = new StackPanel();
        if (_selectedVanilla == null)
        {
            host.Children.Add(HintRow("先在上方选择原版 MC 版本"));
            return host;
        }

        host.Children.Add(HintRow($"选择支持 {_selectedVanilla.Id} 的 Fabric 版本"));
        var list = new StackPanel();
        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        host.Children.Add(scroll);
        LoadFabricVersions(list, _selectedVanilla.Id, _generation);
        return host;
    }

    /// <summary>
    /// 拉取 Fabric 加载器版本。meta.fabricmc.net 走 Cloudflare,部分网络下单次请求可长达数十秒,
    /// 因此必须带超时,超时后给可点击的重试入口而非一直停在「正在获取」。
    /// </summary>
    private void LoadFabricVersions(StackPanel list, string gv, int gen)
    {
        list.Children.Clear();
        list.Children.Add(HintRow("正在获取 Fabric 支持版本…"));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = Task.Run(async () =>
        {
            try
            {
                // gv 可能是 OptiFine 这类派生版本,Fabric meta 只认真正的 MC 版本
                var versions = await FabricInstaller.FetchLoaderVersionsAsync(
                    AppServices.Http, ResolveMcVersion(gv), cts.Token);
                Log($"fabric versions {gv}: {versions.Count} back (gen {gen}/{_generation})");
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _generation) return;
                    list.Children.Clear();
                    if (versions.Count == 0)
                    {
                        list.Children.Add(HintRow($"Fabric 暂不支持 {gv}"));
                        return;
                    }
                    foreach (var lv in versions.Take(6))
                    {
                        var selected = _selectedLoader == lv;
                        list.Children.Add(MakeSelectableRow("Fabric " + lv,
                            selected ? "已选 ✓" : "选择", selected, () =>
                            {
                                // 三个加载器互斥:必须同时改 kind,否则会和已选的 Forge/NeoForge 并存
                                _selectedLoaderKind = "fabric";
                                _selectedLoader = lv;
                                _selectedOptiFine = null;
                                _includeApi = false;
                                _expandedCard = null; // 选完自动收起
                                Rebuild();
                            }));
                    }
                }));
            }
            catch (Exception ex)
            {
                Log($"fabric versions {gv}: FAILED {ex.GetType().Name} {ex.Message}");
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _generation) return;
                    list.Children.Clear();
                    var timedOut = cts.IsCancellationRequested;
                    var text = timedOut ? "Fabric 服务连接超时——点击重试" : "Fabric 版本获取失败：" + ex.Message;
                    list.Children.Add(RetryRow(text, () => LoadFabricVersions(list, gv, gen)));
                }));
            }
        });
    }

    /// <summary>可点击的失败重试行。</summary>
    private static Border RetryRow(string text, Action retry)
    {
        var row = new Border
        {
            Style = (Style)Application.Current.Resources["GlassRowBorder"],
            Padding = new Thickness(10, 8, 10, 8),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        row.MouseLeftButtonUp += (_, _) => retry();
        // 这是可点行:悬停给点击高亮色的实心反馈(静态玻璃卡不带悬停)
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverSolidBrush");
        row.MouseLeave += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "RowBgBrush");
        return row;
    }

    private static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FuseLauncher", "boot.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [download] {message}\n");
        }
        catch (Exception)
        {
        }
    }

    // ---------- Forge / NeoForge 版本列表 ----------

    /// <summary>forge / neoforge 共用的版本选择面板(官方 maven 拉列表,卡顿自动换镜像)。</summary>
    private UIElement BuildModLoaderContent(ModLoaderInstaller.LoaderFlavor flavor)
    {
        var host = new StackPanel();
        if (_selectedVanilla == null)
        {
            host.Children.Add(HintRow("先在上方选择原版 MC 版本"));
            return host;
        }

        host.Children.Add(HintRow($"选择支持 {_selectedVanilla.Id} 的 {flavor.Display} 版本"));
        var list = new StackPanel();
        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        host.Children.Add(scroll);

        LoadModLoaderVersions(list, flavor, _selectedVanilla.Id, _generation);
        return host;
    }

    /// <summary>拉取加载器版本列表。官方源 8s 超时后自动走镜像,整体 25s 兜底并给重试入口。</summary>
    private void LoadModLoaderVersions(
        StackPanel list, ModLoaderInstaller.LoaderFlavor flavor, string gameVersion, int gen)
    {
        list.Children.Clear();
        list.Children.Add(HintRow($"正在获取 {flavor.Display} 版本…"));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _ = Task.Run(async () =>
        {
            try
            {
                var versions = await ModLoaderInstaller.FetchVersionsAsync(
                    AppServices.Http, flavor, gameVersion, cts.Token).ConfigureAwait(false);
                Log($"{flavor.Key} versions {gameVersion}: {versions.Count}");
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _generation) return;
                    list.Children.Clear();
                    if (versions.Count == 0)
                    {
                        list.Children.Add(HintRow($"{flavor.Display} 暂不支持 {gameVersion}"));
                        return;
                    }
                    foreach (var ver in versions.Take(12))
                    {
                        var selected = _selectedLoaderKind == flavor.Key && _selectedLoader == ver;
                        var v = ver;
                        list.Children.Add(MakeSelectableRow($"{flavor.Display} {v}",
                            selected ? "已选 ✓" : "选择", selected, () =>
                            {
                                _selectedLoaderKind = flavor.Key;
                                _selectedLoader = v;
                                _selectedOptiFine = null;
                                _includeApi = false; // 换加载器后 API 勾选作废
                                _expandedCard = null; // 选完自动收起
                                Rebuild();
                            }));
                    }
                }));
            }
            catch (Exception ex)
            {
                Log($"{flavor.Key} versions {gameVersion} failed: {ex.Message}");
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _generation) return;
                    list.Children.Clear();
                    var timedOut = cts.IsCancellationRequested;
                    var text = timedOut
                        ? $"{flavor.Display} 服务连接超时——点击重试"
                        : $"{flavor.Display} 版本获取失败:{ex.Message}";
                    list.Children.Add(RetryRow(text,
                        () => LoadModLoaderVersions(list, flavor, gameVersion, gen)));
                }));
            }
        });
    }

    // ---------- Fabric API ----------

    private UIElement BuildFabricApiContent()
    {
        var host = new StackPanel();
        if (_selectedLoader == null)
        {
            host.Children.Add(HintRow("Fabric API 依赖 Fabric 加载器——先选择 Fabric 版本"));
            return host;
        }
        var row = MakeSelectableRow("Fabric API(最新版)", _includeApi ? "已勾选 ✓" : "未勾选",
            _includeApi, () =>
            {
                _includeApi = !_includeApi;
                Rebuild();
            });
        host.Children.Add(row);
        host.Children.Add(HintRow("作为模组安装到该实例的 mods 目录"));
        return host;
    }

    // ---------- OptiFine ----------

    private string OptiFineSubtitle()
    {
        if (_selectedVanilla == null) return "需要先选择原版版本";
        if (_selectedOptiFine != null) return "已选 " + _selectedOptiFine.Display;
        return "选择 OptiFine 版本";
    }

    /// <summary>该 MC 版本在本地可用的 Java(优先用 Mojang 官方运行时,其次本机安装)。</summary>
    internal static Core.Java.JavaRuntime? FindJava(int requiredMajor)
    {
        var all = Core.Java.JavaLocator.FindInstalled()
            .Concat(Core.Java.JavaLocator.ScanRuntimesRoot(
                System.IO.Path.Combine(AppServices.InstallDirectory, "runtimes")))
            .ToList();
        return Core.Java.JavaLocator.Pick(all, requiredMajor);
    }

    /// <summary>OptiFine 安装器要求的 Java 主版本:优先读版本 JSON,读不到按 MC 版本推断。</summary>
    internal static int RequiredJavaMajor(string mcVersion)
    {
        try
        {
            var v = Core.Versioning.VersionJsonLoader.Load(AppServices.VersionsRoot, mcVersion);
            if (v.JavaVersion != null && v.JavaVersion.MajorVersion > 0)
                return v.JavaVersion.MajorVersion;
        }
        catch (Exception)
        {
        }
        var parts = mcVersion.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[1], out var minor))
            return minor >= 17 ? 17 : 8;
        return 17;
    }

    private UIElement BuildOptiFineContent()
    {
        var host = new StackPanel();
        if (_selectedVanilla == null)
        {
            host.Children.Add(HintRow("先在上方选择原版 MC 版本"));
            return host;
        }

        var required = RequiredJavaMajor(_selectedVanilla.Id);
        if (FindJava(required) == null)
        {
            if (_javaPromptDismissed)
            {
                host.Children.Add(HintRow($"Java {required}+ 缺失——点下方「Java 缺失 —— 去下载」安装后再选"));
                return host;
            }
            host.Children.Add(BuildJavaPrompt(required));
            return host;
        }

        host.Children.Add(HintRow($"选择 OptiFine 版本(Java {required} 已就绪)"));
        var list = new StackPanel();
        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        host.Children.Add(scroll);
        LoadOptiFineVersions(list, _selectedVanilla.Id, _generation);
        return host;
    }

    /// <summary>"Java 缺失 — 为您下载 Java"卡:确定=下载官方运行时,取消=收起到常驻提示。</summary>
    private UIElement BuildJavaPrompt(int required)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
        };
        card.SetResourceReference(Border.BackgroundProperty, "AccentChipSoftBrush");
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "Java 缺失 — 为您下载 Java",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"OptiFine 安装器需要 Java {required}+,可自动下载 Mojang 官方运行时(约 40MB)。",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10),
        });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ok = new Button
        {
            Content = "确定",
            Style = (Style)Application.Current.Resources["GreenBigButton"],
            Height = 34,
        };
        System.Windows.Automation.AutomationProperties.SetName(ok, "确定下载 Java");
        ok.Click += (_, _) => StartJavaDownload(required);
        Grid.SetColumn(ok, 0);
        row.Children.Add(ok);
        var cancel = new Button
        {
            Content = "取消",
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            Height = 34,
        };
        System.Windows.Automation.AutomationProperties.SetName(cancel, "取消下载 Java");
        cancel.Click += (_, _) =>
        {
            _javaPromptDismissed = true;
            _javaHint = "";
            Rebuild();
        };
        Grid.SetColumn(cancel, 2);
        row.Children.Add(cancel);

        stack.Children.Add(row);
        card.Child = stack;
        return card;
    }

    private void StartJavaDownload(int required)
    {
        _javaHint = "正在获取 Java 运行时清单…";
        Rebuild();
        _ = Task.Run(async () =>
        {
            try
            {
                var catalog = await Core.Java.MojangJavaRuntime.FetchCatalogAsync(
                    AppServices.Http, System.Threading.CancellationToken.None).ConfigureAwait(false);
                var entry = Core.Java.MojangJavaRuntime.Pick(catalog, null, required);
                if (entry == null)
                    throw new InvalidOperationException($"没有找到 Java {required} 的官方运行时");

                var dest = System.IO.Path.Combine(AppServices.InstallDirectory, "runtimes",
                    $"java-{entry.Component}-{entry.VersionName}");
                await Core.Java.MojangJavaRuntime.DownloadAsync(AppServices.Http, entry, dest, null,
                    System.Threading.CancellationToken.None).ConfigureAwait(false);

                _javaHint = $"Java {required} 安装完成 ✓";
                _javaPromptDismissed = false;
            }
            catch (Exception ex)
            {
                _javaHint = "Java 下载失败:" + ex.Message;
                _javaPromptDismissed = true;
            }
            _ = Dispatcher.BeginInvoke(new Action(Rebuild));
        });
    }

    private void LoadOptiFineVersions(StackPanel list, string gameVersion, int gen)
    {
        list.Children.Clear();
        list.Children.Add(HintRow("正在获取 OptiFine 版本…"));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _ = Task.Run(async () =>
        {
            try
            {
                var versions = await Core.Sources.OptiFineSource.FetchVersionsAsync(
                    AppServices.Http, gameVersion, cts.Token).ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _generation) return;
                    list.Children.Clear();
                    if (versions.Count == 0)
                    {
                        list.Children.Add(HintRow($"没有 OptiFine 适配 {gameVersion}"));
                        return;
                    }
                    foreach (var v in versions.Take(10))
                    {
                        var item = v;
                        var selected = _selectedOptiFine?.FileName == item.FileName;
                        list.Children.Add(MakeSelectableRow(item.Display,
                            selected ? "已选 ✓" : "选择", selected, () =>
                            {
                                _selectedOptiFine = item;
                                _expandedCard = null; // 选完自动收起
                                Rebuild();
                            }));
                    }
                }));
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _generation) return;
                    list.Children.Clear();
                    list.Children.Add(RetryRow("OptiFine 版本获取失败:" + ex.Message,
                        () => LoadOptiFineVersions(list, gameVersion, gen)));
                }));
            }
        });
    }

    // ---------- 统一下载 ----------

    private void EnqueueSelected()
    {
        if (_selectedVanilla == null) return;

        // OptiFine:独立任务(需要 Java 路径),会下载 installer 并在临时目录静默安装
        if (_selectedOptiFine != null)
        {
            var required = RequiredJavaMajor(_selectedVanilla.Id);
            var java = FindJava(required);
            if (java == null)
            {
                return;
            }
            var ofMeta = JsonConvert.SerializeObject(new
            {
                gameVersion = _selectedVanilla.Id,
                loaderVersion = _selectedOptiFine.Display,
                versionsRoot = AppServices.VersionsRoot,
                librariesRoot = AppServices.LibrariesRoot,
                javaHome = java.Home,
            });
            var ofTask = AppServices.Queue!.Enqueue(new DownloadTaskRecord
            {
                Name = _selectedOptiFine.Display,
                Kind = "optifine-install",
                Meta = ofMeta,
            });
            DownloadFocus.Register(ofTask.TaskId, _panelKind);
            CollapseAfterEnqueue();
            return;
        }

        if (_selectedLoader == null)
        {
            if (_loaderOnly) return; // 加载器模式:必须先选加载器,不重复下载原版
            var meta = JsonConvert.SerializeObject(new
            {
                versionId = _selectedVanilla.Id,
                versionsRoot = AppServices.VersionsRoot,
                librariesRoot = AppServices.LibrariesRoot,
                assetsRoot = AppServices.AssetsRoot,
                gameDirectory = AppServices.InstanceDirectory(_selectedVanilla.Id),
            });
            var vTask = AppServices.Queue!.Enqueue(new DownloadTaskRecord
            {
                Name = $"Minecraft 原版 {_selectedVanilla.Id}",
                Kind = "vanilla-install",
                SourceUrl = _selectedVanilla.Url,
                ExpectedHash = _selectedVanilla.Sha1,
                Meta = meta,
            });
            DownloadFocus.Register(vTask.TaskId, _panelKind);
            CollapseAfterEnqueue();
        }
        else
        {
            var kind = _selectedLoaderKind ?? "fabric";
            var fabricId = $"{_selectedVanilla.Id}-{kind}-{_selectedLoader}";

            // 加载器版本是继承原版的:原版没装的话先排一个原版安装任务,否则装完也无法启动
            if (!HasClientJar(_selectedVanilla.Id))
            {
                var vanillaMeta = JsonConvert.SerializeObject(new
                {
                    versionId = _selectedVanilla.Id,
                    versionsRoot = AppServices.VersionsRoot,
                    librariesRoot = AppServices.LibrariesRoot,
                    assetsRoot = AppServices.AssetsRoot,
                    gameDirectory = AppServices.InstanceDirectory(_selectedVanilla.Id),
                });
                var depTask = AppServices.Queue!.Enqueue(new DownloadTaskRecord
                {
                    Name = $"Minecraft 原版 {_selectedVanilla.Id}(加载器依赖)",
                    Kind = "vanilla-install",
                    SourceUrl = _selectedVanilla.Url,
                    ExpectedHash = _selectedVanilla.Sha1,
                    Meta = vanillaMeta,
                });
                DownloadFocus.Register(depTask.TaskId, _panelKind);
            }

            var meta = JsonConvert.SerializeObject(new
            {
                gameVersion = _selectedVanilla.Id,
                // 底座是 OptiFine 等派生版本时,Meta 查询要用它背后的原版号
                mcVersion = ResolveMcVersion(_selectedVanilla.Id),
                loaderVersion = _selectedLoader,
                versionsRoot = AppServices.VersionsRoot,
                librariesRoot = AppServices.LibrariesRoot,
                assetsRoot = AppServices.AssetsRoot,
                gameDirectory = AppServices.InstanceDirectory(fabricId),
                // Forge/NeoForge 1.17+ 要跑官方安装器补 processors 产物,需要 Java 17+
                javaHome = FindJava(RequiredJavaMajor(_selectedVanilla.Id))?.Home,
            });
            var lTask = AppServices.Queue!.Enqueue(new DownloadTaskRecord
            {
                Name = kind == "fabric"
                    ? $"{_selectedVanilla.Id} + Fabric {_selectedLoader}"
                    : $"{_selectedVanilla.Id} + {(_selectedLoaderKind == "neoforge" ? "NeoForge" : "Forge")} {_selectedLoader}",
                Kind = kind + "-install",
                Meta = meta,
            });
            DownloadFocus.Register(lTask.TaskId, _panelKind);
            CollapseAfterEnqueue();

            if (_includeApi && kind == "fabric")
            {
                var gv = _selectedVanilla.Id;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var search = await ModrinthSource.SearchAsync(AppServices.Http, "fabric api", gv, "fabric", "全部", 0, 20,
                            System.Threading.CancellationToken.None);
                        var hits = search.Hits;
                        var api = hits.FirstOrDefault(h => h.Title.Equals("Fabric API", StringComparison.OrdinalIgnoreCase))
                                  ?? hits.FirstOrDefault();
                        if (api == null) throw new InvalidOperationException("Modrinth 未找到 Fabric API");
                        var file = await ModrinthSource.GetLatestVersionFileAsync(
                            AppServices.Http, api.ProjectId, gv, "fabric", System.Threading.CancellationToken.None);
                        AppServices.Queue!.Enqueue(new DownloadTaskRecord
                        {
                            Name = file.FileName,
                            Kind = "file",
                            SourceUrl = file.Url,
                            DestPath = System.IO.Path.Combine(AppServices.ModsDirectory(fabricId), file.FileName),
                            ExpectedHash = string.IsNullOrEmpty(file.Sha1) ? null : file.Sha1,
                        });
                    }
                    catch (Exception ex)
                    {
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                "FuseLauncher", "crash.log"),
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Fabric API 添加失败\n{ex}\n\n");
                    }
                });
            }
        }

    }

    /// <summary>任务入队成功后收起全部展开项并刷新(版本卡片/集合下拉全部合拢)。</summary>
    private void CollapseAfterEnqueue()
    {
        _expandedCard = null;
        _expandedCollection = null;
        Rebuild();
    }

    // ---------- 公共小件 ----------

    private static TextBlock HintRow(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
        Margin = new Thickness(4, 8, 4, 8),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>可选中的行。用 Button 而非 Border:ScrollViewer 会吞掉内部的鼠标事件,Button 走 Click 稳定可靠。</summary>
    private static Button MakeSelectableRow(string title, string trailing, bool selected, Action onClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var trail = new TextBlock
        {
            Text = trailing,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(trail, 1);
        grid.Children.Add(trail);

        var row = new Button
        {
            Style = (Style)Application.Current.Resources["OptRowButton"],
            Content = grid,
            Margin = new Thickness(0, 0, 0, 6),
        };
        if (selected) row.SetResourceReference(Button.BackgroundProperty, "AccentChipBrush");
        else row.SetResourceReference(Button.BackgroundProperty, "RowBgBrush");
        System.Windows.Automation.AutomationProperties.SetName(row, title);
        row.Click += (_, _) => onClick();
        return row;
    }
}
