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

    /// <summary>版本名数值比较:把数字段逐个取出来比("17.0.10" > "17.0.9"、"8u421" > "8u51")。</summary>
    private static int CompareVersionName(string? a, string? b)
    {
        var da = Digits(a); var db = Digits(b);
        for (var i = 0; i < Math.Max(da.Count, db.Count); i++)
        {
            var x = i < da.Count ? da[i] : 0;
            var y = i < db.Count ? db[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return string.CompareOrdinal(a ?? "", b ?? "");
    }

    private static List<long> Digits(string? s)
    {
        var list = new List<long>();
        foreach (var seg in (s ?? "").Split('u', '.', '+', '-', '_'))
            if (long.TryParse(seg, out var n)) list.Add(n);
        return list;
    }

    /// <summary>挑选：优先 component 精确匹配（官方 javaVersion.component），其次主版本一致，回退更高版本。
    /// 版本名按数值取最新,字符串序会拿 "8u51" 压过 "8u421"。</summary>
    public static JavaRuntimeEntry? Pick(IReadOnlyList<JavaRuntimeEntry> entries, string? component, int majorVersion)
    {
        if (!string.IsNullOrEmpty(component))
        {
            var exact = entries.Where(e => e.Component == component)
                .OrderByDescending(e => e.VersionName, Comparer<string>.Create(CompareVersionName))
                .FirstOrDefault();
            if (exact != null) return exact;
        }
        var sameMajor = entries.Where(e => e.MajorVersion == majorVersion)
            .OrderByDescending(e => e.VersionName, Comparer<string>.Create(CompareVersionName))
            .FirstOrDefault();
        if (sameMajor != null) return sameMajor;
        return entries.Where(e => e.MajorVersion > majorVersion)
            .OrderBy(e => e.MajorVersion)
            .ThenByDescending(e => e.VersionName, Comparer<string>.Create(CompareVersionName))
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
            var sha1 = raw.Value<string>("sha1");
            // 官方清单从不缺 sha1:缺了就是清单被篡改/损坏的信号,fail-closed 而不是静默免检
            if (string.IsNullOrEmpty(sha1))
                throw new InvalidOperationException(
                    $"[liumo-guard] 运行时清单条目缺少 sha1,拒绝下载: {prop.Name}");
            items.Add(new FileDownloadItem
            {
                Url = raw.Value<string>("url") ?? "",
                DestPath = Download.SafeExtractor.SafeTarget(destDir, prop.Name),
                ExpectedHash = sha1,
                Algo = HashAlgo.Sha1,
            });
        }
        return items;
    }

    /// <summary>下载并落盘一个运行时（含清单校验、全部文件 SHA-1 校验、写 runtime-meta.json 标记）。</summary>
    /// <summary>
    /// 下载并校验运行时清单。优先官方 piston-meta,失败换 launchermeta 别名,
    /// 最后才试镜像;镜像内容哈希对不上就直接报错(不把旧清单当官方清单用)。
    /// </summary>
    private static async Task<byte[]> FetchManifestAsync(
        HttpClient http, JavaRuntimeEntry entry, CancellationToken ct)
    {
        var urls = new List<string> { entry.ManifestUrl };
        if (Uri.TryCreate(entry.ManifestUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            urls.Add("https://launchermeta.mojang.com" + uri.PathAndQuery);
        var mirror = Download.UrlMirror.Rewrite(entry.ManifestUrl);
        if (!string.IsNullOrEmpty(mirror)) urls.Add(mirror);

        var errors = new List<string>();
        foreach (var url in urls.Distinct())
        {
            try
            {
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(entry.ManifestSha1) &&
                    !string.Equals(Sha1Hex(bytes), entry.ManifestSha1, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(url + "：SHA-1 不匹配");
                    continue;
                }
                return bytes;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add(url + "：" + ex.Message);
            }
        }
        throw new IOException("Java 运行时清单下载失败：" + string.Join("；", errors));
    }

    private static string Sha1Hex(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static async Task<JavaRuntime> DownloadAsync(
        HttpClient http, JavaRuntimeEntry entry, string destDir,
        IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(destDir);

        // 1. 清单本体（SHA-1 校验）
        // 单独下:BMCL 镜像上的 Java 运行时清单可能是旧版本(哈希对不上),
        // 走官方/launchermeta 别名并在哈希不符时明确失败,不要吞掉原因。
        var manifestPath = Path.Combine(destDir, ".manifest.json");
        var manifestBytes = await FetchManifestAsync(http, entry, cancellation).ConfigureAwait(false);
        File.WriteAllBytes(manifestPath, manifestBytes);
        var manifest = JObject.Parse(System.Text.Encoding.UTF8.GetString(manifestBytes));

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
