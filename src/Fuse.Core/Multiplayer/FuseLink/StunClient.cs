using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Multiplayer.FuseLink;

/// <summary>STUN 探测结果。</summary>
public sealed record StunResult(IPEndPoint? Mapped, string? Server, string? Error);

/// <summary>
/// STUN 客户端(RFC 5389 最小子集):Binding Request → XOR-MAPPED-ADDRESS。
/// 必须在要打洞的那只 UDP socket 上探测,否则拿到的映射端口不是同一只(映射按 socket 建立前状态分配)。
/// 对称 NAT 检测:同一 socket 问两台服务器,映射端口不一致 = 对称 NAT,打洞必败。
/// </summary>
public static class StunClient
{
    private const uint MagicCookie = 0x2112A442;

    /// <summary>免费公共 STUN(国内优先,无账号无费用——启动器不提供服务器,这里只是公共服务)。</summary>
    public static readonly (string Host, int Port)[] Servers =
    {
        ("stun.miwifi.com", 3478),
        ("stun.l.google.com", 19302),
        ("stun.qq.com", 3478),
    };

    public static async Task<StunResult> QueryAsync(UdpClient socket, string host, int port, int timeoutMs = 2000)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            IPAddress? v4 = null;
            foreach (var a in addresses)
                if (a.AddressFamily == AddressFamily.InterNetwork) { v4 = a; break; }
            if (v4 == null) return new StunResult(null, host, "STUN 无 IPv4 地址");

            var txid = new byte[12];
            new Random().NextBytes(txid);
            var request = new byte[20];
            request[0] = 0x00; request[1] = 0x01; // Binding Request
            request[2] = 0x00; request[3] = 0x00; // length 0
            request[4] = 0x21; request[5] = 0x12; request[6] = 0xA4; request[7] = 0x42;
            Array.Copy(txid, 0, request, 8, 12);

            // 用阻塞 Socket + ReceiveTimeout:UdpClient.ReceiveAsync 超时后悬空任务会吃掉
            // 下一个响应,同一 socket 连续查询会被毒化(实测踩过)。Prepare 阶段无并发读,阻塞安全。
            var raw = socket.Client;
            var oldTimeout = raw.ReceiveTimeout;
            raw.ReceiveTimeout = timeoutMs;
            try
            {
                var remote = new IPEndPoint(v4, port);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await socket.SendAsync(request, request.Length, remote).ConfigureAwait(false);
                    var buffer = new byte[512];
                    while (true)
                    {
                        int n;
                        try
                        {
                            n = raw.Receive(buffer);
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                        {
                            break; // 本轮超时,重试
                        }
                        if (n < 20) continue;
                        if (buffer[0] != 0x01 || buffer[1] != 0x01) continue; // 非 Binding Response
                        var mismatch = false;
                        for (var i = 0; i < 12; i++)
                            if (buffer[8 + i] != txid[i]) { mismatch = true; break; }
                        if (mismatch) continue; // 迟到的旧响应
                        var mapped = ParseXorMapped(buffer, n);
                        return mapped != null
                            ? new StunResult(mapped, host, null)
                            : new StunResult(null, host, "响应里没有 XOR-MAPPED-ADDRESS");
                    }
                }
            }
            finally
            {
                raw.ReceiveTimeout = oldTimeout;
            }
            return new StunResult(null, host, "STUN 超时");
        }
        catch (Exception ex)
        {
            return new StunResult(null, host, ex.Message);
        }
    }

    /// <summary>同 socket 问两台服务器,端口一致 = 锥形 NAT(EIM),不一致 = 对称 NAT。</summary>
    public static async Task<(IPEndPoint? Mapped, bool Symmetric, string? Error)> ProbeAsync(
        UdpClient socket, CancellationToken ct = default)
    {
        IPEndPoint? first = null;
        var compared = false;
        foreach (var (host, port) in Servers)
        {
            ct.ThrowIfCancellationRequested();
            var result = await QueryAsync(socket, host, port).ConfigureAwait(false);
            if (result.Mapped == null) continue;
            if (first == null)
            {
                first = result.Mapped;
                continue;
            }
            compared = true;
            var symmetric = first.Port != result.Mapped.Port
                || !first.Address.Equals(result.Mapped.Address);
            if (symmetric) return (first, true, null);
            return (first, false, null);
        }
        if (first != null && !compared)
            return (first, false, null); // 只有一台可达:当锥形处理(无法证伪)
        return (null, false, "所有 STUN 服务器都不可达");
    }

    private static IPEndPoint? ParseXorMapped(byte[] response, int packetLength = int.MaxValue)
    {
        if (response.Length < 20) return null;
        var length = (response[2] << 8) | response[3];
        var pos = 20;
        var end = Math.Min(20 + length, packetLength);
        while (pos + 4 <= end && pos + 4 <= response.Length)
        {
            var attrType = (response[pos] << 8) | response[pos + 1];
            var attrLen = (response[pos + 2] << 8) | response[pos + 3];
            var valuePos = pos + 4;
            if (valuePos + attrLen > response.Length) break;
            if (attrType == 0x0020 && attrLen >= 8) // XOR-MAPPED-ADDRESS
            {
                var family = response[valuePos + 1];
                var xport = ((response[valuePos + 2] << 8) | response[valuePos + 3]) ^ (int)(MagicCookie >> 16);
                if (family == 0x01 && attrLen >= 8)
                {
                    var xaddr = new byte[4];
                    Array.Copy(response, valuePos + 4, xaddr, 0, 4);
                    // cookie 在报文里是网络序 21 12 A4 42,按字节位序 XOR
                    var cookieBytes = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
                    var addr = new byte[4];
                    for (var i = 0; i < 4; i++) addr[i] = (byte)(xaddr[i] ^ cookieBytes[i]);
                    return new IPEndPoint(new IPAddress(addr), xport);
                }
            }
            else if (attrType == 0x0001 && attrLen >= 8) // MAPPED-ADDRESS(旧版兜底)
            {
                var family = response[valuePos + 1];
                if (family == 0x01)
                {
                    var port = (response[valuePos + 2] << 8) | response[valuePos + 3];
                    var addr = new byte[4];
                    Array.Copy(response, valuePos + 4, addr, 0, 4);
                    return new IPEndPoint(new IPAddress(addr), port);
                }
            }
            pos = valuePos + attrLen;
        }
        return null;
    }
}
