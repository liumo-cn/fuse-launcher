using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Multiplayer;

/// <summary>本机的直连通道探测:全局 IPv6 地址与内网 IPv4。</summary>
public static class Ipv6Probe
{
    /// <summary>取所有全局(Global) IPv6 单播地址(不含链路本地 fe80:: / 环回)。</summary>
    public static List<string> GetGlobalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                    if (info.Address.IsIPv6LinkLocal || info.Address.IsIPv6SiteLocal || IPAddress.IPv6Loopback.Equals(info.Address))
                        continue;
                    // ScopeId 会干扰显示,去掉 %后缀
                    var text = info.Address.ToString();
                    var percent = text.IndexOf('%');
                    if (percent > 0) text = text.Substring(0, percent);
                    if (!result.Contains(text)) result.Add(text);
                }
            }
        }
        catch (Exception)
        {
        }
        return result;
    }

    /// <summary>本机内网 IPv4(UPnP 映射目标地址用)。跳过 APIPA(169.254,断网网卡的假地址)。</summary>
    public static string? GetLocalIPv4()
    {
        try
        {
            string? fallback = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.Loopback.Equals(info.Address)) continue;
                    var bytes = info.Address.GetAddressBytes();
                    if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254) continue; // APIPA
                    // 有网关的网卡优先(那是真正连着路由器的)
                    var hasGateway = nic.GetIPProperties().GatewayAddresses.Count > 0;
                    if (hasGateway) return info.Address.ToString();
                    fallback ??= info.Address.ToString();
                }
            }
            return fallback;
        }
        catch (Exception)
        {
        }
        return null;
    }
}

/// <summary>公网出口 IP 探测(按国内可达性排序;HTTP 出口 IP 在 CGNAT 下只是运营商出口,如实提示)。</summary>
public static class PublicIpProbe
{
    private static readonly (string Url, bool NeedsParse)[] Sources =
    {
        ("https://ipv4.icanhazip.com", false),          // Cloudflare,v4-only,国内实测可达
        ("https://ip.3322.net", false),                 // 国内,纯文本 IPv4
        ("http://members.3322.org/dyndns/getip", false), // 国内老牌
        ("https://myip.ipip.net", true),                // 国内,"当前 IP：x.x.x.x 来自于…"
        ("https://api.ipify.org", false),               // 海外兜底
    };

    public static async Task<string?> ProbeAsync(HttpClient http, int timeoutMs = 6000, CancellationToken ct = default)
    {
        foreach (var (url, needsParse) in Sources)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                var text = (await http.GetStringAsync(url).ConfigureAwait(false)).Trim();
                if (needsParse)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,3}(?:\.\d{1,3}){3})");
                    text = m.Success ? m.Groups[1].Value : "";
                }
                if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    return text;
            }
            catch (Exception)
            {
                // 换下一个源
            }
        }
        return null;
    }
}

/// <summary>通道探测结果:开放世界流程的"当前地址 + 走的哪条通道"。</summary>
public sealed record ChannelResult(string Kind, string DisplayName, string Address, string Hint)
{
    public static readonly ChannelResult None = new("none", "未开放", "", "");
}
