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
