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

    /// <summary>文件大小（字节，0 = 未知）。已知时进度按字节算，否则退回按个数。</summary>
    public long Size { get; init; }

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
        if (list.Count == 0) return new SmallFileBatchResult();

        // 预扫:只有真正要下载的文件才计入进度。已是最新、或能从共享缓存补上的先剔掉,
        // 否则重装时几个秒过的文件就能把进度条推到 90%,真正的大文件还没开始下。
        var upToDate = new bool[list.Count];
        var scanSlots = new ConcurrentQueue<int>(Enumerable.Range(0, list.Count));
        var scanLeft = list.Count;
        var scanWorkers = Enumerable.Range(0, Math.Max(1, Math.Min(opts.InitialParallelism, list.Count)))
            .Select(_ => Task.Run(async () =>
            {
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!scanSlots.TryDequeue(out var index))
                    {
                        if (Volatile.Read(ref scanLeft) == 0) return;
                        await Task.Delay(10, cancellation).ConfigureAwait(false);
                        continue;
                    }
                    try
                    {
                        upToDate[index] = await IsUpToDateAsync(list[index], cancellation).ConfigureAwait(false);
                    }
                    // 读不了/被占用当作需要下载,交给下载线程按正常流程报错
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        upToDate[index] = false;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref scanLeft);
                    }
                }
            }, cancellation)).ToArray();
        await Task.WhenAll(scanWorkers).ConfigureAwait(false);

        var pending = new List<FileDownloadItem>(list.Count);
        var skipped = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (upToDate[i]) skipped++;
            else pending.Add(list[i]);
        }
        if (pending.Count == 0) return new SmallFileBatchResult { Skipped = skipped };

        var failed = new List<string>();
        var downloaded = 0;
        var done = 0;

        // 进度权重:文件大小都已知时按字节,否则退回按个数。加载器库的 maven 元数据
        // 没有 size;assets / 原版库 / Forge 库 / 整合包清单都带,能拿到就按真实字节算。
        var byteProgress = pending.All(i => i.Size > 0);
        var totalWeight = byteProgress ? pending.Sum(i => i.Size) : pending.Count;
        long doneWeight = 0;

        // 固定 worker 池 + 动态并发上限:启动 8 个,连续成功后升到 Parallelism,
        // 失败/429 时回退。避免"几千个文件各自 Task.Run 等信号量"的调度开销。
        var queue = new ConcurrentQueue<FileDownloadItem>(pending);
        var remaining = pending.Count;
        var lastReport = DateTime.MinValue;
        var reportLock = new object();
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
                    await DownloadOneAsync(item, opts, cancellation).ConfigureAwait(false);
                    Interlocked.Increment(ref downloaded);
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
                    var weight = byteProgress ? item.Size : 1;
                    var w = Interlocked.Add(ref doneWeight, weight);
                    if (progress != null)
                    {
                        lock (reportLock)
                        {
                            var now = DateTime.UtcNow;
                            // 100ms 节流:几千个小文件逐个 Report 会把 UI 线程淹掉
                            if (d == pending.Count || (now - lastReport).TotalMilliseconds >= 100)
                            {
                                lastReport = now;
                                progress.Report(Math.Min(1.0, w / (double)Math.Max(1, totalWeight)));
                            }
                        }
                    }
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

    /// <summary>本地已是最新，或别的游戏目录已经下过同一个哈希（硬链接/复制补上）时返回 true。</summary>
    private static async Task<bool> IsUpToDateAsync(FileDownloadItem item, CancellationToken ct)
    {
        if (File.Exists(item.DestPath))
        {
            // 没有期望哈希时"存在即信任",不用再读一遍文件
            if (string.IsNullOrEmpty(item.ExpectedHash)) return true;
            var localHash = await Hashing.ComputeFileAsync(item.DestPath, item.Algo, ct).ConfigureAwait(false);
            if (DownloadPlanner.Decide(true, localHash, item.ExpectedHash) == DownloadPlanner.Decision.SkipUpToDate)
            {
                // 顺手挂进共享缓存:再装第二个游戏目录时,这批库/assets/模组直接硬链接过去,不用重下
                DownloadCache.Store(item.DestPath, item.ExpectedHash, item.Algo);
                return true;
            }
        }

        return DownloadCache.TryGet(item.ExpectedHash, item.Algo, out var cached) &&
               DownloadCache.LinkOrCopy(cached, item.DestPath);
    }

    private async Task DownloadOneAsync(FileDownloadItem item, SmallFileOptions opts, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(item.DestPath))!;
        Directory.CreateDirectory(dir);

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
                using (var target = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    64 * 1024, useAsync: true))
                {
                    // 停滞超时不能只靠 token:net48 里它打断不了挂起的 body 读取
                    await HttpBody.CopyToAsync(response, target, opts.StallTimeout, ct).ConfigureAwait(false);
                }
                if (!await Hashing.VerifyFileAsync(tmpPath, item.ExpectedHash, item.Algo, ct).ConfigureAwait(false))
                    throw new IOException("校验不通过：文件可能损坏或被篡改");

                // 先挂到共享缓存(同盘是硬链接),再改名为目标文件
                DownloadCache.Store(tmpPath, item.ExpectedHash, item.Algo);
                if (File.Exists(item.DestPath)) File.Delete(item.DestPath);
                File.Move(tmpPath, item.DestPath);
                return;
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
