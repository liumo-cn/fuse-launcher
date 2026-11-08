using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Sources;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// Modrinth 整合包(.mrpack)的解析与落地:
///   ① modrinth.index.json → MC 版本 / 加载器及版本 / 文件清单(client 侧);
///   ② overrides/ 与 client-overrides/ 覆盖文件解压进实例目录。
/// 下载编排(vanilla / loader / 文件清单)由 DownloadQueue 的整合包执行器负责。
/// </summary>
public static class ModpackInstaller
{
    public sealed record ModpackIndex(
        string GameVersion,
        string? LoaderType,        // fabric / forge / neoforge / quilt
        string? LoaderVersion,
        List<ModpackFile> Files);

    public sealed record ModpackFile(string RelativePath, string Url, string? Sha1);

    /// <summary>作者禁止第三方 API 下载、需要用户手动补的文件。</summary>
    public sealed record MissingModFile(string FileName, int ProjectId, int FileId, string? Sha1);

    /// <summary>解析 .mrpack 内的 modrinth.index.json。</summary>
    public static ModpackIndex ParseIndex(string extractDir)
    {
        var indexPath = Path.Combine(extractDir, "modrinth.index.json");
        if (!File.Exists(indexPath))
            throw new InvalidOperationException("整合包缺少 modrinth.index.json(不是 Modrinth 格式?)");
        var json = JObject.Parse(File.ReadAllText(indexPath));

        var gameVersion = json["dependencies"]?.Value<string>("minecraft")
            ?? throw new InvalidOperationException("整合包索引缺少 minecraft 版本依赖");

        string? loaderType = null, loaderVersion = null;
        var deps = json["dependencies"] as JObject ?? new JObject();
        foreach (var prop in deps.Properties())
        {
            if (prop.Name == "minecraft") continue;
            loaderType = prop.Name switch
            {
                "fabric-loader" => "fabric",
                "forge" => "forge",
                "neoforge" => "neoforge",
                "quilt" => "quilt",
                _ => prop.Name,
            };
            loaderVersion = (string?)prop.Value;
            break;
        }

        var files = new List<ModpackFile>();
        foreach (var f in json["files"] as JArray ?? new JArray())
        {
            var path = f.Value<string>("path");
            var url = (string?)f["downloads"]?.First;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(url)) continue;
            // client 侧不需要(env.client == "unsupported" 是纯服务端文件)
            var envClient = f["env"]?.Value<string>("client") ?? "required";
            if (envClient == "unsupported") continue;
            var sha1 = f["hashes"]?.Value<string>("sha1");
            files.Add(new ModpackFile(NormalizePath(path!), url!, sha1));
        }

        return new ModpackIndex(gameVersion, loaderType, loaderVersion, files);
    }

    /// <summary>把 overrides/ 与 client-overrides/ 解压进实例目录(不覆盖 index 文件清单里的路径)。</summary>
    public static void ExtractOverrides(
        string extractDir, string gameDir, ISet<string> reserved, CancellationToken ct = default)
    {
        foreach (var rootName in new[] { "overrides", "client-overrides" })
        {
            var root = Path.Combine(extractDir, rootName);
            if (!Directory.Exists(root)) continue;
            var fullRoot = Path.GetFullPath(root);
            foreach (var src in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                // netstandard2.0 没有 Path.GetRelativePath:root 是遍历起点,直接截前缀
                var rel = Path.GetFullPath(src).Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                var normalized = rel.Replace('\\', '/');
                if (reserved.Contains(normalized)) continue;
                var target = Path.Combine(gameDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                File.Copy(src, target, overwrite: true);
            }
        }
    }

    /// <summary>路径规范化并拒绝越出实例目录的写法(../ 与绝对路径)。</summary>
    public static string NormalizePath(string raw)
    {
        var p = raw.Replace('\\', '/').TrimStart('/');
        if (p.Split('/').Any(seg => seg == ".." || seg == "."))
            throw new InvalidOperationException("整合包含有非法路径:" + raw);
        return p;
    }

    /// <summary>列出 .mrpack 内 overrides 树的相对路径(index 文件清单的保留集合)。</summary>
    public static ISet<string> ReservedPaths(List<ModpackFile> files)
        => new HashSet<string>(files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>把 zip 里的全部条目解到目录(mrpack 本体解包用)。</summary>
    public static void ExtractAll(string mrpackPath, string extractDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(extractDir);
        using var zip = ZipFile.OpenRead(mrpackPath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = Path.Combine(extractDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// 把 CurseForge 整合包 zip(manifest.json + overrides)转换成等价 .mrpack:
    /// 逐个 fileID 查 API 拿 downloadUrl,再写 modrinth.index.json。
    /// 解析过程带 CancellationToken,放回队列里执行,暂停/进度才有效。
    /// </summary>
    public static async Task<IReadOnlyList<MissingModFile>> BuildCurseForgeMrpackAsync(
        HttpClient http, int projectId, string packZipPath, string outMrpackPath,
        IProgress<double>? progress, CancellationToken ct, Action<string>? log = null)
    {
        using var source = ZipFile.OpenRead(packZipPath);
        var manifestEntry = source.GetEntry("manifest.json")
                            ?? throw new InvalidOperationException("CurseForge 整合包缺少 manifest.json");
        JObject manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
            manifest = JObject.Parse(await reader.ReadToEndAsync().ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();

        var gameVersion = manifest["minecraft"]?["version"]?.ToString() ?? "";
        var loaderId = (manifest["minecraft"]?["modLoaders"] as JArray)?.First?["id"]?.ToString() ?? "";
        var dash = loaderId.IndexOf('-');
        var loaderKey = dash > 0 ? loaderId.Substring(0, dash) : "";
        var loaderVersion = dash > 0 ? loaderId.Substring(dash + 1) : "";
        var depKey = loaderKey == "fabric" ? "fabric-loader" : loaderKey;

        var files = (manifest["files"] as JArray) ?? new JArray();
        var indexFiles = new JArray();
        var missing = new List<MissingModFile>();
        var total = Math.Max(1, files.Count);
        var done = 0;
        foreach (var f in files.OfType<JObject>())
        {
            ct.ThrowIfCancellationRequested();
            var pid = f.Value<int?>("projectID") ?? 0;
            var fid = f.Value<int?>("fileID") ?? 0;
            var required = f.Value<bool?>("required") ?? true;
            CfFile? meta = null;
            if (pid > 0 && fid > 0)
            {
                for (var attempt = 0; attempt < 3 && meta == null; attempt++)
                {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        meta = await CurseForgeSource.GetFileAsync(pid, fid, attemptCts.Token, skipRateLimit: true)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        if (attempt < 2) await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
                    }
                }
            }

            if (meta == null || string.IsNullOrWhiteSpace(meta.DownloadUrl))
            {
                // 官方 launcher 也会遇到旧整合包引用已下架文件。先在同项目里找同一个
                // fileID(有些源单文件接口 404 但列表里还在),找不到就退到最新适配版本。
                try
                {
                    using var listCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    listCts.CancelAfter(TimeSpan.FromSeconds(20));
                    var candidates = await CurseForgeSource.GetFilesAsync(
                        pid, gameVersion, loaderKey, listCts.Token).ConfigureAwait(false);
                    var fallback = candidates.FirstOrDefault(x => x.Id == fid && !string.IsNullOrWhiteSpace(x.DownloadUrl))
                                   ?? candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.DownloadUrl));
                    if (fallback != null)
                    {
                        log?.Invoke("file " + pid + "/" + fid + " unavailable, using "
                                    + fallback.Id + " " + fallback.FileName);
                        meta = fallback;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log?.Invoke("file " + pid + "/" + fid + " fallback failed: " + ex.Message);
                }
            }

            if (meta != null && !string.IsNullOrWhiteSpace(meta.DownloadUrl))
            {
                indexFiles.Add(new JObject
                {
                    ["path"] = "mods/" + meta.FileName,
                    ["hashes"] = new JObject { ["sha1"] = meta.Sha1 ?? "" },
                    ["downloads"] = new JArray(meta.DownloadUrl!),
                    ["fileSize"] = meta.FileLength,
                });
            }
            else if (required)
            {
                // 必需文件拿不到 URL(作者禁止第三方 API 下载):保留文件名,
                // 先装其余文件,最后让调用方明确提示手动补哪几个 jar。
                missing.Add(new MissingModFile(
                    meta?.FileName ?? "", pid, fid, meta?.Sha1));
            }

            done++;
            if (done % 5 == 0 || done == files.Count)
                progress?.Report(0.9 * done / total);
        }

        var deps = new JObject { ["minecraft"] = gameVersion };
        if (!string.IsNullOrEmpty(depKey) && !string.IsNullOrEmpty(loaderVersion))
            deps[depKey] = loaderVersion;
        var index = new JObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = "1.0.0",
            ["name"] = "",
            ["dependencies"] = deps,
            ["files"] = indexFiles,
        };

        if (File.Exists(outMrpackPath)) File.Delete(outMrpackPath);
        using var dest = ZipFile.Open(outMrpackPath, ZipArchiveMode.Create);
        var indexEntry = dest.CreateEntry("modrinth.index.json");
        using (var writer = new StreamWriter(indexEntry.Open()))
            await writer.WriteAsync(index.ToString()).ConfigureAwait(false);
        foreach (var entry in source.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) &&
                !entry.FullName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase)) continue;
            var copy = dest.CreateEntry(entry.FullName);
            using var es = entry.Open();
            using var ds = copy.Open();
            await es.CopyToAsync(ds, 81920, ct).ConfigureAwait(false);
        }
        progress?.Report(1.0);
        return missing;
    }
}
