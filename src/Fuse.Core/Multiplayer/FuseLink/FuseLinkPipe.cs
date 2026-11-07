using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Multiplayer.FuseLink;

/// <summary>
/// fuse-link 通道:同一只 UDP socket 上先打洞、后跑 ARQ 可靠流。
/// 包格式 [type 1B][seq 4B BE][ack 4B BE][len 2B BE][payload]
/// type: 0=keepalive 1=data 2=bye。punch 包单独识别(magic "FLPK")。
/// 打洞:双方互发 FLPK(带 token 前 8 字节),收到对端有效包即建立(源地址=真实端点);
/// 之后 keepalive/重传继续刷 NAT 映射保活。
/// </summary>
public sealed class FuseLinkPipe : IDisposable
{
    public const int PunchMagic1 = 0x464C504B; // "FLPK"

    private readonly UdpClient _udp;
    private readonly byte[] _tokenPrefix; // 8B
    private volatile IPEndPoint? _peer;
    private volatile bool _established;
    private volatile bool _dead;
    private long _lastRecvMs;

    // 发送侧
    private readonly object _txGate = new();
    private uint _txSeq;                       // 下一序号(从 1 开始,0 留给纯 keepalive)
    private readonly Dictionary<uint, (byte[] Packet, long FirstSentMs, long LastSentMs, int Retries)> _inFlight = new();
    // 接收侧
    private uint _expectedSeq = 1;             // 期望的下一序号
    private uint _ackSeq;                      // 已按序收到的最高序号
    private readonly Dictionary<uint, byte[]> _outOfOrder = new();

    public bool IsEstablished => _established;
    public bool IsDead => _dead;
    public IPEndPoint? Peer => _peer;
    public int LocalPort { get; }

    /// <summary>按序收到的 data 段(段=一帧应用数据,发送方保证帧≤1000B)。</summary>
    public event Action<byte[]>? SegmentReceived;
    public event Action? Established;
    public event Action? Dead;
    /// <summary>诊断日志。</summary>
    public event Action<string>? Diagnostic;

    public FuseLinkPipe(UdpClient udp, byte[] token)
    {
        _udp = udp;
        LocalPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        _tokenPrefix = TokenPrefix(token);
        _lastRecvMs = NowMs();
        _ = Task.Run(RxLoop);
        _ = Task.Run(TimerLoop);
    }

    public static byte[] TokenPrefix(byte[] token)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(token);
        var prefix = new byte[8];
        Array.Copy(hash, prefix, 8);
        return prefix;
    }

    // ---------- 打洞 ----------

    /// <summary>向候选端点刷 punch,直到收到对端有效 FLPK。成功返回 true(已建立)。</summary>
    public async Task<bool> PunchAsync(IPEndPoint[] candidates, int timeoutMs, CancellationToken ct)
    {
        var deadline = NowMs() + timeoutMs;
        var buffer = BuildPunchPacket();
        while (NowMs() < deadline && !ct.IsCancellationRequested && !_established && !_dead)
        {
            foreach (var endpoint in candidates)
            {
                try { _udp.Send(buffer, buffer.Length, endpoint); } catch (Exception) { }
            }
            // 收包交给 RxLoop;punch 命中后 Established 置位
            await Task.Delay(120, ct).ConfigureAwait(false);
        }
        return _established;
    }

    private byte[] BuildPunchPacket()
    {
        var packet = new byte[12];
        packet[0] = unchecked((byte)(PunchMagic1 >> 24)); packet[1] = unchecked((byte)(PunchMagic1 >> 16));
        packet[2] = unchecked((byte)(PunchMagic1 >> 8)); packet[3] = unchecked((byte)PunchMagic1);
        Array.Copy(_tokenPrefix, 0, packet, 4, 8);
        return packet;
    }

    private static bool IsPunchPacket(byte[] data)
    {
        if (data.Length < 12) return false;
        return data[0] == unchecked((byte)(PunchMagic1 >> 24)) && data[1] == unchecked((byte)(PunchMagic1 >> 16))
            && data[2] == unchecked((byte)(PunchMagic1 >> 8)) && data[3] == unchecked((byte)PunchMagic1);
    }

    // ---------- 数据 ----------

    /// <summary>发送一帧应用数据(≤1000B,一帧恰好一个可靠段)。</summary>
    public void SendSegment(byte[] payload)
    {
        if (!_established || payload.Length > 1000) throw new InvalidOperationException(
            payload.Length > 1000 ? "fuse-link 帧过大" : "通道未建立");
        uint seq;
        lock (_txGate)
        {
            seq = _txSeq + 1;
            _txSeq = seq;
        }
        var packet = BuildPacket(1, seq, payload);
        lock (_txGate) _inFlight[seq] = (packet, NowMs(), NowMs(), 0);
        Diagnostic?.Invoke("send seq=" + seq + " len=" + payload.Length + " peer=" + _peer);
        RawSend(packet);
    }

    /// <summary>跳过打洞直接绑定对端(本机回环双端测试/已知地址场景)。</summary>
    public void AttachPeer(IPEndPoint peer)
    {
        _peer = peer;
        _established = true;
        Established?.Invoke();
    }

    private static byte[] BuildPacket(byte type, uint seq, byte[] payload)
    {
        var packet = new byte[11 + payload.Length];
        packet[0] = type;
        PutU32(packet, 1, seq);
        PutU32(packet, 5, 0); // ack 由发送时回填?——简单起见 keepalive/数据统一带当前 ack:在发送处填
        PutU16(packet, 9, (ushort)payload.Length);
        Array.Copy(payload, 0, packet, 11, payload.Length);
        return packet;
    }

    private static void PutU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint GetU32(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16)
        | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    private static void PutU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static ushort GetU16(byte[] buffer, int offset) =>
        (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    private void RawSend(byte[] packet)
    {
        var peer = _peer;
        if (peer == null || !_established) return;
        // 回填当前 ack(喂给对端做累积确认)
        PutU32(packet, 5, _ackSeq);
        try { _udp.Send(packet, packet.Length, peer); }
        catch (Exception ex) { Diagnostic?.Invoke("send error: " + ex.Message); }
    }

    private void RawSendTo(IPEndPoint peer, byte[] packet)
    {
        PutU32(packet, 5, _ackSeq);
        try { _udp.Send(packet, packet.Length, peer); } catch (Exception) { }
    }

    private void SendKeepalive()
    {
        var peer = _peer;
        if (peer == null || !_established) return;
        var packet = new byte[11];
        packet[0] = 0;
        PutU32(packet, 1, 0);
        PutU32(packet, 5, _ackSeq);
        PutU16(packet, 9, 0);
        try { _udp.Send(packet, packet.Length, peer); } catch (Exception) { }
    }

    // ---------- RX / 定时器 ----------

    private void RxLoop()
    {
        while (!_dead)
        {
            try
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                var data = _udp.Receive(ref remote);
                _lastRecvMs = NowMs();
                if (IsPunchPacket(data))
                {
                    var ok = true;
                    for (var i = 0; i < 8; i++)
                        if (data[4 + i] != _tokenPrefix[i]) { ok = false; break; }
                    if (!ok) continue;
                    // 回一个 punch(让对端也完成),并锁定对端真实端点
                    var reply = BuildPunchPacket();
                    try { _udp.Send(reply, reply.Length, remote); } catch (Exception) { }
                    if (!_established)
                    {
                        _peer = remote;
                        _established = true;
                        Established?.Invoke();
                    }
                    continue;
                }
                if (data.Length < 11)
                {
                    Diagnostic?.Invoke("rx short " + data.Length);
                    continue;
                }
                if (!_established)
                {
                    Diagnostic?.Invoke("rx before established");
                    continue;
                }
                var type = data[0];
                if (type != 0) Diagnostic?.Invoke("rx type=" + type + " len=" + data.Length + " from=" + remote);
                var seq = GetU32(data, 1);
                var ack = GetU32(data, 5);
                var len = GetU16(data, 9);
                if (type > 2) continue;
                // 对端的 ack:清掉已确认的 in-flight
                if (ack > 0) AckUpTo(ack);
                // 对端真实端点漂移(NAT 重映射):跟随
                if (!_peer!.Equals(remote)) _peer = remote;
                if (type == 2) { MarkDead(); return; }
                if (type == 1 && len > 0 && 11 + len <= data.Length)
                {
                    var payload = new byte[len];
                    Array.Copy(data, 11, payload, 0, len);
                    DeliverInOrder(seq, payload);
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { /* 关闭时的常见噪音 */ if (_dead) return; Thread.Sleep(250); }
            catch (Exception) { if (_dead) return; Thread.Sleep(250); }
        }
    }

    private void AckUpTo(uint ack)
    {
        lock (_txGate)
        {
            var keys = new List<uint>();
            foreach (var pair in _inFlight)
                if (pair.Key <= ack) keys.Add(pair.Key);
            foreach (var key in keys) _inFlight.Remove(key);
        }
    }

    private void DeliverInOrder(uint seq, byte[] payload)
    {
        if (seq == 0) return;
        lock (_outOfOrder)
        {
            if (seq < _expectedSeq) return; // 重复段
            if (seq == _expectedSeq)
            {
                _expectedSeq++;
                _ackSeq = seq;
                SegmentReceived?.Invoke(payload);
            }
            else
            {
                // 乱序先缓存(等待空洞补齐)
                _outOfOrder[seq] = payload;
                return;
            }
            // 补发缓存里的后续段
            while (_outOfOrder.TryGetValue(_expectedSeq, out var buffered))
            {
                _outOfOrder.Remove(_expectedSeq);
                _ackSeq = _expectedSeq;
                SegmentReceived?.Invoke(buffered);
                _expectedSeq++;
            }
        }
    }

    private void TimerLoop()
    {
        while (!_dead)
        {
            Thread.Sleep(120);
            var now = NowMs();
            if (_established && now - _lastRecvMs > 8000)
            {
                MarkDead();
                return;
            }
            if (!_established) continue;
            // RTO 重传:250ms 起,×1.4,上限 1.2s
            lock (_txGate)
            {
                foreach (var pair in _inFlight)
                {
                    var info = pair.Value;
                    if (now - info.LastSentMs >= Math.Min(250 * Math.Pow(1.4, info.Retries), 1200))
                    {
                        var packet = info.Packet;
                        RawSend(packet);
                        info.LastSentMs = now;
                        info.Retries++;
                        _inFlight[pair.Key] = info;
                        if (info.Retries > 25) { _dead = true; Dead?.Invoke(); return; }
                    }
                }
            }
            // 空闲保活(顺带刷 ACK)
            SendKeepalive();
        }
    }

    private void MarkDead()
    {
        if (_dead) return;
        _dead = true;
        Dead?.Invoke();
    }

    public void Close()
    {
        var peer = _peer;
        if (peer != null && _established)
        {
            try
            {
                var packet = new byte[11];
                packet[0] = 2;
                PutU32(packet, 5, _ackSeq);
                _udp.Send(packet, packet.Length, peer);
            }
            catch (Exception) { }
        }
        MarkDead();
    }

    public void Dispose()
    {
        Close();
    }

    private static long NowMs() => (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
}
