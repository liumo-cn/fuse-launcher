using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Installer;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>加载器 installer:分块下载、sha1 sidecar、失败换源。</summary>
public class ModLoaderInstallerTests : IDisposable
{
    private readonly string _dir;

    public ModLoaderInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mcdl-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task DownloadInstaller_UsesSha1AndFallsBackToMirror()
    {
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
        var handler = new InstallerHandler(payload, sha1);
        var flavor = new ModLoaderInstaller.LoaderFlavor(
            "forge", "Forge",
            "http://test/official",
            new[] { "http://test/mirror" },
            "net/minecraftforge/forge",
            "forge");

        var bytes = await ModLoaderInstaller.DownloadInstallerAsync(
            new HttpClient(handler), flavor, "1.20.1", "47.2.0", _dir, null, CancellationToken.None);

        Assert.Equal(payload, bytes);
        Assert.True(handler.OfficialAttempted);
        Assert.True(handler.MirrorJarRequests > 0);
        Assert.True(File.Exists(Path.Combine(_dir, "forge-1.20.1-47.2.0-installer.jar")));
    }

    private sealed class InstallerHandler(byte[] payload, string sha1) : HttpMessageHandler
    {
        public bool OfficialAttempted;
        public int MirrorJarRequests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith(".sha1", StringComparison.OrdinalIgnoreCase))
            {
                if (url.Contains("/official/", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sha1),
                });
            }

            if (url.Contains("/official/", StringComparison.Ordinal))
            {
                OfficialAttempted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            MirrorJarRequests++;
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range == null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                });
            }

            var start = range.From ?? 0;
            var end = range.To ?? payload.Length - 1;
            if (start < 0 || end < start || start >= payload.Length) end = payload.Length - 1;
            var count = (int)Math.Min(end - start + 1, payload.Length - start);
            var body = new byte[count];
            Array.Copy(payload, start, body, 0, count);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body),
            };
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(start, start + count - 1, payload.Length);
            return Task.FromResult(response);
        }
    }
}
