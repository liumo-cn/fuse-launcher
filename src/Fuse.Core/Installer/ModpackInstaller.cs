using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
    public static void ExtractOverrides(string extractDir, string gameDir, ISet<string> reserved)
    {
        foreach (var rootName in new[] { "overrides", "client-overrides" })
        {
            var root = Path.Combine(extractDir, rootName);
            if (!Directory.Exists(root)) continue;
            var fullRoot = Path.GetFullPath(root);
            foreach (var src in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
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
    public static void ExtractAll(string mrpackPath, string extractDir)
    {
        Directory.CreateDirectory(extractDir);
        using var zip = ZipFile.OpenRead(mrpackPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = Path.Combine(extractDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
