using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Multiplayer.FuseLink;

/// <summary>连接帧:可靠段里再包一层多路复用(connId + 操作)。op: 1=OPEN 2=DATA 3=CLOSE。</summary>
public static class LinkFrame
{
    public const int MaxPayload = 1000; // 保证一帧 ≤ 一个可靠段

    /// <summary>[connId 2B][op 1B][data]</summary>
    public static byte[] Encode(int connId, byte op, byte[] data)
    {
        var frame = new byte[3 + data.Length];
        frame[0] = (byte)(connId >> 8);
        frame[1] = (byte)(connId & 0xFF);
        frame[2] = op;
        Array.Copy(data, 0, frame, 3, data.Length);
        return frame;
    }

    public static (int ConnId, byte Op, byte[] Data) Decode(byte[] segment)
    {
        var connId = (segment[0] << 8) | segment[1];
        var op = segment[2];
        var data = new byte[segment.Length - 3];
        Array.Copy(segment, 3, data, 0, data.Length);
        return (connId, op, data);
    }
}

/// <summary>
/// fuse-link 会话层(单好友直连;多人联机请用 UPnP/frp 通道)。
/// 房主:绑定 UDP + STUN 探测 → 候选进邀请码 → 好友 punch 先到(全锥)或粘回执后打洞 →
///       好友 OPEN 帧 → 房主 TcpClient 连本机游戏 LAN 端口 → 双向搬运。
/// 好友:解析候选 → 生成回执码 → 后台打洞 → 本机 TcpListener → 游戏连 127.0.0.1:端口。
/// </summary>
public sealed class FuseLinkHost : IDisposable
{
    private readonly string _lanHost;
    private readonly int _lanPort;
    private UdpClient? _udp;
    private FuseLinkPipe? _pipe;
    private byte[] _token = new byte[16];
    private readonly ConcurrentDictionary<int, TcpClient> _conns = new();
    private readonly ConcurrentDictionary<int, ConcurrentQueue<byte[]>> _pending = new();
    private int _nextConnId = 100;
    private volatile bool _closed;

    public string? CandidatesBase64 { get; private set; }
    public string? SymmetricError { get; private set; }
    public bool HasPeer => _pipe is { IsEstablished: true, IsDead: false };
    /// <summary>诊断:pipe 内部状态。</summary>
    public string DebugState => _pipe == null
        ? "pipe=null"
        : $"est={_pipe.IsEstablished} dead={_pipe.IsDead} peer={_pipe.Peer} local={_pipe.LocalPort}";
    public event Action<string>? StatusChanged;
    /// <summary>诊断日志(接启动器 boot.log / 探针控制台)。</summary>
    public event Action<string>? Diagnostic;

    public FuseLinkHost(string lanHost, int lanPort)
    {
        _lanHost = lanHost;
        _lanPort = lanPort;
    }

    /// <summary>绑定 socket + STUN 探测,生成候选并挂好 pipe(好友 punch 先到也能接住)。
    /// 对称 NAT 时公网打洞成功率极低,降级为"仅局域网候选"(同 WiFi 好友仍可直连)。</summary>
    public bool Prepare()
    {
        try
        {
            _udp = new UdpClient(0);
            var probe = StunClient.ProbeAsync(_udp).ConfigureAwait(false).GetAwaiter().GetResult();
            var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            var endpoints = new List<IPEndPoint>();
            var lanOnly = false;
            if (probe.Mapped == null)
            {
                SymmetricError = probe.Error;
                lanOnly = true; // STUN 全挂:没公网候选,局域网候选还能救同 WiFi 场景
            }
            else if (probe.Symmetric)
            {
                SymmetricError = "对称 NAT";
                lanOnly = true; // 公网映射端口对好友无意义,只留局域网
            }
            else
            {
                endpoints.Add(probe.Mapped);
            }
            foreach (var text in Ipv6Probe.GetGlobalAddresses())
                if (IPAddress.TryParse(text, out var v6))
                    endpoints.Add(new IPEndPoint(v6, port));
            var lanIp = Ipv6Probe.GetLocalIPv4();
            if (!string.IsNullOrEmpty(lanIp) && IPAddress.TryParse(lanIp, out var v4))
                endpoints.Add(new IPEndPoint(v4, port));
            if (endpoints.Count == 0)
            {
                StatusChanged?.Invoke("P2P 不可用:没有可用的候选地址");
                return false;
            }

            new Random().NextBytes(_token);
            CandidatesBase64 = FuseLinkCodec.Encode(new FuseLinkCandidates(_token, port, endpoints.ToArray()));

            _pipe = new FuseLinkPipe(_udp, _token); // 立刻挂上:好友 punch 先到也能接住
            _pipe.SegmentReceived += OnSegment;
            _pipe.Dead += () => StatusChanged?.Invoke("P2P 通道断开(好友可重新点「直连并启动」)");
            StatusChanged?.Invoke(lanOnly
                ? "P2P 限定局域网直连(" + (SymmetricError ?? "无公网候选") + ");跨网好友请走 frp 隧道"
                : "P2P 候选已生成(映射 " + probe.Mapped + "),等好友回执");
            return true;
        }
        catch (Exception ex)
        {
            SymmetricError = ex.Message;
            StatusChanged?.Invoke("P2P 准备失败:" + ex.Message);
            return false;
        }
    }

    /// <summary>测试/离线用:直接注入候选端点(跳过 STUN)。端口为 0 的端点用实际绑定端口替换。</summary>
    public bool PrepareWithEndpoints(IPEndPoint[] endpoints)
    {
        if (endpoints.Length == 0) return false;
        _udp = new UdpClient(0);
        var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        var resolved = new List<IPEndPoint>();
        foreach (var endpoint in endpoints)
            resolved.Add(endpoint.Port == 0 ? new IPEndPoint(endpoint.Address, port) : endpoint);
        new Random().NextBytes(_token);
        CandidatesBase64 = FuseLinkCodec.Encode(new FuseLinkCandidates(_token, port, resolved.ToArray()));
        _pipe = new FuseLinkPipe(_udp, _token);
        _pipe.SegmentReceived += OnSegment;
        return true;
    }

    /// <summary>粘贴好友回执:向好友候选端点打洞(好友在后台一直刷 punch,粘完即通)。</summary>
    public async Task<bool> AcceptAnswerAsync(string answerBase64, int timeoutMs, CancellationToken ct = default)
    {
        if (_pipe == null) throw new InvalidOperationException("先 Prepare()");
        var candidates = FuseLinkCodec.Decode(answerBase64);
        if (candidates == null)
        {
            StatusChanged?.Invoke("回执码格式无效——请让好友重新复制");
            return false;
        }
        StatusChanged?.Invoke("正在向好友候选端点打洞…");
        var ok = await _pipe.PunchAsync(candidates.Endpoints, timeoutMs, ct).ConfigureAwait(false);
        if (!ok)
        {
            StatusChanged?.Invoke("打洞超时——回执过期或 NAT 太严,可重试或走 frp");
            return false;
        }
        StatusChanged?.Invoke("P2P 直连已建立(" + _pipe.Peer + ")——好友点「直连并启动」即可进服");
        return true;
    }

    private void OnSegment(byte[] segment)
    {
        if (segment.Length < 3) return;
        try
        {
            var (connId, op, data) = LinkFrame.Decode(segment);
            Diagnostic?.Invoke("segment conn=" + connId + " op=" + op + " len=" + data.Length);
            switch (op)
            {
                case 1: // OPEN:连真正的游戏端口
                    _ = Task.Run(() => HandleOpen(connId));
                    break;
                case 2: // DATA:连接还没好就先排队(游戏握手包来得很快)
                    if (_conns.TryGetValue(connId, out var client) && client.Connected)
                    {
                        try { client.GetStream().Write(data, 0, data.Length); } catch (Exception) { CloseConn(connId); }
                    }
                    else
                    {
                        var queue = _pending.GetOrAdd(connId, _ => new ConcurrentQueue<byte[]>());
                        queue.Enqueue(data);
                    }
                    break;
                case 3: // CLOSE
                    CloseConn(connId);
                    break;
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task HandleOpen(int connId)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(_lanHost, _lanPort).ConfigureAwait(false);
            Diagnostic?.Invoke("game connect ok " + _lanHost + ":" + _lanPort);
            _conns[connId] = client;
            _pipe?.SendSegment(LinkFrame.Encode(connId, 1, new byte[0])); // OPEN 确认
            if (_pending.TryRemove(connId, out var queue))
            {
                var stream0 = client.GetStream();
                while (queue.TryDequeue(out var buffered))
                    await stream0.WriteAsync(buffered, 0, buffered.Length).ConfigureAwait(false);
            }
            var stream = client.GetStream();
            var buffer = new byte[LinkFrame.MaxPayload];
            while (!_closed && client.Connected && _pipe is { IsDead: false })
            {
                var n = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (n <= 0) break;
                var offset = 0;
                while (offset < n) // 拆帧:每段 ≤ MaxPayload
                {
                    var chunk = Math.Min(n - offset, LinkFrame.MaxPayload);
                    var data = new byte[chunk];
                    Array.Copy(buffer, offset, data, 0, chunk);
                    _pipe.SendSegment(LinkFrame.Encode(connId, 2, data));
                    offset += chunk;
                }
            }
            CloseConn(connId);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke("game connect failed: " + ex.Message);
            CloseConn(connId);
        }
    }

    private void CloseConn(int connId)
    {
        if (_conns.TryRemove(connId, out var client))
        {
            try { client.Close(); } catch (Exception) { }
        }
        _pending.TryRemove(connId, out _);
        try { _pipe?.SendSegment(LinkFrame.Encode(connId, 3, new byte[0])); } catch (Exception) { }
    }

    public void Dispose()
    {
        _closed = true;
        _pipe?.Dispose();
        foreach (var pair in _conns)
        {
            try { pair.Value.Close(); } catch (Exception) { }
        }
        _conns.Clear();
        try { _udp?.Close(); } catch (Exception) { }
    }
}

/// <summary>好友侧:后台打洞到房主,本机开 TcpListener 给游戏连。</summary>
public sealed class FuseLinkGuest : IDisposable
{
    private readonly FuseLinkCandidates _hostCandidates;
    private UdpClient? _udp;
    private FuseLinkPipe? _pipe;
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<int, TcpClient> _conns = new();
    private readonly ConcurrentDictionary<int, ConcurrentQueue<byte[]>> _pending = new();
    private int _nextConnId = 1;
    private volatile bool _closed;
    private Task? _connectTask;

    public int ProxyPort { get; private set; }
    public string? AnswerCode { get; private set; }
    public bool IsReady => ProxyPort > 0 && _pipe is { IsDead: false };
    public event Action? Ready;
    public event Action<string>? StatusChanged;
    /// <summary>诊断日志(接启动器 boot.log)。</summary>
    public event Action<string>? Diagnostic;

    public FuseLinkGuest(FuseLinkCandidates hostCandidates)
    {
        _hostCandidates = hostCandidates;
    }

    /// <summary>绑定 + STUN + 生成回执码,并立刻开始后台打洞(房主粘完回执即通)。
    /// STUN 失败/对称时仍生成候选(局域网地址 + 赌一把的公网映射),状态如实标注。</summary>
    public bool Prepare()
    {
        try
        {
            _udp = new UdpClient(0);
            var probe = StunClient.ProbeAsync(_udp).ConfigureAwait(false).GetAwaiter().GetResult();
            var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            var endpoints = new List<IPEndPoint>();
            if (probe.Mapped != null) endpoints.Add(probe.Mapped);
            foreach (var text in Ipv6Probe.GetGlobalAddresses())
                if (IPAddress.TryParse(text, out var v6))
                    endpoints.Add(new IPEndPoint(v6, port));
            var lanIp = Ipv6Probe.GetLocalIPv4();
            if (!string.IsNullOrEmpty(lanIp) && IPAddress.TryParse(lanIp, out var v4))
                endpoints.Add(new IPEndPoint(v4, port));
            if (endpoints.Count == 0)
            {
                StatusChanged?.Invoke("网络探测失败(无可用地址)");
                return false;
            }
            AnswerCode = FuseLinkCodec.Encode(new FuseLinkCandidates(_hostCandidates.Token, port, endpoints.ToArray()));
            // 回执生成后立刻后台打洞:好友 NAT 的映射先立起来,房主粘完回执马上能通
            _connectTask = Task.Run(() => ConnectCoreAsync(180_000));
            StatusChanged?.Invoke("回执码已生成——复制发给房主;房主粘贴后自动直连");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("准备失败:" + ex.Message);
            return false;
        }
    }

    /// <summary>等直连就绪,返回本机代理端口(0 = 失败/超时)。</summary>
    public async Task<int> WaitForReadyAsync(int extraTimeoutMs, CancellationToken ct = default)
    {
        if (_connectTask == null) _connectTask = Task.Run(() => ConnectCoreAsync(extraTimeoutMs));
        var deadline = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds + extraTimeoutMs;
        while ((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds < deadline)
        {
            if (IsReady) return ProxyPort;
            if (_connectTask.IsCompleted && !IsReady) return 0;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return IsReady ? ProxyPort : 0;
    }

    private async Task ConnectCoreAsync(int timeoutMs)
    {
        _pipe = new FuseLinkPipe(_udp!, _hostCandidates.Token);
        _pipe.SegmentReceived += OnSegment;
        _pipe.Diagnostic += msg => Diagnostic?.Invoke("pipe: " + msg);
        _pipe.Dead += () => StatusChanged?.Invoke("P2P 通道断开——重新点「直连并启动」可重试");
        StatusChanged?.Invoke("正在向房主打洞…(房主粘贴回执后双方自动连通)");
        var ok = await _pipe.PunchAsync(_hostCandidates.Endpoints, timeoutMs, CancellationToken.None).ConfigureAwait(false);
        if (!ok)
        {
            StatusChanged?.Invoke("打洞超时——请房主确认已粘贴回执,或改走邀请里的直连地址/frp");
            return;
        }
        // 代理用独立的临时 TCP 端口(不能复用 UDP 端口:TCP/UDP 端口空间独立,但可能撞上已占用)
        _listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            _listener.Start();
            ProxyPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("本机代理端口启动失败:" + ex.Message);
            ProxyPort = 0;
            return;
        }
        _ = Task.Run(AcceptLoop);
        StatusChanged?.Invoke("P2P 已直连!游戏连接 127.0.0.1:" + ProxyPort);
        Ready?.Invoke();
    }

    private async Task AcceptLoop()
    {
        while (!_closed && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                var connId = _nextConnId++;
                _conns[connId] = client;
                Diagnostic?.Invoke("accepted conn " + connId + " pipe=" +
                    (_pipe == null ? "null" : "est=" + _pipe.IsEstablished + " dead=" + _pipe.IsDead));
                try
                {
                    _pipe?.SendSegment(LinkFrame.Encode(connId, 1, new byte[0])); // OPEN
                }
                catch (Exception ex)
                {
                    Diagnostic?.Invoke("OPEN send failed: " + ex.Message);
                }
                _ = Task.Run(() => PipeClient(connId, client));
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke("accept loop: " + ex.Message);
                if (_closed) return;
                Thread.Sleep(100);
            }
        }
    }

    private async Task PipeClient(int connId, TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[LinkFrame.MaxPayload];
            while (!_closed && client.Connected && _pipe is { IsDead: false })
            {
                var n = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (n <= 0) break;
                var offset = 0;
                while (offset < n)
                {
                    var chunk = Math.Min(n - offset, LinkFrame.MaxPayload);
                    var data = new byte[chunk];
                    Array.Copy(buffer, offset, data, 0, chunk);
                    _pipe!.SendSegment(LinkFrame.Encode(connId, 2, data));
                    offset += chunk;
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            if (_conns.TryRemove(connId, out var removed))
            {
                try { removed.Close(); } catch (Exception) { }
            }
            try { _pipe?.SendSegment(LinkFrame.Encode(connId, 3, new byte[0])); } catch (Exception) { }
        }
    }

    private void OnSegment(byte[] segment)
    {
        if (segment.Length < 3) return;
        try
        {
            var (connId, op, data) = LinkFrame.Decode(segment);
            switch (op)
            {
                case 1: // OPEN 确认(房主已连上游戏端口)
                    StatusChanged?.Invoke("房主已接受连接(" + connId + ")");
                    break;
                case 2: // DATA:游戏还没连上就先排队
                    if (_conns.TryGetValue(connId, out var client) && client.Connected)
                    {
                        try { client.GetStream().Write(data, 0, data.Length); } catch (Exception) { }
                    }
                    else
                    {
                        var queue = _pending.GetOrAdd(connId, _ => new ConcurrentQueue<byte[]>());
                        queue.Enqueue(data);
                    }
                    break;
                case 3:
                    if (_conns.TryRemove(connId, out var removed))
                    {
                        try { removed.Close(); } catch (Exception) { }
                    }
                    _pending.TryRemove(connId, out _);
                    break;
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _closed = true;
        Diagnostic?.Invoke("guest disposed");
        try { _listener?.Stop(); } catch (Exception) { }
        _pipe?.Dispose();
        foreach (var pair in _conns)
        {
            try { pair.Value.Close(); } catch (Exception) { }
        }
        _conns.Clear();
        try { _udp?.Close(); } catch (Exception) { }
    }
}
