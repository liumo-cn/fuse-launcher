using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>小文件下载器:跳过已有、首字节超时、停滞超时、并发爬升。</summary>
public class SmallFileDownloaderTests : IDisposable
{
    private readonly string _dir;

    public SmallFileDownloaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mcdl-small-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static FileDownloadItem Item(string url, string dest, string? hash = null)
        => new() { Url = url, DestPath = dest, ExpectedHash = hash, Algo = HashAlgo.Sha1 };

    [Fact]
    public async Task DownloadsAllAndSkipsExisting()
    {
        var handler = new MapHandler(new Dictionary<string, byte[]>
        {
            ["http://a/x"] = Encoding.UTF8.GetBytes("hello"),
            ["http://a/y"] = Encoding.UTF8.GetBytes("world"),
        });
        var downloader = new SmallFileDownloader(new HttpClient(handler));
        var existing = Path.Combine(_dir, "existing.bin");
        await File.WriteAllBytesAsync(existing, Encoding.UTF8.GetBytes("keep"));
        var existingHash = await Hashing.ComputeFileAsync(existing, HashAlgo.Sha1);

        var result = await downloader.DownloadSetAsync(new[]
        {
            Item("http://a/x", Path.Combine(_dir, "x.bin")),
            Item("http://a/y", Path.Combine(_dir, "y.bin")),
            Item("http://a/z", existing, existingHash),
        }, new SmallFileOptions { Parallelism = 4, InitialParallelism = 2 });

        Assert.Empty(result.Failed);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_dir, "x.bin")));
        Assert.Equal("world", await File.ReadAllTextAsync(Path.Combine(_dir, "y.bin")));
    }

    [Fact]
    public async Task NoResponse_TimesOutAndFails()
    {
        var downloader = new SmallFileDownloader(new HttpClient(new SlowHandler()));
        var result = await downloader.DownloadSetAsync(new[]
        {
            Item("http://slow/file", Path.Combine(_dir, "slow.bin")),
        }, new SmallFileOptions
        {
            Parallelism = 1,
            InitialParallelism = 1,
            MaxRetries = 0,
            FirstByteTimeout = TimeSpan.FromMilliseconds(100),
        });

        Assert.Single(result.Failed);
    }

    [Fact]
    public async Task StallAfterHeaders_TimesOutAndFails()
    {
        var downloader = new SmallFileDownloader(new HttpClient(new StallHandler()));
        var result = await downloader.DownloadSetAsync(new[]
        {
            Item("http://stall/file", Path.Combine(_dir, "stall.bin")),
        }, new SmallFileOptions
        {
            Parallelism = 1,
            InitialParallelism = 1,
            MaxRetries = 0,
            StallTimeout = TimeSpan.FromMilliseconds(200),
        });

        Assert.Single(result.Failed);
    }

    [Fact]
    public async Task Concurrency_RampsUpAboveInitial()
    {
        var handler = new CountingHandler();
        var downloader = new SmallFileDownloader(new HttpClient(handler));
        var items = new List<FileDownloadItem>();
        for (var i = 0; i < 20; i++)
            items.Add(Item("http://count/" + i, Path.Combine(_dir, "c" + i + ".bin")));

        var result = await downloader.DownloadSetAsync(items, new SmallFileOptions
        {
            Parallelism = 4,
            InitialParallelism = 1,
            MaxRetries = 0,
        });

        Assert.Empty(result.Failed);
        Assert.Equal(20, result.Downloaded);
        Assert.InRange(handler.MaxConcurrency, 2, 4);
    }

    [Fact]
    public async Task CacheHit_SkipsSecondNetworkDownload()
    {
        var oldRoot = DownloadCache.Root;
        DownloadCache.Root = Path.Combine(_dir, "cache");
        try
        {
            var payload = Encoding.UTF8.GetBytes("cached");
            var hash = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
            var handler = new CacheHandler(payload);
            var downloader = new SmallFileDownloader(new HttpClient(handler));
            var options = new SmallFileOptions { Parallelism = 1, InitialParallelism = 1 };

            var first = await downloader.DownloadSetAsync(new[]
            {
                Item("http://a/x", Path.Combine(_dir, "one.bin"), hash),
            }, options);
            Assert.Equal(1, handler.Requests);
            Assert.Empty(first.Failed);

            var second = await downloader.DownloadSetAsync(new[]
            {
                Item("http://a/x", Path.Combine(_dir, "two.bin"), hash),
            }, options);
            Assert.Equal(1, handler.Requests); // 第二次直接命中缓存,没有再发请求
            Assert.Empty(second.Failed);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_dir, "two.bin")));
        }
        finally
        {
            DownloadCache.Root = oldRoot;
        }
    }

    [Fact]
    public async Task Progress_WeightsBySizeNotFileCount()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloader = new SmallFileDownloader(new HttpClient(new GatedHandler(gate.Task)));
        var progress = new CollectingProgress();
        var firstReport = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        progress.OnReport = value => firstReport.TrySetResult(value);

        var task = downloader.DownloadSetAsync(new[]
        {
            new FileDownloadItem { Url = "http://size/big", DestPath = Path.Combine(_dir, "big.bin"), Size = 9_000 },
            new FileDownloadItem { Url = "http://size/small", DestPath = Path.Combine(_dir, "small.bin"), Size = 1_000 },
        }, new SmallFileOptions { Parallelism = 2, InitialParallelism = 2, MaxRetries = 0 }, progress);

        try
        {
            var first = await firstReport.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // 小文件先下完只值 1000/10000,不能按"完成 1/2 个文件"算成 50%
            Assert.InRange(first, 0.09, 0.11);
        }
        finally
        {
            gate.TrySetResult(true);
        }

        var result = await task;
        Assert.Empty(result.Failed);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(1.0, progress.Values[^1], 3);
    }

    [Fact]
    public async Task Progress_IgnoresFilesThatAreAlreadyUpToDate()
    {
        var downloader = new SmallFileDownloader(new HttpClient(new MapHandler(new Dictionary<string, byte[]>
        {
            ["http://a/missing"] = Encoding.UTF8.GetBytes("new"),
        })));
        var progress = new CollectingProgress();

        var kept = new List<FileDownloadItem>();
        foreach (var name in new[] { "old1.bin", "old2.bin" })
        {
            var path = Path.Combine(_dir, name);
            await File.WriteAllTextAsync(path, name);
            kept.Add(new FileDownloadItem
            {
                Url = "http://a/" + name,
                DestPath = path,
                ExpectedHash = await Hashing.ComputeFileAsync(path, HashAlgo.Sha1),
            });
        }

        kept.Add(new FileDownloadItem { Url = "http://a/missing", DestPath = Path.Combine(_dir, "missing.bin") });
        var result = await downloader.DownloadSetAsync(kept,
            new SmallFileOptions { Parallelism = 2, InitialParallelism = 2, MaxRetries = 0 }, progress);

        Assert.Equal(2, result.Skipped);
        Assert.Equal(1, result.Downloaded);
        // 跳过的两个文件不占进度:只有真正下完那一个时到 100%
        Assert.Equal(new[] { 1.0 }, progress.Values);
    }

    private sealed class CollectingProgress : IProgress<double>
    {
        private readonly object _lock = new();
        public List<double> Values { get; } = new();
        public Action<double>? OnReport;

        public void Report(double value)
        {
            lock (_lock) Values.Add(value);
            OnReport?.Invoke(value);
        }
    }

    private sealed class GatedHandler(Task gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("big"))
                await gate.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[16]),
            };
        }
    }

    /// <summary>
    /// net48 给 ReadAsync 传 token 掐不断挂起的 body 读取,只有 Dispose 响应有用。
    /// 这里用一个"完全不理 token"的流,验证停滞超时和暂停都能真的打断。
    /// </summary>
    [Fact]
    public async Task BodyThatIgnoresToken_IsAbortedByStallTimeout()
    {
        var downloader = new SmallFileDownloader(new HttpClient(new IgnoringStreamHandler()));
        var dest = Path.Combine(_dir, "stall-ignore.bin");

        var result = await downloader.DownloadSetAsync(new[] { Item("http://stall/ignore", dest) },
            new SmallFileOptions
            {
                Parallelism = 1,
                InitialParallelism = 1,
                MaxRetries = 0,
                StallTimeout = TimeSpan.FromMilliseconds(150),
            });

        Assert.Single(result.Failed);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Cancel_AbortsBodyThatIgnoresToken()
    {
        var downloader = new SmallFileDownloader(new HttpClient(new IgnoringStreamHandler()));
        using var cts = new CancellationTokenSource();
        var task = downloader.DownloadSetAsync(new[] { Item("http://stall/cancel", Path.Combine(_dir, "cancel.bin")) },
            new SmallFileOptions
            {
                Parallelism = 1,
                InitialParallelism = 1,
                MaxRetries = 3,
                FirstByteTimeout = TimeSpan.FromMinutes(5),
                StallTimeout = TimeSpan.FromMinutes(5),
            }, cancellation: cts.Token);

        await Task.Delay(150);
        cts.Cancel();

        // 流不理 token,只有"取消 → Dispose 响应"能立刻打断;挂住就会超时失败
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class IgnoringStreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new IgnoringStream()),
            });
        }
    }

    private sealed class IgnoringStream : Stream
    {
        private readonly TaskCompletionSource<bool> _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // 故意不理会 cancellationToken:模拟 .NET Framework 的网络流
            var abandoned = Task.Delay(Timeout.Infinite);
            if (await Task.WhenAny(abandoned, _disposed.Task).ConfigureAwait(false) == abandoned)
                return 0;
            throw new ObjectDisposedException(nameof(IgnoringStream));
        }

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult(true);
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>本地已校验通过的文件要顺手进共享缓存,装第二个游戏目录时才能直接复用。</summary>
    [Fact]
    public async Task VerifiedLocalFile_IsAddedToSharedCache()
    {
        var oldRoot = DownloadCache.Root;
        DownloadCache.Root = Path.Combine(_dir, "cache-local");
        try
        {
            var path = Path.Combine(_dir, "local.bin");
            await File.WriteAllTextAsync(path, "local");
            var hash = await Hashing.ComputeFileAsync(path, HashAlgo.Sha1);
            var handler = new CacheHandler(Encoding.UTF8.GetBytes("unused"));
            var downloader = new SmallFileDownloader(new HttpClient(handler));

            var result = await downloader.DownloadSetAsync(new[]
            {
                Item("http://a/local", path, hash),
            }, new SmallFileOptions { Parallelism = 1, InitialParallelism = 1 });

            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, handler.Requests);
            Assert.True(DownloadCache.TryGet(hash, HashAlgo.Sha1, out var cached));
            Assert.Equal("local", await File.ReadAllTextAsync(cached));
        }
        finally
        {
            DownloadCache.Root = oldRoot;
        }
    }

    private sealed class CacheHandler(byte[] payload) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }

    private sealed class MapHandler(Dictionary<string, byte[]> payloads) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (payloads.TryGetValue(request.RequestUri!.ToString(), out var data))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(data),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class StallHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallStream()),
            });
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _inFlight;
        private int _max;

        public int MaxConcurrency => Volatile.Read(ref _max);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            while (true)
            {
                var max = Volatile.Read(ref _max);
                if (now <= max || Interlocked.CompareExchange(ref _max, now, max) == max) break;
            }
            try
            {
                await Task.Delay(40, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 1 }),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class StallStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
