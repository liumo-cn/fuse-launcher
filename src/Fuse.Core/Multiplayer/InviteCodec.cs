using System;
using System.Collections.Generic;
using System.Globalization;

namespace Fuse.Core.Multiplayer;

/// <summary>一条联机邀请(房间信息 + 服务器地址 + 可选 fuse-link P2P 候选)。</summary>
public sealed record InviteInfo(
    string Address, string Host, int Port, string? RoomName, string? GameVersion, string? Token,
    string? FuseLinkCandidates = null);

/// <summary>
/// 联机邀请码:生成/解析 fuse-launcher://join?... 深度链接。
/// 地址部分做白名单校验(防命令行注入),端口限 1..65535。
/// </summary>
public static class InviteCodec
{
    public const string Scheme = "fuse-launcher";

    /// <summary>host 部分只允许域名/IPv4/IPv6 字符,绝不放行参数注入字符。</summary>
    private static readonly System.Text.RegularExpressions.Regex HostRegex =
        new("^[0-9a-zA-Z.\\-\\[\\]:]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Build(string address, string? roomName = null, string? gameVersion = null, string? token = null,
        string? fuseLinkCandidates = null)
    {
        var (host, port) = SplitAddress(address);
        if (!IsHostValid(host) || port is < 1 or > 65535)
            throw new ArgumentException("无效的服务器地址: " + address);
        var sb = new System.Text.StringBuilder(Scheme);
        sb.Append("://join?v=1&addr=").Append(Encode(host + ":" + port.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(roomName)) sb.Append("&name=").Append(Encode(roomName));
        if (!string.IsNullOrEmpty(gameVersion)) sb.Append("&mc=").Append(Encode(gameVersion));
        if (!string.IsNullOrEmpty(token)) sb.Append("&t=").Append(Encode(token));
        if (!string.IsNullOrEmpty(fuseLinkCandidates)) sb.Append("&fl=").Append(Encode(fuseLinkCandidates));
        return sb.ToString();
    }

    /// <summary>解析邀请链接或裸地址(兜底:用户直接粘贴 IP:端口)。</summary>
    public static InviteInfo? Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        if (input.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(input);
            if (!uri.Host.Equals("join", StringComparison.OrdinalIgnoreCase)) return null;
            var query = ParseQuery(uri.Query);
            if (!query.TryGetValue("addr", out var addr)) return null;
            return BuildInfo(addr, query.TryGetValue("name", out var n) ? n : null,
                query.TryGetValue("mc", out var mc) ? mc : null,
                query.TryGetValue("t", out var t) ? t : null,
                query.TryGetValue("fl", out var fl) ? fl : null);
        }

        // 裸地址(允许带前缀说明,取最后一段看起来像地址的)
        var candidate = ExtractAddress(input);
        return candidate == null ? null : BuildInfo(candidate, null, null, null, null);
    }

    private static InviteInfo? BuildInfo(string addr, string? name, string? mc, string? token, string? fuseLink)
    {
        string host;
        int port;
        try
        {
            (host, port) = SplitAddress(addr);
        }
        catch (FormatException)
        {
            return null;
        }
        if (!IsHostValid(host) || port is < 1 or > 65535) return null;
        // IPv6 展示地址要带方括号(游戏里加服也认这个格式)
        var display = host.IndexOf(':') >= 0 ? "[" + host + "]:" + port : host + ":" + port;
        return new InviteInfo(display, host, port,
            ClampName(name), ClampName(mc), token, ClampBlob(fuseLink));
    }

    /// <summary>拆 host:port;IPv6 带 [..] 方括号。缺端口时回退 25565(裸 IP 场景)。</summary>
    public static (string Host, int Port) SplitAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith("["))
        {
            var close = address.IndexOf(']');
            if (close < 0) throw new FormatException("IPv6 地址缺少右方括号");
            var host = address[1..close];
            var rest = address[(close + 1)..];
            var port = rest.StartsWith(":") ? int.Parse(rest[1..], CultureInfo.InvariantCulture) : 25565;
            return (host, port);
        }
        var colon = address.LastIndexOf(':');
        if (colon >= 0 && int.TryParse(address[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p))
            return (address.Substring(0, colon), p);
        return (address, 25565);
    }

    /// <summary>自由文本里抓出第一段像地址的东西(用户会把整句聊天一起贴进来)。</summary>
    public static string? ExtractAddress(string text)
    {
        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex("\\[([0-9a-fA-F:]+)\\]:(\\d+)"),
            new System.Text.RegularExpressions.Regex("\\b([0-9a-zA-Z][0-9a-zA-Z.\\-]*):([0-9]{2,5})\\b"),
        };
        foreach (var regex in patterns)
        {
            var m = regex.Match(text);
            if (m.Success) return m.Groups[1].Value + ":" + m.Groups[2].Value;
        }
        return null;
    }

    private static bool IsHostValid(string host) =>
        host.Length > 0 && host.Length <= 253 && HostRegex.IsMatch(host);

    private static string? ClampName(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        value = value.Trim();
        return value.Length == 0 ? null : (value.Length > 60 ? value.Substring(0, 60) : value);
    }

    /// <summary>fuse-link 候选 blob 可能上百字符,限制大小防滥用。</summary>
    private static string? ClampBlob(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        value = value.Trim();
        if (value.Length == 0 || value.Length > 1024) return null;
        return value;
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var kv = part.Split(new[] { '=' }, 2);
            if (kv.Length != 2 || kv[0].Length == 0) continue;
            result[kv[0]] = Uri.UnescapeDataString(kv[1]);
        }
        return result;
    }
}
