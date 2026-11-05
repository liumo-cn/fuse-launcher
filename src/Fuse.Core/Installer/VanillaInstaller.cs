using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuse.Core.Download;
using Fuse.Core.Versioning;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// 原版安装规划（纯逻辑，无 IO）：由 McVersion 展开完整下载清单。
/// 覆盖：客户端 jar（分块）、libraries（规则过滤）、assets 索引与全部对象、log4j 配置。
/// </summary>
public static class VanillaInstallPlanner
{
    public static FileDownloadItem ClientJar(McVersion version, string versionsRoot)
    {
        var client = version.ClientDownload
                     ?? throw new InvalidOperationException("版本 JSON 缺少 downloads.client（非官方清单）");
        var jarId = version.Jar ?? version.Id;
        return new FileDownloadItem
        {
            Url = client.Url,
            DestPath = Path.Combine(versionsRoot, jarId, jarId + ".jar"),
            ExpectedHash = client.Sha1,
            Algo = HashAlgo.Sha1,
        };
    }

    /// <summary>规则通过的所有库（含 natives classifier 库），落盘到 libraries/&lt;path&gt;。</summary>
    public static List<FileDownloadItem> LibraryFiles(McVersion version, string librariesRoot, RuleEnvironment env)
    {
        var items = new List<FileDownloadItem>();
        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.Evaluate(lib.Rules, env)) continue;
            if (lib.Artifact != null && !string.IsNullOrEmpty(lib.Artifact.Url))
            {
                items.Add(new FileDownloadItem
                {
                    Url = lib.Artifact.Url,
                    DestPath = Path.Combine(librariesRoot, lib.Artifact.Path.Replace('/', Path.DirectorySeparatorChar)),
                    ExpectedHash = lib.Artifact.Sha1,
                    Algo = HashAlgo.Sha1,
                });
            }
            // 旧式 natives classifier jar（≤1.18）也要下载用于 natives 提取
            if (lib.Classifiers != null &&
                lib.Classifiers.TryGetValue("natives-" + env.OsName, out var native) &&
                !string.IsNullOrEmpty(native.Url))
            {
                items.Add(new FileDownloadItem
                {
                    Url = native.Url,
                    DestPath = Path.Combine(librariesRoot, native.Path.Replace('/', Path.DirectorySeparatorChar)),
                    ExpectedHash = native.Sha1,
                    Algo = HashAlgo.Sha1,
                });
            }
        }
        return items.DistinctByPath().ToList();
    }

    /// <summary>assets 索引文件（assets/indexes/&lt;id&gt;.json）。</summary>
    public static FileDownloadItem AssetIndex(McVersion version, string assetsRoot)
    {
        var index = version.AssetIndex
                    ?? throw new InvalidOperationException("版本 JSON 缺少 assetIndex");
        return new FileDownloadItem
        {
            Url = index.Url,
            DestPath = Path.Combine(assetsRoot, "indexes", index.Id + ".json"),
            ExpectedHash = index.Sha1,
            Algo = HashAlgo.Sha1,
        };
    }

    /// <summary>由已下载的 assets 索引展开全部对象（官方 resources CDN，objects/&lt;hash前2位&gt;/&lt;hash&gt;）。</summary>
    public static List<FileDownloadItem> AssetObjects(string assetIndexFilePath, string assetsRoot)
    {
        var index = JObject.Parse(File.ReadAllText(assetIndexFilePath));
        var objects = index["objects"] as JObject ?? new JObject();
        var items = new List<FileDownloadItem>(objects.Count);
        foreach (var prop in objects.Properties())
        {
            var hash = prop.Value?["hash"]?.Value<string>();
            if (string.IsNullOrEmpty(hash) || hash!.Length < 3) continue;
            items.Add(new FileDownloadItem
            {
                Url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                DestPath = Path.Combine(assetsRoot, "objects", hash[..2], hash),
                ExpectedHash = hash,
                Algo = HashAlgo.Sha1,
            });
        }
        return items.DistinctByPath().ToList();
    }

    /// <summary>log4j 配置文件（落到实例游戏目录，与 LaunchPipeline 的 ${path} 约定一致）。</summary>
    public static FileDownloadItem? LogConfig(McVersion version, string gameDirectory)
    {
        var file = version.LoggingClient?.File;
        if (file == null || string.IsNullOrEmpty(file.Url)) return null;
        return new FileDownloadItem
        {
            Url = file.Url,
            DestPath = Path.Combine(gameDirectory, "log4jConfig.xml"),
            ExpectedHash = file.Sha1,
            Algo = HashAlgo.Sha1,
        };
    }

}

/// <summary>原版安装执行器：按权重串联各下载阶段（复用 FR-01 的两种下载模式）。</summary>
public sealed class VanillaInstaller
{
    /// <summary>库与 assets 的小文件并发度。</summary>
    public int SmallFileParallelism { get; set; } = 32;

    public async Task InstallAsync(
        McVersion version,
        string versionsRoot,
        string librariesRoot,
        string assetsRoot,
        string gameDirectory,
        IProgress<double>? progress,
        CancellationToken cancellation)
    {
        var env = RuleEnvironment.WindowsX64;

        // 阶段权重：客户端 jar 35% / 库 15% / 索引 2% / assets 45% / log 配置 3%
        var client = VanillaInstallPlanner.ClientJar(version, versionsRoot);
        var chunked = new ChunkedDownloader(_http);
        await chunked.DownloadAsync(client.Url, client.DestPath, client.ExpectedHash,
            null, new CallbackProgress(p => progress?.Report(p * 0.35)), cancellation).ConfigureAwait(false);
        progress?.Report(0.35);

        var libraries = VanillaInstallPlanner.LibraryFiles(version, librariesRoot, env);
        var small = new SmallFileDownloader(_http);
        await small.DownloadSetAsync(libraries, new SmallFileOptions { Parallelism = SmallFileParallelism },
            ToScaled(progress, 0.35, 0.15), cancellation).ConfigureAwait(false);
        progress?.Report(0.50);

        var indexItem = VanillaInstallPlanner.AssetIndex(version, assetsRoot);
        await small.DownloadSetAsync(new[] { indexItem }, new SmallFileOptions { Parallelism = 2 },
            ToScaled(progress, 0.50, 0.02), cancellation).ConfigureAwait(false);
        progress?.Report(0.52);

        var objects = VanillaInstallPlanner.AssetObjects(indexItem.DestPath, assetsRoot);
        await small.DownloadSetAsync(objects, new SmallFileOptions { Parallelism = SmallFileParallelism },
            ToScaled(progress, 0.52, 0.45), cancellation).ConfigureAwait(false);
        progress?.Report(0.97);

        var logConfig = VanillaInstallPlanner.LogConfig(version, gameDirectory);
        if (logConfig != null)
        {
            await small.DownloadSetAsync(new[] { logConfig }, new SmallFileOptions { Parallelism = 1 },
                ToScaled(progress, 0.97, 0.03), cancellation).ConfigureAwait(false);
        }
        progress?.Report(1.0);
    }

    private readonly HttpClient _http;

    public VanillaInstaller(HttpClient http)
    {
        _http = http;
    }

    private static IProgress<double> ToScaled(IProgress<double>? progress, double offset, double weight)
        => new CallbackProgress(p => progress?.Report(offset + weight * p));
}
