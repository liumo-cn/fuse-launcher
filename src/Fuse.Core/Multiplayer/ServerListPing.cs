using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Multiplayer;

/// <summary>SLP(Server List Ping) 结果:房间卡显示用。</summary>
public sealed record McServerStatus(
    int? Protocol, string? VersionName, int PlayersOnline, int PlayersMax,
    string? Motd, long PingMs, string? FaviconBase64);

/// <summary>
/// Minecraft Server List Ping(1.7+ 协议):握手 → status 请求 → 读 JSON。
/// 用于房间卡预检(在线人数/版本/MOTD/延迟),好友进服前确认"服务器还开着"。
/// </summary>
public static class ServerListPing
{
    /// <summary>假协议版本 -1:服务器按自己版本回复,兼容所有版本。</summary>
    public static async Task<McServerStatus> PingAsync(
        string host, int port, int timeoutMs = 4000, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        await client.ConnectAsync(host, port).ConfigureAwait(false);
        client.NoDelay = true;
        client.ReceiveTimeout = timeoutMs;
        client.SendTimeout = timeoutMs;

        using var stream = client.GetStream();
        using var reg = timeoutCts.Token.Register(() => { try { client.Close(); } catch { } });

        // 握手包: protocol=-1, host, port(大端), next state=1(status)
        var hostBytes = Encoding.UTF8.GetBytes(host);
        var handshake = Concat(
            VarInt(-1),
            VarInt(hostBytes.Length), hostBytes,
            new[] { (byte)(port >> 8), (byte)(port & 0xFF) },
            VarInt(1));
        WritePacket(stream, Concat(VarInt(0x00), handshake));
        // status request
        WritePacket(stream, Concat(VarInt(0x00)));

        var body = ReadPacket(stream, 65536, timeoutCts.Token);
        var pos = 0;
        var packetId = ReadVarInt(body, ref pos);
        if (packetId != 0x00) throw new InvalidOperationException("SLP 响应包 id 异常: " + packetId);
        var jsonLen = ReadVarInt(body, ref pos);
        var json = Encoding.UTF8.GetString(body, pos, Math.Min(jsonLen, body.Length - pos));
        sw.Stop();

        return ParseStatus(json, sw.ElapsedMilliseconds);
    }

    public static McServerStatus ParseStatus(string json, long pingMs)
    {
        var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
        var version = obj["version"] as Newtonsoft.Json.Linq.JObject;
        var players = obj["players"] as Newtonsoft.Json.Linq.JObject;
        var desc = obj["description"];
        // 注意:netstandard2.0 下 JObject.Value<T>(string) 扩展解析不稳(踩过),
        // 统一用 JToken 的显式转换运算符。
        var protocol = version != null && version["protocol"] != null ? (int?)version["protocol"] : null;
        var versionName = version != null && version["name"] != null ? (string)version["name"] : null;
        var online = players != null && players["online"] != null ? (int)players["online"] : 0;
        var max = players != null && players["max"] != null ? (int)players["max"] : 0;
        var favicon = obj["favicon"] != null ? (string)obj["favicon"] : null;
        return new McServerStatus(protocol, versionName, online, max, DescribeMotd(desc), pingMs, favicon);
    }

    /// <summary>description 可能是字符串或 chat 组件树,取纯文本。</summary>
    private static string? DescribeMotd(Newtonsoft.Json.Linq.JToken? desc)
    {
        if (desc == null) return null;
        if (desc.Type == Newtonsoft.Json.Linq.JTokenType.String) return (string)desc;
        var sb = new StringBuilder();
        CollectText(desc, sb);
        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? null : text;
    }

    private static void CollectText(Newtonsoft.Json.Linq.JToken node, StringBuilder sb)
    {
        if (node.Type == Newtonsoft.Json.Linq.JTokenType.String)
        {
            sb.Append((string)node);
            return;
        }
        if (node is not Newtonsoft.Json.Linq.JObject obj) return;
        // chat 组件:先渲染自身 text,再渲染 extra 子项
        var t = obj["text"];
        if (t != null && t.Type == Newtonsoft.Json.Linq.JTokenType.String)
            sb.Append((string)t);
        var extra = obj["extra"];
        if (extra is Newtonsoft.Json.Linq.JArray arr)
            foreach (var item in arr) CollectText(item, sb);
    }

    // ---------- 包格式 ----------

    public static byte[] VarInt(int value)
    {
        // 与 Java 一致:zigzag 后 7-bit 分组
        uint v = (uint)value; // MC VarInt = 无符号 LEB128,不是 protobuf 的 zigzag
        var buffer = new byte[5];
        var i = 0;
        while ((v & ~0x7Fu) != 0)
        {
            buffer[i++] = (byte)((v & 0x7F) | 0x80);
            v >>= 7;
        }
        buffer[i++] = (byte)v;
        var result = new byte[i];
        Array.Copy(buffer, result, i);
        return result;
    }

    public static int ReadVarInt(byte[] buffer, ref int pos)
    {
        uint result = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= buffer.Length) throw new EndOfStreamException("VarInt 越界");
            var b = buffer[pos++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) throw new InvalidDataException("VarInt 过长");
        }
        return unchecked((int)result);
    }

    private static void WritePacket(Stream stream, byte[] payload)
    {
        var len = VarInt(payload.Length);
        stream.Write(len, 0, len.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    private static byte[] ReadPacket(Stream stream, int maxLen, CancellationToken ct)
    {
        var header = new byte[5];
        var headerLen = 0;
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException("服务器关闭了连接");
            header[headerLen++] = (byte)b;
            if ((b & 0x80) == 0) break;
            if (headerLen >= 5) throw new InvalidDataException("包长度异常");
        }
        var pos = 0;
        var len = ReadVarInt(header, ref pos);
        if (len < 0 || len > maxLen) throw new InvalidDataException("包长度超限: " + len);
        var body = new byte[len];
        var offset = 0;
        while (offset < len)
        {
            var n = stream.Read(body, offset, len - offset);
            if (n <= 0) throw new EndOfStreamException("读包中途断开");
            offset += n;
        }
        return body;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        var offset = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }
}
