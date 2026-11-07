using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>分块下载器端到端：TcpListener 存根模拟官方 CDN 的 Range 语义（免 URL ACL）。</summary>
public class ChunkedDownloaderTests : IDisposable
{
    private readonly string _dir;
    private readonly StubCdn _cdn;

    public ChunkedDownloaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mcdl-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cdn = new StubCdn();
        _cdn.Start();
    }

    public void Dispose()
    {
        _cdn.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        return data;
    }

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task Download_WithRangeServer_ProducesVerifiedFile()
    {
        var payload = Payload(1_000_000);
        _cdn.SetPayload(payload);
        var dest = Path.Combine(_dir, "client.jar");

        var downloader = new ChunkedDownloader(new HttpClient());
        var result = await downloader.DownloadAsync(_cdn.Url, dest, Sha1(payload),
            new ChunkedDownloadOptions { Parallelism = 4, ChunkBytes = 256 * 1024 });

        Assert.True(result.HashVerified);
        Assert.True(File.Exists(dest));
        Assert.Equal(payload.Length, new FileInfo(dest).Length);
        Assert.Equal(Sha1(payload), Sha1(await File.ReadAllBytesAsync(dest)));
        Assert.False(File.Exists(dest + ".chunks.json"));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Resume_CompletedChunksNotRefetched()
    {
        var payload = Payload(600_000);
        _cdn.SetPayload(payload);
        var dest = Path.Combine(_dir, "lib.jar");
        const int chunkBytes = 200_000; // 3 块
        var part = dest + ".part";

        // 预置 .part 与断点状态：第 0、2 块已完成，只剩第 1 块
        await File.WriteAllBytesAsync(part, payload);
        var state = new ChunkState
        {
            TotalSize = payload.Length,
            ChunkBytes = chunkBytes,
            ExpectedSha1 = Sha1(payload),
            Completed = { 0, 2 },
        };
        await File.WriteAllTextAsync(dest + ".chunks.json",
            System.Text.Json.JsonSerializer.Serialize(state));

        _cdn.ResetCounters();
        var downloader = new ChunkedDownloader(new HttpClient());
        var result = await downloader.DownloadAsync(_cdn.Url, dest, Sha1(payload),
            new ChunkedDownloadOptions { Parallelism = 2, ChunkBytes = chunkBytes });

        Assert.Equal(2, result.ChunksResumed);
        Assert.Equal(1, result.ChunksDownloaded);
        Assert.Equal(Sha1(payload), Sha1(await File.ReadAllBytesAsync(dest)));
        // 第 2 块起点(400000)不应再被请求；第 1 块起点(200000)应被请求
        var starts = _cdn.RangeStarts();
        Assert.DoesNotContain(400_000L, starts);
        Assert.Contains(200_000L, starts);
    }

    [Fact]
    public async Task Download_HashMismatch_Throws()
    {
        var payload = Payload(100_000);
        _cdn.SetPayload(payload);
        var dest = Path.Combine(_dir, "bad.jar");

        var downloader = new ChunkedDownloader(new HttpClient());
        await Assert.ThrowsAnyAsync<IOException>(() =>
            downloader.DownloadAsync(_cdn.Url, dest, new string('0', 40),
                new ChunkedDownloadOptions { Parallelism = 2, ChunkBytes = 50_000 }));

        // 校验失败必须清断点 否则下次会认为所有块都完成 反复校验同一个坏文件
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.False(File.Exists(dest + ".chunks.json"));
    }

    /// <summary>坏源下完哈希不对:清断点后换下一个源整份重下,最终拿到正确文件。</summary>
    [Fact]
    public async Task HashMismatch_RetriesWithAnotherSource()
    {
        var payload = Payload(300_000);
        var sha = Sha1(payload);
        var dest = Path.Combine(_dir, "retry.jar");
        var handler = new FirstSourceCorruptHandler(payload);

        var downloader = new ChunkedDownloader(new HttpClient(handler));
        // libraries.minecraft.net 在 Balanced 下 attempt0=官方、attempt1=USTC 镜像
        var result = await downloader.DownloadAsync(
            "https://libraries.minecraft.net/com/example/fuse-retry.jar",
            dest, sha,
            new ChunkedDownloadOptions { Parallelism = 2, ChunkBytes = 100_000 });

        Assert.True(result.HashVerified);
        Assert.Equal(sha, Sha1(await File.ReadAllBytesAsync(dest)));
        Assert.True(handler.OfficialHits > 0, "应先请求坏源");
        Assert.True(handler.MirrorHits > 0, "换源后应请求好源");
    }

    /// <summary>两个源都返回坏字节:重试一轮后必须停下并清断点,不能死循环。</summary>
    [Fact]
    public async Task HashMismatch_BothSourcesBad_ThrowsAndCleans()
    {
        var payload = Payload(200_000);
        var dest = Path.Combine(_dir, "bad-both.jar");
        var handler = new FirstSourceCorruptHandler(payload, corruptAll: true);

        var downloader = new ChunkedDownloader(new HttpClient(handler));
        await Assert.ThrowsAnyAsync<IOException>(() =>
            downloader.DownloadAsync(
                "https://libraries.minecraft.net/com/example/fuse-bad.jar",
                dest, Sha1(payload),
                new ChunkedDownloadOptions { Parallelism = 2, ChunkBytes = 100_000 }));

        Assert.True(handler.OfficialHits > 0);
        Assert.True(handler.MirrorHits > 0);
        Assert.False(File.Exists(dest + ".part"));
        Assert.False(File.Exists(dest + ".chunks.json"));
    }

    /// <summary>
    /// 回归:官方地址返回 403 时必须继续尝试镜像,而不是把整个下载判死。
    /// 2026-11-06 的 ProbeAsync 优化曾在 4xx 上直接 break,而 attempt 本身会换源,
    /// 结果官方 403 再也不会落到镜像(镜像 404/403 也不会再试官方)。
    /// </summary>
    [Fact]
    public async Task Probe_Http403OnOfficial_FallsBackToMirror()
    {
        var payload = Payload(300_000);
        var sha = Sha1(payload);
        var dest = Path.Combine(_dir, "fallback.jar");
        var handler = new Official403Handler(payload);

        var oldMode = UrlMirror.Mode;
        UrlMirror.Mode = UrlMirror.SourceMode.Balanced;
        try
        {
            var downloader = new ChunkedDownloader(new HttpClient(handler));
            // libraries.minecraft.net 在 Balanced 下 attempt0=官方、attempt1=镜像
            var result = await downloader.DownloadAsync(
                "https://libraries.minecraft.net/com/example/fuse-fallback.jar",
                dest, sha,
                new ChunkedDownloadOptions { Parallelism = 2, ChunkBytes = 100_000 });

            Assert.True(result.HashVerified);
            Assert.True(handler.SawOfficial, "应先请求官方源");
            Assert.True(handler.SawMirror, "官方 403 后应继续请求镜像");
            Assert.Equal(sha, Sha1(await File.ReadAllBytesAsync(dest)));
        }
        finally
        {
            UrlMirror.Mode = oldMode;
        }
    }

    /// <summary>官方域返回 403;镜像域按 Range 返回 206。用于验证换源重试。</summary>
    private sealed class Official403Handler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        public bool SawOfficial;
        public bool SawMirror;

        public Official403Handler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
            {
                SawOfficial = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("blocked"),
                });
            }

            SawMirror = true;
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var start = range?.From ?? 0;
            var end = range?.To ?? _payload.Length - 1;
            if (start < 0 || end < start || start >= _payload.Length) end = _payload.Length - 1;
            var count = (int)Math.Min(end - start + 1, _payload.Length - start);
            var body = new byte[count];
            Array.Copy(_payload, start, body, 0, count);

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body),
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(start, start + count - 1, _payload.Length);
            return Task.FromResult(response);
        }
    }

    /// <summary>官方域返回坏字节,镜像域返回正确字节;用于验证哈希失败后的换源重下。</summary>
    private sealed class FirstSourceCorruptHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly bool _corruptAll;
        public int OfficialHits;
        public int MirrorHits;

        public FirstSourceCorruptHandler(byte[] payload, bool corruptAll = false)
        {
            _payload = payload;
            _corruptAll = corruptAll;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var official = request.RequestUri!.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase);
            if (official) Interlocked.Increment(ref OfficialHits);
            else Interlocked.Increment(ref MirrorHits);
            var corrupt = _corruptAll || official;

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var start = range?.From ?? 0;
            var end = range?.To ?? _payload.Length - 1;
            if (start < 0 || end < start || start >= _payload.Length) end = _payload.Length - 1;
            var count = (int)Math.Min(end - start + 1, _payload.Length - start);
            var body = new byte[count];
            Array.Copy(_payload, start, body, 0, count);
            if (corrupt && body.Length > 0) body[0] ^= 0xFF;

            if (range != null)
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(body),
                };
                response.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(start, start + count - 1, _payload.Length);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }

    /// <summary>单线程 Accept 循环 + Range 语义的 HTTP/1.1 存根。</summary>
    private sealed class StubCdn : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private byte[] _payload = Array.Empty<byte>();
        private readonly ConcurrentBag<long> _rangeStarts = new();
        private long _bytesServed;
        private CancellationTokenSource? _cts;

        public string Url { get; private set; } = "";

        public void SetPayload(byte[] payload) => _payload = payload;

        public void ResetCounters()
        {
            _rangeStarts.Clear();
            _bytesServed = 0;
        }

        public ConcurrentBag<long> RangeStarts() => _rangeStarts;

        public long TotalRangeBytesServed() => Interlocked.Read(ref _bytesServed);

        public void Start()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/file";
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                _ = Handle(client, ct);
            }
        }

        private async Task Handle(TcpClient client, CancellationToken ct)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct);
            if (requestLine == null) return;

            long rangeStart = -1, rangeEnd = -1;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct)))
            {
                if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var spec = line["Range: bytes=".Length..];
                    var dash = spec.IndexOf('-');
                    var endPart = spec[(dash + 1)..];
                    rangeStart = long.Parse(spec[..dash]);
                    rangeEnd = endPart.Length > 0 ? long.Parse(endPart) : _payload.Length - 1;
                }
            }

            if (rangeStart >= 0)
            {
                if (rangeStart == 0 && rangeEnd == 0)
                {
                    // 探测：206 + 完整长度 + 1 字节
                    _rangeStarts.Add(0);
                    var header =
                        "HTTP/1.1 206 Partial Content\r\n" +
                        $"Content-Range: bytes 0-0/{_payload.Length}\r\n" +
                        "Content-Length: 1\r\nConnection: close\r\n\r\n";
                    var headerBytes = Encoding.ASCII.GetBytes(header);
                    await stream.WriteAsync(headerBytes, ct);
                    await stream.WriteAsync(_payload.AsMemory(0, 1), ct);
                    await stream.FlushAsync(ct);
                    return;
                }

                var length = rangeEnd - rangeStart + 1;
                _rangeStarts.Add(rangeStart);
                Interlocked.Add(ref _bytesServed, length);
                var rangeHeader =
                    "HTTP/1.1 206 Partial Content\r\n" +
                    $"Content-Range: bytes {rangeStart}-{rangeEnd}/{_payload.Length}\r\n" +
                    $"Content-Length: {length}\r\nConnection: close\r\n\r\n";
                var rangeHeaderBytes = Encoding.ASCII.GetBytes(rangeHeader);
                await stream.WriteAsync(rangeHeaderBytes, ct);
                await stream.WriteAsync(_payload.AsMemory((int)rangeStart, (int)length), ct);
                await stream.FlushAsync(ct);
                return;
            }

            Interlocked.Add(ref _bytesServed, _payload.Length);
            var full = "HTTP/1.1 200 OK\r\n" +
                       $"Content-Length: {_payload.Length}\r\nConnection: close\r\n\r\n";
            var fullBytes = Encoding.ASCII.GetBytes(full);
            await stream.WriteAsync(fullBytes, ct);
            await stream.WriteAsync(_payload, ct);
            await stream.FlushAsync(ct);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _listener.Stop();
        }
    }
}
