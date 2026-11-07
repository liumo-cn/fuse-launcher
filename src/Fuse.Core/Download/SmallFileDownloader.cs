using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Download;

/// <summary>一个小文件下载项：assets 资源、库 jar、log4j 配置等。</summary>
public sealed class FileDownloadItem
{
    public required string Url { get; init; }
    public required string DestPath { get; init; }
    /// <summary>源站提供的校验值（官方为 SHA-1）；null 表示不校验。</summary>
    public string? ExpectedHash { get; init; }
    public HashAlgo Algo { get; init; } = HashAlgo.Sha1;

    /// <summary>
    /// 可缺省:下载失败不算整批失败(例如 Forge 老版本 version.json 里 url 为空、
    /// 但其实是 processors 生成的 jar,镜像上 404 属正常)。
    /// </summary>
    public bool Optional { get; init; }
}

public sealed class SmallFileOptions
{
    /// <summary>并发上限(worker 数)。</summary>
    public int Parallelism { get; set; } = 32;
    /// <summary>启动并发;连续成功后逐步升到上限,失败时回退。</summary>
    public int InitialParallelism { get; set; } = 8;
    public int MaxRetries { get; set; } = 3;
    /// <summary>连接/响应头超时:这么久拿不到首字节就换源。</summary>
    public TimeSpan FirstByteTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>停滞超时:body 连续这么久没有新字节才算卡死;还在收数据就不掐。</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(20);
}

public sealed class SmallFileBatchResult
{
    public int Skipped { get; init; }
    public int Downloaded { get; init; }
    public List<string> Failed { get; init; } = new();
}

/// <summary>
/// 海量小文件高并发下载（FR-01 双模式之二）：已存在且哈希一致即跳过，
/// 临时文件落盘校验后原子替换，失败指数退避重试。
/// </summary>
public sealed class SmallFileDownloader(HttpClient http)
{
    public async Task<SmallFileBatchResult> DownloadSetAsync(
        IEnumerable<FileDownloadItem> items,
        SmallFileOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default)
    {
        var opts = options ?? new SmallFileOptions();
        var list = items as IList<FileDownloadItem> ?? items.ToList();
        var failed = new List<string>();
        var downloaded = 0;
        var skipped = 0;
        var done = 0;
        if (list.Count == 0) return new SmallFileBatchResult();

        // 固定 worker 池 + 动态并发上限:启动 8 个,连续成功后升到 Parallelism,
        // 失败/429 时回退。避免"几千个文件各自 Task.Run 等信号量"的调度开销。
        var queue = new ConcurrentQueue<FileDownloadItem>(list);
        var remaining = list.Count;
        using var limiter = new AdaptiveLimiter(opts.InitialParallelism, opts.Parallelism);
        var workers = Enumerable.Range(0, Math.Max(1, opts.Parallelism)).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!queue.TryDequeue(out var item))
                {
                    if (Volatile.Read(ref remaining) == 0) return;
                    await Task.Delay(20, cancellation).ConfigureAwait(false);
                    continue;
                }

                var ok = false;
                await limiter.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    if (await DownloadOneAsync(item, opts, cancellation).ConfigureAwait(false))
                        Interlocked.Increment(ref downloaded);
                    else
                        Interlocked.Increment(ref skipped);
                    ok = true;
                }
                // 只有调用方真的取消了才终止;HttpClient 超时也是 TaskCanceledException,
                // 那种属于可重试的网络失败(否则一个慢请求会把整批任务判死)
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (!item.Optional) lock (failed) failed.Add(item.DestPath);
                    else ok = true; // 可缺失项 404 不算网络变差,不要因此降并发
                }
                finally
                {
                    limiter.Release();
                    limiter.NoteResult(ok);
                    Interlocked.Decrement(ref remaining);
                    var d = Interlocked.Increment(ref done);
                    progress?.Report(d / (double)list.Count);
                }
            }
        }, cancellation)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        return new SmallFileBatchResult
        {
            Skipped = skipped,
            Downloaded = downloaded,
            Failed = failed,
        };
    }

    /// <summary>
    /// 动态并发闸门:最多 Parallelism 个 worker,实际放行数量随成功/失败调整。
    /// worker 数量固定,等待时只轮询 20ms,不会为几千个文件各起一个任务。
    /// </summary>
    private sealed class AdaptiveLimiter : IDisposable
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _slots;
        private readonly int _max;
        private int _limit;
        private int _inFlight;
        private int _cleanSuccesses;

        public AdaptiveLimiter(int initial, int max)
        {
            _max = Math.Max(1, max);
            _limit = Math.Min(Math.Max(1, initial), _max);
            _slots = new SemaphoreSlim(_max);
        }

        public async Task WaitAsync(CancellationToken ct)
        {
            while (true)
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_inFlight < _limit)
                    {
                        _inFlight++;
                        return;
                    }
                }
                _slots.Release();
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }

        public void Release()
        {
            lock (_sync) _inFlight--;
            _slots.Release();
        }

        public void NoteResult(bool ok)
        {
            lock (_sync)
            {
                if (!ok)
                {
                    _limit = Math.Max(1, _limit / 2);
                    _cleanSuccesses = 0;
                    return;
                }
                _cleanSuccesses++;
                if (_cleanSuccesses >= 8 && _limit < _max)
                {
                    _limit = Math.Min(_max, _limit * 2);
                    _cleanSuccesses = 0;
                }
            }
        }

        public void Dispose() => _slots.Dispose();
    }

    /// <summary>返回 true 表示执行了下载，false 表示本地已是最新。</summary>
    private async Task<bool> DownloadOneAsync(FileDownloadItem item, SmallFileOptions opts, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(item.DestPath))!;
        Directory.CreateDirectory(dir);

        if (File.Exists(item.DestPath))
        {
            var localHash = await Hashing.ComputeFileAsync(item.DestPath, item.Algo, ct).ConfigureAwait(false);
            if (DownloadPlanner.Decide(true, localHash, item.ExpectedHash) == DownloadPlanner.Decision.SkipUpToDate)
                return false;
        }

        var tmpPath = item.DestPath + ".tmp";
        Exception? last = null;
        for (var attempt = 0; attempt <= opts.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // 429 现在会切到官方源(见 UrlMirror.ForAttempt),不必再等 4 秒;
                // 长时间退避会让"几千个小文件"的批量下载整体卡死
                var wasThrottled = last is IOException io && io.Message.StartsWith("429", StringComparison.Ordinal);
                var baseMs = wasThrottled ? 600 : 400;
                await Task.Delay(Math.Min(baseMs * (1 << Math.Min(attempt, 3)), 8000), ct).ConfigureAwait(false);
            }
            try
            {
                // 首字节超时:连上了但服务器不说话就换源;拿到响应头后立刻关掉计时器
                using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                firstCts.CancelAfter(opts.FirstByteTimeout);
                // Balanced 策略:首次走官方,失败后自动转镜像(熔断期内恒官方)
                var attemptUrl = UrlMirror.ForAttempt(item.Url, attempt);
                using var response = await http.GetAsync(attemptUrl, HttpCompletionOption.ResponseHeadersRead, firstCts.Token)
                    .ConfigureAwait(false);
                firstCts.CancelAfter(Timeout.InfiniteTimeSpan);
                if ((int)response.StatusCode == 429)
                {
                    // 镜像限流:触发全局熔断(后续请求改走官方源),再退避重试
                    UrlMirror.NoteThrottled();
                    throw new IOException("429 请求过于频繁");
                }
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using (var target = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    64 * 1024, useAsync: true))
                {
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        // 每读到一块就重置停滞计时;只有真的没数据了才超时
                        stallCts.CancelAfter(opts.StallTimeout);
                        int read;
                        try
                        {
                            read = await stream.ReadAsync(buffer, 0, buffer.Length, stallCts.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new IOException("下载停滞超时");
                        }
                        if (read <= 0) break;
                        await target.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    }
                }
                if (!await Hashing.VerifyFileAsync(tmpPath, item.ExpectedHash, item.Algo, ct).ConfigureAwait(false))
                    throw new IOException("校验不通过：文件可能损坏或被篡改");

                if (File.Exists(item.DestPath)) File.Delete(item.DestPath);
                File.Move(tmpPath, item.DestPath);
                return true;
            }
            // 只有调用方真的取消了才终止;HttpClient 超时也是 TaskCanceledException,
            // 那种属于可重试的网络失败(否则一个慢请求会把整批任务判死)
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
        }
        throw new IOException($"下载 {item.Url} 重试 {opts.MaxRetries} 次后仍失败", last);
    }
}
