using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fuse.Core.Config;
using Fuse.Core.Multiplayer;
using Fuse.Core.Installer;
using Fuse.Core.Java;
using Fuse.Core.Sources;
using Newtonsoft.Json.Linq;

namespace Fuse.App.Panels;

/// <summary>
/// 版本详情面板(FR-12):顶部「详情 · 显示名」,三分类 修改/模组/导出。
/// 修改:版本命名(只改显示名)/ Java 版本(手风琴)/ 内存滑块——只对该版本生效(每版本配置优先,无则回退全局)。
/// 模组:搜索/源/类型筛选 + Modrinth 下载量降序列表 + 无限滚动;⬇ 前置检测一并下载。
/// 导出:① 完整版本 zip(不含存档/个人数据) ② .mcdl.json 下载清单。
/// </summary>
public class VersionDetailPanel : UserControl
{
    /// <summary>VersionsPanel 打开详情前设置;ShowPanel 消费。</summary>
    public static string? PendingVersionId;
    /// <summary>版本所在的游戏目录(多目录支持),打开详情前设置;为空 = 当前激活目录。</summary>
    public static string? PendingVersionRoot;
    /// <summary>面板标题(「详情 · 显示名」),打开前由入口计算。</summary>
    public static string? PendingTitle;

    private static readonly string[] Categories =
    {
        "adventure", "decoration", "economy", "equipment", "food", "gameplay",
        "library", "magic", "management", "minigame", "optimization", "social",
        "storage", "technology", "transportation", "utility", "worldgen",
    };

    // 光影(Modrinth shader 分类)
    private static readonly string[] ShaderCategories =
        { "realistic", "semi-realistic", "fantasy", "cartoon", "vanilla-like" };

    // 材质包(Modrinth resourcepack 分类)
    internal static readonly string[] PackCategories =
        { "realistic", "simplistic", "cartoon", "vanilla-like", "32x", "16x" };

    private readonly string _versionId;
    /// <summary>该版本所在的游戏目录:详情页所有文件操作(启动/补全/删除/本地页)都锁定在这里,
    /// 不跟随"当前激活目录"变化(用户中途切换目录也不会串目录)。</summary>
    private readonly string _gameRoot;
    private readonly VersionProfile _profile;
    private readonly StackPanel _root = new();
    private readonly Grid _bottomBar = new();
    private readonly StackPanel _content = new();
    private int _generation;

    private string _tab = "mods";
    // 浏览类页签(模组/光影/材质):类型筛选按页签记忆;搜索词不跨页复用
    private readonly Dictionary<string, string> _catByTab = new();
    private string _browseProjectType = "mod";   // Modrinth project_type
    private string _browseDestDir = "";          // 光影/材质落盘目录

    // 内容下载状态(模组/光影/材质共用):打勾靠"记录 + 文件在磁盘",不靠内存
    private sealed class ItemDownloadState
    {
        public string ProjectId = "";
        public string Kind = "mod";
        public string Source = "modrinth";
        public string DestDir = "";
        public string MainFileName = "";
        public readonly HashSet<string> TaskIds = new(StringComparer.Ordinal);
        /// <summary>本体任务:失败才算整个条目失败;前置失败只记日志。</summary>
        public readonly HashSet<string> MainTaskIds = new(StringComparer.Ordinal);
        public string Title = "";
        public Button? Button;
        public bool Failed;
    }
    private readonly Dictionary<string, ItemDownloadState> _itemDownloads =
        new(StringComparer.Ordinal);
    private FrameworkElement? _crashCard;
    private FrameworkElement? _conflictCard;
    private FrameworkElement? _multiplayerCard;
    private MultiplayerHostSession? _mpSession;   // 联机会话(状态机见 MultiplayerHost.cs)
    private TextBlock? _mpStatus;
    private StackPanel? _mpStatusPanel;
    private StackPanel? _mpRetryPanel;
    private Fuse.App.Controls.Spinner? _mpSpinner;
    private Border? _mpBadge;
    private TextBlock? _mpBadgeText;
    private StackPanel? _mpInvitePanel;
    private TextBox? _mpInviteText;
    private TextBox? _mpJoinBox;
    private Button? _mpCopyButton;
    private Button? _mpToggleButton;
    private Action? _mpExpandAdvanced;
    private bool _mpHadSuccess;
    private TextBlock? _p2pStatus;
    private TunnelRunner? _tunnel;
    private TunnelPreset _tunnelPreset = TunnelPreset.SakuraFrp;
    private TextBlock? _tunnelStatus;
    private string? _missingBaseVersion;
    private FrameworkElement? _playtimeCard;
    private FrameworkElement? _jvmCard;
    private FrameworkElement? _backupCard;
    private string[] _browseCategories = Categories;
    private string _localDirKind = "mods";       // 本地页当前目录
    private bool _shaderPrePending;              // 光影前置已入队(用于卡片状态)
    private string _loader = "";      // ""=原版实例, "fabric" / "forge" / "neoforge"
    private bool _isOptiFine;         // OptiFine 版本(补丁,不是模组加载器)
    private string _gameVersion = ""; // 模组/清单查询用的游戏版本
    private bool _modpackWarningDismissed;   // 整合包"自己加模组"提醒是否已点过"我就要装"
    private bool? _isModpackCache;
    // 整合包更新检查
    private bool _packCheckBusy;
    private bool _packUpgrading;
    private string? _packNewVersion, _packNewUrl, _packNewSha1, _packNewTitle;

    // 模组列表状态
    private string _source = "modrinth";
    private string _query = "";
    private string _category = "全部";
    private int _offset;
    private int _total;
    private bool _loadingMods;
    private int _loadToken; // 每次重建自增,让在途请求的回调失效
    private readonly List<ModrinthHit> _hits = new();
    private StackPanel? _modList;
    private ScrollViewer? _modScroll;
    private TextBlock? _modFoot;

    // 本地模组:更新检查结果(按文件完整路径) / 检查中 / 是否已查过
    private readonly Dictionary<string, ModUpdateChecker.UpdateInfo> _modUpdates =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _modUpdateChecking;
    private bool _modUpdateChecked;
    private bool _backupBusy;

    // Java 手风琴
    private StackPanel? _javaOptions;
    private bool _javaExpanded;
    private TextBlock? _javaValue;
    private TextBlock? _javaChevron;

    // 类型浮层(展开状态存字段,重建后保持)
    private bool _typeExpanded;
    private Border? _typeRow;
    private Border? _typeOverlay;
    private TextBlock? _typeValueText;
    private TextBlock? _typeChevron;

    // 修改页删除确认
    private bool _deleteConfirmOpen;

    // 导出
    private int _exportChoice = 1;

    // ---------- 根目录感知路径(详情页所有操作锁定在版本所在目录) ----------
    private string RootDir() => _gameRoot;
    private string VRoot() => System.IO.Path.Combine(_gameRoot, "versions");
    private string LibsRoot() => System.IO.Path.Combine(_gameRoot, "libraries");
    private string AssetsRoot() => System.IO.Path.Combine(_gameRoot, "assets");
    private string InstDir() => AppServices.InstanceDirectoryIn(_gameRoot, _versionId);
    private string ModsDir() => System.IO.Path.Combine(InstDir(), "mods");

    public VersionDetailPanel(string versionId)
    {
        _versionId = versionId;
        _gameRoot = PendingVersionRoot ?? AppServices.FindVersionRoot(versionId) ?? AppServices.ActiveGameDirectory;
        _profile = AppServices.VersionProfiles.Get(versionId);
        DetectLoaderAndGameVersion();
        Content = _root;
        BuildRoot();
        RenderTab();

        if (AppServices.Queue != null)
            AppServices.Queue.TaskChanged += OnDownloadTaskChanged;
        Unloaded += (_, _) =>
        {
            if (AppServices.Queue != null)
                AppServices.Queue.TaskChanged -= OnDownloadTaskChanged;
        };
    }

    /// <summary>
    /// 沿 inheritsFrom 链一路走到根,拿到真正的原版版本号(整合包→加载器→原版)。
    /// 优先用 version.json 的 clientVersion:部分启动器写的自包含版本(如 "26.1-Fabric 0.19.5")
    /// 既没有 jar 也没有 inheritsFrom,只有 clientVersion 能给出真实 MC 版本。
    /// </summary>
    private string ResolveRootVersionId()
    {
        try
        {
            var resolved = Core.Versioning.VersionJsonLoader.ResolveGameVersion(VRoot(), _versionId);
            if (!string.IsNullOrEmpty(resolved)) return resolved;
        }
        catch (Exception)
        {
        }

        var cur = _versionId;
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var p = Path.Combine(VRoot(), cur, cur + ".json");
                if (!File.Exists(p)) break;
                var obj = JObject.Parse(File.ReadAllText(p));
                var parent = obj.Value<string>("inheritsFrom");
                if (string.IsNullOrEmpty(parent)) break;
                cur = parent!;
            }
        }
        catch (Exception)
        {
        }
        return cur;
    }

    private void DetectLoaderAndGameVersion()
    {
        _gameVersion = ResolveRootVersionId();

        // 整合包继承的加载器版本可能被删/移走:此时 merge 会抛异常,loader 识别不出来。
        // 先从 inheritsFrom 推断,保证模组/光影/材质页仍可用,并给出「补齐」入口。
        try
        {
            var own = Path.Combine(VRoot(), _versionId, _versionId + ".json");
            if (File.Exists(own))
            {
                var obj = JObject.Parse(File.ReadAllText(own));
                var parent = obj.Value<string>("inheritsFrom");
                if (!string.IsNullOrEmpty(parent) &&
                    !File.Exists(Path.Combine(VRoot(), parent!, parent! + ".json")))
                {
                    _missingBaseVersion = parent;
                    var parts = parent!.Split('-');
                    if (parts.Length > 0) _gameVersion = parts[0];
                    if (parts.Length > 1)
                    {
                        var kind = parts[1].ToLowerInvariant();
                        if (kind is "forge" or "neoforge" or "fabric") _loader = kind;
                    }
                    return;
                }
            }
        }
        catch (Exception)
        {
        }
        try
        {
            // 加载器识别:用递归合并后的完整版本库列表判断。
            // 整合包版本自己的 json 只有 inheritsFrom(无 libraries),只读原文件会把
            // 整合包误判成原版(实测:光影页走错 OptiFine 分支)。
            _isOptiFine = _versionId.IndexOf("OptiFine", StringComparison.OrdinalIgnoreCase) >= 0;
            var merged = Fuse.Core.Versioning.VersionJsonLoader.Load(VRoot(), _versionId);
            foreach (var lib in merged.Libraries)
            {
                var text = lib.Name;
                if (text.Contains("optifine:OptiFine")) _isOptiFine = true;
                if (text.Contains("net.fabricmc:fabric-loader")) { _loader = "fabric"; break; }
                // NeoForge 的 json 里没有 "net.neoforged:neoforge" 条目,
                // 出现的是 net.neoforged.fancymodloader:* / net.neoforged:* 等
                if (text.Contains("net.neoforged")) { _loader = "neoforge"; break; }
                // 现代 Forge(1.20.1+)的 libraries 里**没有** "net.minecraftforge:forge:" 主条目,
                // 只有 fmlloader/forgespi/... —— 旧判断会导致"Forge 整合包被当成原版"
                if (text.Contains("net.minecraftforge:")) { _loader = "forge"; break; }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 是否为整合包实例:安装器写入的版本 json 只有 id/inheritsFrom(没有 mainClass/libraries),
    /// 新版本会额外带 fuseModpack 标记;解压式导入的包也符合这个特征。
    /// </summary>
    private bool IsModpackVersion()
    {
        if (_isModpackCache.HasValue) return _isModpackCache.Value;
        var result = false;
        try
        {
            var path = Path.Combine(VRoot(), _versionId, _versionId + ".json");
            if (File.Exists(path))
            {
                var obj = JObject.Parse(File.ReadAllText(path));
                result = obj.Value<bool?>("fuseModpack") == true ||
                         (!string.IsNullOrEmpty(obj.Value<string>("inheritsFrom")) &&
                          obj["libraries"] == null &&
                          obj.Value<string>("mainClass") == null);
            }
        }
        catch (Exception)
        {
            result = false;
        }
        _isModpackCache = result;
        return result;
    }

    // ---------- 根骨架:四分类切换 ----------

    private void BuildRoot()
    {
        _root.Children.Clear();
        var chips = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        for (var i = 0; i < 6; i++)
            chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddChip(chips, "修改", "modify", 0);
        AddChip(chips, "模组", "mods", 1);
        AddChip(chips, "光影", "shaders", 2);
        AddChip(chips, "材质", "resourcepacks", 3);
        AddChip(chips, "本地", "local", 4);
        AddChip(chips, "导出", "export", 5);
        _root.Children.Add(chips);
        // 内容区整体可滚动:修改页加了崩溃救援/备份后远超面板高度,
        // 不套 ScrollViewer 会被窗口 MaxHeight 直接裁掉(用户反馈"页面查看不全")
        _root.Children.Add(new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _content,
        });
    }

    private void AddChip(Grid host, string label, string key, int column)
    {
        // Button 包装:便于真实点击与 UIA Invoke 自动化;用 OptRowButton 样式(圆角 + 微软绿高亮)
        var chip = new Button
        {
            Style = (Style)Application.Current.Resources["OptRowButton"],
            Content = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            Background = _tab == key
                ? null
                : new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
            Padding = new Thickness(0, 8, 0, 8),
            Margin = new Thickness(3, 0, 3, 0),
        };
        if (_tab == key) chip.SetResourceReference(Button.BackgroundProperty, "AccentChipBrush");
        System.Windows.Automation.AutomationProperties.SetName(chip, label);
        chip.Click += (_, _) =>
        {
            if (_tab == key) return;
            _catByTab[_tab] = _category;
            _tab = key;
            _category = _catByTab.TryGetValue(key, out var saved) ? saved : "全部";
            _query = "";   // 搜索词不跨页复用
            BuildRoot();
            RenderTab();
        };
        Grid.SetColumn(chip, column);
        host.Children.Add(chip);
    }

    private void RenderTab()
    {
        _generation++;
        _content.Children.Clear();
        if (_tab == "modify") BuildModifyTab();
        else if (_tab == "local") BuildLocalTab();
        else if (_tab == "export") BuildExportTab();
        else if (_tab == "shaders") BuildBrowseTab("shaders");
        else if (_tab == "resourcepacks") BuildBrowseTab("resourcepacks");
        else BuildModsTab();
    }

    // ---------- 修改 ----------

    private void BuildModifyTab()
    {
        // 联机卡片置顶(测试功能,用户要求放最上面)
        BuildMultiplayerSection();

        // 版本命名
        var nameCard = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 0, 0, 8),
        };
        var nameHost = new StackPanel();
        nameHost.Children.Add(new TextBlock
        {
            Text = "版本命名", FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = T2, Margin = new Thickness(0, 0, 0, 8),
        });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            Text = _profile.DisplayName ?? _versionId,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(box, 0);
        row.Children.Add(box);
        var save = new Button { Content = "保存", Style = (Style)Application.Current.Resources["ChipButton"], Margin = new Thickness(8, 0, 0, 0) };
        var nameHint = new TextBlock { FontSize = 11, Foreground = T3, Margin = new Thickness(0, 6, 0, 0) };
        save.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (name.Length == 0 || name == _versionId) _profile.DisplayName = null;
            else _profile.DisplayName = name;
            AppServices.VersionProfiles.Set(_versionId, _profile);
            nameHint.Text = "已保存 ✓";
        };
        Grid.SetColumn(save, 1);
        row.Children.Add(save);
        nameHost.Children.Add(row);
        nameHost.Children.Add(nameHint);
        nameCard.Child = nameHost;
        _content.Children.Add(nameCard);

        // Java 版本(手风琴)
        var javaHeader = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        javaHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        javaHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var javaInfo = new StackPanel();
        javaInfo.Children.Add(new TextBlock
        {
            Text = "Java 版本", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2,
        });
        _javaValue = new TextBlock
        {
            Text = JavaSummary(), FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontFamily = new FontFamily("Consolas"),
        };
        javaInfo.Children.Add(_javaValue);
        Grid.SetColumn(javaInfo, 0);
        javaHeader.Children.Add(javaInfo);
        _javaChevron = new TextBlock
        {
            Text = _javaExpanded ? "▴" : "▾", FontSize = 12, Foreground = T2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_javaChevron, 1);
        javaHeader.Children.Add(_javaChevron);
        javaHeader.MouseLeftButtonUp += (_, _) =>
        {
            _javaExpanded = !_javaExpanded;
            if (_javaChevron != null) _javaChevron.Text = _javaExpanded ? "▴" : "▾";
            RefreshJavaSection();
        };
        _content.Children.Add(WrapCard(javaHeader));
        _javaOptions = new StackPanel { Visibility = Visibility.Collapsed };
        _content.Children.Add(_javaOptions);

        // 内存滑块(只对该版本生效)
        var memRow = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var memTop = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        memTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        memTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        memTop.Children.Add(new TextBlock
        {
            Text = "内存分配(仅此版本)", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = T2,
        });
        var memValue = new TextBlock
        {
            Text = "", FontSize = 12,
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
        var modCount = Fuse.Core.Config.MemoryAdvisor.CountMods(ModsDir());
        var advisedHeap = Fuse.Core.Config.MemoryAdvisor.Suggest(
            modCount, Fuse.Core.Config.MemoryAdvisor.TotalPhysicalMemoryMb());
        var effectiveHeap = AppServices.ResolveHeapMb(_versionId, out _, out _);
        var followsAuto = _profile.MaxHeapMb <= 0;

        var slider = new Slider
        {
            Style = (Style)Application.Current.Resources["GreenSlider"],
            Minimum = 1024,
            Maximum = 16384,
            TickFrequency = 512,
            IsSnapToTickEnabled = true,
        };
        slider.Value = Math.Max(1024, Math.Min(16384, effectiveHeap));
        memValue.Text = followsAuto && AppServices.Config.AutoHeap
            ? "自动 · " + effectiveHeap + " MB"
            : effectiveHeap + " MB";

        // 推荐值提示:未固定时说明"自动推荐多少",固定后给一键改回自动
        var hintHost = new StackPanel { Margin = new Thickness(14, 0, 14, 12) };
        void RefreshHint()
        {
            hintHost.Children.Clear();
            if (_profile.MaxHeapMb > 0)
            {
                var back = new Button
                {
                    Style = (Style)Application.Current.Resources["ChipButton"],
                    Content = "改回自动（推荐 " + advisedHeap + " MB）",
                    Padding = new Thickness(10, 4, 10, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                back.Click += (_, _) =>
                {
                    _profile.MaxHeapMb = 0;
                    AppServices.VersionProfiles.Set(_versionId, _profile);
                    RenderTab();
                };
                hintHost.Children.Add(back);
            }
            else
            {
                hintHost.Children.Add(new TextBlock
                {
                    Text = "自动推荐 " + advisedHeap + " MB（" + modCount + " 个模组）· 拖动滑块改为固定值",
                    FontSize = 11,
                    Foreground = T2,
                    Opacity = 0.66,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        slider.ValueChanged += (_, _) =>
        {
            memValue.Text = (int)slider.Value + " MB";
            _profile.MaxHeapMb = (long)slider.Value;
            AppServices.VersionProfiles.Set(_versionId, _profile);
            RefreshHint();
        };
        memRow.Children.Add(slider);
        RefreshHint();
        memRow.Children.Add(hintHost);
        _content.Children.Add(WrapCard(memRow));

        BuildJvmArgsSection();
        BuildPlayStatsSection();
        BuildPackUpdateSection();
        BuildMissingBaseSection();
        BuildCrashSection();
        BuildModConflictSection();
        BuildBackupSection();
        BuildDeleteSection();
    }

    // ---------- 游玩统计 ----------

    private void BuildPlayStatsSection()
    {
        var stat = AppServices.PlayStats.Get(_versionId);
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 4, 0, 8),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "游玩统计",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = T2,
        });

        if (stat.Launches == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "还没有记录：启动这个版本后会自动开始计时。",
                FontSize = 11,
                Foreground = T2,
                Opacity = 0.66,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        else
        {
            var summary = "累计 " + Fuse.Core.Config.PlayStatsStore.FormatLong(stat.TotalSeconds) +
                          " · 启动 " + stat.Launches + " 次";
            if (stat.Crashes > 0) summary += " · 崩溃 " + stat.Crashes + " 次";
            host.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });

            var last = stat.LastPlayedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
            var first = stat.FirstPlayedUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "-";
            host.Children.Add(new TextBlock
            {
                Text = "上次游玩 " + last + " · 首次 " + first,
                FontSize = 10.5,
                Foreground = T2,
                Opacity = 0.66,
                Margin = new Thickness(0, 3, 0, 0),
            });

            var recent = AppServices.PlayStats.RecentOf(_versionId, 5);
            if (recent.Count > 0)
            {
                var rows = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var session in recent)
                {
                    var line = session.StartedUtc.ToLocalTime().ToString("MM-dd HH:mm") + " · " +
                               Fuse.Core.Config.PlayStatsStore.FormatLong(session.Seconds);
                    if (session.ExitCode != 0) line += " · 崩溃";
                    rows.Children.Add(new TextBlock
                    {
                        Text = line,
                        FontSize = 10.5,
                        Foreground = T2,
                        Opacity = 0.75,
                        Margin = new Thickness(0, 0, 0, 2),
                    });
                }
                host.Children.Add(rows);
            }

            var clear = new Button
            {
                Content = "清除统计",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            System.Windows.Automation.AutomationProperties.SetName(clear, "清除游玩统计");
            clear.Click += (_, _) =>
            {
                var confirm = new ConfirmWindow("清除游玩统计",
                    "将清除这个版本的累计时长/启动次数/崩溃次数和最近记录，无法恢复。", "清除", danger: true);
                if (Show(confirm) != true) return;
                AppServices.PlayStats.Clear(_versionId);
                Toast("游玩统计已清除", _playtimeCard);
                RenderTab();
            };
            host.Children.Add(clear);
        }

        card.Child = host;
        _playtimeCard = card;
        _content.Children.Add(card);
    }

    // ---------- 整合包更新(仅 Modrinth 安装的包) ----------

    private (string Slug, string Version, string Title)? ReadPackMeta()
    {
        try
        {
            var path = Path.Combine(VRoot(), _versionId, _versionId + ".json");
            if (!File.Exists(path)) return null;
            var obj = JObject.Parse(File.ReadAllText(path));
            var slug = obj.Value<string>("fusePackSlug");
            if (string.IsNullOrEmpty(slug)) return null;
            return (slug!, obj.Value<string>("fusePackVersion") ?? "",
                obj.Value<string>("fusePackTitle") ?? slug!);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void BuildPackUpdateSection()
    {
        var pack = ReadPackMeta();
        if (pack == null) return;

        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 4, 0, 8),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "整合包更新",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = T2,
        });
        host.Children.Add(new TextBlock
        {
            Text = "当前版本：" + (string.IsNullOrEmpty(pack.Value.Version) ? "未知" : pack.Value.Version) +
                   "（「安装新版本」= 独立实例；「原地升级」= 当前实例替换，旧目录保留可回滚）",
            FontSize = 11,
            Foreground = T2,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 6),
        });

        if (_packNewVersion != null)
        {
            host.Children.Add(new TextBlock
            {
                Text = "发现新版本：" + _packNewVersion,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["AccentTextBrush"],
                Margin = new Thickness(0, 2, 0, 6),
            });
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4),
            };
            var install = new Button
            {
                Content = "安装新版本",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(install, "安装整合包新版本");
            install.Click += (_, _) => InstallNewPackVersion();
            buttons.Children.Add(install);

            var upgrade = new Button
            {
                Content = _packUpgrading ? "升级中…" : "原地升级",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(14, 7, 14, 7),
                IsEnabled = !_packUpgrading,
            };
            if (Application.Current.Resources["AccentChipBrush"] is Brush accent)
                upgrade.Background = accent;
            System.Windows.Automation.AutomationProperties.SetName(upgrade, "原地升级整合包");
            upgrade.Click += (_, _) => UpgradePackInPlaceAsync();
            buttons.Children.Add(upgrade);
            host.Children.Add(buttons);
        }
        else
        {
            var check = new Button
            {
                Content = _packCheckBusy ? "检查中…" : "检查更新",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = !_packCheckBusy,
            };
            System.Windows.Automation.AutomationProperties.SetName(check, "检查整合包更新");
            check.Click += (_, _) => _ = CheckPackUpdateAsync();
            host.Children.Add(check);
        }

        // 上次原地升级留下的回滚点
        var record = Fuse.Core.Installer.InstanceUpgrader.LoadRecord(_gameRoot, _versionId);
        if (record != null)
        {
            host.Children.Add(new TextBlock
            {
                Text = "可回滚到 " + (string.IsNullOrEmpty(record.OldPackVersion) ? "上一版本" : record.OldPackVersion) +
                       "（" + record.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm") + "）",
                FontSize = 10.5,
                Foreground = T2,
                Opacity = 0.7,
                Margin = new Thickness(0, 8, 0, 4),
            });
            var rollback = new Button
            {
                Content = "回滚上次升级",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(14, 6, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            System.Windows.Automation.AutomationProperties.SetName(rollback, "回滚整合包升级");
            rollback.Click += (_, _) => RollbackPackUpgrade(record);
            host.Children.Add(rollback);
        }

        card.Child = host;
        _content.Children.Add(card);
    }

    private async Task CheckPackUpdateAsync()
    {
        var pack = ReadPackMeta();
        if (pack == null || _packCheckBusy) return;
        _packCheckBusy = true;
        RenderTab();
        try
        {
            var (file, versionNumber, versionName) = await ModrinthSource.GetLatestPackFileAsync(
                AppServices.Http, pack.Value.Slug, CancellationToken.None);
            var current = pack.Value.Version ?? "";
            if (string.Equals(versionNumber, current, StringComparison.OrdinalIgnoreCase))
            {
                _packNewVersion = null;
                Toast("整合包已是最新（" + (current.Length > 0 ? current : versionNumber) + "）");
            }
            else
            {
                _packNewVersion = versionNumber;
                _packNewUrl = file.Url;
                _packNewSha1 = file.Sha1;
                _packNewTitle = pack.Value.Title;
            }
        }
        catch (Exception ex)
        {
            Toast("检查更新失败:" + ex.Message);
        }
        finally
        {
            _packCheckBusy = false;
            RenderTab();
        }
    }

    private void InstallNewPackVersion()
    {
        var pack = ReadPackMeta();
        if (pack == null || string.IsNullOrEmpty(_packNewVersion) || string.IsNullOrEmpty(_packNewUrl)) return;
        var confirm = new ConfirmWindow("安装整合包新版本",
            "将把 " + _packNewVersion + " 安装为一个新版本，并自动迁移旧实例的存档/设置/服务器列表。\n\n" +
            "旧实例会完整保留，随时可以回滚。", "安装");
        if (Show(confirm) != true) return;
        try
        {
            var newVersion = _packNewVersion!;
            var newId = Fuse.Core.Download.DownloadQueue.SanitizePackVersionId(pack.Value.Slug, newVersion);
            if (string.Equals(newId, _versionId, StringComparison.OrdinalIgnoreCase))
            {
                Toast("新旧版本 id 相同，无法作为新实例安装");
                return;
            }
            var fromDir = InstDir();
            var meta = new JObject
            {
                ["mrpackUrl"] = _packNewUrl,
                ["mrpackSha1"] = _packNewSha1,
                ["packSlug"] = pack.Value.Slug,
                ["versionNumber"] = newVersion,
                ["title"] = _packNewTitle ?? pack.Value.Title,
                ["versionsRoot"] = VRoot(),
                ["librariesRoot"] = LibsRoot(),
                ["assetsRoot"] = AssetsRoot(),
                ["instancesRoot"] = Path.Combine(_gameRoot, "instances"),
            }.ToString();
            var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
            {
                Name = "整合包更新 " + newVersion,
                Kind = "modpack-install",
                Meta = meta,
            });
            DownloadFocus.Register(task.TaskId, "version-detail");
            Toast("已开始安装新版本 " + newVersion);
            _packNewVersion = null;
            RenderTab();

            // 装完后迁移个人数据(存档/设置/服务器列表/光影/材质),旧实例保留用于回滚
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    var rec = AppServices.Queue?.GetTasks()?.FirstOrDefault(t => t.TaskId == task.TaskId);
                    if (rec == null) return;
                    if (rec.Status == Fuse.Core.Download.DownloadTaskStatus.Queued ||
                        rec.Status == Fuse.Core.Download.DownloadTaskStatus.Downloading) continue;
                    if (rec.Status != Fuse.Core.Download.DownloadTaskStatus.Completed)
                    {
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                            Toast("新版本安装失败:" + rec.ErrorMessage)));
                        return;
                    }
                    break;
                }

                var copied = 0;
                try
                {
                    copied = Fuse.Core.Installer.InstanceMigrator.Migrate(
                        fromDir, AppServices.InstanceDirectoryIn(_gameRoot, newId));
                }
                catch (Exception)
                {
                    // 迁移失败不影响新版本本身
                }
                var profile = AppServices.VersionProfiles.Get(newId);
                profile.DisplayName = (pack.Value.Title ?? newId) + " " + newVersion;
                AppServices.VersionProfiles.Set(newId, profile);

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    Toast("新版本安装完成，已迁移 " + copied + " 个文件；旧实例保留，可用于回滚");
                    if (PanelWindow.OpenPanels.TryGetValue("versions", out var versionsPanel) &&
                        versionsPanel.PanelContent is VersionsPanel list)
                        list.Refresh();
                }));
            });
        }
        catch (Exception ex)
        {
            Toast("安装失败:" + ex.Message);
        }
    }

    /// <summary>
    /// 原地升级:新包下载到 &lt;实例&gt;.upgrade-* 暂存目录,校验通过后原子交换;旧目录保留用于回滚。
    /// 只支持同 MC 版本、同加载器;跨版本/跨加载器请用「安装新版本」。
    /// </summary>
    private async void UpgradePackInPlaceAsync()
    {
        var pack = ReadPackMeta();
        if (pack == null || string.IsNullOrEmpty(_packNewVersion) || string.IsNullOrEmpty(_packNewUrl) || _packUpgrading)
            return;
        if (LaunchState.IsBusy(_versionId))
        {
            Toast("游戏运行中，请先退出再升级");
            return;
        }
        var newVersion = _packNewVersion!;
        var confirm = new ConfirmWindow("原地升级整合包",
            "将把当前实例升级到 " + newVersion + "：\n\n" +
            "· 只支持同 MC 版本、同加载器（加载器版本沿用当前实例）\n" +
            "· 模组/配置会被新版本替换，存档/设置/服务器列表/光影/材质保留\n" +
            "· 升级前自动整实例备份，旧目录保留可回滚",
            "升级", danger: true);
        if (Show(confirm) != true) return;

        _packUpgrading = true;
        RenderTab();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var staging = Fuse.Core.Installer.InstanceUpgrader.StagingDir(InstDir(), stamp);
        var tempRoot = Path.Combine(Path.GetTempPath(), "fuse-pack-upgrade-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            Toast("正在下载新整合包…");
            var mrpack = Path.Combine(tempRoot, "pack.mrpack");
            var packDownload = await new Fuse.Core.Download.SmallFileDownloader(AppServices.Http).DownloadSetAsync(
                new[]
                {
                    new Fuse.Core.Download.FileDownloadItem
                    {
                        Url = _packNewUrl!,
                        DestPath = mrpack,
                        ExpectedHash = string.IsNullOrEmpty(_packNewSha1) ? null : _packNewSha1,
                        Algo = Fuse.Core.Download.HashAlgo.Sha1,
                    },
                },
                new Fuse.Core.Download.SmallFileOptions { Parallelism = 1 }, null, CancellationToken.None);
            if (packDownload.Failed.Count > 0)
                throw new IOException("整合包下载失败:" + packDownload.Failed[0]);

            Fuse.Core.Installer.ModpackInstaller.ExtractAll(mrpack, tempRoot);
            var index = Fuse.Core.Installer.ModpackInstaller.ParseIndex(tempRoot);
            var currentLoader = string.IsNullOrEmpty(_loader) ? "" : _loader;
            var newLoader = index.LoaderType ?? "";
            if (!string.Equals(index.GameVersion, _gameVersion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(newLoader, currentLoader, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "新版本需要 " + index.GameVersion + " / " + (newLoader.Length == 0 ? "原版" : newLoader) +
                    "，当前实例是 " + _gameVersion + " / " + (currentLoader.Length == 0 ? "原版" : currentLoader) +
                    "。跨版本/跨加载器请用「安装新版本」。");

            Toast("正在备份当前实例…");
            Fuse.Core.Installer.InstanceBackup.Create(_gameRoot, VRoot(), InstDir(), _versionId,
                _profile.DisplayName ?? _versionId);

            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            Toast("正在下载新版本文件…");
            var items = index.Files.Select(f => new Fuse.Core.Download.FileDownloadItem
            {
                Url = f.Url,
                DestPath = Path.Combine(staging, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                ExpectedHash = string.IsNullOrEmpty(f.Sha1) ? null : f.Sha1,
                Algo = Fuse.Core.Download.HashAlgo.Sha1,
            }).ToList();
            if (items.Count > 0)
            {
                var result = await new Fuse.Core.Download.SmallFileDownloader(AppServices.Http).DownloadSetAsync(
                    items,
                    new Fuse.Core.Download.SmallFileOptions { Parallelism = AppServices.Config.SmallFileParallelism },
                    null, CancellationToken.None);
                if (result.Failed.Count > 0)
                    throw new IOException("新版本文件有 " + result.Failed.Count + " 个下载失败:" + result.Failed[0]);
            }
            Fuse.Core.Installer.ModpackInstaller.ExtractOverrides(
                tempRoot, staging, Fuse.Core.Installer.ModpackInstaller.ReservedPaths(index.Files));

            // 存档/设置/服务器列表/光影/材质:只补缺失,不覆盖新包自带文件
            Fuse.Core.Installer.InstanceMigrator.Migrate(InstDir(), staging);

            Toast("正在切换实例目录…");
            var oldDir = Fuse.Core.Installer.InstanceUpgrader.SwapIn(InstDir(), staging, stamp);
            Fuse.Core.Installer.InstanceUpgrader.SaveRecord(_gameRoot, _versionId,
                new Fuse.Core.Installer.InstanceUpgrader.UpgradeRecord
                {
                    OldDir = oldDir,
                    OldPackVersion = pack.Value.Version ?? "",
                    NewPackVersion = newVersion,
                    CreatedUtc = DateTime.UtcNow,
                });

            // 版本 json 元数据(失败不影响已完成的升级)
            try
            {
                var versionJson = Path.Combine(VRoot(), _versionId, _versionId + ".json");
                if (File.Exists(versionJson))
                {
                    var obj = JObject.Parse(File.ReadAllText(versionJson));
                    obj["fusePackVersion"] = newVersion;
                    if (!string.IsNullOrEmpty(_packNewTitle)) obj["fusePackTitle"] = _packNewTitle;
                    File.WriteAllText(versionJson, obj.ToString());
                }
            }
            catch (Exception)
            {
            }

            Toast("原地升级完成；旧目录已保留，可在本页回滚");
            _packNewVersion = null;
            _packNewUrl = null;
            if (PanelWindow.OpenPanels.TryGetValue("versions", out var versionsPanel) &&
                versionsPanel.PanelContent is VersionsPanel list)
                list.Refresh();
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception) { }
            Toast("原地升级失败:" + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (Exception) { }
            _packUpgrading = false;
            RenderTab();
        }
    }

    private void RollbackPackUpgrade(Fuse.Core.Installer.InstanceUpgrader.UpgradeRecord record)
    {
        if (LaunchState.IsBusy(_versionId))
        {
            Toast("游戏运行中，请先退出再回滚");
            return;
        }
        var confirm = new ConfirmWindow("回滚整合包升级",
            "将把实例回滚到 " + (string.IsNullOrEmpty(record.OldPackVersion) ? "上一版本" : record.OldPackVersion) +
            "（" + record.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm") + "）。\n\n当前升级后的文件会被删除。",
            "回滚", danger: true);
        if (Show(confirm) != true) return;
        try
        {
            Fuse.Core.Installer.InstanceUpgrader.Rollback(InstDir(), record);
            try
            {
                var versionJson = Path.Combine(VRoot(), _versionId, _versionId + ".json");
                if (File.Exists(versionJson))
                {
                    var obj = JObject.Parse(File.ReadAllText(versionJson));
                    obj["fusePackVersion"] = record.OldPackVersion;
                    File.WriteAllText(versionJson, obj.ToString());
                }
            }
            catch (Exception)
            {
            }
            Fuse.Core.Installer.InstanceUpgrader.DeleteRecord(_gameRoot, _versionId);
            Toast("已回滚到 " + (string.IsNullOrEmpty(record.OldPackVersion) ? "上一版本" : record.OldPackVersion));
            if (PanelWindow.OpenPanels.TryGetValue("versions", out var versionsPanel) &&
                versionsPanel.PanelContent is VersionsPanel list)
                list.Refresh();
        }
        catch (Exception ex)
        {
            Toast("回滚失败:" + ex.Message);
        }
        RenderTab();
    }

    // ---------- 自定义 JVM 参数 ----------

    private void BuildJvmArgsSection()
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 4, 0, 8),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "JVM 参数（仅此版本）",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = T2,
        });
        host.Children.Add(new TextBlock
        {
            Text = "例如 -XX:+UseG1GC -Dfile.encoding=UTF-8；留空使用默认。内存请用上面的滑块，不要在这里写 -Xmx。",
            FontSize = 11,
            Foreground = T2,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 6),
        });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            Text = _profile.JvmArgs ?? "",
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "JVM 参数");
        Grid.SetColumn(box, 0);
        row.Children.Add(box);
        var save = new Button
        {
            Content = "保存",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Margin = new Thickness(8, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(save, "保存 JVM 参数");
        save.Click += (_, _) =>
        {
            var text = box.Text.Trim();
            _profile.JvmArgs = text.Length == 0 ? null : text;
            AppServices.VersionProfiles.Set(_versionId, _profile);
            Toast("JVM 参数已保存，下次启动生效", _jvmCard);
        };
        Grid.SetColumn(save, 1);
        row.Children.Add(save);
        host.Children.Add(row);
        card.Child = host;
        _jvmCard = card;
        _content.Children.Add(card);
    }

    // ---------- 崩溃救援 / 校验修复 ----------

    private void BuildCrashSection()
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 4, 0, 8),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "崩溃救援",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = T2,
        });
        host.Children.Add(new TextBlock
        {
            Text = "启动失败时先分析最近的崩溃报告，或临时禁用全部模组（安全模式）排查。",
            FontSize = 11,
            Foreground = T2,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        wrap.Children.Add(Chip("分析最近崩溃", "分析最近崩溃", AnalyzeCrash));
        wrap.Children.Add(Chip("查看最新日志", "查看最新日志", () =>
            OpenLogViewer(Path.Combine(InstDir(), "logs", "latest.log"), "最新日志")));
        wrap.Children.Add(Chip("查看最近崩溃", "查看最近崩溃", OpenLatestCrashReport));
        wrap.Children.Add(Chip("全部禁用（安全模式）", "全部禁用模组", SafeModeDisableAll));
        wrap.Children.Add(Chip("校验并修复文件", "校验并修复文件", VerifyAndRepair));
        wrap.Children.Add(Chip("打开崩溃报告", "打开崩溃报告", OpenCrashFolder));
        host.Children.Add(wrap);

        card.Child = host;
        _crashCard = card;
        _content.Children.Add(card);
    }

    private static Button Chip(string text, string autoName, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 6),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, autoName);
        button.Click += (_, _) => onClick();
        return button;
    }

    private async void AnalyzeCrash()
    {
        try
        {
            Toast("正在分析最近一次崩溃…", _crashCard);
            var result = await Task.Run(() => CrashAnalyzer.Analyze(InstDir(), ModsDir()));
            if (result == null)
            {
                Toast("没有找到崩溃报告或日志", _crashCard);
                return;
            }
            if (result.Suspects.Count == 0)
            {
                OpenCrashFolder();
                Toast("没有匹配到具体模组，已打开崩溃报告目录", _crashCard);
                return;
            }

            var names = string.Join("\n", result.Suspects.Take(8).Select(s => "· " + s.Name));
            var confirm = new ConfirmWindow("疑似模组",
                "最近一次崩溃（" + result.When.ToString("MM-dd HH:mm") + "）里出现了这些模组的名字：\n\n" +
                names + "\n\n禁用它们后再启动试试。",
                "禁用这些模组", danger: true);
            if (Show(confirm) != true) return;
            var count = DisableMods(result.Suspects.Select(s => s.FilePath));
            Toast("已禁用 " + count + " 个疑似模组", _crashCard);
            RenderTab();
        }
        catch (Exception ex)
        {
            Toast("分析失败:" + ex.Message, _crashCard);
        }
    }

    private void SafeModeDisableAll()
    {
        var mods = Directory.Exists(ModsDir())
            ? Directory.GetFiles(ModsDir(), "*.jar")
                .Where(p => !p.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();
        if (mods.Length == 0)
        {
            Toast("没有启用中的模组", _crashCard);
            return;
        }
        var confirm = new ConfirmWindow("安全模式",
            "将禁用全部 " + mods.Length + " 个模组（重命名为 .disabled）。\n\n" +
            "启动游戏确认问题后，再到「本地」页逐个启用排查。",
            "全部禁用", danger: true);
        if (Show(confirm) != true) return;
        var count = DisableMods(mods);
        Toast("已禁用 " + count + " 个模组", _crashCard);
        RenderTab();
    }

    private int DisableMods(IEnumerable<string> paths)
    {
        var count = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var target = path + ".disabled";
                if (File.Exists(target)) continue;
                File.Move(path, target);
                count++;
            }
            catch (Exception)
            {
                // 单个文件失败不影响其余
            }
        }
        return count;
    }

    private async void VerifyAndRepair()
    {
        try
        {
            Toast("正在获取官方清单…");
            var manifest = await MojangSource.FetchManifestAsync(AppServices.Http, CancellationToken.None);
            var entry = manifest.FirstOrDefault(v => v.Id == _gameVersion)
                        ?? throw new InvalidOperationException("官方清单里没有 " + _gameVersion);
            var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
            {
                Name = "校验修复 " + _gameVersion,
                Kind = "vanilla-install",
                SourceUrl = entry.Url,
                ExpectedHash = entry.Sha1,
                Meta = new JObject
                {
                    ["versionId"] = _gameVersion,
                    ["versionsRoot"] = VRoot(),
                    ["librariesRoot"] = LibsRoot(),
                    ["assetsRoot"] = AssetsRoot(),
                    ["gameDirectory"] = InstDir(),
                }.ToString(),
            });
            DownloadFocus.Register(task.TaskId, "version-detail");
            Toast("已加入校验修复任务，可在下载页「任务」查看进度");
        }
        catch (Exception ex)
        {
            Toast("校验失败:" + ex.Message);
        }
    }

    private void OpenCrashFolder()
    {
        try
        {
            var dir = Path.Combine(InstDir(), "crash-reports");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
        }
        catch (Exception ex)
        {
            Toast("打开失败:" + ex.Message, _crashCard);
        }
    }

    private void OpenLogViewer(string path, string title)
    {
        if (!File.Exists(path))
        {
            Toast("还没有日志文件");
            return;
        }
        Show(new LogViewerWindow(path, title));
    }

    private void OpenLatestCrashReport()
    {
        var dir = Path.Combine(InstDir(), "crash-reports");
        if (!Directory.Exists(dir))
        {
            Toast("还没有崩溃报告", _crashCard);
            return;
        }
        var file = new DirectoryInfo(dir).GetFiles("*.txt")
            .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        if (file == null)
        {
            Toast("还没有崩溃报告", _crashCard);
            return;
        }
        OpenLogViewer(file.FullName, "崩溃报告 · " + file.LastWriteTime.ToString("MM-dd HH:mm"));
    }

    // ---------- 备份 / 还原 / 复制实例 ----------



    private void BuildMissingBaseSection()
    {
        if (string.IsNullOrEmpty(_missingBaseVersion)) return;
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 4, 0, 8),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "基础版本缺失",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["DangerBrush"],
        });
        host.Children.Add(new TextBlock
        {
            Text = "这个整合包继承自 " + _missingBaseVersion +
                   "，但当前游戏目录里没有它。补齐后模组、光影、材质列表才能正常工作。",
            FontSize = 11,
            Foreground = T2,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        var fix = new Button
        {
            Content = "补齐 " + _missingBaseVersion,
            Style = (Style)Application.Current.Resources["ChipButton"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 8, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(fix, "补齐基础版本");
        fix.Click += async (_, _) => await RepairMissingBaseAsync();
        host.Children.Add(fix);
        card.Child = host;
        _content.Children.Add(card);
    }

    private async Task RepairMissingBaseAsync()
    {
        var parent = _missingBaseVersion;
        if (string.IsNullOrEmpty(parent)) return;
        try
        {
            var parts = parent!.Split('-');
            var gameVersion = parts[0];
            var kind = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            var loaderVersion = parts.Length > 2 ? string.Join("-", parts.Skip(2)) : "";
            Toast("正在补齐基础版本 " + parent + "…", _conflictCard);

            if (!File.Exists(Path.Combine(VRoot(), gameVersion, gameVersion + ".json")))
            {
                var vanillaMeta = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    versionId = gameVersion,
                    versionsRoot = VRoot(),
                    librariesRoot = LibsRoot(),
                    assetsRoot = AssetsRoot(),
                    gameDirectory = InstDir(),
                });
                var v = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                {
                    Name = "Minecraft " + gameVersion + "（整合包底座）",
                    Kind = "vanilla-install",
                    Meta = vanillaMeta,
                });
                await WaitForInstallAsync(v.TaskId, "原版 " + gameVersion);
            }

            if (kind is "forge" or "neoforge" or "fabric")
            {
                var java = Fuse.Core.Java.JavaLocator.Pick(Fuse.Core.Java.JavaLocator.FindInstalled(), 17);
                var loaderMeta = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    gameVersion,
                    mcVersion = gameVersion,
                    loaderVersion,
                    versionsRoot = VRoot(),
                    librariesRoot = LibsRoot(),
                    assetsRoot = AssetsRoot(),
                    gameDirectory = InstDir(),
                    javaHome = java?.Home,
                });
                var t = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                {
                    Name = gameVersion + " + " + kind + " " + loaderVersion,
                    Kind = kind + "-install",
                    Meta = loaderMeta,
                });
                await WaitForInstallAsync(t.TaskId, kind + " " + loaderVersion);
            }

            _missingBaseVersion = null;
            DetectLoaderAndGameVersion();
            Toast("基础版本已补齐", _conflictCard);
            RenderTab();
        }
        catch (Exception ex)
        {
            Toast("补齐失败:" + ex.Message, _conflictCard);
        }
    }

    private void BuildModConflictSection()
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 4, 0, 8),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "模组冲突修复",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = T2,
        });
        host.Children.Add(new TextBlock
        {
            // Forge/NeoForge 的模组元数据在 mods.toml，目前不解析。之前按 Fabric 规则硬判
            // 会把整包模组当成"缺前置"，修一次反而删坏一次（zombie 整合包教训）。
            Text = string.Equals(_loader, "fabric", StringComparison.OrdinalIgnoreCase)
                ? "启动报 Incompatible mods 时，点这里自动分析缺前置/版本不匹配/重复安装，并给出一键修复。"
                : "当前是 " + LoaderLabel() + " 实例：自动分析/修复目前只支持 Fabric（不解析 Forge 的 mods.toml，避免误删模组）。启动失败请用上面的「崩溃救援」。",
            FontSize = 11,
            Foreground = T2,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        var analyze = new Button
        {
            Content = "分析并修复",
            Style = (Style)Application.Current.Resources["ChipButton"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(analyze, "分析并修复模组冲突");
        analyze.Click += async (_, _) => await AnalyzeAndFixModsAsync();
        host.Children.Add(analyze);

        var latestBackup = Fuse.Core.ModConflict.ModConflictFixer.LatestBackup(ModsDir());
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        host.Children.Remove(analyze);
        analyze.Margin = new Thickness(0);
        row.Children.Add(analyze);

        if (!string.IsNullOrEmpty(latestBackup))
        {
            var undo = new Button
            {
                Content = "撤销上次修复",
                Style = (Style)Application.Current.Resources["ChipButton"],
                HorizontalAlignment = HorizontalAlignment.Left,
                Height = 30,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(undo, "撤销上次模组修复");
            undo.Click += (_, _) =>
            {
                var confirm = new ConfirmWindow("撤销修复",
                    "将当前 mods 换回上一次修复前的备份？" + Environment.NewLine + Environment.NewLine +
                    "备份：" + latestBackup, "撤销", danger: true);
                confirm.Owner = Window.GetWindow(this);
                if (Show(confirm) != true) return;
                if (Fuse.Core.ModConflict.ModConflictFixer.RestoreBackup(ModsDir(), latestBackup!, out var error))
                {
                    Toast("已撤销修复", _conflictCard);
                    RenderTab();
                }
                else Toast("撤销失败:" + error, _conflictCard);
            };
            row.Children.Add(undo);

            var backups = new Button
            {
                Content = "查看备份",
                Style = (Style)Application.Current.Resources["ChipButton"],
                HorizontalAlignment = HorizontalAlignment.Left,
                Height = 30,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(backups, "查看模组修复备份");
            backups.Click += (_, _) =>
            {
                var window = new ModBackupsWindow(ModsDir())
                {
                    Owner = Window.GetWindow(this),
                };
                window.ShowDialog();
                RenderTab();
            };
            row.Children.Add(backups);
        }
        host.Children.Add(row);

        card.Child = host;
        _conflictCard = card;
        _content.Children.Add(card);
    }

    private string ReadRecentErrorText()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var crash = Path.Combine(InstDir(), "crash-reports");
            if (Directory.Exists(crash))
            {
                var latest = new DirectoryInfo(crash).GetFiles("*.txt")
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (latest != null) sb.AppendLine(ReadTail(latest.FullName));
            }
            var log = Path.Combine(InstDir(), "logs", "latest.log");
            if (File.Exists(log)) sb.AppendLine(ReadTail(log));
        }
        catch (Exception)
        {
        }
        return sb.ToString();
    }

    private static string ReadTail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var take = (int)Math.Min(fs.Length, 256 * 1024);
            fs.Seek(-take, SeekOrigin.End);
            var buffer = new byte[take];
            var read = fs.Read(buffer, 0, take);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// 一键切换游戏版本:安装目标 MC + 同版本加载器,复制模组/配置/存档到新实例,
    /// 再按目标 MC 修一遍版本不兼容的模组。原实例保留。
    /// </summary>
    private async Task SwitchGameVersionAsync(string targetVersion)
    {
        if (!string.Equals(_loader, "fabric", StringComparison.OrdinalIgnoreCase))
        {
            new InfoWindow("切换游戏版本", "当前只支持 Fabric 实例的一键切换。").ShowDialog();
            return;
        }
        try
        {
            WidgetWindow.Boot("[switch] begin target=" + targetVersion + " loader=" + _loader);
            var merged = Core.Versioning.VersionJsonLoader.Load(VRoot(), _versionId);
            var loaderName = merged.Libraries
                .Select(l => l.Name)
                .FirstOrDefault(n => n.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase));
            var loaderVersion = loaderName?.Split(':').ElementAtOrDefault(2);
            if (string.IsNullOrEmpty(loaderVersion))
                throw new InvalidOperationException("识别不到当前实例的 Fabric Loader 版本");

            var newId = targetVersion + "-fabric-" + loaderVersion;
            var newInstance = AppServices.InstanceDirectoryIn(_gameRoot, newId);
            Toast("正在安装 Minecraft " + targetVersion + "（" + newId + "）…", _conflictCard);

            if (!File.Exists(Path.Combine(VRoot(), newId, newId + ".json")))
            {
                // 走下载队列(VanillaInstaller 直连 Mojang 在国内会卡死;队列带镜像/重试/续传)
                var vanillaMeta = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    versionId = targetVersion,
                    versionsRoot = VRoot(),
                    librariesRoot = LibsRoot(),
                    assetsRoot = AssetsRoot(),
                    gameDirectory = newInstance,
                });
                var vTask = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                {
                    Name = "Minecraft " + targetVersion + "（切换版本）",
                    Kind = "vanilla-install",
                    Meta = vanillaMeta,
                });
                await WaitForInstallAsync(vTask.TaskId, "原版 " + targetVersion);

                var fabricMeta = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    gameVersion = targetVersion,
                    mcVersion = targetVersion,
                    loaderVersion,
                    versionsRoot = VRoot(),
                    librariesRoot = LibsRoot(),
                    assetsRoot = AssetsRoot(),
                    gameDirectory = newInstance,
                });
                var fTask = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                {
                    Name = targetVersion + " + Fabric " + loaderVersion,
                    Kind = "fabric-install",
                    Meta = fabricMeta,
                });
                await WaitForInstallAsync(fTask.TaskId, "Fabric " + loaderVersion);
            }

            WidgetWindow.Boot("[switch] installed " + newId);
            CopyInstanceData(InstDir(), newInstance);
            Toast("正在按 " + targetVersion + " 修复模组…", _conflictCard);
            var newMods = Path.Combine(newInstance, "mods");
            var report = await Task.Run(() =>
                Fuse.Core.ModConflict.ModConflictAnalyzer.Analyze(newMods, targetVersion, "fabric"));
            var actions = report.Actions.Where(a => a.CanAutoFix).ToList();
            if (actions.Count > 0)
            {
                await Fuse.Core.ModConflict.ModConflictFixer.ApplyAsync(
                    newMods, targetVersion, "fabric", actions, AppServices.Http,
                    new Progress<string>(msg => Toast(msg, _conflictCard)), CancellationToken.None);
            }

            new InfoWindow("已切换到 " + targetVersion,
                "已创建版本：" + newId + Environment.NewLine +
                "模组 / 配置 / 存档已复制到新实例；原实例保留未动。").ShowDialog();
            var confirm = new ConfirmWindow("切换完成", "立即启动 " + newId + "？", "启动游戏")
            {
                Owner = Window.GetWindow(this),
            };
            if (Show(confirm) == true)
                await AppLauncher.LaunchAsync(newId, _ => { });
        }
        catch (Exception ex)
        {
            WidgetWindow.Boot("[switch] failed: " + ex);
            Toast("切换版本失败:" + ex.Message, _conflictCard);
        }
    }

    private async Task WaitForInstallAsync(string taskId, string label)
    {
        var lastPct = -10;
        while (true)
        {
            var record = AppServices.Queue?.GetTasks()?.FirstOrDefault(t => t.TaskId == taskId);
            if (record == null) throw new InvalidOperationException(label + " 任务丢失");
            var pct = (int)(record.Progress * 100);
            if (pct >= lastPct + 10)
            {
                lastPct = pct;
                Toast("正在安装 " + label + "：" + pct + "%", _conflictCard);
            }
            if (record.Status == Fuse.Core.Download.DownloadTaskStatus.Completed) return;
            if (record.Status is Fuse.Core.Download.DownloadTaskStatus.Failed
                or Fuse.Core.Download.DownloadTaskStatus.Removed)
                throw new InvalidOperationException(label + " 安装失败：" + (record.ErrorMessage ?? "未知错误"));
            await Task.Delay(1000);
        }
    }

    private static void CopyInstanceData(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var name in new[]
                 {
                     "mods", "config", "saves", "resourcepacks", "shaderpacks",
                     "shaders", "defaultconfigs", "CustomSkinLoader",
                 })
        {
            var from = Path.Combine(source, name);
            if (Directory.Exists(from)) CopyDirectory(from, Path.Combine(dest, name));
        }
        foreach (var name in new[] { "options.txt", "servers.dat" })
        {
            var from = Path.Combine(source, name);
            if (File.Exists(from)) File.Copy(from, Path.Combine(dest, name), overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>
    /// 联机卡片:开放世界(UPnP/IPv6/局域网 + e4mc 捕获)+ 复制邀请 + 加入房间 + 一键装联机 Mod + frp 隧道。
    /// 启动器不提供任何服务器;frp 只是托管用户自己账号的第三方客户端。
    /// </summary>
    /// <summary>
    /// 联机卡片(小白友好版):首屏只放「创建房间 / 加入房间」两件事 + 状态机反馈。
    /// 所有技术名词(直连/中转/备用组件/口令/中转网络)收进「高级联机选项」折叠区。
    /// </summary>
    private void BuildMultiplayerSection()
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 0, 0, 8),
        };
        var host = new StackPanel();

        // 标题
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "联机",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "测试功能",
            FontSize = 10.5,
            Foreground = T3,
            Opacity = 0.7,
            Margin = new Thickness(8, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        host.Children.Add(titleRow);
        host.Children.Add(new TextBlock
        {
            Text = "一键创建房间，自动选择最佳网络。绝大多数情况下不需要任何设置。",
            FontSize = 11.5,
            Foreground = T2,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // ---------- 卡片 A:创建房间 ----------
        var createCard = new Border
        {
            Background = (Brush)Application.Current.Resources["RowBgBrush"],
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 12, 0, 8),
        };
        var createHost = new StackPanel();
        createHost.Children.Add(new TextBlock
        {
            Text = "创建房间",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        });
        createHost.Children.Add(new TextBlock
        {
            Text = "进入游戏建好世界后，点下面的按钮生成邀请。",
            FontSize = 11.5,
            Foreground = T2,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        _mpToggleButton = new Button
        {
            Content = "创建房间",
            Style = (Style)Application.Current.Resources["GreenBigButton"],
            Height = 44,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(_mpToggleButton, "创建房间");
        _mpToggleButton.Click += async (_, _) => await ToggleHostAsync();
        createHost.Children.Add(_mpToggleButton);

        // 创建成功后的邀请行
        _mpInvitePanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        _mpInvitePanel.Children.Add(new TextBlock
        {
            Text = "房间已创建，把邀请发给朋友：",
            FontSize = 11.5,
            Foreground = T2,
            Opacity = 0.7,
        });
        _mpInviteText = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            FontSize = 11,
            IsReadOnly = true,
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // 固定 Height + VerticalContentAlignment 会让 TextBoxView 画到裁剪区外(框有值但屏幕空白)
        System.Windows.Automation.AutomationProperties.SetName(_mpInviteText, "房间邀请链接");
        _mpInvitePanel.Children.Add(_mpInviteText);
        _mpCopyButton = new Button
        {
            Content = "复制邀请",
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            Height = 44,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(_mpCopyButton, "复制邀请");
        _mpCopyButton.Click += (_, _) => CopyInvite();
        _mpInvitePanel.Children.Add(_mpCopyButton);
        createHost.Children.Add(_mpInvitePanel);
        createCard.Child = createHost;
        host.Children.Add(createCard);

        // ---------- 卡片 B:加入房间 ----------
        var joinCard = new Border
        {
            Background = (Brush)Application.Current.Resources["RowBgBrush"],
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var joinHost = new StackPanel();
        joinHost.Children.Add(new TextBlock
        {
            Text = "加入房间",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        });
        joinHost.Children.Add(new TextBlock
        {
            Text = "把朋友发来的邀请粘贴到这里。",
            FontSize = 11.5,
            Foreground = T2,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        _mpJoinBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            FontSize = 12,
            Height = 44,
            Margin = new Thickness(0, 10, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(_mpJoinBox, "房间邀请输入");
        joinHost.Children.Add(_mpJoinBox);
        var joinButton = new Button
        {
            Content = "加入房间",
            Style = (Style)Application.Current.Resources["GreenBigButton"],
            Height = 44,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(joinButton, "加入房间");
        joinButton.Click += (_, _) => JoinRoomFromBox();
        joinHost.Children.Add(joinButton);
        joinCard.Child = joinHost;
        host.Children.Add(joinCard);

        // ---------- 状态反馈区 ----------
        _mpStatusPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(2, 2, 2, 0) };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        _mpSpinner = new Fuse.App.Controls.Spinner((Brush)Application.Current.Resources["AccentTextBrush"]);
        _mpSpinner.Margin = new Thickness(0, 0, 8, 0);
        _mpSpinner.VerticalAlignment = VerticalAlignment.Center;
        _mpSpinner.Visibility = Visibility.Collapsed;
        statusRow.Children.Add(_mpSpinner);
        _mpBadge = new Border
        {
            Background = (Brush)Application.Current.Resources["AccentChipBrush"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _mpBadgeText = new TextBlock
        {
            Text = "直连",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        };
        _mpBadge.Child = _mpBadgeText;
        statusRow.Children.Add(_mpBadge);
        _mpStatus = new TextBlock
        {
            FontSize = 11.5,
            Foreground = T2,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusRow.Children.Add(_mpStatus);
        _mpStatusPanel.Children.Add(statusRow);

        _mpRetryPanel = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var retry = new Button
        {
            Content = "重试",
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            Height = 44,
            Padding = new Thickness(18, 0, 18, 0),
            Margin = new Thickness(0, 0, 10, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(retry, "重试联机");
        retry.Click += async (_, _) => await ToggleHostAsync();
        _mpRetryPanel.Children.Add(retry);
        var backup = new Button
        {
            Content = "尝试备用方案",
            Style = (Style)Application.Current.Resources["FlatRowButton"],
            Height = 44,
            Padding = new Thickness(6, 0, 6, 0),
            Foreground = (Brush)Application.Current.Resources["AccentTextBrush"],
        };
        System.Windows.Automation.AutomationProperties.SetName(backup, "尝试备用方案");
        backup.Click += (_, _) => _mpExpandAdvanced?.Invoke();
        _mpRetryPanel.Children.Add(backup);
        _mpStatusPanel.Children.Add(_mpRetryPanel);
        host.Children.Add(_mpStatusPanel);

        // ---------- 高级选项(默认收起) ----------
        var advancedOptions = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        BuildAdvancedMultiplayerOptions(advancedOptions);
        var advHeader = new Grid { Background = Brushes.Transparent };
        advHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        advHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        advHeader.Children.Add(new TextBlock
        {
            Text = "高级联机选项（连不上再点这里）",
            FontSize = 11.5,
            Foreground = T3,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var advChev = new TextBlock
        {
            Text = "▾", FontSize = 11, Foreground = T3, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(advChev, 1);
        advHeader.Children.Add(advChev);
        var advButton = new Button
        {
            Style = (Style)Application.Current.Resources["FlatRowButton"],
            Content = advHeader,
            Padding = new Thickness(6, 8, 6, 8),
            Margin = new Thickness(0, 8, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(advButton, "高级联机选项");
        var advOpen = false;
        Action toggleAdv = () =>
        {
            advOpen = !advOpen;
            advancedOptions.Visibility = advOpen ? Visibility.Visible : Visibility.Collapsed;
            advChev.Text = advOpen ? "▴" : "▾";
        };
        advButton.Click += (_, _) => toggleAdv();
        _mpExpandAdvanced = () =>
        {
            if (!advOpen) toggleAdv();
        };
        host.Children.Add(advButton);
        host.Children.Add(advancedOptions);

        card.Child = host;
        _multiplayerCard = card;
        _content.Children.Add(card);
    }

    /// <summary>高级联机选项:保留全部旧功能,只是收进折叠区并换成人类语言。</summary>
    private void BuildAdvancedMultiplayerOptions(StackPanel options)
    {
        // 1. 备用联机组件
        options.Children.Add(SectionLabel("备用联机组件"));
        options.Children.Add(SectionHint("自动连接失败时，可以安装备用组件再试一次。"));
        var modRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var e4mc = new Button
        {
            Content = "安装 e4mc",
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            Height = 44,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(e4mc, "安装 e4mc");
        e4mc.Click += (_, _) => InstallMultiplayerMod(new[] { "e4mc" }, "e4mc");
        modRow.Children.Add(e4mc);
        var offline = new Button
        {
            Content = "安装离线局域网",
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            Height = 44,
            Padding = new Thickness(14, 0, 14, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(offline, "安装离线局域网");
        offline.Click += (_, _) =>
        {
            // 按版本自动挑组件 26.3 用 offlinelan 老版本用 mcwifipnp / lan-properties
            InstallMultiplayerMod(
                new[] { "offlinelan", "mcwifipnp", "lan-properties", "custom-lan", "lan-server-properties" },
                "离线局域网");
            Toast("装好后重启游戏，对局域网开放时把正版验证关掉", _multiplayerCard);
        };
        modRow.Children.Add(offline);
        options.Children.Add(modRow);

        // 2. 口令直连
        options.Children.Add(SectionLabel("口令直连", top: 16));
        options.Children.Add(SectionHint("如果你拿到了好友发来的连接口令，可以粘贴到这里。"));
        var p2pBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            FontSize = 11.5,
            Height = 44,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(p2pBox, "好友口令");
        options.Children.Add(p2pBox);
        var p2pBtn = new Button
        {
            Content = "通过口令直连",
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            Height = 44,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        System.Windows.Automation.AutomationProperties.SetName(p2pBtn, "通过口令直连");
        p2pBtn.Click += (_, _) =>
        {
            var answer = p2pBox.Text.Trim();
            if (answer.Length == 0)
            {
                Toast("先把好友发来的口令粘进来", _multiplayerCard);
                return;
            }
            if (_p2pStatus != null) _p2pStatus.Text = "正在通过口令连接…";
            _ = Task.Run(async () =>
            {
                var ok = await (_mpSession?.AcceptFuseLinkAnswerAsync(answer) ?? Task.FromResult(false));
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_p2pStatus != null && ok) _p2pStatus.Text = "口令连接成功，好友可以进房间了";
                }));
            });
        };
        options.Children.Add(p2pBtn);
        _p2pStatus = new TextBlock
        {
            FontSize = 10.5,
            Foreground = T3,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        options.Children.Add(_p2pStatus);

        // 3. 自建中转网络(FRP)
        options.Children.Add(SectionLabel("自建中转网络（FRP）", top: 16));
        options.Children.Add(SectionHint("需要一点网络基础，适合怎么都连不上时手动搭建中转。"));
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var preset in new[] { TunnelPreset.SakuraFrp, TunnelPreset.OpenFrp })
        {
            var chip = new Button
            {
                Content = preset.Id == "sakura" ? "樱花内网穿透" : "OpenFrp / 自建",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Height = 44,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 0),
            };
            var captured = preset;
            if (preset.Id == _tunnelPreset.Id)
                chip.Background = (Brush)Application.Current.Resources["AccentChipBrush"];
            chip.Click += (_, _) =>
            {
                _tunnelPreset = captured;
                foreach (var other in presetRow.Children.OfType<Button>())
                    other.Background = null;
                chip.Background = (Brush)Application.Current.Resources["AccentChipBrush"];
            };
            presetRow.Children.Add(chip);
        }
        options.Children.Add(presetRow);

        options.Children.Add(FieldLabel("frpc 路径"));
        var exeRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        exeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        exeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var exeBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            FontSize = 11,
            Height = 44,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = _tunnelExePath ?? "",
        };
        exeBox.TextChanged += (_, _) => _tunnelExePath = exeBox.Text.Trim();
        Grid.SetColumn(exeBox, 0);
        exeRow.Children.Add(exeBox);
        var browse = new Button
        {
            Content = "选择 frpc 路径",
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            Height = 44,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(8, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(browse, "选择 frpc 路径");
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 frpc.exe（平台官网下载）",
                Filter = "frpc (frpc.exe)|frpc*.exe|所有程序 (*.exe)|*.exe",
            };
            if (dialog.ShowDialog() == true)
            {
                exeBox.Text = dialog.FileName;
                _tunnelExePath = dialog.FileName;
            }
        };
        Grid.SetColumn(browse, 1);
        exeRow.Children.Add(browse);
        options.Children.Add(exeRow);

        options.Children.Add(FieldLabel("访问密钥，或直接粘贴平台生成的配置文本", top: 10));
        var keyBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            FontSize = 11,
            Height = 56,
            Margin = new Thickness(0, 6, 0, 0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(keyBox, "中转密钥或配置");
        options.Children.Add(keyBox);

        options.Children.Add(FieldLabel("中转地址（平台面板里显示的）", top: 10));
        var remoteRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        remoteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        remoteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var remoteBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            FontSize = 11,
            Height = 44,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(remoteBox, "中转地址");
        Grid.SetColumn(remoteBox, 0);
        remoteRow.Children.Add(remoteBox);
        var startTunnel = new Button
        {
            Content = "启动隧道",
            Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
            Height = 44,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(8, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(startTunnel, "启动隧道");
        startTunnel.Click += (_, _) => StartTunnel(keyBox.Text, remoteBox.Text.Trim());
        Grid.SetColumn(startTunnel, 1);
        remoteRow.Children.Add(startTunnel);
        options.Children.Add(remoteRow);

        _tunnelStatus = new TextBlock
        {
            Text = "隧道未启动",
            FontSize = 10.5,
            Foreground = T3,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        options.Children.Add(_tunnelStatus);
    }

    private static TextBlock FieldLabel(string text, double top = 10) => new()
    {
        Text = text,
        FontSize = 10.5,
        Foreground = T2,
        Opacity = 0.85,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
    };

    private static TextBlock SectionLabel(string text, double top = 0) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = T2,
        Margin = new Thickness(0, top, 0, 0),
    };

    private static TextBlock SectionHint(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Foreground = T3,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 0),
    };

    /// <summary>首屏状态机:加载动画 → 直连/中转徽章 → 失败重试。技术细节只进折叠区。</summary>
    private void ApplyMpState(MultiplayerHostState state)
    {
        if (_mpStatusPanel == null || _mpStatus == null) return;
        var running = state.Phase is "Launching" or "WaitingLan" or "Probing";
        if (state.Phase is "Idle")
        {
            _mpStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }
        _mpStatusPanel.Visibility = Visibility.Visible;

        if (running)
        {
            _mpSpinner!.Visibility = Visibility.Visible;
            _mpSpinner.Start();
            _mpBadge!.Visibility = Visibility.Collapsed;
            _mpRetryPanel!.Visibility = Visibility.Collapsed;
            _mpStatus.Text = "正在寻找最佳连接方式…";
            _mpStatus.Foreground = T2;
            if (_mpInvitePanel != null) _mpInvitePanel.Visibility = Visibility.Collapsed;
            if (_mpToggleButton != null)
            {
                _mpToggleButton.Content = "关闭房间";
                System.Windows.Automation.AutomationProperties.SetName(_mpToggleButton, "关闭房间");
            }
            return;
        }

        _mpSpinner?.Stop();
        if (_mpSpinner != null) _mpSpinner.Visibility = Visibility.Collapsed;

        if (state.Phase == "Ready")
        {
            _mpHadSuccess = true;
            _mpRetryPanel!.Visibility = Visibility.Collapsed;
            _mpStatus.Foreground = T2;
            var direct = state.ChannelKind is not ("frp" or "mod");
            if (direct)
            {
                _mpBadgeText!.Text = "直连";
                _mpBadge!.Background = (Brush)Application.Current.Resources["AccentChipBrush"];
                _mpStatus.Text = "连接成功，延迟较低。";
            }
            else
            {
                _mpBadgeText!.Text = "中转";
                _mpBadge!.Background = new SolidColorBrush(Color.FromArgb(255, 70, 90, 120));
                _mpStatus.Text = "已通过中转连接。";
            }
            _mpBadge.Visibility = Visibility.Visible;
            if (_mpInvitePanel != null)
                _mpInvitePanel.Visibility = string.IsNullOrEmpty(state.InviteLink) ? Visibility.Collapsed : Visibility.Visible;
            if (_mpInviteText != null)
            {
                _mpInviteText.Text = state.InviteLink ?? "";
                _mpInviteText.ScrollToHome();
            }
            if (_mpCopyButton != null) _mpCopyButton.IsEnabled = state.InviteLink != null;
            if (_mpToggleButton != null)
            {
                _mpToggleButton.Content = "关闭房间";
                System.Windows.Automation.AutomationProperties.SetName(_mpToggleButton, "关闭房间");
            }
            return;
        }

        // Closed
        if (_mpInvitePanel != null) _mpInvitePanel.Visibility = Visibility.Collapsed;
        if (_mpToggleButton != null)
        {
            _mpToggleButton.Content = "创建房间";
            System.Windows.Automation.AutomationProperties.SetName(_mpToggleButton, "创建房间");
        }
        _mpBadge?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        _mpStatusPanel.Visibility = Visibility.Visible;
        if (_mpHadSuccess)
        {
            _mpRetryPanel!.Visibility = Visibility.Collapsed;
            _mpStatus.Text = "房间已关闭。";
            _mpStatus.Foreground = T2;
        }
        else
        {
            _mpRetryPanel!.Visibility = Visibility.Visible;
            _mpStatus.Text = "自动连接失败，可能受网络限制。";
            _mpStatus.Foreground = T2;
        }
    }

    private void CopyInvite()
    {
        var invite = _mpSession?.Current.InviteLink;
        if (string.IsNullOrEmpty(invite))
        {
            Toast("房间还没创建好，稍等一下", _multiplayerCard);
            return;
        }
        Toast(ClipboardHelper.TrySetText(invite)
            ? "邀请已复制，发给朋友就行"
            : "复制失败：剪贴板被其他程序占用，请再点一次", _multiplayerCard);
    }

    private void JoinRoomFromBox()
    {
        var text = _mpJoinBox?.Text.Trim() ?? "";
        if (text.Length == 0)
        {
            Toast("先把朋友发来的邀请粘贴进来", _multiplayerCard);
            return;
        }
        MultiplayerJoinWindow.ShowForInvite(text);
    }

    private string? _tunnelExePath;


    private async Task ToggleHostAsync()
    {
        if (_mpSession?.IsRunning == true)
        {
            _mpSession.CloseByUser();
            _mpSession.Dispose();
            _mpSession = null;
            _tunnel?.Stop();
            _mpSpinner?.Stop();
            if (_mpSpinner != null) _mpSpinner.Visibility = Visibility.Collapsed;
            if (_mpBadge != null) _mpBadge.Visibility = Visibility.Collapsed;
            if (_mpRetryPanel != null) _mpRetryPanel.Visibility = Visibility.Collapsed;
            if (_mpInvitePanel != null) _mpInvitePanel.Visibility = Visibility.Collapsed;
            if (_mpStatusPanel != null) _mpStatusPanel.Visibility = Visibility.Visible;
            if (_mpStatus != null) _mpStatus.Text = "房间已关闭。";
            if (_mpToggleButton != null)
            {
                _mpToggleButton.Content = "创建房间";
                System.Windows.Automation.AutomationProperties.SetName(_mpToggleButton, "创建房间");
            }
            if (_mpCopyButton != null) _mpCopyButton.IsEnabled = false;
            return;
        }

        // 先认领启动器重启前就在跑的同一局,认领到就不弹"先启动游戏"
        var gameRunning = LaunchState.IsBusy(_versionId) || AppLauncher.AdoptRunningGame(_versionId);
        if (!gameRunning)
        {
            var confirm = new ConfirmWindow("创建房间",
                "会先启动 " + _versionId + "。进入世界后，回来点一次「创建房间」就能生成邀请。", "启动游戏")
            { Owner = Window.GetWindow(this) };
            if (confirm.ShowDialog() != true) return;
        }

        _mpSession?.Dispose();
        _mpHadSuccess = false;
        _mpSession = new MultiplayerHostSession(_versionId, RootDir());
        _mpSession.StateChanged += state => Dispatcher.BeginInvoke(new Action(() => ApplyMpState(state)));
        _mpSession.P2pStatus += msg => Dispatcher.BeginInvoke(new Action(() =>
        {
            // 口令/P2P 属于高级功能:状态只进折叠区,不污染首屏
            if (_p2pStatus != null) _p2pStatus.Text = msg;
        }));
        _ = _mpSession.RunAsync(CancellationToken.None);

        if (!gameRunning)
        {
            try
            {
                await AppLauncher.LaunchAsync(_versionId, msg => Toast(msg, _multiplayerCard));
            }
            catch (Exception ex)
            {
                Toast("启动失败:" + ex.Message, _multiplayerCard);
            }
        }
    }

    /// <summary>一键装联机 Mod:按 slug 候选逐个找当前 MC/加载器可用版本,复用模组安装管线(含前置)。</summary>
    private void InstallMultiplayerMod(string[] slugs, string label)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Fuse.Core.Installer.ModInstallPlan? plan = null;
                string? used = null;
                foreach (var slug in slugs)
                {
                    try
                    {
                        plan = await Fuse.Core.Installer.ModInstaller.PlanAsync(
                            AppServices.Http, slug, _gameVersion, _loader, ModsDir(),
                            CancellationToken.None).ConfigureAwait(false);
                        used = slug;
                        break;
                    }
                    catch (Exception)
                    {
                        // 换下一个候选
                    }
                }
                if (plan == null || used == null)
                    throw new InvalidOperationException("没有找到适配 " + _gameVersion + " / " + _loader + " 的版本");

                foreach (var item in plan.Items)
                {
                    var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                    {
                        Name = System.IO.Path.GetFileName(item.DestPath),
                        Kind = "file",
                        SourceUrl = item.Url,
                        DestPath = item.DestPath,
                        ExpectedHash = item.ExpectedHash,
                    });
                    DownloadFocus.Register(task.TaskId, "version-detail");
                }
                Dispatcher.BeginInvoke(new Action(() =>
                    Toast("已开始下载 " + label + "(" + plan.Items.Count + " 个文件,装完重开世界)", _multiplayerCard)));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    Toast("装 " + label + " 失败:" + ex.Message, _multiplayerCard)));
            }
        });
    }

    private void StartTunnel(string keyOrConfig, string remoteAddress)
    {
        if (_tunnelStatus == null) return;
        if (string.IsNullOrWhiteSpace(_tunnelExePath) || !System.IO.File.Exists(_tunnelExePath))
        {
            Toast("先选 frpc.exe(平台官网下载)", _multiplayerCard);
            return;
        }
        if (string.IsNullOrWhiteSpace(keyOrConfig))
        {
            Toast("填访问密钥:隧道ID(樱花)或面板配置文本(OpenFrp)", _multiplayerCard);
            return;
        }

        _tunnel?.Stop();
        _tunnel = new TunnelRunner(_tunnelPreset, _tunnelExePath);
        var localPort = _mpSession?.LanPort?.Port ?? 25565;
        _tunnel.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_tunnelStatus == null) return;
            _tunnelStatus.Text = status switch
            {
                TunnelStatus.Starting => "隧道启动中…(看 frpc 输出确认)",
                TunnelStatus.Connected => "隧道已连通!邀请地址:" + (string.IsNullOrWhiteSpace(remoteAddress) ? "(在「远端地址」里填面板给的地址)" : remoteAddress),
                TunnelStatus.Failed => "隧道失败:" + (_tunnel.LastError ?? "未知"),
                TunnelStatus.Exited => "隧道已退出",
                _ => "隧道未启动",
            };
            if (status == TunnelStatus.Connected && !string.IsNullOrWhiteSpace(remoteAddress))
            {
                _mpSession?.SetTunnelAddress(remoteAddress);
            }
        }));
        var ok = _tunnel.Start(keyOrConfig, localPort);
        if (!ok)
            _tunnelStatus.Text = "隧道启动失败:" + _tunnel.LastError;
    }

    /// <summary>加入房间:粘贴邀请码/链接/裸地址 → 复用好友邀请窗。</summary>
    private void ShowJoinDialog()
    {
        var stack = new StackPanel();
        var head = DialogKit.Title("加入好友房间");
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock
        {
            Text = "粘贴好友发来的邀请(点过的链接 / fuse-launcher://… / 裸地址 均可)",
            FontSize = 11.5,
            Foreground = DialogKit.Res("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
        });
        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["DarkInput"],
            FontSize = 12,
            AcceptsReturn = true,
            Height = 64,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "邀请码输入");
        stack.Children.Add(box);
        stack.Children.Add(DialogKit.Buttons(
            DialogKit.Primary("下一步", () =>
            {
                var text = box.Text.Trim();
                if (text.Length == 0) return;
                MultiplayerJoinWindow.ShowForInvite(text);
            }),
            DialogKit.Secondary("取消", () => { })));
        var window = new Window
        {
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Topmost = true,
            Content = DialogKit.Card(stack),
        };
        window.Owner = Window.GetWindow(this);
        DialogKit.WireDrag(window, head);
        DialogKit.WireKeys(window, () => window.DialogResult = false);
        window.ShowDialog();
    }

    private async Task AnalyzeAndFixModsAsync()
    {
        try
        {
            // 只对 Fabric 做自动修复。Forge/NeoForge 的模组元数据不在 fabric.mod.json,
            // 强行分析会把正常模组全判成"缺前置",修一次坏一次。
            if (!string.Equals(_loader, "fabric", StringComparison.OrdinalIgnoreCase))
            {
                new InfoWindow("暂不支持自动修复",
                    "当前是 " + LoaderLabel() + " 实例。" + Environment.NewLine + Environment.NewLine +
                    "自动分析/修复目前只支持 Fabric 模组：Forge / NeoForge 的依赖写在 mods.toml 里，" +
                    "按 Fabric 规则判断会误删正常模组。" + Environment.NewLine + Environment.NewLine +
                    "启动失败可以先用「崩溃救援」查看日志、进安全模式排查。")
                { Owner = Window.GetWindow(this) }.ShowDialog();
                return;
            }

            var errorText = ReadRecentErrorText();
            var report = await Task.Run(() =>
                Fuse.Core.ModConflict.ModConflictAnalyzer.Analyze(ModsDir(), _gameVersion, _loader, errorText));
            if (!report.HasProblems)
            {
                Toast("没有发现模组冲突", _conflictCard);
                return;
            }

            var dialog = new ModConflictWindow(report, _loader) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            if (dialog.SwitchGameVersion)
            {
                await SwitchGameVersionAsync(report.SuggestedGameVersion!);
                return;
            }
            if (dialog.Selected.Count == 0) return;

            Toast("正在修复 " + dialog.Selected.Count + " 项…", _conflictCard);
            var outcome = await Fuse.Core.ModConflict.ModConflictFixer.ApplyAsync(
                ModsDir(), _gameVersion, _loader, dialog.Selected, AppServices.Http,
                new Progress<string>(msg => Dispatcher.BeginInvoke(new Action(() => Toast(msg, _conflictCard)))),
                CancellationToken.None);

            var message = outcome.Applied.Count + " 项已修复";
            if (outcome.Failed.Count > 0)
                message += "\n\n未完成：\n· " + string.Join("\n· ", outcome.Failed);
            if (outcome.BackupCreated)
                message += "\n\n旧文件已备份到：\n" + outcome.BackupDirectory;
            new InfoWindow("模组冲突修复", message) { Owner = Window.GetWindow(this) }.ShowDialog();
            RenderTab();
        }
        catch (Exception ex)
        {
            Toast("修复失败:" + ex.Message, _conflictCard);
        }
    }

    private void BuildBackupSection()
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 4, 0, 8),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "备份与还原",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = T2,
        });

        if (!AppServices.IsManagedLayout(RootDir()))
        {
            host.Children.Add(new TextBlock
            {
                Text = "外部游戏目录不支持备份/复制（实例目录就是整个 .minecraft）。",
                FontSize = 11,
                Foreground = T2,
                Opacity = 0.66,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
            card.Child = host;
            _backupCard = card;
            _content.Children.Add(card);
            return;
        }

        host.Children.Add(new TextBlock
        {
            Text = "备份包含存档、模组、配置、光影和材质，不含日志。",
            FontSize = 11,
            Foreground = T2,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 4),
        };
        var backup = new Button
        {
            Content = "立即备份",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(12, 6, 12, 6),
            IsEnabled = !_backupBusy,
        };
        System.Windows.Automation.AutomationProperties.SetName(backup, "立即备份");
        backup.Click += (_, _) => _ = RunBackupAsync();
        bar.Children.Add(backup);

        var clone = new Button
        {
            Content = "复制此版本",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(clone, "复制此版本");
        clone.Click += (_, _) => RunClone();
        bar.Children.Add(clone);
        host.Children.Add(bar);

        var backups = InstanceBackup.List(RootDir(), _versionId);
        if (backups.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "还没有备份",
                FontSize = 11,
                Foreground = T2,
                Opacity = 0.6,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        else
        {
            var list = new StackPanel();
            foreach (var info in backups.Take(10))
            {
                var captured = info;
                var row = new Border
                {
                    Background = (Brush)Application.Current.Resources["RowBgBrush"],
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                infoPanel.Children.Add(new TextBlock
                {
                    Text = info.Created.ToString("yyyy-MM-dd HH:mm"),
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                });
                infoPanel.Children.Add(new TextBlock
                {
                    Text = FormatSize(info.Size) + " · " + info.DisplayName,
                    FontSize = 10.5,
                    Foreground = T2,
                    Opacity = 0.66,
                });
                Grid.SetColumn(infoPanel, 0);
                grid.Children.Add(infoPanel);

                var restore = new Button
                {
                    Content = "还原",
                    Style = (Style)Application.Current.Resources["ChipButton"],
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                System.Windows.Automation.AutomationProperties.SetName(restore, "还原备份 " + info.DisplayName);
                restore.Click += (_, _) => RestoreBackup(captured);
                Grid.SetColumn(restore, 1);
                grid.Children.Add(restore);

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
                System.Windows.Automation.AutomationProperties.SetName(remove, "删除备份 " + info.DisplayName);
                remove.Click += (_, _) => DeleteBackup(captured);
                Grid.SetColumn(remove, 2);
                grid.Children.Add(remove);

                row.Child = grid;
                list.Children.Add(row);
            }
            var scroll = new ScrollViewer
            {
                Style = (Style)Application.Current.Resources["ModernScrollViewer"],
                MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = list,
            };
            host.Children.Add(scroll);
        }

        // 与「模组冲突修复」的备份联动:只换 mods/ 的备份也在这里给入口
        try
        {
            var modBackups = Directory.Exists(InstDir())
                ? Directory.GetDirectories(InstDir(), "mods-backup-*")
                : Array.Empty<string>();
            if (modBackups.Length > 0)
            {
                var linkRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 8, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                linkRow.Children.Add(new TextBlock
                {
                    Text = "模组修复备份（" + modBackups.Length + " 个）",
                    FontSize = 11,
                    Foreground = T2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                });
                var view = new Button
                {
                    Content = "查看",
                    Style = (Style)Application.Current.Resources["ChipButton"],
                    Padding = new Thickness(10, 4, 10, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                System.Windows.Automation.AutomationProperties.SetName(view, "查看模组修复备份");
                view.Click += (_, _) =>
                {
                    var window = new ModBackupsWindow(ModsDir()) { Owner = Window.GetWindow(this) };
                    window.ShowDialog();
                    RenderTab();
                };
                linkRow.Children.Add(view);
                host.Children.Add(linkRow);
            }
        }
        catch (Exception)
        {
        }

        card.Child = host;
        _backupCard = card;
        _content.Children.Add(card);
    }

    private async Task RunBackupAsync()
    {
        if (_backupBusy) return;
        _backupBusy = true;
        RenderTab();
        try
        {
            var path = await Task.Run(() => InstanceBackup.Create(
                RootDir(), VRoot(), InstDir(), _versionId,
                _profile.DisplayName ?? _versionId));
            WidgetWindow.Boot("[backup] created " + path);
            Toast("备份完成:" + Path.GetFileName(path), _backupCard);
        }
        catch (Exception ex)
        {
            Toast("备份失败:" + ex.Message, _backupCard);
        }
        finally
        {
            _backupBusy = false;
            RenderTab();
        }
    }

    private void RestoreBackup(InstanceBackup.BackupInfo info)
    {
        var confirm = new ConfirmWindow("还原备份",
            "将用这个备份覆盖当前实例的存档、模组和配置。\n\n" +
            "恢复前会自动再备份一次当前状态。\n\n备份时间:" + info.Created.ToString("yyyy-MM-dd HH:mm"),
            "还原", danger: true);
        if (Show(confirm) != true) return;

        try
        {
            var safety = InstanceBackup.Create(RootDir(), VRoot(), InstDir(), _versionId,
                _profile.DisplayName ?? _versionId);
            InstanceBackup.Restore(info.Path, InstDir());
            WidgetWindow.Boot("[backup] restored " + info.Path + " (safety " + safety + ")");
            Toast("已还原;恢复前状态已另存为 " + Path.GetFileName(safety), _backupCard);
        }
        catch (Exception ex)
        {
            Toast("还原失败:" + ex.Message, _backupCard);
        }
        RenderTab();
    }

    private void DeleteBackup(InstanceBackup.BackupInfo info)
    {
        var confirm = new ConfirmWindow("删除备份",
            "确定删除这个备份吗？\n\n" + info.Created.ToString("yyyy-MM-dd HH:mm") +
            "\n" + FormatSize(info.Size),
            "删除", danger: true);
        if (Show(confirm) != true) return;
        try
        {
            File.Delete(info.Path);
            Toast("备份已删除", _backupCard);
        }
        catch (Exception ex)
        {
            Toast("删除失败:" + ex.Message, _backupCard);
        }
        RenderTab();
    }

    private void RunClone()
    {
        try
        {
            var newId = InstanceBackup.Clone(
                RootDir(), VRoot(), Path.Combine(RootDir(), "instances"),
                _versionId, _profile.DisplayName ?? _versionId);
            var profile = AppServices.VersionProfiles.Get(newId);
            profile.DisplayName = (_profile.DisplayName ?? _versionId) + " 副本";
            AppServices.VersionProfiles.Set(newId, profile);
            WidgetWindow.Boot("[backup] cloned " + _versionId + " -> " + newId);
            Toast("已复制为 " + profile.DisplayName, _backupCard);
            // 版本列表在后面,顺手刷新一下
            if (PanelWindow.OpenPanels.TryGetValue("versions", out var versionsPanel) &&
                versionsPanel.PanelContent is VersionsPanel list)
                list.Refresh();
        }
        catch (Exception ex)
        {
            Toast("复制失败:" + ex.Message, _backupCard);
        }
        RenderTab();
    }

    private bool? Show(Window dialog)
    {
        var owner = Window.GetWindow(this);
        if (owner != null) dialog.Owner = owner;
        return dialog.ShowDialog();
    }

    /// <summary>删除此版本:两段式(先出确认区,两个等大按钮),确认后连实例目录与存档一起删。</summary>
    private void BuildDeleteSection()
    {
        if (!_deleteConfirmOpen)
        {
            var open = new Button
            {
                Content = "删除此版本",
                Style = (Style)Application.Current.Resources["GlassSecondaryButton"],
                Height = 40,
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x52, 0x52)),
            };
            System.Windows.Automation.AutomationProperties.SetName(open, "删除此版本");
            open.Click += (_, _) =>
            {
                _deleteConfirmOpen = true;
                RenderTab();
            };
            _content.Children.Add(open);
            return;
        }

        var confirm = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(38, 224, 82, 82)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 12, 0, 0),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "确定删除 " + _versionId + " 吗?",
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        });
        host.Children.Add(new TextBlock
        {
            Text = "版本文件、模组、配置以及存档都会一并删除,无法恢复。",
            FontSize = 11.5, Foreground = T2,
            Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancel = new Button
        {
            Content = "取消",
            Style = (Style)Application.Current.Resources["GreenBigButton"],
            Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            Height = 42,
        };
        System.Windows.Automation.AutomationProperties.SetName(cancel, "取消删除");
        cancel.Click += (_, _) =>
        {
            _deleteConfirmOpen = false;
            RenderTab();
        };
        Grid.SetColumn(cancel, 0);
        buttons.Children.Add(cancel);

        var confirmBtn = new Button
        {
            Content = "确定",
            Style = (Style)Application.Current.Resources["GreenBigButton"],
            Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            Height = 42,
        };
        System.Windows.Automation.AutomationProperties.SetName(confirmBtn, "确定删除");
        confirmBtn.Click += (_, _) => DeleteVersion();
        Grid.SetColumn(confirmBtn, 2);
        buttons.Children.Add(confirmBtn);

        host.Children.Add(buttons);
        confirm.Child = host;
        _content.Children.Add(confirm);
    }

    private void DeleteVersion()
    {
        try
        {
            var versionsDir = Path.Combine(VRoot(), _versionId);
            var instanceDir = InstDir();
            if (Directory.Exists(versionsDir)) Directory.Delete(versionsDir, true);
            if (Directory.Exists(instanceDir)) Directory.Delete(instanceDir, true);
            AppServices.VersionProfiles.Remove(_versionId);
            if (string.Equals(AppServices.Config.LastLaunchedVersion, _versionId, StringComparison.OrdinalIgnoreCase))
            {
                AppServices.Config.LastLaunchedVersion = null;
                AppServices.SaveConfig();
            }
        }
        catch (Exception ex)
        {
            // 面板还开着,用行内提示;失败时不关任何窗口
            Toast("删除失败：" + ex.Message);
            _deleteConfirmOpen = false;
            RenderTab();
            return;
        }

        // 只关掉详情页;版本列表若开着就原地刷新,让用户退回到总版本页面
        if (PanelWindow.OpenPanels.TryGetValue("version-detail", out var panel))
            panel.Close();
        if (PanelWindow.OpenPanels.TryGetValue("versions", out var vp) &&
            vp.PanelContent is VersionsPanel list)
        {
            list.Refresh();
        }
    }

    private string JavaSummary()
        => _profile.JavaPolicy == "manual" && !string.IsNullOrEmpty(_profile.JavaHomeOverride)
            ? "手动指定:" + _profile.JavaHomeOverride
            : "自动检测(跟随全局)";

    private void RefreshJavaSection()
    {
        if (_javaOptions == null) return;
        _javaOptions.Children.Clear();
        _javaOptions.Visibility = _javaExpanded ? Visibility.Visible : Visibility.Collapsed;
        AddJavaOption("自动检测(推荐)", "", _profile.JavaPolicy == "auto");
        foreach (var rt in JavaLocator.FindInstalled()
                     .Concat(JavaLocator.ScanRuntimesRoot(Path.Combine(AppServices.InstallDirectory, "runtimes"))))
            AddJavaOption($"Java {rt.VersionString}({rt.MajorVersion})· {rt.Source}", rt.Home,
                _profile.JavaPolicy == "manual" && _profile.JavaHomeOverride == rt.Home);
    }

    private void AddJavaOption(string label, string home, bool selected)
    {
        var opt = new Border
        {
            Background = selected
                ? null
                : new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (selected)
        {
            var check = new TextBlock
            {
                Text = "✓", FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            grid.Children.Add(check);
        }
        if (selected) opt.SetResourceReference(Border.BackgroundProperty, "AccentChipBrush");
        // 可点行也要有悬停反馈:全站统一为点击高亮色的实心填充
        opt.MouseEnter += (_, _) => opt.SetResourceReference(Border.BackgroundProperty, "HoverSolidBrush");
        opt.MouseLeave += (_, _) =>
        {
            if (selected) opt.SetResourceReference(Border.BackgroundProperty, "AccentChipBrush");
            else opt.Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));
        };
        opt.Child = grid;
        opt.MouseLeftButtonUp += (_, _) =>
        {
            if (string.IsNullOrEmpty(home))
            {
                _profile.JavaPolicy = "auto";
                _profile.JavaHomeOverride = null;
            }
            else
            {
                _profile.JavaPolicy = "manual";
                _profile.JavaHomeOverride = home;
            }
            AppServices.VersionProfiles.Set(_versionId, _profile);
            if (_javaValue != null) _javaValue.Text = JavaSummary();
            RefreshJavaSection();
        };
        _javaOptions!.Children.Add(opt);
    }

    /// <summary>沿 inheritsFrom 找到真正的 MC 版本(OptiFine 等派生版本返回父版本号)。</summary>
    private string ResolveMcVersion(string versionId)
    {
        try
        {
            return Core.Versioning.VersionJsonLoader.ResolveGameVersion(VRoot(), versionId);
        }
        catch (Exception)
        {
            return versionId;
        }
    }

    // ---------- 模组 ----------

    private void BuildModsTab()
    {
        if (_loader == "")
        {
            // 原版实例:提示安装加载器 + 跳转(逻辑同下载面板)
            var card = new Border
            {
                Style = (Style)Application.Current.Resources["GlassRowBorder"],
                Padding = new Thickness(14, 14, 14, 14),
                Margin = new Thickness(0, 4, 0, 0),
            };
            var host = new StackPanel();
            host.Children.Add(new TextBlock
            {
                // OptiFine 只是补丁,单独说清楚(用户指定,仅在这个页面显示)
                Text = _isOptiFine ? "OptiFine只是补丁,需要加载器才能使用模组" : "这是原版实例,无法加载模组",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            host.Children.Add(new TextBlock
            {
                Text = "建议安装加载器(如 Fabric)后再管理模组。", FontSize = 12, Foreground = T2,
                Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap,
            });
            // OptiFine 版本额外给一个"一键组装"入口:Fabric 底座 + OptiFabric + OptiFine 当 mod
            if (_isOptiFine)
            {
                var assemble = new Button
                {
                    Content = "创建可装模组的版本(Fabric + OptiFine)",
                    Style = (Style)Application.Current.Resources["GreenBigButton"],
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 8),
                    Height = 42,
                };
                System.Windows.Automation.AutomationProperties.SetName(assemble, "创建可装模组的版本");
                assemble.Click += async (_, _) =>
                {
                    try
                    {
                        var mc = ResolveMcVersion(_versionId);
                        var versions = await FabricInstaller.FetchLoaderVersionsAsync(
                            AppServices.Http, mc, CancellationToken.None);
                        if (versions.Count == 0)
                        {
                            ErrorReporter.Report("无法创建 OptiFine 模组环境",
                                new InvalidOperationException($"Fabric 暂不支持 {mc}"));
                            return;
                        }
                        var meta = new JObject
                        {
                            ["mcVersion"] = mc,
                            ["loaderVersion"] = versions[0],
                            ["versionsRoot"] = VRoot(),
                            ["librariesRoot"] = LibsRoot(),
                            ["assetsRoot"] = AssetsRoot(),
                            ["instancesRoot"] = Path.Combine(AppServices.InstallDirectory, "instances"),
                        }.ToString();
                        var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                        {
                            Name = $"{mc} + Fabric + OptiFine",
                            Kind = "optifine-assemble",
                            Meta = meta,
                        });
                        DownloadFocus.Register(task.TaskId, "version-detail");
                        RenderTab();

                        // 组装完成后提示"还差 OptiFabric"这一步(半自动方案)
                        var fabricId = $"{mc}-fabric-{versions[0]}";
                        var modsDir = Path.Combine(AppServices.InstallDirectory, "instances", fabricId, "mods");
                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(1),
                        };
                        timer.Tick += (_, _) =>
                        {
                            var rec = AppServices.Queue?.GetTasks()?.FirstOrDefault(t => t.TaskId == task.TaskId);
                            if (rec == null) return;
                            if (rec.Status == Fuse.Core.Download.DownloadTaskStatus.Downloading ||
                                rec.Status == Fuse.Core.Download.DownloadTaskStatus.Queued) return;
                            timer.Stop();
                            if (rec.Status != Fuse.Core.Download.DownloadTaskStatus.Completed) return;
                            NoticeDialog.Show(
                                "Fabric + OptiFine 已就绪",
                                "OptiFine 已作为模组放进 mods 目录。" + Environment.NewLine
                                + "还差 OptiFabric（让 OptiFine 能在 Fabric 下运行）：" + Environment.NewLine
                                + "下载它的 jar，放进下面这个目录即可。" + Environment.NewLine + Environment.NewLine
                                + modsDir,
                                "打开下载页",
                                () => System.Diagnostics.Process.Start(
                                    "https://www.curseforge.com/minecraft/mc-mods/optifabric"));
                        };
                        timer.Start();
                    }
                    catch (Exception ex)
                    {
                        ErrorReporter.Report("创建 OptiFine 模组环境失败", ex);
                    }
                };
                host.Children.Add(assemble);
            }

            var go = new Button
            {
                Content = "去安装加载器", Style = (Style)Application.Current.Resources["GreenBigButton"],
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            go.Click += (_, _) =>
            {
                // 打开独立的加载器安装页(与下载页互不覆盖);标题用本版本显示名。
                // OptiFine 版本:默认底座就是当前 OptiFine 版本(用户指定),而不是它的原版。
                DownloadPanel.PendingFixedVersionId = _isOptiFine ? _versionId : _gameVersion;
                DownloadPanel.PendingFixedTitle = _profile.DisplayName ?? _versionId;
                var widget = AppServices.Widget;
                if (widget != null)
                    PanelWindow.ShowPanel("loader-install", new Point(0, 0), false, widget);
            };
            host.Children.Add(go);

            // 已存在的加载器版(装过但当前看的仍是原版)——给个直达入口,免得用户以为没装成功
            var derived = FindDerivedLoaderVersions();
            if (derived.Count > 0)
            {
                host.Children.Add(new TextBlock
                {
                    Text = "已有加载器版(原版保持不动):", FontSize = 11.5, Foreground = T3,
                    Margin = new Thickness(0, 12, 0, 6),
                });
                foreach (var d in derived)
                    host.Children.Add(MakeDerivedRow(d));
            }

            card.Child = host;
            _content.Children.Add(card);
            return;
        }

        // 回到模组页必须把浏览状态切回 mod:否则从光影页切过来时,
        // 列表会继续按 project_type=shader 查询,而"类型"显示的是模组分类(用户报的串页 bug)
        _browseProjectType = "mod";
        _browseDestDir = "";
        BuildBrowseUi(isModsTab: true);

        // 整合包实例:顶栏提醒一句,但列表照常显示(之前整页只剩提醒,用户以为内容看不了)
        if (IsModpackVersion() && !_modpackWarningDismissed)
            _content.Children.Insert(0, BuildModpackModWarning());
    }

    /// <summary>
    /// 光影/材质页:与模组页同骨架(搜索 + 类型浮层 + 无限滚动),数据源与落盘目录不同。
    /// 光影页先做前置检测,没有 Iris / Oculus / OptiFine 时给"一键安装配套前置"。
    /// </summary>
    /// <summary>整合包实例进模组下载页时的提醒:避免用户随手加模组导致冲突。</summary>
    private Border BuildModpackModWarning()
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Margin = new Thickness(0, 0, 0, 8),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "整合包不建议自己加模组哦",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        });
        host.Children.Add(new TextBlock
        {
            Text = "整合包里的模组是作者搭配好的，自己加或换版本容易冲突、启动失败。\n确实要加的话，建议先在「修改」页备份一次。想看已装的模组，去「本地」页。",
            FontSize = 11.5,
            Foreground = T2,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        var go = new Button
        {
            Content = "知道了",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (Application.Current.Resources["AccentChipBrush"] is Brush accent)
            go.Background = accent;
        System.Windows.Automation.AutomationProperties.SetName(go, "知道了");
        go.Click += (_, _) =>
        {
            _modpackWarningDismissed = true;
            if (card.Parent is Panel parent) parent.Children.Remove(card);
        };
        host.Children.Add(go);
        card.Child = host;
        return card;
    }

    private void BuildBrowseTab(string kind)
    {
        var isShader = kind == "shaders";
        _browseProjectType = isShader ? "shader" : "resourcepack";
        _browseCategories = isShader ? ShaderCategories : PackCategories;
        _browseDestDir = Path.Combine(InstDir(),
            isShader ? "shaderpacks" : "resourcepacks");

        if (isShader)
        {
            if (!HasShaderLoader())
            {
                _content.Children.Add(BuildShaderPreCard());
                return;   // 没前置不显示列表
            }
            _shaderPrePending = false;
        }
        else if (HasShaderLoader())
        {
            // 材质页:只在检测到光影前置时提示一句(用户要求,不做常驻说明、不拦截)
            _content.Children.Add(new TextBlock
            {
                Text = "已检测到光影前置，高级材质特性可用",
                FontSize = 11, Foreground = T3, Margin = new Thickness(2, 0, 0, 6),
            });
        }

        BuildBrowseUi(isModsTab: false);
    }

    private void BuildBrowseUi(bool isModsTab)
    {
        if (isModsTab) _browseCategories = Categories;

        // 下载源(仅模组页):CurseForge 尚未接入,占位展示
        if (isModsTab)
        {
            _content.Children.Add(BuildAccordion("下载源", _source == "modrinth" ? "Modrinth" : "CurseForge", options =>
            {
                options.Children.Add(SourceOption("Modrinth", "官方 API", "modrinth"));
                var cf = SourceOption("CurseForge",
                    CurseForgeSource.IsConfigured ? "官方 API" : "未配置 API Key", "curseforge");
                if (!CurseForgeSource.IsConfigured)
                {
                    cf.Opacity = 0.45;
                    cf.IsEnabled = false;
                }
                options.Children.Add(cf);
            }));
        }

        // 搜索框(圆角同卡片)
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
            _query = search.Text.Trim();
            ReloadMods();
        };
        search.TextChanged += (_, _) =>
        {
            debounce.Stop();
            debounce.Start();
        };
        _content.Children.Add(search);

        // 类型:浮层下拉(固定 4 行高、半宽、右对齐,覆盖在模组列表之上而非把布局撑开)
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
        _typeValueText = new TextBlock
        {
            Text = _category == "全部" ? "全部" : CategoryLabel(_category),
            FontSize = 12, Margin = new Thickness(0, 0, 8, 0),
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_typeValueText, 1);
        typeGrid.Children.Add(_typeValueText);
        _typeChevron = new TextBlock
        {
            Text = "▾", FontSize = 12, Foreground = T2, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_typeChevron, 2);
        typeGrid.Children.Add(_typeChevron);
        var typeRow = new Button
        {
            Style = (Style)Application.Current.Resources["FlatRowButton"],
            Content = typeGrid,
            Padding = new Thickness(6, 4, 6, 4),
        };
        System.Windows.Automation.AutomationProperties.SetName(typeRow, "类型 展开");
        typeRowOuter.Child = typeRow;
        _typeRow = typeRowOuter;

        // 列表与浮层同处一个 Grid:浮层从列表顶部向下覆盖,不推挤任何元素
        var listHost = new Grid();
        listHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _modList = new StackPanel();
        _modFoot = null;
        _modScroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _modList,
        };
        _modScroll.ScrollChanged += (_, _) =>
        {
            if (_modScroll == null || _loadingMods || _hits.Count >= _total) return;
            if (_modScroll.VerticalOffset >= _modScroll.ScrollableHeight - 32 && _modScroll.ScrollableHeight > 0)
                LoadMoreMods();
        };
        listHost.Children.Add(_modScroll);

        _typeOverlay = BuildTypeOverlay();
        Panel.SetZIndex(_typeOverlay, 10);
        listHost.Children.Add(_typeOverlay);

        typeRow.Click += (_, _) => ToggleTypeOverlay();
        _content.Children.Add(typeRowOuter);
        _content.Children.Add(listHost);


        ReloadMods();
    }

    // ---------- BuildModsTab / BuildBrowseUi 结束 ----------

    private UIElement BuildAccordion(string title, string value, Action<StackPanel> fillOptions)
    {
        var expanded = false;
        var header = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, -4, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = T2, VerticalAlignment = VerticalAlignment.Center,
        });
        var valueT = new TextBlock
        {
            Text = value, FontSize = 12, Margin = new Thickness(0, 0, 8, 0),
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueT, 1);
        header.Children.Add(valueT);
        var chev = new TextBlock { Text = "▾", FontSize = 12, Foreground = T2, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chev, 2);
        header.Children.Add(chev);

        var options = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 2) };
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["OptRow"],
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var host = new StackPanel();
        // 表头用 Button:真实点击与 UIA Invoke 都可靠(Border 的鼠标事件在置顶透明窗上不收合成输入)
        var headerBtn = new Button
        {
            Style = (Style)Application.Current.Resources["FlatRowButton"],
            Content = header,
            Padding = new Thickness(6, 4, 6, 4),
        };
        System.Windows.Automation.AutomationProperties.SetName(headerBtn, title + " 展开");
        host.Children.Add(headerBtn);
        host.Children.Add(options);
        card.Child = host;
        headerBtn.Click += (_, _) =>
        {
            expanded = !expanded;
            if (expanded && options.Children.Count == 0) fillOptions(options);
            options.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chev.Text = expanded ? "▴" : "▾";
        };
        return card;
    }

    private UIElement SourceOption(string title, string sub, string key)
    {
        var selected = _source == key;
        var opt = MakeOptionRow(title, sub, selected);
        opt.Click += (_, _) =>
        {
            if (_source == key) return;
            _source = key;
            // 重建整个页签:否则卡片副标题/选中勾不会刷新,看起来像"点了没反应"
            RenderTab();
        };
        return opt;
    }

    private UIElement CategoryOption(string label, string key)
    {
        var selected = _category == key;
        var opt = MakeOptionRow(label, selected ? "已选 ✓" : "选择", selected);
        opt.Click += (_, _) =>
        {
            if (_category == key)
            {
                _typeExpanded = false; // 再点已选项 = 收起浮层
                RenderTab();
                return;
            }
            _category = key;
            _typeExpanded = false; // 选完收起,回到列表
            // 只重建:Rebuild 内部会带着新条件重新拉列表(先 ReloadMods 会填到即将被丢弃的旧控件上)
            RenderTab();
        };
        return opt;
    }

    /// <summary>
    /// 类型浮层:固定可见 4 行,宽度取类型行的 1/2,右对齐,
    /// 叠在模组列表之上(不改动布局高度,不会把列表推走)。
    /// </summary>
    private Border BuildTypeOverlay()
    {
        var options = new StackPanel();
        options.Children.Add(CategoryOption("全部", "全部"));
        foreach (var c in _browseCategories)
            options.Children.Add(CategoryOption(CategoryLabel(c), c));

        var scroller = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            Height = 4 * 40, // 恰好四行(行高 40)
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = options,
        };
        var overlay = new Border
        {
            Background = (Brush)Application.Current.Resources["PanelBgBrush"],
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 2, 0, 0),
            Width = 174, // 类型行宽(约 348)的一半;Show 时按实际宽度校正
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = _typeExpanded ? Visibility.Visible : Visibility.Collapsed,
            Child = scroller,
        };
        overlay.Loaded += (_, _) =>
        {
            if (_typeRow != null && _typeRow.ActualWidth > 0)
                overlay.Width = Math.Round(_typeRow.ActualWidth / 2);
        };
        return overlay;
    }

    private void ToggleTypeOverlay()
    {
        if (_typeOverlay == null) return;
        _typeExpanded = !_typeExpanded;
        _typeOverlay.Visibility = _typeExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (_typeChevron != null) _typeChevron.Text = _typeExpanded ? "▴" : "▾";
    }

    /// <summary>找出 inheritsFrom 指向当前版本的已安装版本(即"这个原版的加载器版")。</summary>
    private List<string> FindDerivedLoaderVersions()
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(VRoot())) return result;
            foreach (var dir in Directory.GetDirectories(VRoot()))
            {
                var id = Path.GetFileName(dir);
                if (string.Equals(id, _versionId, StringComparison.OrdinalIgnoreCase)) continue;
                var json = Path.Combine(dir, id + ".json");
                if (!File.Exists(json)) continue;
                try
                {
                    var obj = JObject.Parse(File.ReadAllText(json));
                    if (string.Equals(obj.Value<string>("inheritsFrom"), _versionId, StringComparison.OrdinalIgnoreCase))
                        result.Add(id);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
        return result;
    }

    /// <summary>已存在的加载器版行:点击直接打开它的详情页。</summary>
    private Button MakeDerivedRow(string id)
    {
        var row = new Button
        {
            Style = (Style)Application.Current.Resources["OptRowButton"],
            Margin = new Thickness(0, 0, 0, 6),
            Content = new TextBlock
            {
                Text = id + "   ›",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            },
        };
        row.SetResourceReference(Button.BackgroundProperty, "AccentChipSoftBrush");
        System.Windows.Automation.AutomationProperties.SetName(row, "打开加载器版 " + id);
        row.Click += (_, _) =>
        {
            VersionDetailPanel.PendingVersionId = id;
            VersionDetailPanel.PendingTitle = "详情 · " + id;
            var widget = AppServices.Widget;
            if (widget != null)
            {
                // 关掉当前详情页再开目标版本的详情页(同类单实例,必须换实例)
                if (PanelWindow.OpenPanels.TryGetValue("version-detail", out var existing))
                    existing.Close();
                PanelWindow.ShowPanel("version-detail", new Point(0, 0), false, widget);
            }
        };
        return row;
    }

    /// <summary>
    /// 光影前置检测:mods 目录里有 optifine / iris / oculus / optifabric 任意一个,
    /// 或当前版本本身就是 / 继承自 OptiFine,即认为可以加载光影。
    /// </summary>
    private bool HasShaderLoader()
    {
        try
        {
            var modsDir = ModsDir();
            if (Directory.Exists(modsDir))
            {
                foreach (var f in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
                {
                    var n = Path.GetFileName(f);
                    if (n.IndexOf("optifine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("optifabric", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("iris", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("oculus", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            if (_versionId.IndexOf("optifine", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var merged = Fuse.Core.Versioning.VersionJsonLoader.Load(VRoot(), _versionId);
            foreach (var lib in merged.Libraries)
                if (lib.Name.IndexOf("optifine", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
        }
        catch (Exception)
        {
        }
        return false;
    }

    /// <summary>光影前置缺失卡片:按加载器给"一键安装配套前置"。</summary>
    private UIElement BuildShaderPreCard()
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["GlassRowBorder"],
            Padding = new Thickness(14),
            Margin = new Thickness(0, 4, 0, 0),
        };
        var host = new StackPanel();
        host.Children.Add(new TextBlock
        {
            Text = "未检测到光影前置",
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        host.Children.Add(new TextBlock
        {
            Text = _loader == ""
                ? "原版实例需要 OptiFine 才能加载光影。"
                : "光影需要 Iris（Fabric）或 Oculus（Forge / NeoForge）之一，当前实例都没有。",
            FontSize = 12, Foreground = T2, Margin = new Thickness(0, 4, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        // 原版实例:已装过 OptiFine 独立版 → 直达入口
        if (_loader == "")
        {
            var optifineVersions = FindDerivedLoaderVersions()
                .Where(id => id.IndexOf("optifine", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (optifineVersions.Count > 0)
            {
                host.Children.Add(new TextBlock
                {
                    Text = "已找到 OptiFine 独立版本(在它的详情页里下载光影即可):",
                    FontSize = 11.5, Foreground = T3, Margin = new Thickness(0, 4, 0, 6),
                });
                foreach (var id in optifineVersions)
                    host.Children.Add(MakeDerivedRow(id));
            }
            else
            {
                var go = new Button
                {
                    Content = "去安装 OptiFine",
                    Style = (Style)Application.Current.Resources["GreenBigButton"],
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Height = 42,
                };
                System.Windows.Automation.AutomationProperties.SetName(go, "去安装 OptiFine");
                go.Click += (_, _) =>
                {
                    // 复用下载页的 OptiFine 卡片(含 Java 自动下载)
                    DownloadPanel.PendingFixedVersionId = _versionId;
                    DownloadPanel.PendingFixedTitle = _profile.DisplayName ?? _versionId;
                    var widget = AppServices.Widget;
                    if (widget != null)
                        PanelWindow.ShowPanel("loader-install", new Point(0, 0), false, widget);
                };
                host.Children.Add(go);
            }
            card.Child = host;
            return card;
        }

        // Fabric / Forge / NeoForge:Iris+Sodium 或 Oculus+Embeddium,直接进 mods
        if (_shaderPrePending)
        {
            host.Children.Add(new TextBlock
            {
                Text = "前置下载中…完成后点「重新检测」。",
                FontSize = 12, Foreground = T2, Margin = new Thickness(0, 0, 0, 8),
            });
        }
        else
        {
            var install = new Button
            {
                Content = "一键安装配套前置",
                Style = (Style)Application.Current.Resources["GreenBigButton"],
                HorizontalAlignment = HorizontalAlignment.Left,
                Height = 42,
            };
            System.Windows.Automation.AutomationProperties.SetName(install, "一键安装配套前置");
            install.Click += (_, _) =>
            {
                _shaderPrePending = true;
                _ = InstallShaderPreRequisite();
                RenderTab();
            };
            host.Children.Add(install);
        }

        var recheck = new Button
        {
            Content = "重新检测",
            Style = (Style)Application.Current.Resources["ChipButton"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(recheck, "重新检测");
        recheck.Click += (_, _) => RenderTab();
        host.Children.Add(recheck);

        card.Child = host;
        return card;
    }

    /// <summary>
    /// 把与当前加载器匹配的光影前置加进下载队列。
    /// 按候选组合依次尝试,第一组全部取到就用它:
    ///   Fabric   → Iris+Sodium(fabric)
    ///   NeoForge → Iris+Sodium(neoforge,Iris 现已原生支持 NeoForge,26.3 实测存在)
    ///              → Oculus+Embeddium(neoforge) → Oculus+Embeddium(forge 兼容)
    ///   Forge    → Oculus+Embeddium(forge) → Iris+Sodium(forge)
    /// </summary>
    private async System.Threading.Tasks.Task InstallShaderPreRequisite()
    {
        try
        {
            var gv = string.IsNullOrEmpty(_gameVersion) ? ResolveMcVersion(_versionId) : _gameVersion;
            var modsDir = ModsDir();

            var (slugA, slugB, loaderA, loaderB) = _loader switch
            {
                "fabric" => ("iris", "sodium", "fabric", "fabric"),
                "neoforge" => ("iris", "sodium", "neoforge", "neoforge"),
                _ => ("oculus", "embeddium", "forge", "forge"),
            };
            var fallbacks = _loader switch
            {
                "neoforge" => new[]
                {
                    ("oculus", "embeddium", "neoforge", "neoforge"),
                    ("oculus", "embeddium", "forge", "forge"),
                },
                "forge" => new[]
                {
                    ("iris", "sodium", "forge", "forge"),
                },
                _ => Array.Empty<(string, string, string, string)>(),
            };

            var queued = 0;
            foreach (var (a, b, la, lb) in new[] { (slugA, slugB, loaderA, loaderB) }.Concat(fallbacks))
            {
                var pair = await ResolveCompatiblePairAsync(a, b, la, lb, gv).ConfigureAwait(false);
                if (pair == null) continue;

                foreach (var f in new[] { pair.Value.A, pair.Value.B })
                {
                    var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                    {
                        Name = f.FileName,
                        Kind = "file",
                        SourceUrl = f.Url,
                        DestPath = Path.Combine(modsDir, f.FileName),
                        ExpectedHash = string.IsNullOrEmpty(f.Sha1) ? null : f.Sha1,
                    });
                    DownloadFocus.Register(task.TaskId, "version-detail");
                    queued++;
                }
                break;   // 第一组成功即止,不混装不同加载器的前置
            }
            if (queued == 0)
            {
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    Toast("该版本暂无适配的光影前置，建议改用 Fabric / Forge 实例");
                    _shaderPrePending = false;
                    RenderTab();
                }));
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(new Action(() =>
            {
                _shaderPrePending = false;
                Toast("前置下载失败:" + ex.Message);
                RenderTab();
            }));
        }
    }

    /// <summary>
    /// 选一对"都能在当前 MC 版本上跑"的前置:
    /// 先看 A 依赖里 pin 的配套版本(如 Iris → 指定 Sodium 版本),pin 的版本不支持当前
    /// MC 就跳过这个 A(实测 Iris 1.11.4 要求 Sodium 0.9.x,而 0.9.x 只有 26.1.2 的构建,
    /// 装到 26.1 上启动直接报不兼容);没有 pin 时从 B 的候选里挑支持当前版本的最新版。
    /// </summary>
    private static async System.Threading.Tasks.Task<(Fuse.Core.Sources.ModrinthVersionFile A,
        Fuse.Core.Sources.ModrinthVersionFile B)?> ResolveCompatiblePairAsync(
        string slugA, string slugB, string loaderA, string loaderB, string gv)
    {
        try
        {
            var aCandidates = await Fuse.Core.Sources.ModrinthSource.GetVersionFilesAsync(
                AppServices.Http, slugA, gv, loaderA, CancellationToken.None).ConfigureAwait(false);

            foreach (var a in aCandidates)
            {
                Fuse.Core.Sources.ModrinthVersionFile? pinned = null;
                foreach (var dep in a.RequiredDeps)
                {
                    if (string.IsNullOrEmpty(dep.VersionId)) continue;
                    try
                    {
                        var cand = await Fuse.Core.Sources.ModrinthSource.GetVersionFileByIdAsync(
                            AppServices.Http, dep.VersionId, CancellationToken.None).ConfigureAwait(false);
                        if (cand.FileName.IndexOf(slugB, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            pinned = cand;
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        // 某个依赖取不到不算整体失败,继续找下一个
                    }
                }

                if (pinned != null)
                {
                    // A 明确要求这个配套版本:它不支持当前 MC 就换更老的 A
                    if (!await Fuse.Core.Sources.ModrinthSource.IsJarCompatibleWithMcAsync(
                            AppServices.Http, pinned, gv, CancellationToken.None).ConfigureAwait(false)) continue;
                    return (a, pinned);
                }

                try
                {
                    var bs = await Fuse.Core.Sources.ModrinthSource.GetVersionFilesAsync(
                        AppServices.Http, slugB, gv, loaderB, CancellationToken.None).ConfigureAwait(false);
                    var b = bs.FirstOrDefault(x => SupportsGameVersion(x, gv));
                    if (b != null) return (a, b);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static bool SupportsGameVersion(Fuse.Core.Sources.ModrinthVersionFile f, string gv)
        => f.GameVersions.Count == 0 ||
           f.GameVersions.Any(v => string.Equals(v, gv, StringComparison.OrdinalIgnoreCase));

    private static Button MakeOptionRow(string title, string trailing, bool selected)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(trailing))
        {
            var trail = new TextBlock
            {
                Text = trailing, FontSize = 11, Foreground = T2, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(trail, 1);
            grid.Children.Add(trail);
        }
        var opt = new Button
        {
            Style = (Style)Application.Current.Resources["OptRowButton"],
            Content = grid,
            Background = selected
                ? null
                : new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
            Margin = new Thickness(0, 0, 0, 6),
        };
        if (selected) opt.SetResourceReference(Button.BackgroundProperty, "AccentChipBrush");
        System.Windows.Automation.AutomationProperties.SetName(opt, title);
        return opt;
    }

    internal static string CategoryLabel(string slug)
    {
        switch (slug)
        {
            case "全部": return "全部";
            case "adventure": return "冒险";
            case "decoration": return "装饰";
            case "economy": return "经济";
            case "equipment": return "装备";
            case "food": return "食物";
            case "gameplay": return "玩法";
            case "library": return "库 / 前置";
            case "magic": return "魔法";
            case "management": return "管理";
            case "minigame": return "小游戏";
            case "optimization": return "优化";
            case "social": return "社交";
            case "storage": return "存储";
            case "technology": return "科技";
            case "transportation": return "运输";
            case "utility": return "实用";
            case "worldgen": return "世界生成";
            // 光影 / 材质分类
            case "realistic": return "写实";
            case "semi-realistic": return "半写实";
            case "fantasy": return "幻想";
            case "cartoon": return "卡通";
            case "vanilla-like": return "原版风";
            case "simplistic": return "简约";
            case "16x": return "16x";
            case "32x": return "32x";
            default: return slug;
        }
    }

    private void ReloadMods()
    {
        _offset = 0;
        _total = 0;
        _hits.Clear();
        // 重建即作废在途请求:否则旧请求的"加载中"标志会把后续加载永久挡住(列表空白且不再恢复)
        _loadingMods = false;
        _loadToken++;
        if (_modList != null) _modList.Children.Clear();
        LoadMoreMods();
    }

    private void LoadMoreMods()
    {
        if (_loadingMods || _modList == null) return;
        _loadingMods = true;
        var gen = _generation;
        var token = _loadToken;
        if (_offset == 0)
        {
            _modList.Children.Clear();
            _modList.Children.Add(Hint("正在搜索…"));
        }
        var query = _query;
        var category = _category;
        var offset = _offset;
        _ = Task.Run(async () =>
        {
            try
            {
                // 中文关键词先查本地对照表映射成英文名,否则 Modrinth 只会返回汉化包/整合包
                var effectiveQuery = ModLocalization.ToEnglishQuery(query) ?? query;
                List<ModrinthHit> hits;
                int total;
                if (_source == "curseforge")
                {
                    var ldr = _browseProjectType == "mod" ? _loader : "";
                    var cf = await CurseForgeSource.SearchAsync(
                        effectiveQuery, _gameVersion, ldr, category, offset, 20,
                        CancellationToken.None, _browseProjectType).ConfigureAwait(false);
                    hits = cf.Hits.Select(h => new ModrinthHit(
                        h.Id.ToString(), h.Slug, h.Name, h.Summary, h.Downloads,
                        h.ProjectType, h.IconUrl ?? "", "")).ToList();
                    total = cf.Total;
                }
                else
                {
                    var r = await ModrinthSource.SearchAsync(
                        AppServices.Http, effectiveQuery, _gameVersion,
                        _browseProjectType == "mod" ? _loader : "", category, offset, 20,
                        CancellationToken.None, _browseProjectType).ConfigureAwait(false);
                    hits = r.Hits;
                    total = r.Total;
                }
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    // token 过期 = 有更新的加载批次接管,这里不能碰任何状态
                    if (token != _loadToken) return;
                    _loadingMods = false;
                    if (gen != _generation || _modList == null) return;
                    _total = total;
                    if (_offset == 0) _modList.Children.Clear();
                    if (hits.Count == 0 && _offset == 0)
                    {
                        _modList.Children.Add(Hint("没有找到适配 " + _gameVersion + " 的模组"));
                        return;
                    }
                    // 第一页本地重排:用映射后的英文名评分(中文关键词比不过英文标题)
                    var ranked = offset == 0 ? RankByMatch(hits, query, effectiveQuery) : hits;
                    foreach (var h in ranked)
                    {
                        _hits.Add(h);
                        _modList.Children.Add(BuildModRow(h));
                    }
                    _offset += hits.Count;
                    RefreshFoot();
                }));
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    if (token != _loadToken) return;
                    _loadingMods = false;
                    if (gen != _generation || _modList == null) return;
                    if (_offset == 0)
                    {
                        _modList.Children.Clear();
                        _modList.Children.Add(Hint("搜索失败:" + ex.Message));
                    }
                }));
            }
        });
    }

    /// <summary>
    /// 按与查询词的匹配程度排序(稳定排序,同档内保持服务端相关性顺序):
    /// 完全相等 > 前缀 > 包含 > 其他。中文名对中文查询、标题/slug 对英文查询分别评分,取高分。
    /// </summary>
    private static List<ModrinthHit> RankByMatch(List<ModrinthHit> hits, string zhQuery, string enQuery)
    {
        var zhQ = (zhQuery ?? "").Trim();
        var enQ = (enQuery ?? "").Trim();
        if (zhQ.Length == 0 && enQ.Length == 0) return hits;

        int Score(ModrinthHit h)
        {
            var zh = ModLocalization.ChineseName(h.Slug) ?? "";
            var title = h.Title ?? "";
            var slug = h.Slug ?? "";
            var best = 1;

            void Consider(string candidate, string q)
            {
                if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(q)) return;
                if (string.Equals(candidate, q, StringComparison.OrdinalIgnoreCase)) { if (best < 4) best = 4; }
                else if (candidate.StartsWith(q, StringComparison.OrdinalIgnoreCase)) { if (best < 3) best = 3; }
                else if (candidate.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { if (best < 2) best = 2; }
            }

            Consider(zh, zhQ);
            Consider(title, enQ);
            Consider(slug, enQ);
            Consider(title, zhQ); // 少数模组标题本身就带中文
            return best;
        }

        return hits.OrderByDescending(Score).ToList();
    }

    private void RefreshFoot()
    {
        if (_modList == null) return;
        // 移除旧 footer(最后一条若是 Hint 文本则重建)
        if (_modFoot != null)
        {
            _modList!.Children.Remove(_modFoot);
            _modFoot = null;
        }
        if (_hits.Count == 0) return;
        _modFoot = _hits.Count >= _total ? Hint("没有更多了") : Hint("下拉加载更多…");
        _modList!.Children.Add(_modFoot);
    }

    private Border BuildModRow(ModrinthHit hit)
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

        // 图标(失败回退首字母块)
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

        // 标题行:中文名在前(大字),英文名在后(小字灰、超出省略);未收录中文名时只显示英文
        var zhName = ModLocalization.ChineseName(hit.Slug);
        if (string.Equals(zhName, hit.Title, StringComparison.Ordinal)) zhName = null; // 相同就不重复显示
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var primary = new TextBlock
        {
            Text = zhName ?? hit.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        titleRow.Children.Add(primary);
        if (zhName != null)
        {
            var english = new TextBlock
            {
                Text = hit.Title,
                FontSize = 10.5,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };
            Grid.SetColumn(english, 1);
            titleRow.Children.Add(english);
        }
        info.Children.Add(titleRow);
        var date = hit.DateModified.Length >= 10 ? hit.DateModified.Substring(0, 10) : hit.DateModified;
        var sourceLabel = _source == "curseforge" ? "CurseForge" : "Modrinth";
        info.Children.Add(new TextBlock
        {
            Text = date.Length > 0
                ? $"{FormatCount(hit.Downloads)} 下载 · {date} · {sourceLabel}"
                : $"{FormatCount(hit.Downloads)} 下载 · {sourceLabel}",
            FontSize = 10.5, Foreground = T2, Opacity = 0.66,
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        // 操作区:网页跳转 + 下载/下载中/已安装
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var web = new Button
        {
            Style = (Style)Application.Current.Resources["ChipButton"],
            Width = 28, Height = 28,
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = _source == "curseforge" ? "在 CurseForge 打开" : "在 Modrinth 打开",
            Content = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(web,
            (_source == "curseforge" ? "在 CurseForge 打开 " : "在 Modrinth 打开 ") + hit.Title);
        web.Click += (_, _) => OpenProjectPage(_source, hit.Slug, _browseProjectType);
        actions.Children.Add(web);

        var dl = new Button
        {
            Style = (Style)Application.Current.Resources["ChipButton"],
            Width = 28, Height = 28,
            Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        dl.Click += (_, _) => DownloadItem(hit, dl);
        actions.Children.Add(dl);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        ApplyDownloadButton(dl, hit.ProjectId, hit.Title, BrowseKind(), DestDirForKind(BrowseKind()));

        row.Child = grid;
        return row;
    }

    private string BrowseKind() => _browseProjectType switch
    {
        "shader" => "shader",
        "resourcepack" => "resourcepack",
        _ => "mod",
    };

    private string DestDirForKind(string kind) => kind switch
    {
        "shader" => System.IO.Path.Combine(InstDir(), "shaderpacks"),
        "resourcepack" => System.IO.Path.Combine(InstDir(), "resourcepacks"),
        _ => ModsDir(),
    };

    /// <summary>网页跳转:按来源拼地址(CurseForge 条目接入后传 "curseforge")。</summary>
    private void OpenProjectPage(string source, string slug, string projectType)
    {
        try
        {
            var url = source == "curseforge"
                ? Fuse.Core.Sources.CurseForgeSource.ProjectPageUrl(slug, projectType)
                : Fuse.Core.Sources.ModrinthSource.ProjectPageUrl(slug, projectType);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Toast("打开网页失败:" + ex.Message);
        }
    }

    /// <summary>按钮视觉:下载中=旋转 / 已安装=打勾 / 失败=红色感叹号 / 其余=下载。</summary>
    private void ApplyDownloadButton(Button button, string projectId, string title,
        string kind, string destDir)
    {
        if (button.Content is Fuse.App.Controls.Spinner oldSpinner) oldSpinner.Stop();

        if (_itemDownloads.TryGetValue(projectId, out var state) && !state.Failed)
        {
            var spinnerBrush = (Brush)Application.Current.Resources["GreenBrush"];
            var spinner = new Fuse.App.Controls.Spinner(spinnerBrush);
            spinner.Start();
            button.Content = spinner;
            button.IsEnabled = false;
            System.Windows.Automation.AutomationProperties.SetName(button, "正在下载 " + title);
            return;
        }
        if (state != null && state.Failed)
        {
            button.Content = Glyph("!", (Brush)Application.Current.Resources["DangerBrush"]);
            button.IsEnabled = false;
            System.Windows.Automation.AutomationProperties.SetName(button, "下载失败 " + title);
            return;
        }
        if (Fuse.Core.Sources.ContentRecordStore.IsInstalled(InstDir(), kind, projectId, destDir))
        {
            button.Content = Glyph("✓", (Brush)Application.Current.Resources["GreenBrush"]);
            button.IsEnabled = false;
            button.ToolTip = "已安装";
            System.Windows.Automation.AutomationProperties.SetName(button, "已安装 " + title);
            return;
        }
        button.Content = Glyph("⬇", (Brush)Application.Current.Resources["TextPrimaryBrush"]);
        button.IsEnabled = true;
        button.ToolTip = null;
        System.Windows.Automation.AutomationProperties.SetName(button, "下载 " + title);
    }

    private static TextBlock Glyph(string text, Brush brush) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void DownloadItem(ModrinthHit hit, Button button)
    {
        var kind = BrowseKind();
        var destDir = DestDirForKind(kind);
        if (_itemDownloads.ContainsKey(hit.ProjectId)) return;
        if (Fuse.Core.Sources.ContentRecordStore.IsInstalled(InstDir(), kind, hit.ProjectId, destDir))
            return;

        var state = new ItemDownloadState
        {
            ProjectId = hit.ProjectId,
            Kind = kind,
            Source = _source,
            DestDir = destDir,
            Title = hit.Title,
            Button = button,
        };
        _itemDownloads[hit.ProjectId] = state;
        ApplyDownloadButton(button, hit.ProjectId, hit.Title, kind, destDir);

        _ = Task.Run(async () =>
        {
            try
            {
                if (_source == "curseforge")
                {
                    await DownloadCurseForgeAsync(hit, state).ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(new Action(() => ReconcileDownload(state)));
                    return;
                }
                if (kind == "mod")
                {
                    var plan = await Fuse.Core.Installer.ModInstaller.PlanAsync(
                        AppServices.Http, hit.ProjectId, _gameVersion, _loader, destDir,
                        CancellationToken.None).ConfigureAwait(false);
                    state.MainFileName = plan.MainFileName;
                    foreach (var item in plan.Items)
                    {
                        var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                        {
                            Name = System.IO.Path.GetFileName(item.DestPath),
                            Kind = "file",
                            SourceUrl = item.Url,
                            DestPath = item.DestPath,
                            ExpectedHash = item.ExpectedHash,
                        });
                        DownloadFocus.Register(task.TaskId, "version-detail");
                        state.TaskIds.Add(task.TaskId);
                    }
                }
                else
                {
                    var file = await Fuse.Core.Sources.ModrinthSource.GetLatestVersionFileAsync(
                        AppServices.Http, hit.ProjectId, _gameVersion, "", CancellationToken.None)
                        .ConfigureAwait(false);
                    state.MainFileName = file.FileName;
                    var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                    {
                        Name = file.FileName,
                        Kind = "file",
                        SourceUrl = file.Url,
                        DestPath = System.IO.Path.Combine(destDir, file.FileName),
                        ExpectedHash = string.IsNullOrEmpty(file.Sha1) ? null : file.Sha1,
                    });
                    DownloadFocus.Register(task.TaskId, "version-detail");
                    state.TaskIds.Add(task.TaskId);
                }

                await Dispatcher.InvokeAsync(new Action(() => ReconcileDownload(state)));
            }
            catch (Exception ex)
            {
                WidgetWindow.Boot("[content] download failed: " + hit.Title + " · " + ex.Message);
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    _itemDownloads.Remove(hit.ProjectId);
                    if (state.Button != null)
                    {
                        ApplyDownloadButton(state.Button, hit.ProjectId, hit.Title, kind, destDir);
                        FlashFailure(state.Button, hit.ProjectId, hit.Title, kind, destDir);
                    }
                    Toast("下载失败:" + ex.Message);
                }));
            }
        });
    }

    /// <summary>CurseForge 下载:只使用 API 返回的 downloadUrl,并带上一级 required 依赖。</summary>
    private async Task DownloadCurseForgeAsync(ModrinthHit hit, ItemDownloadState state)
    {
        if (!CurseForgeSource.IsConfigured)
            throw new InvalidOperationException("CurseForge 未启用（构建时未注入 API Key）");
        if (!int.TryParse(hit.ProjectId, out var projectId))
            throw new InvalidOperationException("CurseForge 项目 id 无效");

        var loader = state.Kind == "mod" ? _loader : "";
        var files = await CurseForgeSource.GetFilesAsync(projectId, _gameVersion, loader, CancellationToken.None)
            .ConfigureAwait(false);
        var main = files.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.DownloadUrl));
        if (main == null)
            throw new InvalidOperationException("作者未授权第三方下载，请点网页按钮到 CurseForge 下载");

        state.MainFileName = main.FileName;
        Fuse.Core.Sources.ContentRecordStore.Record(
            InstDir(), "curseforge", state.ProjectId, main.FileName, state.Kind, main.Sha1);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnqueueCfFile(state, main, names, isMain: true);

        if (state.Kind == "mod")
        {
            var deps = await CurseForgeSource.GetDependenciesAsync(projectId, main.Id, CancellationToken.None)
                .ConfigureAwait(false);
            foreach (var dep in deps.Where(d => d.RelationType == 3))
            {
                try
                {
                    var depFiles = await CurseForgeSource.GetFilesAsync(
                        dep.ModId, _gameVersion, loader, CancellationToken.None).ConfigureAwait(false);
                    var depFile = depFiles.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.DownloadUrl));
                    if (depFile != null) EnqueueCfFile(state, depFile, names, isMain: false);
                }
                catch (Exception)
                {
                    // 单个前置失败不阻塞本体;缺少前置由游戏启动时的模组加载器报出来
                }
            }
        }
    }

    private void EnqueueCfFile(ItemDownloadState state, CfFile file, HashSet<string> names, bool isMain)
    {
        if (!names.Add(file.FileName)) return;
        var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
        {
            Name = file.FileName,
            Kind = "file",
            SourceUrl = file.DownloadUrl!,
            DestPath = System.IO.Path.Combine(state.DestDir, file.FileName),
            ExpectedHash = string.IsNullOrWhiteSpace(file.Sha1) ? null : file.Sha1,
        });
        DownloadFocus.Register(task.TaskId, "version-detail");
        state.TaskIds.Add(task.TaskId);
        if (isMain) state.MainTaskIds.Add(task.TaskId);
    }

    /// <summary>把队列里这些任务的终态收敛成"已安装 / 失败"。</summary>
    private void ReconcileDownload(ItemDownloadState state)
    {
        if (!_itemDownloads.ContainsKey(state.ProjectId)) return;
        var tasks = AppServices.Queue?.GetTasks();
        if (tasks != null)
        {
            foreach (var id in state.TaskIds.ToList())
            {
                var task = tasks.FirstOrDefault(t => t.TaskId == id);
                if (task == null) continue;
                if (task.Status == Fuse.Core.Download.DownloadTaskStatus.Completed)
                    state.TaskIds.Remove(id);
                else if (task.Status is Fuse.Core.Download.DownloadTaskStatus.Failed
                         or Fuse.Core.Download.DownloadTaskStatus.Removed)
                {
                    if (state.MainTaskIds.Contains(id))
                    {
                        MarkDownloadFailed(state);
                        return;
                    }
                    // 前置失败:移除该任务继续等本体,避免本体已装好却显示红叹号
                    state.TaskIds.Remove(id);
                    WidgetWindow.Boot("[content] dependency failed: " + task.Name);
                }
            }
        }
        if (state.TaskIds.Count == 0) CompleteDownload(state);
    }

    private void CompleteDownload(ItemDownloadState state)
    {
        if (!string.IsNullOrEmpty(state.MainFileName))
        {
            Fuse.Core.Sources.ContentRecordStore.Record(
                InstDir(), state.Source, state.ProjectId, state.MainFileName, state.Kind, null);
        }
        _itemDownloads.Remove(state.ProjectId);
        if (state.Button != null)
            ApplyDownloadButton(state.Button, state.ProjectId, state.Title, state.Kind, state.DestDir);
    }

    private void MarkDownloadFailed(ItemDownloadState state)
    {
        state.Failed = true;
        if (state.Button != null)
        {
            ApplyDownloadButton(state.Button, state.ProjectId, state.Title, state.Kind, state.DestDir);
            state.Button.IsEnabled = false;
        }
        var projectId = state.ProjectId;
        var button = state.Button;
        var title = state.Title;
        var kind = state.Kind;
        var dest = state.DestDir;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _itemDownloads.Remove(projectId);
            if (button != null) ApplyDownloadButton(button, projectId, title, kind, dest);
        };
        timer.Start();
        Toast("下载失败,请重试");
    }

    private void FlashFailure(Button button, string projectId, string title, string kind, string destDir)
    {
        // 入队/解析阶段就失败:没有任务可跟踪,短暂红叹号后回到下载
        button.Content = Glyph("!", (Brush)Application.Current.Resources["DangerBrush"]);
        button.IsEnabled = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyDownloadButton(button, projectId, title, kind, destDir);
        };
        timer.Start();
    }

    private void OnDownloadTaskChanged(Fuse.Core.Download.DownloadTaskRecord record)
    {
        if (record.Status is not (Fuse.Core.Download.DownloadTaskStatus.Completed
            or Fuse.Core.Download.DownloadTaskStatus.Failed
            or Fuse.Core.Download.DownloadTaskStatus.Removed))
            return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var state in _itemDownloads.Values.ToList())
            {
                if (!state.TaskIds.Contains(record.TaskId)) continue;
                if (record.Status == Fuse.Core.Download.DownloadTaskStatus.Completed)
                {
                    state.TaskIds.Remove(record.TaskId);
                    if (state.TaskIds.Count == 0) CompleteDownload(state);
                }
                else if (state.MainTaskIds.Contains(record.TaskId))
                {
                    MarkDownloadFailed(state);
                }
                else
                {
                    state.TaskIds.Remove(record.TaskId);
                    if (state.TaskIds.Count == 0) CompleteDownload(state);
                }
                break;
            }
        }));
    }

    internal async void LoadIcon(Border host, string url, string title)
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
            var bytes = await AppServices.Http.GetByteArrayAsync(url).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(new Action(() =>
            {
                if (!host.IsLoaded && host.Parent == null) return;
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = new MemoryStream(bytes);
                    image.DecodePixelWidth = 72;
                    image.EndInit();
                    image.Freeze();
                    host.Child = new Image
                    {
                        Source = image, Width = 34, Height = 34, Stretch = Stretch.UniformToFill,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }
                catch (Exception)
                {
                }
            }));
        }
        catch (Exception)
        {
        }
    }

    // ---------- 本地(已安装模组) ----------

    private void BuildLocalTab()
    {
        // 目录切换:模组 / 光影包 / 材质包(删除逻辑一致)
        var isMods = _localDirKind == "mods";
        var isShaders = _localDirKind == "shaderpacks";
        var dir = isMods
            ? ModsDir()
            : Path.Combine(InstDir(), _localDirKind);
        var modsDir = dir;

        var kindRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (label, key) in new[]
                 {
                     ("模组", "mods"), ("光影包", "shaderpacks"), ("材质包", "resourcepacks"),
                     ("存档", "saves"), ("截图", "screenshots"), ("服务器", "servers"),
                 })
        {
            var chipBtn = new Button
            {
                Style = (Style)Application.Current.Resources["ChipButton"],
                Content = label,
                // 第二行(服务器)会换行:必须留上下间距,否则两行 chip 贴在一起像重叠
                Margin = new Thickness(0, 0, 6, 6),
            };
            if (_localDirKind == key) chipBtn.SetResourceReference(Button.BackgroundProperty, "AccentChipBrush");
            System.Windows.Automation.AutomationProperties.SetName(chipBtn, "本地" + label);
            var captured = key;
            chipBtn.Click += (_, _) =>
            {
                if (_localDirKind == captured) return;
                _localDirKind = captured;
                RenderTab();
            };
            kindRow.Children.Add(chipBtn);
        }
        _content.Children.Add(kindRow);
        if (_localDirKind == "servers")
        {
            BuildServersList();
            return;
        }
        if (_localDirKind == "saves")
        {
            BuildSavesList(dir);
            return;
        }
        if (_localDirKind == "screenshots")
        {
            BuildScreenshotsList(dir);
            return;
        }
        if (isMods) _content.Children.Add(BuildModUpdateRow());

        var files = new List<FileInfo>();
        try
        {
            if (Directory.Exists(dir))
                files = new DirectoryInfo(dir).GetFiles()
                    // 注意:.jar.disabled 的 Extension 是 ".disabled",不能用扩展名判断
                    .Where(f => isMods
                        ? f.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                          f.Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                        : f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch (Exception)
        {
        }

        var list = new StackPanel();
        if (files.Count == 0)
        {
            list.Children.Add(Hint(isMods
                ? "这个版本还没有安装模组"
                : isShaders ? "这个版本还没有安装光影" : "这个版本还没有安装材质包"));
        }
        else
        {
            foreach (var f in files)
            {
                var entry = f;
                var row = new Border
                {
                    Style = (Style)Application.Current.Resources["GlassRowBorder"],
                    Margin = new Thickness(0, 0, 0, 8),
                };
                var isDisabled = entry.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                if (isDisabled) row.Opacity = 0.62;
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 图标走磁盘缓存:命中直接显示,未命中先放首字母块、后台按 SHA-1 反查后补上
                var icon = new Border
                {
                    Width = 34,
                    Height = 34,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                icon.SetResourceReference(Border.BackgroundProperty, "AccentChipSoftBrush");
                ModIconCache.Attach(icon, entry.FullName, Dispatcher);
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = Path.GetFileNameWithoutExtension(entry.Name),
                    FontSize = 12.5,
                    Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                info.Children.Add(new TextBlock
                {
                    Text = FormatSize(entry.Length) + (isDisabled ? " · 已禁用" : ""),
                    FontSize = 10.5, Foreground = T2, Opacity = 0.66,
                });
                if (!isDisabled && _modUpdates.TryGetValue(entry.FullName, out var pendingUpdate))
                {
                    info.Children.Add(new TextBlock
                    {
                        Text = "可更新 → " + pendingUpdate.LatestVersion,
                        FontSize = 10.5,
                        Foreground = (Brush)Application.Current.Resources["AccentTextBrush"],
                        Margin = new Thickness(0, 2, 0, 0),
                    });
                }
                Grid.SetColumn(info, 1);
                grid.Children.Add(info);

                if (isMods)
                {
                    var toggle = new Button
                    {
                        Style = (Style)Application.Current.Resources["ChipButton"],
                        Height = 28,
                        Padding = new Thickness(10, 0, 10, 0),
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
                        Content = new TextBlock
                        {
                            Text = isDisabled ? "启用" : "禁用",
                            FontSize = 11,
                            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    };
                    System.Windows.Automation.AutomationProperties.SetName(toggle,
                        (isDisabled ? "启用模组 " : "禁用模组 ") + entry.Name);
                    toggle.Click += (_, _) =>
                    {
                        try
                        {
                            if (isDisabled)
                            {
                                var target = entry.FullName.Substring(0, entry.FullName.Length - ".disabled".Length);
                                File.Move(entry.FullName, target);
                            }
                            else
                            {
                                var target = entry.FullName + ".disabled";
                                if (File.Exists(target))
                                    throw new IOException("目标文件已存在:" + Path.GetFileName(target));
                                File.Move(entry.FullName, target);
                            }
                        }
                        catch (Exception ex)
                        {
                            Toast((isDisabled ? "启用失败:" : "禁用失败:") + ex.Message);
                            return;
                        }
                        RenderTab();
                    };
                    Grid.SetColumn(toggle, 2);
                    grid.Children.Add(toggle);
                }

                var del = new Button
                {
                    Style = (Style)Application.Current.Resources["ChipButton"],
                    Width = 28, Height = 28,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new TextBlock
                    {
                        Text = "✕", FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                System.Windows.Automation.AutomationProperties.SetName(del,
                    "删除" + (isMods ? "模组" : isShaders ? "光影" : "材质包") + " " + entry.Name);
                del.Click += (_, _) =>
                {
                    try
                    {
                        File.Delete(entry.FullName);
                    }
                    catch (Exception ex)
                    {
                        Toast("删除失败：" + ex.Message);
                        return;
                    }
                    RenderTab();
                };
                Grid.SetColumn(del, 3);
                grid.Children.Add(del);

                row.Child = grid;
                list.Children.Add(row);
            }
        }

        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        _content.Children.Add(scroll);

        // 左下「打开文件夹」
        var bar = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var open = new Button
        {
            Content = "打开文件夹",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(14, 7, 14, 7),
        };
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(modsDir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + modsDir + "\"");
            }
            catch (Exception ex)
            {
                Toast("打开失败：" + ex.Message);
            }
        };
        Grid.SetColumn(open, 0);
        bar.Children.Add(open);
        _content.Children.Add(bar);
    }

    // ---------- 本地模组:更新检查 / 启停 ----------

    private UIElement BuildModUpdateRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            FontSize = 11.5,
            Foreground = T2,
            VerticalAlignment = VerticalAlignment.Center,
            Text = _modUpdateChecking
                ? "正在检查模组更新…"
                : _modUpdates.Count > 0
                    ? _modUpdates.Count + " 个模组可更新"
                    : _modUpdateChecked ? "全部模组已是最新" : "从 Modrinth 检查已装模组的更新",
        };
        if (_modUpdates.Count > 0)
            text.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var check = new Button
        {
            Content = "检查更新",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = !_modUpdateChecking,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(check, "检查模组更新");
        check.Click += (_, _) => _ = CheckModUpdatesAsync();
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);

        if (!_modUpdateChecking && _modUpdates.Count > 0)
        {
            var all = new Button
            {
                Content = "一键更新 (" + _modUpdates.Count + ")",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["AccentChipBrush"],
            };
            System.Windows.Automation.AutomationProperties.SetName(all, "一键更新模组");
            all.Click += (_, _) => UpdateAllMods();
            Grid.SetColumn(all, 2);
            grid.Children.Add(all);
        }
        return grid;
    }

    private async Task CheckModUpdatesAsync()
    {
        if (_modUpdateChecking) return;
        _modUpdateChecking = true;
        _modUpdateChecked = false;
        _modUpdates.Clear();
        RenderTab();
        try
        {
            var gv = string.IsNullOrEmpty(_gameVersion) ? ResolveMcVersion(_versionId) : _gameVersion;
            var updates = await ModUpdateChecker.CheckAsync(
                ModsDir(), gv, _loader, CancellationToken.None);
            foreach (var u in updates) _modUpdates[u.FilePath] = u;
            _modUpdateChecked = true;
            if (updates.Count == 0) Toast("模组已是最新");
        }
        catch (Exception ex)
        {
            Toast("检查更新失败:" + ex.Message);
        }
        finally
        {
            _modUpdateChecking = false;
            RenderTab();
        }
    }

    private void UpdateAllMods()
    {
        var modsDir = ModsDir();
        var pending = _modUpdates.Values.Where(u => File.Exists(u.FilePath)).ToList();
        if (pending.Count == 0)
        {
            Toast("没有可更新的模组");
            return;
        }
        var taskIds = new List<string>();
        foreach (var u in pending)
        {
            try
            {
                var meta = new JObject
                {
                    ["url"] = u.DownloadUrl,
                    ["sha1"] = u.Sha1,
                    ["destPath"] = Path.Combine(modsDir, u.NewFileName),
                    ["oldPath"] = u.FilePath,
                }.ToString();
                var task = AppServices.Queue!.Enqueue(new Fuse.Core.Download.DownloadTaskRecord
                {
                    Name = u.NewFileName,
                    Kind = "mod-update",
                    Meta = meta,
                });
                DownloadFocus.Register(task.TaskId, "version-detail");
                taskIds.Add(task.TaskId);
            }
            catch (Exception ex)
            {
                Toast("加入下载队列失败:" + ex.Message);
            }
        }
        if (taskIds.Count == 0) return;
        _modUpdates.Clear();
        _modUpdateChecked = false;
        RenderTab();
        Toast("已开始更新 " + taskIds.Count + " 个模组");
        _ = WatchModUpdateTasksAsync(taskIds);
    }

    private async Task WatchModUpdateTasksAsync(List<string> taskIds)
    {
        while (true)
        {
            await Task.Delay(1000);
            var tasks = AppServices.Queue?.GetTasks();
            if (tasks == null) return;
            var alive = tasks.Any(t => taskIds.Contains(t.TaskId) &&
                (t.Status == Fuse.Core.Download.DownloadTaskStatus.Queued ||
                 t.Status == Fuse.Core.Download.DownloadTaskStatus.Downloading));
            if (!alive) break;
        }
        await Dispatcher.InvokeAsync(new Action(() =>
        {
            Toast("模组更新完成");
            RenderTab();
        }));
    }

    /// <summary>存档列表:每个世界一个文件夹,可打开或删除(带确认)。</summary>
    private void BuildSavesList(string dir)
    {
        var saves = Directory.Exists(dir)
            ? new DirectoryInfo(dir).GetDirectories()
                .OrderByDescending(d => d.LastWriteTime).ToList()
            : new List<DirectoryInfo>();
        var list = new StackPanel();
        if (saves.Count == 0) list.Children.Add(Hint("这个版本还没有存档"));

        foreach (var save in saves)
        {
            var captured = save;
            var row = new Border
            {
                Style = (Style)Application.Current.Resources["GlassRowBorder"],
                Margin = new Thickness(0, 0, 0, 8),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
            info.Children.Add(new TextBlock
            {
                Text = save.Name,
                FontSize = 12.5,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text = "最后修改 " + save.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                FontSize = 10.5,
                Foreground = T2,
                Opacity = 0.66,
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var open = new Button
            {
                Content = "打开",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(open, "打开存档 " + save.Name);
            open.Click += (_, _) => OpenPath(captured.FullName);
            Grid.SetColumn(open, 1);
            grid.Children.Add(open);

            var backup = new Button
            {
                Content = "备份",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(backup, "备份存档 " + save.Name);
            backup.Click += (_, _) => BackupWorld(captured.Name);
            Grid.SetColumn(backup, 2);
            grid.Children.Add(backup);

            var del = new Button
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
            System.Windows.Automation.AutomationProperties.SetName(del, "删除存档 " + save.Name);
            del.Click += (_, _) =>
            {
                var confirm = new ConfirmWindow("删除存档",
                    "确定删除存档「" + captured.Name + "」吗？此操作不可恢复。", "删除", danger: true);
                if (Show(confirm) != true) return;
                try
                {
                    Directory.Delete(captured.FullName, true);
                    Toast("存档已删除");
                }
                catch (Exception ex)
                {
                    Toast("删除失败:" + ex.Message, _backupCard);
                }
                RenderTab();
            };
            Grid.SetColumn(del, 3);
            grid.Children.Add(del);

            row.Child = grid;
            list.Children.Add(row);
        }

        AddLocalScroll(list);
        AddWorldBackupSection();
        AddOpenFolderBar(dir);
    }

    private string SavesDir() => Path.Combine(InstDir(), "saves");

    private void AddWorldBackupSection()
    {
        var backups = Fuse.Core.Installer.WorldBackup.List(
            Fuse.Core.Installer.WorldBackup.BackupRoot(_gameRoot, _versionId));
        if (backups.Count == 0) return;

        _content.Children.Add(new TextBlock
        {
            Text = "存档备份",
            FontSize = 11.5,
            Foreground = T2,
            Margin = new Thickness(0, 10, 0, 6),
        });

        var list = new StackPanel();
        foreach (var info in backups.Take(20))
        {
            var captured = info;
            var row = new Border
            {
                Style = (Style)Application.Current.Resources["GlassRowBorder"],
                Margin = new Thickness(0, 0, 0, 6),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 0, 5) };
            text.Children.Add(new TextBlock
            {
                Text = info.WorldName,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = info.Created.ToString("yyyy-MM-dd HH:mm") + " · " + FormatSize(info.Size),
                FontSize = 10.5,
                Foreground = T2,
                Opacity = 0.66,
            });
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var restore = new Button
            {
                Content = "还原",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(restore, "还原存档备份 " + info.WorldName);
            restore.Click += (_, _) => RestoreWorldBackup(captured);
            Grid.SetColumn(restore, 1);
            grid.Children.Add(restore);

            var del = new Button
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
            System.Windows.Automation.AutomationProperties.SetName(del, "删除存档备份 " + info.WorldName);
            del.Click += (_, _) => DeleteWorldBackup(captured);
            Grid.SetColumn(del, 2);
            grid.Children.Add(del);

            row.Child = grid;
            list.Children.Add(row);
        }

        _content.Children.Add(new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        });
    }

    private void BackupWorld(string worldName)
    {
        if (LaunchState.IsBusy(_versionId))
        {
            Toast("游戏运行中，请先退出再备份存档");
            return;
        }
        try
        {
            var path = Fuse.Core.Installer.WorldBackup.Create(
                SavesDir(), worldName, Fuse.Core.Installer.WorldBackup.BackupRoot(_gameRoot, _versionId));
            Toast("存档已备份:" + Path.GetFileName(path));
            RenderTab();
        }
        catch (Exception ex)
        {
            Toast("备份失败:" + ex.Message, _backupCard);
        }
    }

    private void RestoreWorldBackup(Fuse.Core.Installer.WorldBackup.BackupInfo info)
    {
        if (LaunchState.IsBusy(_versionId))
        {
            Toast("游戏运行中，请先退出再还原存档");
            return;
        }
        var confirm = new ConfirmWindow("还原存档",
            "将用备份「" + info.WorldName + " / " + info.Created.ToString("MM-dd HH:mm") +
            "」覆盖当前存档。\n\n还原前建议先手动备份一次当前状态。",
            "还原", danger: true);
        if (Show(confirm) != true) return;
        try
        {
            Fuse.Core.Installer.WorldBackup.Restore(info.Path, SavesDir());
            Toast("存档已还原");
        }
        catch (Exception ex)
        {
            Toast("还原失败:" + ex.Message, _backupCard);
        }
        RenderTab();
    }

    private void DeleteWorldBackup(Fuse.Core.Installer.WorldBackup.BackupInfo info)
    {
        var confirm = new ConfirmWindow("删除存档备份", "确定删除这个存档备份吗？", "删除", danger: true);
        if (Show(confirm) != true) return;
        try
        {
            File.Delete(info.Path);
            Toast("备份已删除", _backupCard);
        }
        catch (Exception ex)
        {
            Toast("删除失败:" + ex.Message, _backupCard);
        }
        RenderTab();
    }

    /// <summary>截图列表:打开查看或删除。</summary>
    private void BuildScreenshotsList(string dir)
    {
        var files = Directory.Exists(dir)
            ? new DirectoryInfo(dir).GetFiles()
                .Where(f => f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime).ToList()
            : new List<FileInfo>();
        var list = new StackPanel();
        if (files.Count == 0) list.Children.Add(Hint("这个版本还没有截图"));

        foreach (var shot in files)
        {
            var captured = shot;
            var row = new Border
            {
                Style = (Style)Application.Current.Resources["GlassRowBorder"],
                Margin = new Thickness(0, 0, 0, 8),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
            info.Children.Add(new TextBlock
            {
                Text = shot.Name,
                FontSize = 12.5,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text = FormatSize(shot.Length) + " · " + shot.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                FontSize = 10.5,
                Foreground = T2,
                Opacity = 0.66,
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var open = new Button
            {
                Content = "打开",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(open, "打开截图 " + shot.Name);
            open.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(captured.FullName) { UseShellExecute = true }); }
                catch (Exception ex) { Toast("打开失败:" + ex.Message, _crashCard); }
            };
            Grid.SetColumn(open, 1);
            grid.Children.Add(open);

            var del = new Button
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
            System.Windows.Automation.AutomationProperties.SetName(del, "删除截图 " + shot.Name);
            del.Click += (_, _) =>
            {
                try { File.Delete(captured.FullName); Toast("截图已删除"); }
                catch (Exception ex) { Toast("删除失败:" + ex.Message, _backupCard); }
                RenderTab();
            };
            Grid.SetColumn(del, 2);
            grid.Children.Add(del);

            row.Child = grid;
            list.Children.Add(row);
        }

        AddLocalScroll(list);
        AddOpenFolderBar(dir);
    }

    /// <summary>服务器列表:读 servers.dat,一键启动并加入。</summary>
    private string ServersPath() => Path.Combine(InstDir(), "servers.dat");

    private void BuildServersList()
    {
        var servers = Fuse.Core.Versioning.ServersDat.Read(ServersPath());

        var add = new Button
        {
            Content = "＋ 添加服务器",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        System.Windows.Automation.AutomationProperties.SetName(add, "添加服务器");
        add.Click += (_, _) => AddServer();
        _content.Children.Add(add);

        var list = new StackPanel();
        if (servers.Count == 0)
            list.Children.Add(Hint("还没有服务器。点上面的「添加服务器」，或先在游戏里添加。"));

        for (var i = 0; i < servers.Count; i++)
        {
            var index = i;
            var captured = servers[i];
            var row = new Border
            {
                Style = (Style)Application.Current.Resources["GlassRowBorder"],
                Margin = new Thickness(0, 0, 0, 6),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Margin = new Thickness(0, 6, 0, 6), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = captured.Name.Length > 0 ? captured.Name : captured.Ip,
                FontSize = 12.5,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text = captured.Ip + (captured.Hidden ? " · 隐藏" : "") +
                       (string.Equals(_profile.AutoJoinServer, captured.Ip, StringComparison.OrdinalIgnoreCase)
                           ? " · 自动加入"
                           : ""),
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = T2,
                Opacity = 0.66,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var join = new Button
            {
                Content = "加入",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Application.Current.Resources["AccentChipBrush"] is Brush accent)
                join.Background = accent;
            System.Windows.Automation.AutomationProperties.SetName(join,
                "加入服务器 " + captured.Name + " " + captured.Ip);
            join.Click += (_, _) => _ = JoinServer(captured.Ip);
            Grid.SetColumn(join, 1);
            grid.Children.Add(join);

            var edit = new Button
            {
                Content = "编辑",
                Style = (Style)Application.Current.Resources["ChipButton"],
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(edit, "编辑服务器 " + captured.Ip);
            edit.Click += (_, _) => EditServer(index, captured);
            Grid.SetColumn(edit, 2);
            grid.Children.Add(edit);

            var del = new Button
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
            System.Windows.Automation.AutomationProperties.SetName(del, "删除服务器 " + captured.Ip);
            del.Click += (_, _) => DeleteServer(index, captured);
            Grid.SetColumn(del, 3);
            grid.Children.Add(del);

            row.Child = grid;
            list.Children.Add(row);
        }

        AddLocalScroll(list);
        AddOpenFolderBar(InstDir());
    }

    private bool ServersEditable()
    {
        if (LaunchState.IsBusy(_versionId))
        {
            Toast("游戏运行中，请先退出游戏再修改服务器列表");
            return false;
        }
        return true;
    }

    private void AddServer()
    {
        if (!ServersEditable()) return;
        var dialog = new ServerEditWindow(null, null, false);
        if (Show(dialog) != true) return;
        var servers = Fuse.Core.Versioning.ServersDat.Read(ServersPath());
        var name = dialog.ServerName.Length > 0 ? dialog.ServerName : dialog.ServerIp;
        servers.Add(new Fuse.Core.Versioning.ServerEntry(name, dialog.ServerIp, false));
        if (dialog.AutoJoin)
        {
            _profile.AutoJoinServer = dialog.ServerIp;
            AppServices.VersionProfiles.Set(_versionId, _profile);
        }
        SaveServers(servers);
    }

    private void EditServer(int index, Fuse.Core.Versioning.ServerEntry entry)
    {
        if (!ServersEditable()) return;
        var wasAuto = string.Equals(_profile.AutoJoinServer, entry.Ip, StringComparison.OrdinalIgnoreCase);
        var dialog = new ServerEditWindow(entry.Name, entry.Ip, wasAuto);
        if (Show(dialog) != true) return;
        var servers = Fuse.Core.Versioning.ServersDat.Read(ServersPath());
        if (index < 0 || index >= servers.Count) return;
        var name = dialog.ServerName.Length > 0 ? dialog.ServerName : dialog.ServerIp;
        servers[index] = new Fuse.Core.Versioning.ServerEntry(name, dialog.ServerIp, entry.Hidden, entry.Icon);
        if (dialog.AutoJoin)
            _profile.AutoJoinServer = dialog.ServerIp;
        else if (wasAuto)
            _profile.AutoJoinServer = null;
        AppServices.VersionProfiles.Set(_versionId, _profile);
        SaveServers(servers);
    }

    private void DeleteServer(int index, Fuse.Core.Versioning.ServerEntry entry)
    {
        if (!ServersEditable()) return;
        var confirm = new ConfirmWindow("删除服务器",
            "确定从列表删除「" + (entry.Name.Length > 0 ? entry.Name : entry.Ip) + "」吗？\n\n只删列表记录，不影响服务器本身。",
            "删除", danger: true);
        if (Show(confirm) != true) return;
        var servers = Fuse.Core.Versioning.ServersDat.Read(ServersPath());
        if (index < 0 || index >= servers.Count) return;
        servers.RemoveAt(index);
        if (string.Equals(_profile.AutoJoinServer, entry.Ip, StringComparison.OrdinalIgnoreCase))
        {
            _profile.AutoJoinServer = null;
            AppServices.VersionProfiles.Set(_versionId, _profile);
        }
        SaveServers(servers);
    }

    private void SaveServers(List<Fuse.Core.Versioning.ServerEntry> servers)
    {
        try
        {
            Fuse.Core.Versioning.ServersDat.Write(ServersPath(), servers);
            Toast("服务器列表已保存");
            RenderTab();
        }
        catch (Exception ex)
        {
            Toast("保存失败:" + ex.Message);
        }
    }

    private async Task JoinServer(string address)
    {
        if (LaunchState.IsBusy(_versionId))
        {
            Toast("这个版本已在启动或运行中");
            return;
        }
        var outcome = await AppLauncher.LaunchAsync(_versionId, msg => Toast(msg), serverAddress: address);
        if (outcome == LaunchOutcome.Started)
            Toast("已启动，正在连接 " + address);
    }

    private void AddLocalScroll(StackPanel list)
    {
        _content.Children.Add(new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        });
    }

    private void AddOpenFolderBar(string dir)
    {
        var open = new Button
        {
            Content = "打开文件夹",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        System.Windows.Automation.AutomationProperties.SetName(open, "打开文件夹");
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                OpenPath(dir);
            }
            catch (Exception ex)
            {
                Toast("打开失败:" + ex.Message, _crashCard);
            }
        };
        _content.Children.Add(open);
    }

    private static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\""); }
        catch (Exception) { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
        return bytes + " B";
    }

    // ---------- 导出 ----------

    private void BuildExportTab()
    {
        _content.Children.Add(new TextBlock
        {
            Text = "选择导出内容", FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = T2, Margin = new Thickness(0, 2, 0, 8),
        });
        _content.Children.Add(ExportOption(1, "完整版本 zip", "含存档"));
        _content.Children.Add(ExportOption(2, "下载清单 .mcdl.json",
            "拖到下载页面可直接下载(不含存档)"));

        var bar = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hint = new TextBlock
        {
            FontSize = 11, Foreground = T3, Margin = new Thickness(2, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(hint, 0);
        bar.Children.Add(hint);
        _exportHint = hint;
        var export = new Button { Content = "导出", Style = (Style)Application.Current.Resources["GreenBigButton"] };
        export.Click += (_, _) => DoExport(hint);
        Grid.SetColumn(export, 1);
        bar.Children.Add(export);
        _content.Children.Add(bar);
    }

    private TextBlock? _exportHint;

    private UIElement ExportOption(int key, string title, string sub)
    {
        var selected = _exportChoice == key;
        var opt = MakeOptionRow(title, selected ? "已选 ✓" : "选择", selected);
        opt.Click += (_, _) =>
        {
            _exportChoice = key;
            RenderTab();
        };
        var host = new StackPanel();
        host.Children.Add(opt);
        host.Children.Add(new TextBlock
        {
            Text = sub, FontSize = 11, Foreground = T3,
            Margin = new Thickness(12, -2, 0, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
        });
        return new Border { Child = host };
    }

    private async void DoExport(TextBlock hint)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _exportChoice == 1 ? _versionId + ".zip" : _versionId + ".mcdl.json",
            Filter = _exportChoice == 1 ? "版本压缩包|*.zip" : "下载清单|*.mcdl.json",
        };
        if (dialog.ShowDialog() != true) return;
        var target = dialog.FileName;
        hint.Text = "正在导出…";
        var gen = _generation;
        try
        {
            if (_exportChoice == 1)
            {
                var result = await Task.Run(() => VersionExporter.ExportFullZipAsync(
                    _versionId, VRoot(), AppServices.InstancesRoot,
                    LibsRoot(), target, CancellationToken.None)).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    if (gen != _generation) return;
                    hint.Text = $"已导出 {result.FileCount} 个文件({result.TotalBytes / 1024.0 / 1024.0:F1} MB)";
                }));
            }
            else
            {
                var mods = new List<VersionExporter.ManifestModItem>();
                var recordsPath = Path.Combine(InstDir(), "mods.mcdl.json");
                if (File.Exists(recordsPath))
                {
                    var arr = JArray.Parse(File.ReadAllText(recordsPath));
                    foreach (var o in arr.OfType<JObject>())
                        mods.Add(new VersionExporter.ManifestModItem
                        {
                            Url = o.Value<string>("url") ?? "",
                            FileName = o.Value<string>("fileName") ?? "",
                            Sha1 = o.Value<string>("sha1") ?? "",
                        });
                }
                string? loaderVersion = null, loaderUrl = null;
                if (_loader == "fabric" && _versionId!.Contains("-fabric-"))
                {
                    loaderVersion = _versionId.Substring(_versionId.IndexOf("-fabric-", StringComparison.Ordinal) + 8);
                    loaderUrl = $"https://meta.fabricmc.net/v2/versions/loader/{_gameVersion}/{loaderVersion}/profile/json";
                }
                var entry = (await MojangSource.FetchManifestAsync(AppServices.Http, CancellationToken.None)
                    .ConfigureAwait(true)).FirstOrDefault(v => v.Id == _gameVersion);
                var manifest = VersionExporter.BuildDownloadManifest(
                    _gameVersion, loaderVersion!,
                    entry?.Url ?? "", entry?.Sha1 ?? "", loaderUrl!, mods);
                File.WriteAllText(target, manifest);
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    if (gen != _generation) return;
                    hint.Text = $"已导出清单(MC {(_gameVersion)} + {mods.Count} 个模组)";
                }));
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(new Action(() =>
            {
                if (gen != _generation) return;
                hint.Text = "导出失败:" + ex.Message;
            }));
        }
    }

    // ---------- 公共小件 ----------

    private string LoaderLabel() => _loader switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => "原版",
    };

    private void Toast(string message, FrameworkElement? anchor = null)
    {
        // 行内提示:短暂出现,2.5s 后消失。
        // anchor 非空时插到该卡片正上方(占布局位置,不会和别的卡片重叠);
        // 为空时保持旧行为(内容区顶部),用于全局消息。
        var banner = new TextBlock
        {
            Text = message, FontSize = 11.5,
            Margin = new Thickness(2, 0, 2, 6), TextWrapping = TextWrapping.Wrap,
        };
        banner.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");

        Panel? host = anchor?.Parent as Panel;
        if (host != null && anchor != null)
        {
            var index = host.Children.IndexOf(anchor);
            host.Children.Insert(index < 0 ? host.Children.Count : index, banner);
        }
        else
        {
            _content.Children.Insert(0, banner);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            (host as Panel ?? _content).Children.Remove(banner);
            timer.Stop();
        };
        timer.Start();
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
        Margin = new Thickness(4, 8, 4, 8),
        TextWrapping = TextWrapping.Wrap,
    };

    private static Border WrapCard(UIElement child)
        => new() { Style = (Style)Application.Current.Resources["OptRow"], Margin = new Thickness(0, 0, 0, 8), Child = child };

    internal static string FormatCount(long n)
    {
        if (n >= 1000000) return (n / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (n >= 1000) return (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static Brush T2 => (Brush)Application.Current.Resources["TextSecondaryBrush"];
    private static Brush T3 => (Brush)Application.Current.Resources["TextTertiaryBrush"];
}
