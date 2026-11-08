using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Fuse.Core.Download;

/// <summary>分块断点下载器状态，与 .part 文件同目录、后缀 .chunks.json。</summary>
public sealed class ChunkState
{
    public long TotalSize { get; set; }
    public long ChunkBytes { get; set; }
    public string? ExpectedSha1 { get; set; }
    public List<int> Completed { get; set; } = new();
}

public sealed class ChunkedDownloadOptions
{
    public int Parallelism { get; set; } = 4;
    /// <summary>0 = 按文件大小自动选(小文件 512KB,大文件 2MB)。</summary>
    public long ChunkBytes { get; set; }
    public int MaxRetriesPerChunk { get; set; } = 4;
    /// <summary>校验算法;默认 SHA-1(MC 官方文件全部是 SHA-1),用户下载可传 SHA-256。</summary>
    public HashAlgo Algo { get; set; } = HashAlgo.Sha1;
}

public sealed class ChunkedDownloadResult
{
    public string DestPath { get; init; } = "";
    public long TotalBytes { get; init; }
    public int ChunksResumed { get; init; }
    public int ChunksDownloaded { get; init; }
    public bool HashVerified { get; init; }
}

/// <summary>
/// 大文件分块下载（FR-01）：Range 并发 + .chunks.json 断点状态 + SHA-1 校验。
/// 服务器不支持 Range 时退化为单流下载（无断点）。
/// </summary>
public sealed class ChunkedDownloader(HttpClient http)
{
    public async Task<ChunkedDownloadResult> DownloadAsync(
        string url,
        string destPath,
        string? expectedSha1 = null,
        ChunkedDownloadOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default)
    {
        var opts = options ?? new ChunkedDownloadOptions();
        var dir = Path.GetDirectoryName(Path.GetFullPath(destPath))!;
        Directory.CreateDirectory(dir);

        // 已存在且哈希一致:直接跳过(重复导入清单/重跑任务时不用再下一遍)
        if (File.Exists(destPath) && !string.IsNullOrEmpty(expectedSha1) &&
            await Hashing.VerifyFileAsync(destPath, expectedSha1, opts.Algo, cancellation)
                .ConfigureAwait(false))
        {
            // 顺手把旧目录里已有的文件挂进共享缓存,别的游戏目录就能直接复用
            DownloadCache.Store(destPath, expectedSha1, opts.Algo);
            progress?.Report(1.0);
            return new ChunkedDownloadResult
            {
                DestPath = destPath,
                TotalBytes = new FileInfo(destPath).Length,
                HashVerified = true,
            };
        }

        // 别的游戏目录已经下过同一个哈希:直接硬链接/复制过来,大文件也不用重下
        if (DownloadCache.TryGet(expectedSha1, opts.Algo, out var cached) &&
            DownloadCache.LinkOrCopy(cached, destPath))
        {
            progress?.Report(1.0);
            return new ChunkedDownloadResult
            {
                DestPath = destPath,
                TotalBytes = new FileInfo(destPath).Length,
                HashVerified = true,
            };
        }

        var partPath = destPath + ".part";
        var statePath = destPath + ".chunks.json";
        var sourceOffset = 0;
        for (var round = 0; ; round++)
        {
            try
            {
                return await DownloadOnceAsync(url, destPath, partPath, statePath,
                    expectedSha1, opts, progress, cancellation, sourceOffset).ConfigureAwait(false);
            }
            catch (HashMismatchException)
            {
                // 校验失败说明这份数据不能用:先清断点,否则下次会认为所有块都完成了
                TryDeleteFile(statePath);
                TryDeleteFile(partPath);
                if (round > 0 || !HasAlternativeSource(url, sourceOffset)) throw;
                // 分块没有独立哈希定位不到坏块 换下一个源整份重下
                sourceOffset++;
                progress?.Report(0);
            }
        }
    }

    /// <summary>一轮完整下载;哈希不匹配时抛 <see cref="HashMismatchException"/>,由外层换源重来。</summary>
    private async Task<ChunkedDownloadResult> DownloadOnceAsync(
        string url, string destPath, string partPath, string statePath,
        string? expectedSha1, ChunkedDownloadOptions opts,
        IProgress<double>? progress, CancellationToken cancellation, int sourceOffset)
    {
        var (total, supportsRange) = await ProbeAsync(url, cancellation, sourceOffset).ConfigureAwait(false);
        if (!supportsRange)
        {
            await DownloadSingleStreamAsync(url, partPath, progress, cancellation, sourceOffset).ConfigureAwait(false);
            await VerifyOrThrowAsync(partPath, expectedSha1, opts.Algo, cancellation).ConfigureAwait(false);
            DownloadCache.Store(partPath, expectedSha1, opts.Algo);
            ReplaceFile(partPath, destPath);
            return new ChunkedDownloadResult { DestPath = destPath, TotalBytes = total, HashVerified = true };
        }

        var chunkBytes = opts.ChunkBytes > 0 ? opts.ChunkBytes : ChunkPlanner.ChooseChunkBytes(total);
        var allChunks = ChunkPlanner.Plan(total, chunkBytes);
        var state = LoadState(statePath, total, chunkBytes, expectedSha1);
        var completed = new HashSet<int>(state.Completed.Where(i => i >= 0 && i < allChunks.Count));

        // 复用已有 .part（断点）；尺寸不一致则重置
        var reuse = File.Exists(partPath) && new FileInfo(partPath).Length == total;
        if (!reuse)
        {
            using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
                fs.SetLength(total);
            completed.Clear();
            state.Completed.Clear();
        }

        var pending = new List<(int Index, ByteRange Chunk)>();
        for (var i = 0; i < allChunks.Count; i++)
            if (!completed.Contains(i))
                pending.Add((i, allChunks[i]));
        var resumed = allChunks.Count - pending.Count;
        var doneBytes = total - pending.Sum(p => p.Chunk.Length);
        var report = new Action(() => progress?.Report(Volatile.Read(ref doneBytes) / (double)Math.Max(1, total)));
        report();

        var stateLock = new object();
        // 并发随文件大小自适应:小文件(<=8MB)用 2 线程足够,再高只有调度开销;
        // 大文件拉满配置并发,配合 Range 分段把带宽吃满(实测 MC 官方 CDN 16 并发可到 2MB/s+)
        // 并发也按文件大小分档:小文件 2 路够了,中等/大文件再拉满
        var effectiveParallelism = total <= 4 * 1024 * 1024
            ? Math.Min(opts.Parallelism, 2)
            : total <= 64 * 1024 * 1024
                ? Math.Min(opts.Parallelism, 4)
                : opts.Parallelism;
        using (var gate = new SemaphoreSlim(Math.Max(1, effectiveParallelism)))
        {
            var workers = pending.Select(item => Task.Run(async () =>
            {
                await gate.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    await DownloadChunkAsync(http, url, partPath, item.Chunk, opts.MaxRetriesPerChunk,
                        sourceOffset, cancellation).ConfigureAwait(false);
                    lock (stateLock)
                    {
                        state.Completed.Add(item.Index);
                        WriteState(statePath, state);
                    }
                    Interlocked.Add(ref doneBytes, item.Chunk.Length);
                    report();
                }
                finally
                {
                    gate.Release();
                }
            }, cancellation));

            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellation.IsCancellationRequested)
            {
                // 分块在镜像上不稳定(实测 8MB 块并发会超时/被限流):丢弃断点,退回单流全量下载。
                // 镜像的全量 GET 反而更快(17.5MB / 6s),这条兜底比死磕 Range 可靠。
                TryDeleteFile(statePath);
                TryDeleteFile(partPath);
                await DownloadSingleStreamAsync(url, partPath, progress, cancellation, sourceOffset + 1).ConfigureAwait(false);
                await VerifyOrThrowAsync(partPath, expectedSha1, opts.Algo, cancellation).ConfigureAwait(false);
                DownloadCache.Store(partPath, expectedSha1, opts.Algo);
                ReplaceFile(partPath, destPath);
                return new ChunkedDownloadResult
                {
                    DestPath = destPath,
                    TotalBytes = total,
                    HashVerified = true,
                };
            }
        }

        await VerifyOrThrowAsync(partPath, expectedSha1, opts.Algo, cancellation).ConfigureAwait(false);
        File.Delete(statePath);
        DownloadCache.Store(partPath, expectedSha1, opts.Algo);
        ReplaceFile(partPath, destPath);

        return new ChunkedDownloadResult
        {
            DestPath = destPath,
            TotalBytes = total,
            ChunksResumed = resumed,
            ChunksDownloaded = pending.Count,
            HashVerified = true,
        };
    }

    /// <summary>当前 attempt 和下一个 attempt 是否指向不同地址(普通 URL 没有备用源)。</summary>
    private static bool HasAlternativeSource(string url, int sourceOffset)
    {
        var current = UrlMirror.ForAttempt(url, sourceOffset);
        var next = UrlMirror.ForAttempt(url, sourceOffset + 1);
        return !string.Equals(current, next, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 探测文件大小与 Range 支持。**必须带重试**:镜像限流(429)时这里是最先撞上的地方,
    /// 裸请求会让整个大文件下载在第一步就失败(用户看到的"原版也下不了"就是这条)。
    /// </summary>
    private async Task<(long Total, bool SupportsRange)> ProbeAsync(string url, CancellationToken ct, int sourceOffset)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(Math.Min(1500 * attempt, 8000), ct).ConfigureAwait(false);
            try
            {
                var attemptUrl = UrlMirror.ForAttempt(url, sourceOffset + attempt);
                using var request = new HttpRequestMessage(HttpMethod.Get, attemptUrl);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                var status = (int)response.StatusCode;
                if (status == 429)
                {
                    UrlMirror.NoteThrottled();
                    last = new IOException("429 请求过于频繁");
                    continue;
                }
                if (status >= 400 && status != 408)
                {
                    // 有些 CDN(如 CurseForge edge)对带 Range 的 GET 直接 404,普通 GET 才 302。
                    // 先在同一个源上不带 Range 试一次;能拿到长度就退化为单流下载。
                    if (attempt == 0)
                    {
                        try
                        {
                            using var plain = new HttpRequestMessage(HttpMethod.Get, attemptUrl);
                            using var plainResponse = await http.SendAsync(
                                plain, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                            if ((int)plainResponse.StatusCode < 400 &&
                                plainResponse.Content.Headers.ContentLength.HasValue)
                                return (plainResponse.Content.Headers.ContentLength.Value, false);
                        }
                        catch (Exception ex)
                        {
                            last = ex;
                        }
                    }

                    // 同一个 URL 上重试 403/404 没意义(403 的响应体还往往只有 1 字节,
                    // 继续读会被当成“文件大小 1 字节”);但 attempt 本身会换源
                    // (官方↔镜像 / BMCL↔USTC↔QLU),所以只要下一个 attempt 指向别的地址就继续。
                    last = new IOException($"服务器返回 HTTP {status}（{response.ReasonPhrase}）");
                    var nextUrl = attempt < 3 ? UrlMirror.ForAttempt(url, sourceOffset + attempt + 1) : null;
                    if (nextUrl != null && !string.Equals(nextUrl, attemptUrl, StringComparison.OrdinalIgnoreCase))
                        continue;
                    break;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    var contentRange = response.Content.Headers.ContentRange;
                    if (contentRange?.Length.HasValue == true)
                        return (contentRange.Length.Value, true);
                }
                if (response.Content.Headers.ContentLength.HasValue)
                    return (response.Content.Headers.ContentLength.Value, false);
                return (-1, false);
            }
            // HttpClient 超时同样抛 TaskCanceledException,只有调用方真的取消才终止
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                last = new IOException("分块下载超时", ex);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (last is IOException io && io.Message.StartsWith("服务器返回 HTTP ", StringComparison.Ordinal))
            throw io;
        throw new IOException("探测文件大小失败(重试 3 次)", last);
    }

    private static async Task DownloadChunkAsync(
        HttpClient http, string url, string partPath, ByteRange chunk, int maxRetries,
        int sourceOffset, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var throttled = last is IOException io && io.Message.StartsWith("429", StringComparison.Ordinal);
                await Task.Delay(Math.Min((throttled ? 4000 : 1000) * attempt, throttled ? 20000 : 5000), ct)
                    .ConfigureAwait(false);
            }
            try
            {
                // 单块超时:防止连接建立后服务端不再发数据,流读取永远挂住(实测 Mojang 分块偶发)
                using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                chunkCts.CancelAfter(TimeSpan.FromSeconds(120));
                var chunkToken = chunkCts.Token;

                // Balanced:首次官方,失败后转镜像(熔断期内恒官方)
                var attemptUrl = UrlMirror.ForAttempt(url, sourceOffset + attempt);
                using var request = new HttpRequestMessage(HttpMethod.Get, attemptUrl);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                    chunk.Offset, chunk.Offset + chunk.Length - 1);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, chunkToken)
                    .ConfigureAwait(false);
                if ((int)response.StatusCode == 429)
                {
                    UrlMirror.NoteThrottled();
                    throw new IOException("429 请求过于频繁");
                }
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var target = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.Read,
                    64 * 1024, useAsync: true);
                target.Seek(chunk.Offset, SeekOrigin.Begin);
                await stream.CopyToAsync(target, 64 * 1024, chunkToken).ConfigureAwait(false);

                var written = target.Position - chunk.Offset;
                if (written != chunk.Length)
                    throw new IOException($"分块 {chunk.Offset} 字节数不符：期望 {chunk.Length}，实际 {written}");
                return;
            }
            // HttpClient 超时同样抛 TaskCanceledException,只有调用方真的取消才终止
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                last = new IOException("分块下载超时", ex);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new IOException($"分块 {chunk.Offset}+{chunk.Length} 重试 {maxRetries} 次后仍失败", last);
    }

    private async Task DownloadSingleStreamAsync(
        string url, string partPath, IProgress<double>? progress, CancellationToken ct, int sourceOffset)
    {
        using var response = await http.GetAsync(UrlMirror.ForAttempt(url, sourceOffset), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        // 256KB 缓冲:单流回退时 64KB 缓冲在高带宽链路上会成为 CPU 拷贝瓶颈
        using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true);
        var buffer = new byte[256 * 1024];
        long done = 0;
        int read;
        var lastReport = DateTime.MinValue;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
            done += read;
            // 高速链路上每秒报告一次即可:每次 Report 都跨线程投递,高频投递会拖慢拷贝循环
            var now = DateTime.UtcNow;
            if (total > 0 && (now - lastReport).TotalMilliseconds >= 250)
            {
                lastReport = now;
                progress?.Report(done / (double)total);
            }
        }
        if (total > 0) progress?.Report(1.0);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ChunkState LoadState(string statePath, long total, long chunkBytes, string? expectedSha1)
    {
        try
        {
            if (File.Exists(statePath))
            {
                var state = JsonConvert.DeserializeObject<ChunkState>(File.ReadAllText(statePath));
                if (state != null && state.TotalSize == total && state.ChunkBytes == chunkBytes
                    && string.Equals(state.ExpectedSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
                    return state;
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
            // 状态文件损坏即作废，全部重下
        }
        return new ChunkState { TotalSize = total, ChunkBytes = chunkBytes, ExpectedSha1 = expectedSha1 };
    }

    private static void WriteState(string statePath, ChunkState state)
    {
        var tmp = statePath + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(state));
        ReplaceFile(tmp, statePath);
    }

    /// <summary>netstandard2.0 没有 File.Move(src,dst,true)，用删后拷贝替代。</summary>
    private static void ReplaceFile(string src, string dst)
    {
        if (File.Exists(dst)) File.Delete(dst);
        File.Copy(src, dst, true);
        if (src != dst) File.Delete(src);
    }

    private static async Task VerifyOrThrowAsync(string path, string? expectedSha1, HashAlgo algo, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedSha1)) return;
        if (!await Hashing.VerifyFileAsync(path, expectedSha1, algo, ct).ConfigureAwait(false))
            throw new HashMismatchException("校验不通过：文件可能损坏或被篡改（" + algo + " 不匹配）");
    }

    /// <summary>整文件哈希不匹配。数据不能用,但换源重下可能成功。</summary>
    private sealed class HashMismatchException : IOException
    {
        public HashMismatchException(string message) : base(message)
        {
        }
    }
}
