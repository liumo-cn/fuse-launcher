using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Multiplayer;

namespace Fuse.App;

/// <summary>开放世界会话的状态。</summary>
public sealed record MultiplayerHostState(
    string Phase,        // Idle/Launching/WaitingLan/Probing/Ready/Closed
    string Detail,
    string? Address,     // 可生成邀请的地址(host:port 或域名)
    string? ChannelKind, // UPnP/IPv6/局域网/e4mc隧道/frp
    string? InviteLink)
{
    public static readonly MultiplayerHostState Idle =
        new("Idle", "未开放——点「开放世界」开始", null, null, null);
}

/// <summary>
/// 开放世界会话:启动游戏 → 抓 LAN 端口 → 通道探测(UPnP → IPv6 → 仅局域网;e4mc 地址优先) →
/// 游戏退出自动清理映射。启动器不提供任何服务器;frp 是用户自己账号的第三方隧道。
/// </summary>
internal sealed class MultiplayerHostSession : IDisposable
{
    private readonly string _versionId;
    private readonly string _gameRoot;
    private readonly UpnpSupport _upnp = new();
    private LanPortWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private string? _modAddress;
    private (string Host, int Port)? _lanPort;
    private string? _channelKind;
    private string? _address;
    private volatile bool _sawProcess;   // 看门狗:进程出现过才开始盯退出
    private volatile bool _closed;       // 看门狗/超时已收尾,主流程不再覆盖状态
    private Fuse.Core.Multiplayer.FuseLink.FuseLinkHost? _fuseLink;  // P2P 直连(零服务器)
    private string? _fuseLinkCandidates;

    /// <summary>UI 线程消费:每次状态变化都发。</summary>
    public event Action<MultiplayerHostState>? StateChanged;

    public MultiplayerHostState Current { get; private set; } = MultiplayerHostState.Idle;

    public MultiplayerHostSession(string versionId, string gameRoot)
    {
        _versionId = versionId;
        _gameRoot = gameRoot;
    }

    public bool IsRunning => Current.Phase is "Launching" or "WaitingLan" or "Probing" or "Ready";

    /// <summary>捕获到的 LAN 端口(frp 隧道 localPort 用)。</summary>
    public (string Host, int Port)? LanPort => _lanPort;

    private void Publish(string phase, string detail, string? channel = null)
    {
        _channelKind = channel ?? _channelKind;
        Current = new MultiplayerHostState(phase, detail, _address, _channelKind,
            _address == null ? null : TryBuildInvite());
        StateChanged?.Invoke(Current);
    }

    /// <summary>主流程:已启动的游戏里等 LAN 端口,然后逐级探测通道。</summary>
    public Task RunAsync(CancellationToken ct) => Task.Run(() => RunCore(ct), ct);

    private void RunCore(CancellationToken ct)
    {
        var instance = AppServices.InstanceDirectoryIn(_gameRoot, _versionId);
        _watcher = new LanPortWatcher(Path.Combine(instance, "logs", "latest.log"), 800);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 看门狗:游戏任何阶段退出(WaitingLan/Probing/Ready)都要清理 UPnP 并落状态
        var watchdog = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                Thread.Sleep(2500);
                if (!_sawProcess) continue;
                if (_closed) return;
                if (LaunchState.GetRunning(_versionId) != null) continue;
                Cleanup();
                _closed = true;
                Publish("Closed", "游戏已退出,联机已关闭,UPnP 映射已删除");
                return;
            }
        }, ct);

        if (LaunchState.GetRunning(_versionId) == null)
        {
            Publish("Launching", "正在启动游戏…");
            // 启动(由 UI 触发;这里只等进程出现)
            var deadline = UtcNowMs() + 120_000;
            while (LaunchState.GetRunning(_versionId) == null && UtcNowMs() < deadline && !ct.IsCancellationRequested)
                Thread.Sleep(1000);
        }
        if (ct.IsCancellationRequested || _closed) return;
        _sawProcess = true;
        Publish("WaitingLan", "游戏已启动——在里面按 Esc →「对局域网开放」(e4mc 用户点开放后聊天栏会给地址)");
        Boot("[multiplayer] watching " + instance);

        LanScanResult scan;
        try
        {
            scan = _watcher.WaitForAsync(120_000, _cts.Token, p => LanAlive(p)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return; // 看门狗已收尾
        }
        if (_closed) return;
        if (scan.ModAddress != null)
        {
            _modAddress = scan.ModAddress;
            _address = scan.ModAddress;
            _lanPort = scan.Port;   // e4mc 也在桥接本地 LAN 端口,留着做存活检查
            Publish("Ready", "联机 Mod 已开放:" + scan.ModAddress, "mod");
        }
        else if (scan.Port != null)
        {
            _lanPort = scan.Port;
            Boot("[multiplayer] lan port " + scan.Port.Value.Port);
            // 端口行先于 e4mc 域名出现:装了联机 Mod 就等域名落地,否则会误走直连
            if (HasE4mcMod(instance))
            {
                Publish("Probing", "已捕获 LAN 端口,等待联机 Mod 分配隧道地址…");
                var later = _watcher.WaitForModAddressAsync(30_000, _cts.Token).GetAwaiter().GetResult();
                if (_closed || ct.IsCancellationRequested) return;
                if (later.ModAddress != null)
                {
                    _modAddress = later.ModAddress;
                    _address = later.ModAddress;
                    Publish("Ready", "联机 Mod 已开放:" + later.ModAddress, "mod");
                }
            }
            if (Current.Phase != "Ready") ProbeChannels(_cts.Token);
        }
        else
        {
            _closed = true;
            Publish("Closed", "120 秒内没等到「对局域网开放」——已停止(游戏可以继续玩,再点一次重试)");
            return;
        }

        // 维护到游戏退出(看门狗兜底,这里快速路径再查一遍)
        var lastLanCheck = UtcNowMs();
        while (!ct.IsCancellationRequested && !_closed && Current.Phase == "Ready")
        {
            if (LaunchState.GetRunning(_versionId) == null)
            {
                _closed = true;
                Cleanup();
                Publish("Closed", "游戏已退出,联机已关闭,UPnP 映射已删除");
                return;
            }
            // 世界退回菜单/停止托管后 LAN 端口就没了,但游戏进程和 e4mc 隧道还活着——
            // 不查的话房主会一直挂着一条死邀请
            if (_lanPort != null && UtcNowMs() - lastLanCheck > 10_000)
            {
                lastLanCheck = UtcNowMs();
                if (!LanAlive(_lanPort.Value.Port))
                {
                    _closed = true;
                    Cleanup();
                    Publish("Closed", "世界已退回菜单或已停止托管,房间失效——回到世界再点一次「创建房间」");
                    return;
                }
            }
            Thread.Sleep(3000);
        }
    }

    /// <summary>本地 LAN 端口是否还在监听。</summary>
    private static bool LanAlive(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            var r = c.BeginConnect("127.0.0.1", port, null, null);
            if (!r.AsyncWaitHandle.WaitOne(700)) { c.Close(); return false; }
            c.EndConnect(r);
            c.Close();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>实例 mods 目录里有没有 e4mc(被停用的不算)。</summary>
    private static bool HasE4mcMod(string instance)
    {
        try
        {
            var mods = Path.Combine(instance, "mods");
            if (!Directory.Exists(mods)) return false;
            foreach (var f in Directory.EnumerateFiles(mods, "*.jar"))
            {
                if (Path.GetFileName(f).IndexOf("e4mc", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        catch (Exception)
        {
        }
        return false;
    }

    /// <summary>通道探测:UPnP → IPv6 → 仅局域网。</summary>
    private void ProbeChannels(CancellationToken ct)
    {
        var port = _lanPort!.Value.Port;
        Publish("Probing", "已捕获 LAN 端口 " + port + ",正在探测穿透通道…");
        // P2P 直连候选并行准备(STUN 探测阻塞几秒,不能卡主流程)
        _ = Task.Run(PrepareFuseLink);

        // 1. UPnP
        var upnp = _upnp.AddMapping(port);
        if (upnp.Success)
        {
            var publicIp = PublicIpProbe.ProbeAsync(AppServices.Http, 6000).GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(publicIp))
            {
                _address = publicIp + ":" + upnp.ExternalPort;
                Publish("Ready", "UPnP 已打通:外网地址 " + _address, "UPnP");
                EnsureFirewallAsync(ct);
                return;
            }
            Boot("[multiplayer] upnp mapped but public ip probe failed");
            _upnp.RemoveMapping(); // 没有公网地址等于白开
        }
        else
        {
            Boot("[multiplayer] upnp failed: " + upnp.Error);
        }

        // 2. IPv6 直连
        var v6 = Ipv6Probe.GetGlobalAddresses();
        if (v6.Count > 0)
        {
            _address = "[" + v6[0] + "]:" + port;
            Publish("Ready", "IPv6 直连(需好友网络也支持 IPv6):" + _address, "IPv6");
            EnsureFirewallAsync(ct);
            return;
        }

        // 3. 仅局域网
        var lanIp = Ipv6Probe.GetLocalIPv4();
        if (!string.IsNullOrEmpty(lanIp))
        {
            _address = lanIp + ":" + port;
            Publish("Ready", "仅局域网可连(同一 WiFi):" + _address + "\n无公网的朋友请用「联机 Mod」或「frp 隧道」兜底", "局域网");
            return;
        }

        Publish("Ready", "端口已开但没有可用通道——试试下方的「联机 Mod」或「frp 隧道」", null);
        _address = null;
        Current = new MultiplayerHostState("Ready", Current.Detail, null, _channelKind, null);
    }

    private void EnsureFirewallAsync(CancellationToken ct)
    {
        _ = Task.Run(() =>
        {
            if (FirewallSupport.RuleExists()) return;
            string? javaw = null;
            try
            {
                javaw = LaunchState.GetRunning(_versionId)?.MainModule?.FileName;
            }
            catch (Exception)
            {
            }
            if (!string.IsNullOrEmpty(javaw))
            {
                var ok = FirewallSupport.EnsureRuleAsync(javaw!).GetAwaiter().GetResult();
                Boot("[multiplayer] firewall rule " + (ok ? "ok" : "declined/failed"));
            }
        }, ct);
    }

    /// <summary>联机 Mod 的地址也可以手动粘贴(聊天栏复制兜底)。</summary>
    public void SetModAddress(string address)
    {
        _modAddress = address;
        _address = address;
        Publish("Ready", "联机地址已设置:" + address, "mod");
    }

    /// <summary>frp 隧道通了:邀请走隧道地址。</summary>
    public void SetTunnelAddress(string address)
    {
        _address = address;
        Publish("Ready", "frp 隧道已就绪:" + address, "frp");
    }

    public string? TryBuildInvite()
    {
        if (_address == null) return null;
        try
        {
            var room = AppServices.VersionProfiles.Get(_versionId)?.DisplayName;
            if (string.IsNullOrWhiteSpace(room)) room = _versionId;
            var mc = ResolveGameVersionSafe();
            return InviteCodec.Build(_address, room, mc, null, _fuseLinkCandidates);
        }
        catch (Exception ex)
        {
            Boot("[multiplayer] invite build failed: " + ex.Message);
            return null;
        }
    }

    /// <summary> fuse-link P2P 准备(零服务器:STUN 免费公共服务 + 候选进邀请码)。</summary>
    private void PrepareFuseLink()
    {
        try
        {
            var host = new Fuse.Core.Multiplayer.FuseLink.FuseLinkHost("127.0.0.1", _lanPort!.Value.Port);
            host.StatusChanged += msg => P2pStatus?.Invoke(msg);
            if (!host.Prepare())
            {
                Boot("[fuselink] prepare failed: " + (host.SymmetricError ?? "?"));
                host.Dispose();
                return;
            }
            _fuseLink = host;
            _fuseLinkCandidates = host.CandidatesBase64;
            Boot("[fuselink] candidates ready");
            // 邀请要刷新(带上 fl 候选)
            Publish(Current.Phase, Current.Detail);
        }
        catch (Exception ex)
        {
            Boot("[fuselink] prepare exception: " + ex.Message);
        }
    }

    /// <summary>好友回执码接上并打洞(房主卡片按钮)。</summary>
    public Task<bool> AcceptFuseLinkAnswerAsync(string answerBase64)
    {
        var host = _fuseLink;
        if (host == null || _lanPort == null)
        {
            P2pStatus?.Invoke("P2P 还没准备好(等 STUN 探测,或本网络不支持)");
            return Task.FromResult(false);
        }
        return host.AcceptAnswerAsync(answerBase64, 20000);
    }

    /// <summary>P2P 状态文本(卡片行)。</summary>
    public event Action<string>? P2pStatus;

    private string ResolveGameVersionSafe()
    {
        try
        {
            return Core.Versioning.VersionJsonLoader.ResolveGameVersion(
                Path.Combine(_gameRoot, "versions"), _versionId);
        }
        catch (Exception)
        {
            return _versionId;
        }
    }

    public void Cleanup()
    {
        try { _upnp.RemoveMapping(); } catch (Exception) { }
        try { _cts?.Cancel(); } catch (Exception) { }
        try { _fuseLink?.Dispose(); } catch (Exception) { }
        _fuseLink = null;
        _fuseLinkCandidates = null;
        _address = null;
        _modAddress = null;
        _lanPort = null;
    }

    /// <summary>用户主动点「关闭联机」:置收尾标记并清理。</summary>
    public void CloseByUser()
    {
        _closed = true;
        Cleanup();
        Current = MultiplayerHostState.Idle;
    }

    public void Dispose()
    {
        Cleanup();
        _watcher?.Dispose();
    }

    private static long UtcNowMs() => (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;

    private static void Boot(string message) => WidgetWindow.Boot(message);
}
