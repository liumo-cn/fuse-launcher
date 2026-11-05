using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Download;

/// <summary>校验算法按源站提供选择：Mojang 官方清单为 SHA-1，Modrinth 为 SHA-1/SHA-512。</summary>
public enum HashAlgo
{
    Sha1,
    Sha256,
    Sha512,
}

public static class Hashing
{
    public static HashAlgo Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "sha1" or "sha-1" or "" or null => HashAlgo.Sha1,
        "sha256" or "sha-256" => HashAlgo.Sha256,
        "sha512" or "sha-512" => HashAlgo.Sha512,
        _ => throw new ArgumentException($"不支持的哈希算法：{name}", nameof(name)),
    };

    public static HashAlgorithm Create(HashAlgo algo) => algo switch
    {
        HashAlgo.Sha1 => SHA1.Create(),
        HashAlgo.Sha256 => SHA256.Create(),
        HashAlgo.Sha512 => SHA512.Create(),
        _ => throw new ArgumentOutOfRangeException(nameof(algo)),
    };

    public static string Hex(byte[] hash)
    {
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static async Task<string> ComputeFileAsync(string path, HashAlgo algo, CancellationToken ct = default)
    {
        using var hasher = Create(algo);
        using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            hasher.TransformBlock(buffer, 0, read, null, 0);
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Hex(hasher.Hash!);
    }

    /// <summary>校验通过返回 true；期望值为空视为跳过校验（返回 true）。</summary>
    public static async Task<bool> VerifyFileAsync(string path, string? expectedHash, HashAlgo algo, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedHash)) return true;
        var actual = await ComputeFileAsync(path, algo, ct).ConfigureAwait(false);
        return string.Equals(actual, expectedHash!.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
