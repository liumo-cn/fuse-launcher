using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Installer;
using Fuse.Core.Sources;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>CurseForge 整合包在队列内转 mrpack:manifest 解析、index 生成、overrides 复制。</summary>
public class CurseForgePackTests : IDisposable
{
    private readonly string _dir;

    public CurseForgePackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mcdl-cfpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        CurseForgeSource.Configure(null);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task BuildCurseForgeMrpack_ConvertsManifestAndOverrides()
    {
        var packZip = Path.Combine(_dir, "pack.zip");
        using (var zip = ZipFile.Open(packZip, ZipArchiveMode.Create))
        {
            var manifest = new JObject
            {
                ["minecraft"] = new JObject
                {
                    ["version"] = "1.21.1",
                    ["modLoaders"] = new JArray
                    {
                        new JObject { ["id"] = "fabric-0.16.14", ["primary"] = true },
                    },
                },
                ["files"] = new JArray
                {
                    new JObject { ["projectID"] = 98765, ["fileID"] = 12345, ["required"] = true },
                },
            };
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                w.Write(manifest.ToString());
            using (var w = new StreamWriter(zip.CreateEntry("overrides/config/test.txt").Open()))
                w.Write("hello");
        }

        var response = new JObject
        {
            ["data"] = new JObject
            {
                ["id"] = 12345,
                ["modId"] = 98765,
                ["displayName"] = "Example Mod",
                ["fileName"] = "example-mod.jar",
                ["downloadUrl"] = "https://edge.forgecdn.net/files/12345/example-mod.jar",
                ["fileLength"] = 123,
                ["gameVersions"] = new JArray("1.21.1", "Fabric"),
                ["releaseType"] = 1,
                ["fileDate"] = "2026-01-01T00:00:00Z",
                ["hashes"] = new JArray { new JObject { ["algo"] = 1, ["value"] = "abcdef1234" } },
            },
        };
        CurseForgeSource.Configure(new FakeTransport(response.ToString()));

        var outPath = Path.Combine(_dir, "pack.mrpack");
        await ModpackInstaller.BuildCurseForgeMrpackAsync(
            new System.Net.Http.HttpClient(), 98765, packZip, outPath, null, CancellationToken.None);

        using var result = ZipFile.OpenRead(outPath);
        var indexEntry = result.GetEntry("modrinth.index.json");
        Assert.NotNull(indexEntry);
        JObject index;
        using (var reader = new StreamReader(indexEntry!.Open()))
            index = JObject.Parse(reader.ReadToEnd());

        Assert.Equal("1.21.1", index["dependencies"]?.Value<string>("minecraft"));
        Assert.Equal("0.16.14", index["dependencies"]?.Value<string>("fabric-loader"));
        Assert.Equal("mods/example-mod.jar", index["files"]?[0]?.Value<string>("path"));
        Assert.Equal("https://edge.forgecdn.net/files/12345/example-mod.jar",
            (string?)index["files"]?[0]?["downloads"]?.First);
        Assert.Equal("abcdef1234", index["files"]?[0]?["hashes"]?.Value<string>("sha1"));

        var overrideEntry = result.GetEntry("overrides/config/test.txt");
        Assert.NotNull(overrideEntry);
        using var sr = new StreamReader(overrideEntry!.Open());
        Assert.Equal("hello", sr.ReadToEnd());
    }

    [Fact]
    public async Task BuildCurseForgeMrpack_CanceledTokenStops()
    {
        var packZip = Path.Combine(_dir, "pack.zip");
        using (var zip = ZipFile.Open(packZip, ZipArchiveMode.Create))
        {
            var manifest = new JObject
            {
                ["minecraft"] = new JObject
                {
                    ["version"] = "1.21.1",
                    ["modLoaders"] = new JArray { new JObject { ["id"] = "fabric-0.16.14" } },
                },
                ["files"] = new JArray { new JObject { ["projectID"] = 1, ["fileID"] = 2 } },
            };
            using var w = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            w.Write(manifest.ToString());
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModpackInstaller.BuildCurseForgeMrpackAsync(
                new System.Net.Http.HttpClient(), 1, packZip, Path.Combine(_dir, "out.mrpack"), null, cts.Token));
    }

    private sealed class FakeTransport(string json) : ICurseForgeTransport
    {
        public Task<string> GetAsync(string pathAndQuery, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(json);
        }
    }
}
