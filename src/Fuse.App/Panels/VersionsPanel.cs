using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fuse.Core.Versioning;

namespace Fuse.App.Panels;

/// <summary>版本面板(FR-12):本地已安装版本,点击行直接启动。支持多游戏目录(导入官方或第三方目录)。</summary>
public class VersionsPanel : UserControl
{
    private readonly StackPanel _list = new();
    private readonly FolderListPanel _folders = new();
    private bool _skipFirstMouseEnter = true;

    public VersionsPanel()
    {
        var scroll = new ScrollViewer
        {
            Style = (Style)Application.Current.Resources["ModernScrollViewer"],
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        };
        Content = scroll;
        _folders.Changed += () => Dispatcher.BeginInvoke(new Action(Refresh));
        // 重进面板时刷新(改名后别名立即可见)。
        // 但面板刚 Show 出来时鼠标进入会立刻再扫一遍磁盘,正好撞上 300ms 出场动画,
        // 造成"动画中卡一下";构造时已经刷过一次,第一次 MouseEnter 直接跳过。
        MouseEnter += (_, _) =>
        {
            if (_skipFirstMouseEnter)
            {
                _skipFirstMouseEnter = false;
                return;
            }
            Refresh();
        };
        Refresh();
    }

    private int _scanGeneration;   // 每次刷新自增:大目录扫描挪到后台后,过期回调用它作废
    private string? _renderedSignature;   // 已渲染的数据签名:重扫后没变化就不重绘(消闪烁)

    public void Refresh()
    {
        // 版本扫描含递归算体积(Roaming .minecraft 级别的目录要几秒),必须离开 UI 线程,
        // 否则切换目录/打开面板/删除版本后刷新都会把界面冻住(2026-11-13 用户实测卡顿)。
        // 另:MouseEnter 也会触发刷新,重扫期间保留旧行不动,数据真有变化才重绘——
        // 否则每次鼠标进出面板都会闪一下(2026-11-14 用户实测"闪烁一两秒")
        _scanGeneration++;
        var gen = _scanGeneration;
        _folders.Reload();
        if (_renderedSignature == null)
        {
            _list.Children.Clear();
            _list.Children.Add(_folders);
            _list.Children.Add(new TextBlock
            {
                Text = "正在扫描版本…",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                Margin = new Thickness(4, 10, 4, 10),
            });
        }
        var versionsRoot = AppServices.VersionsRoot;
        var instancesRoot = AppServices.InstancesRoot;
        var librariesRoot = AppServices.LibrariesRoot;
        var externalLayout = !AppServices.IsManagedLayout(AppServices.ActiveGameDirectory);
        _ = Task.Run(async () =>
        {
            try
            {
                var versions = InstalledVersionScanner.Scan(
                    versionsRoot, instancesRoot, librariesRoot, externalLayout);
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _scanGeneration) return;   // 已有更新的一次扫描接管
                    var signature = VersionSignature(versions);
                    if (signature == _renderedSignature) return;   // 数据没变:保持现状,不闪
                    _renderedSignature = signature;
                    RenderVersions(versions);
                }));
            }
            catch (Exception ex)
            {
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _scanGeneration) return;
                    if (_renderedSignature == null)
                    {
                        _list.Children.Clear();
                        _list.Children.Add(_folders);
                    }
                    _list.Children.Add(new TextBlock
                    {
                        Text = "扫描版本失败:" + ex.Message,
                        FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                        Margin = new Thickness(4, 10, 4, 10),
                    });
                }));
            }
        });
    }

    /// <summary>版本列表的数据签名:重扫后比对,没变化就不重绘(消鼠标进出引发的闪烁)。</summary>
    private static string VersionSignature(IReadOnlyList<InstalledVersionInfo> versions)
        => string.Join("|", versions.Select(v => v.Id + ":" + v.JavaDisplay + ":" + v.ModsDisplay + ":"
            + v.SizeBytes + ":" + (v.HasClientJar ? "1" : "0") + ":"
            + (AppServices.VersionProfiles.Get(v.Id).DisplayName ?? "")));

    private void RenderVersions(IReadOnlyList<InstalledVersionInfo> versions)
    {
        _list.Children.Clear();
        _list.Children.Add(_folders);
        if (versions.Count == 0)
        {
            // 空态必须给出"当前在看哪个目录":版本全空最常见的原因是激活目录选错层级
            // 或目录被移动/清空,只写"还没有版本"用户无法区分这两种情况
            _list.Children.Add(new TextBlock
            {
                Text = "还没有版本",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                Margin = new Thickness(4, 10, 4, 4),
            });
            _list.Children.Add(new TextBlock
            {
                Text = "当前游戏目录:\n" + AppServices.ActiveGameDirectory,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                Margin = new Thickness(4, 0, 4, 4),
            });
            _list.Children.Add(new TextBlock
            {
                Text = "版本应位于该目录的 versions/<版本>/<版本>.json。若版本在别的文件夹,在上方列表点击切换;" +
                       "找不到本机的 MC 目录可用下方「自动查找」。",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                Margin = new Thickness(4, 0, 4, 10),
            });
            return;
        }

        var defaultId = AppServices.Config.LastLaunchedVersion;
        foreach (var v in versions.Take(20))
        {
            var row = BuildRow(v, v.Id == defaultId);
            // 点击卡片本体 → 打开详情页;▶ 才启动(用户确认的交互)
            row.Click += (_, _) =>
            {
                VersionDetailPanel.PendingVersionId = v.Id;
                VersionDetailPanel.PendingVersionRoot = v.RootDirectory;
                VersionDetailPanel.PendingTitle = "详情 · " + DisplayName(v);
                var widget = AppServices.Widget;
                if (widget != null)
                {
                    // 详情页是同类单实例:点别的版本时必须先关掉旧的,否则只会激活旧页面
                    if (PanelWindow.OpenPanels.TryGetValue("version-detail", out var existing))
                        existing.Close();
                    PanelWindow.ShowPanel("version-detail", new Point(0, 0), false, widget);
                }
            };
            _list.Children.Add(row);
        }
    }

    internal static string DisplayName(InstalledVersionInfo v)
    {
        var alias = AppServices.VersionProfiles.Get(v.Id).DisplayName;
        return string.IsNullOrEmpty(alias) ? v.Id : alias!;
    }

    private Button BuildRow(InstalledVersionInfo v, bool isDefault)
    {
        // 整行做成 Button:既可点击打开详情,也暴露 InvokePattern 便于自动化验证
        var row = new Button
        {
            Style = (Style)Application.Current.Resources["VersionRowButton"],
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(row, "版本 " + v.Id);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new StackPanel();
        name.Children.Add(new TextBlock
        {
            Text = DisplayName(v),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        });
        var verified = v.HasClientJar ? "已验证" : "未验证";
        var playStat = AppServices.PlayStats.Get(v.Id);
        var played = playStat.TotalSeconds > 0
            ? " · 已玩 " + Fuse.Core.Config.PlayStatsStore.FormatShort(playStat.TotalSeconds)
            : "";
        name.Children.Add(new TextBlock
        {
            Text = $"{v.JavaDisplay} · {v.ModsDisplay} · {verified}" + (isDefault ? " · 默认" : "") + played,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        // ▶ 必须是 Button 而不是 Border:整行本身是 Button,它在按下时会 CaptureMouse,
        // 内部 Border 的 MouseLeftButtonUp 永远收不到。内层 Button 自己处理 Click 并标记 Handled,
        // 外层的"打开详情页"就不会被触发。
        var launch = new Button
        {
            Style = (Style)Application.Current.Resources["ChipButton"],
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = "▶",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(launch, "启动 " + v.Id);
        Grid.SetColumn(launch, 1);
        grid.Children.Add(launch);
        // 已在启动/运行的同版本:按钮置灰(点击仍会被 LaunchState 拦下,不会多开一个)
        if (LaunchState.IsBusy(v.Id))
        {
            launch.Opacity = 0.5;
            launch.ToolTip = LaunchState.IsLaunching(v.Id) ? "正在启动…" : "游戏运行中";
        }
        // ▶ 小按钮:唯一启动入口(卡片本体现为打开详情页)
        launch.Click += async (_, e) =>
        {
            // Click 是冒泡事件,内层不拦会一路传到外层 Button,导致同时打开详情页
            e.Handled = true;
            if (row.Tag is "busy") return;
            row.Tag = "busy";
            var outcome = await AppLauncher.LaunchAsync(v.Id, msg =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (name.Children.Count > 1)
                        ((TextBlock)name.Children[1]).Text = msg;
                })));
            if (outcome != LaunchOutcome.Failed)
            {
                launch.Opacity = 0.5;
                launch.ToolTip = "游戏运行中";
            }
            row.Tag = null;
        };

        row.Content = grid;

        // 行与行尾 ▶ 的高亮互不影响:两者都是 Button,子元素悬停时父级 IsMouseOver 同样为真,
        // 所以行的背景不能交给模板触发器,这里按"鼠标是否在 ▶ 上"手动切换。
        var rowHovered = false;
        void UpdateRowHover()
        {
            var want = !launch.IsMouseOver;
            if (want == rowHovered) return;
            rowHovered = want;
            row.SetResourceReference(BackgroundProperty, want ? "HoverSolidBrush" : "RowBgBrush");
        }
        row.MouseMove += (_, _) => UpdateRowHover();
        row.MouseLeave += (_, _) =>
        {
            rowHovered = false;
            row.SetResourceReference(BackgroundProperty, "RowBgBrush");
        };

        return row;
    }
}
