using System;
using System.Collections.Generic;

namespace Fuse.Core.Download;

/// <summary>一个字节区间 [Offset, Offset + Length)。</summary>
public readonly record struct ByteRange(long Offset, long Length);

/// <summary>
/// 把文件切分为下载分块（纯逻辑，无 IO）：前端并发按块拉 Range 请求；
/// FR-01 大文件模式（客户端 jar、Java 运行时）。
/// </summary>
public static class ChunkPlanner
{
    /// <summary>
    /// 分块大小。8MB 块在多并发下容易被镜像限流/超时,2MB 更稳且断点粒度更细;
    /// 真正的兜底是 ChunkedDownloader 的"分块失败转单流"。
    /// </summary>
    public const long DefaultChunkBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 按文件大小选块:小文件 512KB(断点更细、并发更均匀),
    /// 中等 1MB,大文件回到 2MB(少发请求、少被镜像限流)。
    /// </summary>
    public static long ChooseChunkBytes(long total)
    {
        if (total <= 16L * 1024 * 1024) return 512 * 1024;
        if (total <= 64L * 1024 * 1024) return 1024 * 1024;
        return DefaultChunkBytes;
    }

    /// <summary>切满整个文件，不重叠、无缝隙；total 为 0 时返回空。</summary>
    public static IReadOnlyList<ByteRange> Plan(long total, long chunkBytes = DefaultChunkBytes)
    {
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
        if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
        if (total == 0) return Array.Empty<ByteRange>();

        var chunks = new List<ByteRange>((int)Math.Min((total + chunkBytes - 1) / chunkBytes, 4096));
        for (long offset = 0; offset < total; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, total - offset);
            chunks.Add(new ByteRange(offset, length));
        }
        return chunks;
    }

    /// <summary>由已完成块索引推导仍需下载的块（断点续传）。</summary>
    public static IReadOnlyList<ByteRange> Remaining(IReadOnlyList<ByteRange> all, ISet<int> completedIndexes)
    {
        var remain = new List<ByteRange>();
        for (var i = 0; i < all.Count; i++)
            if (!completedIndexes.Contains(i))
                remain.Add(all[i]);
        return remain;
    }
}

/// <summary>本地文件与期望哈希的判定（纯逻辑）：决定跳过 / 需要下载。</summary>
public static class DownloadPlanner
{
    public enum Decision { SkipUpToDate, NeedsDownload }

    public static Decision Decide(bool localExists, string? localHash, string? expectedHash)
    {
        // 无期望哈希时无法校验：存在即信任（调用方应尽量提供哈希）
        if (string.IsNullOrEmpty(expectedHash))
            return localExists ? Decision.SkipUpToDate : Decision.NeedsDownload;
        if (!localExists || string.IsNullOrEmpty(localHash))
            return Decision.NeedsDownload;
        return string.Equals(localHash, expectedHash!.Trim(), StringComparison.OrdinalIgnoreCase)
            ? Decision.SkipUpToDate
            : Decision.NeedsDownload;
    }
}
