using System;
using System.Security.Cryptography;

namespace Fuse.Core.Accounts;

/// <summary>
/// 离线档案：仅凭用户名生成确定性 UUID（与主流启动器互认的通行算法，
/// 即 Java UUID.nameUUIDFromBytes("OfflinePlayer:" + name) 的 MD5 v3 形式）。
/// 不存储任何凭证。
/// </summary>
public static class OfflineAccount
{
    public static Guid CreateUuid(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            throw new ArgumentException("用户名不能为空", nameof(playerName));

        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName.Trim()));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30); // version 3（基于 MD5 的名字空间 UUID）
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // IETF 变体

        // Minecraft 认的是 RFC 4122 大端字符串形式；new Guid(byte[]) 前三组按 Windows
        // 小端解释，直接构造会得到字节序相反的字符串，故按 RFC 顺序拼串再解析（可往返）
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        var hex = sb.ToString();
        return Guid.Parse($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}");
    }
}

/// <summary>账号产物：启动管线的登录节点输出。</summary>
public sealed record AuthResult(string PlayerName, Guid PlayerUuid, string AccessToken, string UserType)
{
    public static AuthResult Offline(string playerName)
        => new(playerName.Trim(), OfflineAccount.CreateUuid(playerName), "0", "legacy");
}
