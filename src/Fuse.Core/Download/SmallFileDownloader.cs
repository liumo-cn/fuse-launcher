using System;
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
    /// <summary>小文件队列并发度（FR-01：assets 是首次安装的耗时大头）。</summary>
    public int Parallelism { get; set; } = 32;
    public int MaxRetries { get; set; } = 3;
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

        using var gate = new SemaphoreSlim(Math.Max(1, opts.Parallelism));
        var tasks = list.Select(item => Task.Run(async () =>
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (await DownloadOneAsync(item, opts.MaxRetries, cancellation).ConfigureAwait(false))
                    Interlocked.Increment(ref downloaded);
                else
                    Interlocked.Increment(ref skipped);
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
            }
            finally
            {
                var d = Interlocked.Increment(ref done);
                progress?.Report(d / (double)list.Count);
                gate.Release();
            }
        }, cancellation));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new SmallFileBatchResult
        {
            Skipped = skipped,
            Downloaded = downloaded,
            Failed = failed,
        };
    }

    /// <summary>返回 true 表示执行了下载，false 表示本地已是最新。</summary>
    private async Task<bool> DownloadOneAsync(FileDownloadItem item, int maxRetries, CancellationToken ct)
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
        for (var attempt = 0; attempt <= maxRetries; attempt++)
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
                // Balanced 策略:首次走官方,失败后自动转镜像(熔断期内恒官方)
                var attemptUrl = UrlMirror.ForAttempt(item.Url, attempt);
                using var response = await http.GetAsync(attemptUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if ((int)response.StatusCode == 429)
                {
                    // 镜像限流:触发全局熔断(后续请求改走官方源),再退避重试
                    UrlMirror.NoteThrottled();
                    throw new IOException("429 请求过于频繁");
                }
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using (var target = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    64 * 1024, useAsync: true))
                {
                    await stream.CopyToAsync(target, 64 * 1024, ct).ConfigureAwait(false);
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
        throw new IOException($"下载 {item.Url} 重试 {maxRetries} 次后仍失败", last);
    }
}
