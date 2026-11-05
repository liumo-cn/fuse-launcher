using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Fuse.Core.Sources;
using Fuse.Core.Versioning;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// Fabric 加载器安装器（官方 meta.fabricmc.net v2，纯 JSON 拼装，无需跑安装器）。
/// 版本命名规则（用户确认）：原版 MC 版本号 + 加载器版本号，如 1.20.1-fabric-0.16.14。
/// </summary>
public static class FabricInstaller
{
    public const string MetaBase = "https://meta.fabricmc.net/v2/versions/loader";

    /// <summary>
    /// 官方 meta 走 Cloudflare，部分网络下单次可达数十秒甚至超时；BMCLAPI 镜像只代理
    /// 「版本清单」这一个 JSON（库下载地址原样保留为官方 maven），用于兜底。
    /// </summary>
    public const string MetaMirror = "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader";

    /// <summary>官方源单次等待上限,超时后改走镜像。</summary>
    private static readonly TimeSpan OfficialTimeout = TimeSpan.FromSeconds(8);

    /// <summary>拉 meta 版本清单:先官方(8s),失败/超时/网络错误改用镜像。</summary>
    private static async Task<JArray> FetchMetaAsync(
        HttpClient http, string gameVersion, CancellationToken ct)
    {
        // 按实测速度排序(官方 vs BMCLAPI 镜像),最快的先试
        // 官方 meta 在本网络经常 30s 才吐完/直接超时,镜像 0.4s:候选里镜像放前面,
        // 测速结果(修好前缀匹配后)会再按实测排序
        var sources = Download.MirrorProbe.Order("fabric",
            new[] { MetaMirror, MetaBase });
        Exception? last = null;
        foreach (var baseUrl in sources)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(OfficialTimeout);
                using var response = await http.GetAsync($"{baseUrl}/{gameVersion}", cts.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"{(int)response.StatusCode} {baseUrl}");
                    continue;
                }
                // 响应体也要受同一个 8 秒预算约束:ReadAsStringAsync 没有 token,
                // 慢速涓流的官方源会一直读到 HttpClient 默认超时(100s),外层 20s 也打断不了。
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
                var sb = new System.Text.StringBuilder();
                var buffer = new char[8192];
                int read;
                while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    sb.Append(buffer, 0, read);
                }
                return JArray.Parse(sb.ToString());
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("Fabric meta 所有源均不可用");
    }

    /// <summary>某 MC 版本支持的 Fabric 加载器版本列表（第一条通常最新）。</summary>
    public static async Task<List<string>> FetchLoaderVersionsAsync(
        HttpClient http, string gameVersion, CancellationToken ct)
    {
        var arr = await FetchMetaAsync(http, gameVersion, ct).ConfigureAwait(false);
        return arr.Cast<JObject>()
            .Select(e => e["loader"]?.Value<string>("version"))
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();
    }

    /// <summary>Maven 坐标 → 相对路径：group:artifact:version(:classifier)。</summary>
    public static string MavenPath(string name, string urlBase)
    {
        var parts = name.Split(':');
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    /// <summary>组装 Fabric 版本 JSON（继承原版），并返回需下载的库清单。loaderEntry 为 meta v2 的完整条目。</summary>
    public static (JObject VersionJson, List<FileDownloadItem> Libraries) BuildVersionJson(
        JObject vanillaJson, string gameVersion, string loaderVersion, JObject loaderEntry, string librariesRoot)
    {
        var newId = $"{gameVersion}-fabric-{loaderVersion}";
        var launcherMeta = loaderEntry["launcherMeta"] as JObject
                           ?? throw new InvalidOperationException("Fabric meta 缺少 launcherMeta");

        var libs = new JArray();
        var items = new List<FileDownloadItem>();

        // launcherMeta.libraries.common/client —— maven 坐标 + 自定义仓库,按名称显式去重
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        // fabric-loader 本体:meta 的 launcherMeta 只列间接依赖,loader 自身必须手动加入,
        // 否则 mainClass(KnotClient)无处可寻,启动即失败。
        var loaderName = $"net.fabricmc:fabric-loader:{loaderVersion}";
        if (seenNames.Add(loaderName))
        {
            libs.Add(new JObject
            {
                ["name"] = loaderName,
                ["url"] = "https://maven.fabricmc.net/",
            });
            var loaderRel = MavenPath(loaderName, "");
            items.Add(new FileDownloadItem
            {
                Url = "https://maven.fabricmc.net/" + loaderRel,
                DestPath = Path.Combine(librariesRoot, loaderRel.Replace('/', Path.DirectorySeparatorChar)),
            });
        }

        foreach (var section in new[]
                 {
                     launcherMeta["libraries"]?["common"] as JArray,
                     launcherMeta["libraries"]?["client"] as JArray,
                 })
        {
            if (section == null) continue;
            foreach (var lib in section.Cast<JObject>())
            {
                var name = lib.Value<string>("name")!;
                if (!seenNames.Add(name)) continue;
                var urlBase = lib.Value<string>("url") ?? "https://maven.fabricmc.net/";
                var relPath = MavenPath(name, urlBase);
                libs.Add(new JObject
                {
                    ["name"] = name,
                    ["url"] = urlBase,
                });
                var fullUrl = urlBase.EndsWith("/") ? urlBase + relPath : urlBase + "/" + relPath;
                items.Add(new FileDownloadItem
                {
                    Url = fullUrl,
                    DestPath = Path.Combine(librariesRoot, relPath.Replace('/', Path.DirectorySeparatorChar)),
                });
            }
        }

        // intermediary:优先用 meta 给的 maven 坐标。很多版本里它等于游戏版本,
        // 但 26.3 这类新版 meta 写的是 net.fabricmc:intermediary:0.0.0,
        // 按游戏版本硬拼会 404(用户实测 "Fabric 库下载失败")。
        var intermediary = loaderEntry["intermediary"];
        if (intermediary != null)
        {
            var mavenName = intermediary.Value<string>("maven")
                            ?? $"net.fabricmc:intermediary:{gameVersion}";
            libs.Add(new JObject
            {
                ["name"] = mavenName,
                ["url"] = "https://maven.fabricmc.net/",
            });
            var relPath = MavenPath(mavenName, "");
            items.Add(new FileDownloadItem
            {
                Url = "https://maven.fabricmc.net/" + relPath,
                DestPath = Path.Combine(librariesRoot, relPath.Replace('/', Path.DirectorySeparatorChar)),
            });
        }

        var mainClass = launcherMeta["mainClass"]?.Value<string>("client")
                        ?? throw new InvalidOperationException("Fabric meta 缺少 client mainClass");

        var versionJson = new JObject
        {
            ["id"] = newId,
            ["inheritsFrom"] = gameVersion,
            ["releaseTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["type"] = "release",
            ["mainClass"] = mainClass,
            ["arguments"] = new JObject
            {
                ["game"] = new JArray { "--fabric-version", loaderVersion },
            },
            ["libraries"] = libs,
        };
        return (versionJson, items.DistinctByPath().ToList());
    }

    /// <summary>
    /// 完整安装：确保原版底座已装（无则先跑 VanillaInstaller），再写 Fabric 版本 JSON + 下载其库。
    /// </summary>
    public static async Task InstallAsync(
        HttpClient http, string gameVersion, string loaderVersion,
        string versionsRoot, string librariesRoot, string assetsRoot, string gameDirectory,
        IProgress<double>? progress = null, CancellationToken cancellation = default,
        string? mcVersion = null)
    {
        // gameVersion 是"继承的底座"(可能是 OptiFine 这种派生版本),
        // mcVersion 是真正的 MC 版本(Fabric meta 只认它;不传就退化成 gameVersion)。
        var lookupVersion = string.IsNullOrEmpty(mcVersion) ? gameVersion : mcVersion!;

        // 1. 原版底座（占 80% 进度；已装则秒过）
        var vanillaJsonPath = Path.Combine(versionsRoot, lookupVersion, lookupVersion + ".json");
        var vanillaJarPath = Path.Combine(versionsRoot, lookupVersion, lookupVersion + ".jar");
        // 底座是派生版本(如 OptiFine)时自己没有 jar,客户端 jar 在父版本目录里
        if (File.Exists(vanillaJsonPath) && !File.Exists(vanillaJarPath))
        {
            var merged = Versioning.VersionJsonLoader.Load(versionsRoot, lookupVersion);
            var jarId = string.IsNullOrEmpty(merged.Jar) ? lookupVersion : merged.Jar!;
            var parentJar = Path.Combine(versionsRoot, jarId, jarId + ".jar");
            if (File.Exists(parentJar)) vanillaJarPath = parentJar;
        }
        if (!File.Exists(vanillaJsonPath) || !File.Exists(vanillaJarPath))
        {
            var manifest = await MojangSource.FetchManifestAsync(http, cancellation).ConfigureAwait(false);
            var entry = manifest.FirstOrDefault(v => v.Id == lookupVersion)
                        ?? throw new InvalidOperationException($"官方清单中没有 {lookupVersion}");
            var version = await MojangSource.DownloadVersionJsonAsync(http, entry, versionsRoot, cancellation).ConfigureAwait(false);
            var installer = new VanillaInstaller(http);
            await installer.InstallAsync(version, versionsRoot, librariesRoot, assetsRoot, gameDirectory,
                ToScaled(progress, 0, 0.8), cancellation).ConfigureAwait(false);
        }
        progress?.Report(0.8);

        // 2. Fabric meta
        var arr = await FetchMetaAsync(http, lookupVersion, cancellation).ConfigureAwait(false);
        var entryObj = arr.Cast<JObject>().FirstOrDefault(e => e["loader"]?.Value<string>("version") == loaderVersion)
                       ?? throw new InvalidOperationException($"Fabric {loaderVersion} 不支持 {lookupVersion}");

        // 3. 写版本 JSON + 下载库（占 20%）
        var baseJsonPath = Path.Combine(versionsRoot, gameVersion, gameVersion + ".json");
        var baseJson = File.Exists(baseJsonPath) ? baseJsonPath : vanillaJsonPath;
        var vanillaJson = JObject.Parse(File.ReadAllText(baseJson));
        var (versionJson, libItems) = BuildVersionJson(vanillaJson, gameVersion, loaderVersion, entryObj, librariesRoot);

        var dir = Path.Combine(versionsRoot, $"{gameVersion}-fabric-{loaderVersion}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{gameVersion}-fabric-{loaderVersion}.json"),
            versionJson.ToString(Formatting.Indented));

        var small = new SmallFileDownloader(http);
        var result = await small.DownloadSetAsync(libItems,
            new SmallFileOptions { Parallelism = 16 },
            ToScaled(progress, 0.8, 0.2), cancellation).ConfigureAwait(false);
        if (result.Failed.Count > 0)
            throw new IOException($"Fabric 库下载失败 {result.Failed.Count} 个：{result.Failed[0]}");
        progress?.Report(1.0);
    }

    private static IProgress<double> ToScaled(IProgress<double>? progress, double offset, double weight)
        => new CallbackProgress(p => progress?.Report(offset + weight * p));
}

internal static class FileDownloadItemExtensions
{
    public static IEnumerable<FileDownloadItem> DistinctByPath(this IEnumerable<FileDownloadItem> items)
        => items.GroupBy(i => i.DestPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
}
