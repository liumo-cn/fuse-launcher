using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Fuse.Core.Monitoring;

/// <summary>
/// TCP 连接延迟探测:对 host:port 发起一次完整握手,往返时间即游戏网络延迟的合理近似
/// (ICMP ping 部分主机/防火墙会丢弃,TCP 握手不会被拦,且这才是"游戏实际走到的延迟")。
/// </summary>
public static class PingProbe
{
    /// <summary>测一次 TCP 握手 RTT;失败返回 null(超时/不可达)。</summary>
    public static async Task<int?> MeasureAsync(string host, int port, int timeoutMs = 3000)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        try
        {
            using var client = new TcpClient();
            var start = Stopwatch.GetTimestamp();
            var connect = client.ConnectAsync(host, port);
            var finished = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (finished != connect || !client.Connected) return null;
            return (int)((Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>解析 "host" 或 "host:port"(无端口默认 25565)。</summary>
    public static (string Host, int Port) Split(string address)
    {
        var value = (address ?? "").Trim();
        if (value.Length == 0) return ("", 0);
        var lastColon = value.LastIndexOf(':');
        // IPv6 字面量([::1]:25565 或 ::1)不按冒号切
        if (lastColon > 0 && value.IndexOf(':') == lastColon &&
            int.TryParse(value[(lastColon + 1)..], out var port) && port is > 0 and <= 65535)
            return (value[..lastColon], port);
        return (value, 25565);
    }

    /// <summary>从 servers.dat 记录里挑"自动加入的服务器优先,否则第一条"的地址。</summary>
    public static string? PickAddress(IEnumerable<Versioning.ServerEntry> servers, string? autoJoin)
    {
        var list = (servers ?? Array.Empty<Versioning.ServerEntry>()).ToList();
        if (list.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(autoJoin))
        {
            var match = list.FirstOrDefault(s => s.Ip.Equals(autoJoin, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Ip;
        }
        return list[0].Ip;
    }
}
