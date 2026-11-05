using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Fuse.Core.Java;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Java;

/// <summary>官方 java-runtime 清单中一个可下载的运行时条目。</summary>
public sealed record JavaRuntimeEntry(string Component, string VersionName, int MajorVersion, string ManifestUrl, string ManifestSha1, string Platform);

/// <summary>
/// Mojang 官方 Java 运行时源（FR-05，官方启动器自用渠道，溯源官方）：
/// 目录清单 → 按 javaVersion.component（回退按主版本）挑选 → 下载解包到 runtimes 目录。
/// 清单地址为官方启动器固定提交（业界通用），结构已实测。
/// </summary>
public static class MojangJavaRuntime
{
    public const string CatalogUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    public static string PlatformKey =>
        Environment.Is64BitOperatingSystem ? "windows-x64" : "windows-x86";

    public static async Task<List<JavaRuntimeEntry>> FetchCatalogAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync(Download.UrlMirror.Rewrite(CatalogUrl), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var platform = root[PlatformKey] as JObject
                       ?? throw new InvalidOperationException($"官方清单缺少平台 {PlatformKey}");

        var entries = new List<JavaRuntimeEntry>();
        foreach (var componentProp in platform.Properties())
        {
            var list = componentProp.Value as JArray;
            if (list == null) continue;
            foreach (var item in list.Cast<JObject>())
            {
                var versionName = item["version"]?.Value<string>("name") ?? "";
                var manifest = item["manifest"] as JObject;
                if (manifest == null) continue;
                entries.Add(new JavaRuntimeEntry(
                    componentProp.Name,
                    versionName,
                    JavaLocator.ParseMajor(versionName),
                    manifest.Value<string>("url") ?? "",
                    manifest.Value<string>("sha1") ?? "",
                    PlatformKey));
            }
        }
        return entries;
    }

    /// <summary>挑选：优先 component 精确匹配（官方 javaVersion.component），其次主版本一致，回退更高版本。</summary>
    public static JavaRuntimeEntry? Pick(IReadOnlyList<JavaRuntimeEntry> entries, string? component, int majorVersion)
    {
        if (!string.IsNullOrEmpty(component))
        {
            var exact = entries.Where(e => e.Component == component)
                .OrderByDescending(e => e.VersionName)
                .FirstOrDefault();
            if (exact != null) return exact;
        }
        var sameMajor = entries.Where(e => e.MajorVersion == majorVersion)
            .OrderByDescending(e => e.VersionName)
            .FirstOrDefault();
        if (sameMajor != null) return sameMajor;
        return entries.Where(e => e.MajorVersion > majorVersion)
            .OrderBy(e => e.MajorVersion)
            .ThenByDescending(e => e.VersionName)
            .FirstOrDefault();
    }

    /// <summary>
    /// 由运行时清单展开下载清单（纯逻辑，可测）：type=file 的 raw 下载项，
    /// 路径按运行时目录落盘；directory 条目由下载器的建目录逻辑覆盖。
    /// </summary>
    public static List<FileDownloadItem> PlanFiles(JObject manifest, string destDir)
    {
        var files = manifest["files"] as JObject
                    ?? throw new InvalidOperationException("运行时清单缺少 files 字段");
        var items = new List<FileDownloadItem>();
        foreach (var prop in files.Properties())
        {
            var entry = prop.Value as JObject;
            if (entry?.Value<string>("type") != "file") continue;
            var raw = entry["downloads"]?["raw"] as JObject;
            if (raw == null) continue;
            items.Add(new FileDownloadItem
            {
                Url = raw.Value<string>("url") ?? "",
                DestPath = Path.Combine(destDir, prop.Name.Replace('/', Path.DirectorySeparatorChar)),
                ExpectedHash = raw.Value<string>("sha1"),
                Algo = HashAlgo.Sha1,
            });
        }
        return items;
    }

    /// <summary>下载并落盘一个运行时（含清单校验、全部文件 SHA-1 校验、写 runtime-meta.json 标记）。</summary>
    public static async Task<JavaRuntime> DownloadAsync(
        HttpClient http, JavaRuntimeEntry entry, string destDir,
        IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(destDir);

        // 1. 清单本体（SHA-1 校验）
        var manifestPath = Path.Combine(destDir, ".manifest.json");
        // 运行时清单只有几十 KB,走小文件下载即可:分块(Range)在部分镜像上会被忽略,
        // 拼出来的内容哈希校验必然失败。
        var manifestDownloader = new SmallFileDownloader(http);
        var manifestResult = await manifestDownloader.DownloadSetAsync(new[]
        {
            new FileDownloadItem
            {
                Url = entry.ManifestUrl,
                DestPath = manifestPath,
                ExpectedHash = entry.ManifestSha1,
                Algo = HashAlgo.Sha1,
            },
        }, new SmallFileOptions { Parallelism = 1 }, null, cancellation).ConfigureAwait(false);
        if (manifestResult.Failed.Count > 0)
            throw new IOException("Java 运行时清单下载失败:" + manifestResult.Failed[0]);
        var manifest = JObject.Parse(File.ReadAllText(manifestPath));

        // 2. 全部文件（小文件高并发，raw 渠道）
        var items = PlanFiles(manifest, destDir);
        var downloader = new SmallFileDownloader(http);
        var result = await downloader.DownloadSetAsync(items,
            new SmallFileOptions { Parallelism = 16 }, progress, cancellation).ConfigureAwait(false);
        if (result.Failed.Count > 0)
            throw new IOException($"Java 运行时下载有 {result.Failed.Count} 个文件失败：{result.Failed[0]}");

        // 3. 标记（后续扫描识别，免重复下载）
        var marker = new
        {
            component = entry.Component,
            version = entry.VersionName,
            major = entry.MajorVersion,
            platform = entry.Platform,
        };
        File.WriteAllText(Path.Combine(destDir, "runtime-meta.json"),
            JsonConvert.SerializeObject(marker, Formatting.Indented));

        return new JavaRuntime
        {
            Home = destDir,
            VersionString = entry.VersionName,
            MajorVersion = entry.MajorVersion,
            Source = "Mojang 官方 java-runtime",
        };
    }
}
