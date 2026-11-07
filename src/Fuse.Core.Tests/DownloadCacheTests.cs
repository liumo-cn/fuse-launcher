using System;
using System.IO;
using System.Security.Cryptography;
using Fuse.Core.Download;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>共享对象缓存:按哈希落盘,命中后硬链接/复制到目标。</summary>
public class DownloadCacheTests : IDisposable
{
    private readonly string _root;
    private readonly string? _oldRoot;

    public DownloadCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcdl-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _oldRoot = DownloadCache.Root;
        DownloadCache.Root = _root;
    }

    public void Dispose()
    {
        DownloadCache.Root = _oldRoot;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void StoreThenLinkOrCopy_RoundTripsContent()
    {
        var payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);
        var hash = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
        var source = Path.Combine(_root, "source.bin");
        File.WriteAllBytes(source, payload);

        DownloadCache.Store(source, hash, HashAlgo.Sha1);
        Assert.True(DownloadCache.TryGet(hash, HashAlgo.Sha1, out var cached));
        Assert.Equal(payload, File.ReadAllBytes(cached));

        var dest = Path.Combine(_root, "nested", "dest.bin");
        Assert.True(DownloadCache.LinkOrCopy(cached, dest));
        Assert.Equal(payload, File.ReadAllBytes(dest));
    }

    [Fact]
    public void InvalidHash_IsIgnored()
    {
        Assert.False(DownloadCache.TryGet(null, HashAlgo.Sha1, out _));
        Assert.False(DownloadCache.TryGet("not-a-hash", HashAlgo.Sha1, out _));
        DownloadCache.Store(Path.Combine(_root, "missing.bin"), "xyz", HashAlgo.Sha1);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }
}
