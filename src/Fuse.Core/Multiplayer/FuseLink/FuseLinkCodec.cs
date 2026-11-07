using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Fuse.Core.Multiplayer.FuseLink;

/// <summary>一对 fuse-link 候选(放进邀请码的 fl 参数 / 回执码)。双方 blob 结构相同,靠 token 配对。</summary>
public sealed record FuseLinkCandidates(byte[] Token, int UdpPort, IPEndPoint[] Endpoints);

/// <summary>
/// fuse-link 候选 blob 编解码:紧凑二进制 → Base64Url。
/// 无自营服务器,信令=房主把带候选的邀请发给好友,好友回发回执码(微信/QQ 人工中转)。
/// </summary>
public static class FuseLinkCodec
{
    public static string Encode(FuseLinkCandidates candidates)
    {
        var buffer = new List<byte> { 1 }; // version
        buffer.AddRange(candidates.Token);
        buffer.Add((byte)(candidates.UdpPort >> 8));
        buffer.Add((byte)(candidates.UdpPort & 0xFF));
        buffer.Add((byte)candidates.Endpoints.Length);
        foreach (var endpoint in candidates.Endpoints)
        {
            var ip = endpoint.Address;
            var isV4 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
            buffer.Add((byte)(isV4 ? 4 : 6));
            var bytes = ip.GetAddressBytes();
            buffer.AddRange(bytes);
            buffer.Add((byte)(endpoint.Port >> 8));
            buffer.Add((byte)(endpoint.Port & 0xFF));
        }
        return Base64UrlEncode(buffer.ToArray());
    }

    public static FuseLinkCandidates? Decode(string text)
    {
        try
        {
            var data = Base64UrlDecode(text.Trim());
            if (data.Length < 20 || data[0] != 1) return null;
            var token = new byte[16];
            Array.Copy(data, 1, token, 0, 16);
            var port = (data[17] << 8) | data[18];
            var count = data[19];
            var pos = 20;
            var endpoints = new List<IPEndPoint>();
            for (var i = 0; i < count && pos < data.Length; i++)
            {
                var family = data[pos++];
                var size = family == 4 ? 4 : family == 6 ? 16 : 0;
                if (size == 0 || pos + size + 2 > data.Length) return null;
                var addrBytes = new byte[size];
                Array.Copy(data, pos, addrBytes, 0, size);
                pos += size;
                var p = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                endpoints.Add(new IPEndPoint(new IPAddress(addrBytes), p));
            }
            if (endpoints.Count == 0) return null;
            return new FuseLinkCandidates(token, port, endpoints.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string text)
    {
        text = text.Replace('-', '+').Replace('_', '/');
        switch (text.Length % 4)
        {
            case 2: text += "=="; break;
            case 3: text += "="; break;
        }
        return Convert.FromBase64String(text);
    }
}
