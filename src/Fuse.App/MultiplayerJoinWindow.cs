using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fuse.Core.Multiplayer;
using Fuse.Core.Versioning;

namespace Fuse.App;

/// <summary>
/// 好友邀请确认窗:点 fuse-launcher:// 链接后弹出。
/// 展示房间名/地址 + SLP 实测(在线人数/版本/延迟),选版本 → 写 servers.dat → 自动进服。
/// </summary>
internal sealed class MultiplayerJoinWindow : Window
{
    private readonly InviteInfo _invite;
    private readonly TextBlock _statusText;
    private readonly StackPanel _versionList;
    private InstalledVersionInfo? _selected;
    private Fuse.Core.Multiplayer.FuseLink.FuseLinkGuest? _guest;  // P2P 直连(邀请带 fl 候选时)
    private TextBlock? _p2pStatus;
    private Button? _p2pJoinButton;
    private bool _handedOff;  // 隧道已移交注册表,窗口关闭不释放

    private MultiplayerJoinWindow(InviteInfo invite)
    {
        _invite = invite;
        DialogKit.ApplyWindow(this, 400);
        var stack = new StackPanel();
        var head = DialogKit.Title("好友邀请");
        stack.Children.Add(head);

        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(invite.RoomName) ? "好友的世界" : invite.RoomName,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = DialogKit.Res("TextPrimaryBrush"),
            Margin = new Thickness(0, 10, 0, 2),
        });
        stack.Children.Add(new TextBlock
        {
            Text = invite.Address + (string.IsNullOrEmpty(invite.GameVersion) ? "" : " · 建议 " + invite.GameVersion),
            FontSize = 11.5,
            Foreground = DialogKit.Res("TextSecondaryBrush"),
        });
        _statusText = new TextBlock
        {
            Text = "正在检测房间状态…",
            FontSize = 11.5,
            Foreground = DialogKit.Res("AccentTextBrush"),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(_statusText);

        if (!string.IsNullOrEmpty(invite.FuseLinkCandidates)) BuildP2pSection(stack);
        stack.Children.Add(new TextBlock
        {
            Text = "用哪个版本加入:",
            FontSize = 12,
            Foreground = DialogKit.Res("TextSecondaryBrush"),
            Margin = new Thickness(0, 14, 0, 6),
        });
        _versionList = new StackPanel();
        stack.Children.Add(_versionList);

        stack.Children.Add(DialogKit.Buttons(
            DialogKit.Primary("加入房间", () => ConfirmJoin()),
            DialogKit.Secondary("取消", () => DialogResult = false)));
        Content = DialogKit.Card(stack);
        DialogKit.WireDrag(this, head);
        DialogKit.WireKeys(this, () => DialogResult = false);

        Loaded += async (_, _) =>
        {
            await FillVersionsAsync().ConfigureAwait(true);
            _ = PingRoomAsync();
            _ = PrepareP2pAsync();
        };
        Closed += (_, _) =>
        {
            WidgetWindow.Boot("[p2p] join window closed handedOff=" + _handedOff);
            if (!_handedOff) _guest?.Dispose();
        };
    }

    /// <summary>DeepLink 转发入口:解析失败给轻提示,成功弹窗(Owner=组件窗)。</summary>
    public static void ShowForInvite(string url)
    {
        var invite = InviteCodec.Parse(url);
        if (invite == null)
        {
            var dialog = new InfoWindow("邀请码无效",
                "这个链接不是有效的 Fuse 联机邀请。\n\n" + (url.Length > 160 ? url.Substring(0, 160) + "…" : url));
            if (AppServices.Widget != null) dialog.Owner = AppServices.Widget;
            dialog.ShowDialog();
            return;
        }
        var window = new MultiplayerJoinWindow(invite);
        if (AppServices.Widget != null) window.Owner = AppServices.Widget;
        window.ShowDialog();
    }

    // ---------- P2P 直连(fuse-link:零服务器,回执码人工交换) ----------

    private void BuildP2pSection(StackPanel stack)
    {
        stack.Children.Add(new TextBlock
        {
            Text = "P2P 直连(推荐,延迟最低):",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = DialogKit.Res("TextPrimaryBrush"),
            Margin = new Thickness(0, 12, 0, 4),
        });
        _p2pStatus = new TextBlock
        {
            Text = "正在生成回执码…",
            FontSize = 11.5,
            Foreground = DialogKit.Res("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(_p2pStatus);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var copy = new Button
        {
            Content = "复制回执码",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 8, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(copy, "复制回执码");
        copy.Click += (_, _) =>
        {
            var code = _guest?.AnswerCode;
            if (!string.IsNullOrEmpty(code))
            {
                if (_p2pStatus != null) _p2pStatus.Text = ClipboardHelper.TrySetText(code)
                    ? "回执码已复制——发给房主,等房主粘贴后点「直连并启动」"
                    : "复制失败（剪贴板被占用）,请稍后重试";
            }
        };
        row.Children.Add(copy);
        _p2pJoinButton = new Button
        {
            Content = "直连并启动",
            Style = (Style)Application.Current.Resources["ChipButton"],
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            IsEnabled = false,
        };
        System.Windows.Automation.AutomationProperties.SetName(_p2pJoinButton, "直连并启动");
        _p2pJoinButton.Click += (_, _) => _ = JoinViaP2p();
        row.Children.Add(_p2pJoinButton);
        stack.Children.Add(row);
    }

    private async Task PrepareP2pAsync()
    {
        var blob = _invite.FuseLinkCandidates;
        if (string.IsNullOrEmpty(blob)) return;
        var candidates = Fuse.Core.Multiplayer.FuseLink.FuseLinkCodec.Decode(blob);
        if (candidates == null)
        {
            if (_p2pStatus != null) _p2pStatus.Text = "P2P 候选无效(邀请码过旧)";
            return;
        }
        var guest = new Fuse.Core.Multiplayer.FuseLink.FuseLinkGuest(candidates);
        guest.StatusChanged += msg => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_p2pStatus != null) _p2pStatus.Text = msg;
            if (_p2pJoinButton != null && guest.IsReady) _p2pJoinButton.IsEnabled = true;
        }));
        guest.Diagnostic += msg => WidgetWindow.Boot("[p2p] " + msg);
        _guest = guest;
        await Task.Run(() => guest.Prepare()).ConfigureAwait(true);
    }

    /// <summary>把房间写进实例 servers.dat(按地址去重),下次还能从服务器列表进。</summary>
    private void SaveServerEntry(InstalledVersionInfo version, string address)
    {
        try
        {
            var instance = AppServices.InstanceDirectoryIn(version.RootDirectory, version.Id);
            var path = Path.Combine(instance, "servers.dat");
            var servers = ServersDat.Read(path);
            var entryName = string.IsNullOrEmpty(_invite.RoomName) ? "好友房间" : _invite.RoomName!;
            if (!servers.Any(s => s.Ip.Equals(address, StringComparison.OrdinalIgnoreCase)))
            {
                servers.Add(new Fuse.Core.Versioning.ServerEntry(entryName, address, false));
                ServersDat.Write(path, servers);
            }
        }
        catch (Exception ex)
        {
            var fail = new InfoWindow("写入服务器列表失败", ex.Message);
            fail.Owner = this;
            fail.ShowDialog(); // 不拦截启动
        }
    }

    private async Task JoinViaP2p()
    {
        if (_guest == null || _selected == null) return;
        if (_p2pJoinButton != null) _p2pJoinButton.IsEnabled = false;
        if (_p2pStatus != null) _p2pStatus.Text = "等待直连建立…(房主粘贴回执后自动连通)";
        var port = await _guest.WaitForReadyAsync(30000).ConfigureAwait(true);
        if (_p2pJoinButton != null) _p2pJoinButton.IsEnabled = true;
        if (port <= 0)
        {
            if (_p2pStatus != null) _p2pStatus.Text = "P2P 直连未就绪——可先走下面的普通加入";
            return;
        }
        // 写服务器列表用原始地址(长期有效),游戏连接走本机隧道端口
        SaveServerEntry(_selected, _invite.Address);
        // 窗口马上要关(DialogResult=true),隧道必须活到游戏连上:移交应用级注册表
        _handedOff = true;
        MultiplayerP2pRegistry.Hold(_guest);
        WidgetWindow.Boot("[p2p] guest handed off, proxy=127.0.0.1:" + port);
        StartLaunch(_selected, "127.0.0.1:" + port);
    }

    private async Task PingRoomAsync()
    {
        try
        {
            var status = await ServerListPing.PingAsync(_invite.Host, _invite.Port, 4000).ConfigureAwait(true);
            _statusText.Text = "在线 · " + status.VersionName + " · " + status.PlayersOnline + "/" + status.PlayersMax
                + " 人 · " + status.PingMs + "ms";
        }
        catch (Exception)
        {
            _statusText.Text = "暂时检测不到房间(可能还没开好)——仍可尝试加入,进不去会卡在连接界面";
            _statusText.Foreground = DialogKit.Res("TextSecondaryBrush");
        }
    }

    private async Task FillVersionsAsync()
    {
        var versions = new List<InstalledVersionInfo>();
        foreach (var root in AppServices.GameDirectories)
        {
            try
            {
                var external = !AppServices.IsManagedLayout(root);
                versions.AddRange(InstalledVersionScanner.Scan(
                    Path.Combine(root, "versions"),
                    Path.Combine(root, "instances"),
                    Path.Combine(root, "libraries"),
                    externalLayout: external));
            }
            catch (Exception)
            {
            }
        }

        // 优先展示:上次玩的 → 可启动的在前
        var last = AppServices.Config.LastLaunchedVersion;
        versions = versions
            .Where(v => v.HasClientJar)
            .OrderByDescending(v => string.Equals(v.Id, last, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v.ModsCount)
            .ToList();

        if (versions.Count == 0)
        {
            _versionList.Children.Add(new TextBlock
            {
                Text = "启动器还没有已安装版本。请先在下载页装一个游戏版本再加入。",
                FontSize = 11.5,
                Foreground = DialogKit.Res("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var v in versions.Take(12))
            _versionList.Children.Add(BuildVersionRow(v));
        SelectRow(versions[0].Id);
    }

    private UIElement BuildVersionRow(InstalledVersionInfo v)
    {
        var row = new Border
        {
            Background = DialogKit.Res("RowBgBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Tag = v.Id,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = v.Id,
            FontSize = 12,
            Foreground = DialogKit.Res("TextPrimaryBrush"),
        });
        text.Children.Add(new TextBlock
        {
            Text = v.JavaDisplay + " · " + v.ModsDisplay,
            FontSize = 10.5,
            Foreground = DialogKit.Res("TextSecondaryBrush"),
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        var check = new TextBlock
        {
            Text = "",
            FontSize = 13,
            Foreground = DialogKit.Res("AccentTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "check",
        };
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        row.Child = grid;

        // 行包成 Button:可点 + UIA InvokePattern
        var button = new Button
        {
            Content = row,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => SelectRow(v.Id);
        System.Windows.Automation.AutomationProperties.SetName(button, "版本 " + v.Id);
        return button;
    }

    private void SelectRow(string versionId)
    {
        _selected = FindVersion(versionId);
        foreach (var button in _versionList.Children.OfType<Button>())
        {
            if (button.Content is not Border row) continue;
            var check = row.Child is Grid g
                ? g.Children.OfType<TextBlock>().FirstOrDefault(t => (string)t.Tag == "check")
                : null;
            if (check != null) check.Text = string.Equals((string)row.Tag, versionId, StringComparison.OrdinalIgnoreCase) ? "✓" : "";
        }
    }

    private InstalledVersionInfo? FindVersion(string id)
    {
        foreach (var button in _versionList.Children.OfType<Button>())
        {
            if (button.Content is Border row && string.Equals((string)row.Tag, id, StringComparison.OrdinalIgnoreCase))
                return new InstalledVersionInfo
                {
                    Id = id,
                    // RootDirectory 在扫描时记录过:重新按 id 在各目录找(行里存的是 id)
                    RootDirectory = RootForVersion(id),
                };
        }
        return null;
    }

    private static string RootForVersion(string id)
    {
        foreach (var root in AppServices.GameDirectories)
        {
            var external = !AppServices.IsManagedLayout(root);
            var versionsRoot = Path.Combine(root, "versions");
            if (Directory.Exists(Path.Combine(versionsRoot, id)))
            {
                _ = external;
                return root;
            }
        }
        return AppServices.ActiveGameDirectory;
    }

    private void ConfirmJoin()
    {
        if (_selected == null)
        {
            var hint = new InfoWindow("选一个版本", "先在列表里选一个要用来加入的版本。");
            hint.Owner = this;
            hint.ShowDialog();
            return;
        }

        SaveServerEntry(_selected, _invite.Address);
        StartLaunch(_selected, _invite.Address);
    }

    private void StartLaunch(InstalledVersionInfo version, string connectAddress)
    {
        var versionId = version.Id;
        var confirm = new ConfirmWindow("加入房间",
            "启动 " + versionId + " 并连接到 " + connectAddress + "？", "启动游戏")
        { Owner = AppServices.Widget };
        if (confirm.ShowDialog() != true) return;
        DialogResult = true;
        _ = AppLauncher.LaunchAsync(versionId, _ => { }, connectAddress);
    }
}
