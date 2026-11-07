using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Fuse.Core.Download;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// Forge / NeoForge 安装器。
///
/// 两者结构相同:官方 maven 上提供一个 installer.jar,里面带 <c>version.json</c>(继承原版的完整描述,
/// 含 mainClass 与库清单)。库走镜像下载加速;但 1.17+ 的启动还依赖 installer 的 processors
/// 现场生成的 client-srg/client-extra/forge-client 三个 jar(不在 version.json 里),
/// 所以最后会静默跑一次官方 installer 补齐,安装目标就是本启动器根目录。
///
/// 网络:优先官方 maven(forge 是 maven.minecraftforge.net,neoforge 是 maven.neoforged.net),
/// 单次等待超过 <see cref="OfficialTimeout"/> 或失败时自动改用 BMCLAPI 镜像。
/// </summary>
public static class ModLoaderInstaller
{
    private static readonly TimeSpan OfficialTimeout = TimeSpan.FromSeconds(90);

    /// <summary>加载器类型。Mirrors 按优先级排列,官方超时/失败时依次尝试。</summary>
    public sealed record LoaderFlavor(
        string Key,          // forge / neoforge
        string Display,      // Forge / NeoForge
        string OfficialMaven,
        string[] Mirrors,    // BMCLAPI 主镜像 + 大学镜像备份
        string GroupPath,    // net/minecraftforge/forge 或 net/neoforged/neoforge
        string Artifact);    // forge 或 neoforge

    public static readonly LoaderFlavor Forge = new(
        "forge", "Forge",
        "https://maven.minecraftforge.net",
        new[]
        {
            "https://bmclapi2.bangbang93.com/maven",
            "https://mirrors4.qlu.edu.cn/bmclapi",
        },
        "net/minecraftforge/forge",
        "forge");

    public static readonly LoaderFlavor NeoForge = new(
        "neoforge", "NeoForge",
        // 官方 maven.neoforged.net 在本网络经常 20s+ 无响应;NeoForged 自己的 CDN
        // 用同样的 /releases 目录结构,实测 metadata 1.2s、installer 1.9s。
        "https://neoforged.forgecdn.net/releases",
        new[]
        {
            "https://maven.neoforged.net/releases",
            "https://bmclapi2.bangbang93.com/maven",
            "https://mirrors4.qlu.edu.cn/bmclapi",
        },
        "net/neoforged/neoforge",
        "neoforge");

    /// <summary>
    /// MC 版本 → NeoForge 版本前缀:1.20.4 → "20.4."、1.21 → "21.0."、26.3 → "26.3."。
    /// NeoForge 从 1.20.2 才开始提供,1.20.1 没有对应版本。
    /// </summary>
    private static string? NeoForgePrefix(string mc)
    {
        var parts = mc.Split('.');
        if (parts.Length == 0) return null;
        if (parts[0] == "1")
        {
            if (parts.Length == 2) return parts[1] + ".0.";          // 1.21 → 21.0.
            if (parts.Length >= 3) return parts[1] + "." + parts[2] + "."; // 1.20.4 → 20.4.
            return null;
        }
        // 26.3 / 26.1 这类新编号
        return parts.Length >= 2 ? parts[0] + "." + parts[1] + "." : null;
    }

    /// <summary>
    /// 取某个 MC 版本可用的加载器版本(新→旧)。
    /// Forge 的 maven 版本号形如 <c>1.20.1-47.2.0</c>;NeoForge 形如 <c>20.4.237</c>(取前缀匹配)。
    /// </summary>
    public static async Task<List<string>> FetchVersionsAsync(
        HttpClient http, LoaderFlavor flavor, string gameVersion, CancellationToken ct)
    {
        var sources = Download.MirrorProbe.Order(flavor.Key,
            new[] { flavor.OfficialMaven }.Concat(flavor.Mirrors));
        var xml = await GetStringFromSourcesAsync(http,
            sources.Select(s => $"{s}/{flavor.GroupPath}/maven-metadata.xml").ToArray(), ct)
            .ConfigureAwait(false);

        var doc = XDocument.Parse(xml);
        var all = doc.Descendants("version").Select(v => v.Value.Trim()).Where(v => v.Length > 0).ToList();

        IEnumerable<string> filtered;
        if (flavor.Key == "forge")
        {
            // 1.20.1-47.2.0 → 只看以 "<mc>-" 开头的
            var prefix = gameVersion + "-";
            filtered = all.Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                .Select(v => v.Substring(prefix.Length));
        }
        else
        {
            // NeoForge 版本号 = 「MC 的次版本.补丁.构建」:1.20.4 → 20.4.x、1.21 → 21.0.x、
            // 1.21.1 → 21.1.x、26.3 → 26.3.x。按这个前缀筛。
            var prefix = NeoForgePrefix(gameVersion);
            filtered = prefix == null
                ? Enumerable.Empty<string>()
                : all.Where(v => v.StartsWith(prefix, StringComparison.Ordinal));
        }

        // maven-metadata 的顺序并不可靠(实测 1.16.5 的 reverse 会拿到 36.0.0 这种最早版本),
        // 按版本号数值降序排,保证返回的第一个是最新版。
        return filtered
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersion))
            .ToList();
    }

    /// <summary>比较形如 45.4.5 / 20.4.253 的版本号(按数值逐段比较)。</summary>
    private static int CompareVersion(string? a, string? b)
    {
        if (a == null) return b == null ? 0 : -1;
        if (b == null) return 1;
        var pa = a.Split('.');
        var pb = b.Split('.');
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var av = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            var bv = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (av != bv) return av < bv ? -1 : 1;
        }
        return string.CompareOrdinal(a, b);
    }

    /// <summary>installer 的 maven 相对路径(forge 的 artifact 带 MC 前缀)。</summary>
    private static string InstallerRelPath(LoaderFlavor flavor, string gameVersion, string loaderVersion)
    {
        var full = flavor.Key == "forge" ? $"{gameVersion}-{loaderVersion}" : loaderVersion;
        return $"{flavor.GroupPath}/{full}/{flavor.Artifact}-{full}-installer.jar";
    }

    /// <summary>下载 installer 并取出其中的 version.json(官方失败自动换镜像)。</summary>
    public static async Task<JObject> FetchInstallerVersionJsonAsync(
        HttpClient http, LoaderFlavor flavor, string gameVersion, string loaderVersion, CancellationToken ct)
    {
        var bytes = await DownloadInstallerAsync(http, flavor, gameVersion, loaderVersion, ct).ConfigureAwait(false);
        return ParseVersionJson(bytes, flavor, loaderVersion);
    }

    /// <summary>下载 installer.jar 本体(官方失败自动换镜像)。</summary>
    public static async Task<byte[]> DownloadInstallerAsync(
        HttpClient http, LoaderFlavor flavor, string gameVersion, string loaderVersion, CancellationToken ct)
    {
        var rel = InstallerRelPath(flavor, gameVersion, loaderVersion);
        var sources = Download.MirrorProbe.Order(flavor.Key,
            new[] { flavor.OfficialMaven }.Concat(flavor.Mirrors));
        return await GetBytesFromSourcesAsync(http,
            sources.Select(s => $"{s}/{rel}").ToArray(), ct).ConfigureAwait(false);
    }

    private static JObject ParseVersionJson(byte[] installerBytes, LoaderFlavor flavor, string loaderVersion)
    {
        using var stream = new MemoryStream(installerBytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("version.json")
                    ?? throw new InvalidOperationException(
                        $"{flavor.Display} {loaderVersion} 的 installer 里没有 version.json(过老的版本需要运行官方安装器)");
        using var reader = new StreamReader(entry.Open());
        var json = JObject.Parse(reader.ReadToEnd());

        // installer 的 version.json 里可能用 ${library_directory} 之类的占位符,统一换成实际 maven 地址
        NormalizeLibraries(json, flavor);
        return json;
    }

    /// <summary>把 version.json 的 id 改成启动器约定的 <c>&lt;mc&gt;-&lt;loader&gt;-&lt;ver&gt;</c> 并收集下载清单。</summary>
    public static (JObject VersionJson, List<FileDownloadItem> Libraries) Prepare(
        JObject installerJson, LoaderFlavor flavor, string gameVersion, string loaderVersion, string librariesRoot)
    {
        var id = $"{gameVersion}-{flavor.Key}-{loaderVersion}";
        var json = (JObject)installerJson.DeepClone();
        json["id"] = id;
        json["inheritsFrom"] = gameVersion;
        json["type"] = "release";
        json["releaseTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        json["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var items = new List<FileDownloadItem>();
        if (json["libraries"] is JArray libs)
        {
            foreach (var lib in libs.OfType<JObject>())
            {
                var name = lib.Value<string>("name");
                var url = lib.Value<string>("url") ?? flavor.OfficialMaven + "/";
                if (string.IsNullOrEmpty(name)) continue;
                // 只下载普通 artifact(带 classifier 的 natives 由 NativesExtractor 在启动时处理)
                var art = lib["downloads"]?["artifact"] as JObject;
                if (art != null)
                {
                    var path = art.Value<string>("path");
                    var artUrl = art.Value<string>("url");
                    if (string.IsNullOrEmpty(path)) continue;

                    // url 为空有两种情况:① processors 现场生成的产物(forge-client 等),maven 没有;
                    // ② 老版本(如 1.12.2)的 universal jar,maven 上确实有,只是 installer 没写 url。
                    // 用分类器区分:生成物都是 client/srg/extra/slim/official/mappings 这类。
                    var urlWasEmpty = string.IsNullOrEmpty(artUrl);
                    if (urlWasEmpty)
                    {
                        if (IsGeneratedClassifier(name!)) continue;
                        var baseUrl = lib.Value<string>("url") ?? flavor.OfficialMaven + "/";
                        if (baseUrl.Contains("${")) baseUrl = flavor.OfficialMaven + "/";
                        artUrl = baseUrl.EndsWith("/") ? baseUrl + path : baseUrl + "/" + path;

                        // Forge ≤1.16 的"无分类器" jar 在 maven 上不存在,只有 -universal 版本;
                        // 一并下载(不校验哈希,内容就是 universal),下载后再复制成正式文件名。
                        var uniRel = path!.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                            ? path.Substring(0, path.Length - 4) + "-universal.jar"
                            : path;
                        items.Add(new FileDownloadItem
                        {
                            Url = baseUrl.EndsWith("/") ? baseUrl + uniRel : baseUrl + "/" + uniRel,
                            DestPath = Path.Combine(librariesRoot, uniRel.Replace('/', Path.DirectorySeparatorChar)),
                            Optional = true,
                        });
                    }

                    items.Add(new FileDownloadItem
                    {
                        Url = artUrl!,
                        DestPath = Path.Combine(librariesRoot, path!.Replace('/', Path.DirectorySeparatorChar)),
                        ExpectedHash = art.Value<string>("sha1"),
                        Algo = HashAlgo.Sha1,
                        // 补出来的地址不保证存在(1.16.5 的普通 jar 就是 processors 产物),
                        // 失败不阻塞安装;真缺了启动时会有明确报错。
                        Optional = urlWasEmpty,
                    });
                    continue;
                }

                var rel = FabricInstaller.MavenPath(name!, url);
                var full = url.EndsWith("/") ? url + rel : url + "/" + rel;
                items.Add(new FileDownloadItem
                {
                    Url = full,
                    DestPath = Path.Combine(librariesRoot, rel.Replace('/', Path.DirectorySeparatorChar)),
                });
            }
        }

        return (json, items);
    }

    /// <summary>
    /// <summary>installer 的 processors 会现场生成的分类器(这些不是可下载件)。</summary>
    private static bool IsGeneratedClassifier(string mavenName)
    {
        var at = mavenName.IndexOf('@');
        if (at >= 0) mavenName = mavenName.Substring(0, at);
        var parts = mavenName.Split(':');
        if (parts.Length < 4) return false;
        var classifier = parts[3].ToLowerInvariant();
        return classifier is "client" or "server" or "srg" or "extra" or "slim"
            or "official" or "mappings" or "unpacked";
    }

    /// 完整安装:写版本 JSON → 镜像下载库 → 必要时运行官方 installers 补 processors 产物。
    ///
    /// Forge/NeoForge 1.17+ 的启动依赖三个 installer 现场生成的 jar
    /// (client-&lt;ver&gt;-srg.jar / client-&lt;ver&gt;-extra.jar / forge-&lt;ver&gt;-client.jar),
    /// 它们不在 version.json 的库清单里,只能跑 install_profile 的 processors 得到。
    /// 安装目标就是本启动器的根目录(versions/ 与 libraries/ 的父级),installer 会对已存在
    /// 且 sha1 匹配的文件跳过下载,所以前面用镜像下好的库不会重复拉。
    /// </summary>
    public static async Task InstallAsync(
        HttpClient http, LoaderFlavor flavor, string gameVersion, string loaderVersion,
        string versionsRoot, string librariesRoot, string? javaExe, string tempRoot,
        IProgress<double>? progress, CancellationToken ct)
    {
        progress?.Report(0.03);
        var installerBytes = await DownloadInstallerAsync(http, flavor, gameVersion, loaderVersion, ct)
            .ConfigureAwait(false);
        var installerJson = ParseVersionJson(installerBytes, flavor, loaderVersion);
        // processors 在 install_profile.json 里(version.json 没有),有它就必须跑官方 installer
        var needsProcessors = InstallerHasProcessors(installerBytes);
        progress?.Report(0.12);

        // processors 的 jar/classpath 都是 libraries 里的 maven 坐标,先用我们的下载器(多源+哈希)
        // 全部备好,再自己执行 processors——不跑官方 installer:1.20.4 起它是签名 jar,
        // 改安装源会被判定篡改,不改又会卡在 files.minecraftforge.net。
        var items = new List<FileDownloadItem>();
        if (needsProcessors)
            items.AddRange(CollectInstallerLibraries(installerBytes, flavor, librariesRoot));

        var (versionJson, versionItems) = Prepare(installerJson, flavor, gameVersion, loaderVersion, librariesRoot);
        items.AddRange(versionItems);

        var id = versionJson.Value<string>("id")!;
        var dir = Path.Combine(versionsRoot, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), versionJson.ToString(Newtonsoft.Json.Formatting.Indented));
        progress?.Report(0.22);

        // installer 库与 version.json 库有大量重叠,按目标路径去重,避免同一文件并发写
        var unique = items
            .GroupBy(i => i.DestPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var small = new SmallFileDownloader(http);
        var result = await small.DownloadSetAsync(unique,
            new SmallFileOptions { Parallelism = 8 }, progress, ct).ConfigureAwait(false);
        if (result.Failed.Count > 0)
        {
            // 镜像偶发限流/官方源抖动:对失败项换低并发再跑一轮,常能救回
            var failedSet = new HashSet<string>(result.Failed, StringComparer.OrdinalIgnoreCase);
            var retryItems = unique.Where(u => failedSet.Contains(u.DestPath)).ToList();
            var second = await small.DownloadSetAsync(retryItems,
                new SmallFileOptions { Parallelism = 3 }, null, ct).ConfigureAwait(false);
            if (second.Failed.Count > 0)
                throw new IOException($"{flavor.Display} 库下载失败 {second.Failed.Count} 个：{second.Failed[0]}");
        }

        // Forge ≤1.16 的部分构件 maven 上没有:它们被打包在 installer.jar 的
        // maven/<path> 里(如 forge-1.16.5-36.2.42.jar,212KB),缺了就当场解出来;
        // 再退一步,同目录的 -universal.jar 也可以顶替(内容等价)。
        var libs = versionJson["libraries"] as JArray;
        if (libs != null)
        {
            using var installerZip = new ZipArchive(new MemoryStream(installerBytes), ZipArchiveMode.Read);
            foreach (var lib in libs.OfType<JObject>())
            {
                var path = lib["downloads"]?["artifact"]?.Value<string>("path");
                if (string.IsNullOrEmpty(path)) continue;
                var full = Path.Combine(librariesRoot, path!.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) continue;
                var parent = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(parent)) continue;
                Directory.CreateDirectory(parent);

                var embedded = installerZip.GetEntry("maven/" + path);
                if (embedded != null)
                {
                    using var src = embedded.Open();
                    using var dst = File.Create(full);
                    src.CopyTo(dst);
                    continue;
                }

                var bare = Path.GetFileNameWithoutExtension(full);
                var universal = Path.Combine(parent, bare + "-universal.jar");
                if (File.Exists(universal))
                    File.Copy(universal, full, overwrite: true);
            }
        }
        progress?.Report(0.80);

        if (needsProcessors)
        {
            await RunProcessorsAsync(installerBytes, javaExe, versionsRoot, librariesRoot, gameVersion,
                tempRoot, flavor, id, progress, ct).ConfigureAwait(false);
        }

        progress?.Report(1.0);
    }

    /// <summary>install_profile.json 是否声明了 processors(Forge/NeoForge 1.17+ 都有)。</summary>
    private static bool InstallerHasProcessors(byte[] installerBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(installerBytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("install_profile.json");
        if (entry == null) return false;
        using var reader = new StreamReader(entry.Open());
        var profile = JObject.Parse(reader.ReadToEnd());
        return profile["processors"] is JArray { Count: > 0 };
    }

    /// <summary>install_profile.json 里的库(processors 的依赖与补丁源),提前用我们的下载器拉好。</summary>
    private static List<FileDownloadItem> CollectInstallerLibraries(
        byte[] patchedInstaller, LoaderFlavor flavor, string librariesRoot)
    {
        var list = new List<FileDownloadItem>();
        using var zip = new ZipArchive(new MemoryStream(patchedInstaller), ZipArchiveMode.Read);
        var entry = zip.GetEntry("install_profile.json");
        if (entry == null) return list;
        using var reader = new StreamReader(entry.Open());
        var profile = JObject.Parse(reader.ReadToEnd());
        if (profile["libraries"] is not JArray libs) return list;

        foreach (var lib in libs.OfType<JObject>())
        {
            var art = lib["downloads"]?["artifact"] as JObject;
            if (art != null)
            {
                var path = art.Value<string>("path");
                var url = art.Value<string>("url");
                // url 为空 = installer 现场生成的产物,不下
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(url)) continue;
                list.Add(new FileDownloadItem
                {
                    Url = url!,
                    DestPath = Path.Combine(librariesRoot, path!.Replace('/', Path.DirectorySeparatorChar)),
                    ExpectedHash = art.Value<string>("sha1"),
                    Algo = HashAlgo.Sha1,
                });
                continue;
            }

            var name = lib.Value<string>("name");
            if (string.IsNullOrEmpty(name)) continue;
            var baseUrl = lib.Value<string>("url") ?? flavor.OfficialMaven + "/";
            if (baseUrl.Contains("${")) baseUrl = flavor.OfficialMaven + "/";
            var rel = FabricInstaller.MavenPath(name!, baseUrl);
            var full = baseUrl.EndsWith("/") ? baseUrl + rel : baseUrl + "/" + rel;
            list.Add(new FileDownloadItem
            {
                Url = full,
                DestPath = Path.Combine(librariesRoot, rel.Replace('/', Path.DirectorySeparatorChar)),
            });
        }
        return list;
    }

    /// <summary>执行 installer 的 processors,生成 client/srg/extra 等启动必需的 jar。</summary>
    private static async Task RunProcessorsAsync(
        byte[] installerBytes, string? javaExe, string versionsRoot, string librariesRoot,
        string gameVersion, string tempRoot, LoaderFlavor flavor, string versionId,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe))
            javaExe = Java.JavaLocator.FindInstalled()
                .Where(r => r.MajorVersion >= 17)
                .OrderByDescending(r => r.MajorVersion)
                .Select(r => r.JavaExecutable)
                .FirstOrDefault();
        if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe))
            throw new InvalidOperationException(
                $"{flavor.Display} 需要 Java 17+ 来生成客户端补丁,但本机没有可用的 Java。请先安装 Java 或在设置里指定。");

        var launcherRoot = Path.GetDirectoryName(
            Path.GetFullPath(versionsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (string.IsNullOrEmpty(launcherRoot) || !Directory.Exists(launcherRoot))
            throw new InvalidOperationException("无法定位启动器根目录:" + versionsRoot);

        Directory.CreateDirectory(tempRoot);
        var jarPath = Path.Combine(tempRoot, versionId + "-installer.jar");
        File.WriteAllBytes(jarPath, installerBytes);
        progress?.Report(0.82);

        await ForgeProcessorRunner.RunAsync(installerBytes, jarPath, launcherRoot, librariesRoot,
            versionsRoot, gameVersion, javaExe!, Path.Combine(tempRoot, versionId + "-data"),
            progress, ct).ConfigureAwait(false);
    }

    /// <summary>跑 Java 子进程并收集输出;超时/取消都 kill。</summary>
    private static async Task<(int ExitCode, string Output)> RunJavaAsync(
        string javaExe, string arguments, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = javaExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        using var process = System.Diagnostics.Process.Start(psi)
                            ?? throw new InvalidOperationException("无法启动 Java 进程");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (Exception) { } });

        // netstandard2.0 的 Process 没有 WaitForExitAsync,用轮询等待并执行超时
        var deadline = DateTime.UtcNow + timeout;
        while (!process.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                try { process.Kill(); } catch (Exception) { }
                ct.ThrowIfCancellationRequested();
            }
            if (DateTime.UtcNow > deadline)
            {
                try { process.Kill(); } catch (Exception) { }
                throw new TimeoutException("安装器运行超过 " + timeout.TotalMinutes + " 分钟,已终止");
            }
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
        }
        var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        return (process.ExitCode, output);
    }

    // ---------- 网络:官方优先,卡顿/失败换镜像 ----------

    private static async Task<string> GetStringFromSourcesAsync(
        HttpClient http, string[] sources, CancellationToken ct)
    {
        var bytes = await GetBytesFromSourcesAsync(http, sources, ct).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 按给定顺序逐个尝试(顺序来自 MirrorProbe 的实测速度,最快的排第一),
    /// 某个源失败立刻换下一个,不做"先等官方超时"。
    /// </summary>
    private static async Task<byte[]> GetBytesFromSourcesAsync(
        HttpClient http, string[] sources, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in sources)
        {
            // 镜像偶发 502/超时很常见,同一个源给两次机会再换下一个
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(OfficialTimeout);
                    using var response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        last = new HttpRequestException($"{(int)response.StatusCode} {url}");
                        break;
                    }
                    // 响应体同样受本次尝试的超时约束(ReadAsByteArrayAsync 无 token)
                    using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    var buf = new byte[64 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false)) > 0)
                        buffer.Write(buf, 0, read);
                    return buffer.ToArray();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    last = ex; // 超时/网络错误 → 重试或换下一个源
                    await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        if (last is OperationCanceledException)
            throw new TimeoutException("下载加载器安装包超时(所有下载源都失败)", last);
        throw last ?? new HttpRequestException("所有下载源均不可用");
    }

    /// <summary>占位符库地址(installer 里的 ${library_directory} 等)→ 实际 maven,避免拼出非法 URL。</summary>
    private static void NormalizeLibraries(JObject json, LoaderFlavor flavor)
    {
        if (json["libraries"] is not JArray libs) return;
        foreach (var lib in libs.OfType<JObject>())
        {
            var url = lib.Value<string>("url");
            if (url == null) continue;
            if (url.Contains("${"))
                lib["url"] = flavor.OfficialMaven + "/";
        }
    }
}
