using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Versioning;
using Newtonsoft.Json;

namespace Fuse.Core.Installer;

/// <summary>版本导出结果。</summary>
public sealed class ExportResult
{
    public string TargetPath { get; init; } = "";
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>
/// 版本导出(FR-12 详情页「导出」分类):
/// ① 完整版本 zip(含存档,不含个人数据/日志);
/// ② 启动器可识别的下载清单 .mcdl.json(拖回下载页面自动开始下载:MC 版本/加载器版本/模组)。
/// </summary>
public static class VersionExporter
{
    /// <summary>排除项:日志与临时目录。存档(saves)按用户要求**包含**在完整版本 zip 里。</summary>
    private static readonly string[] ExcludedDirs = { "logs", "crash-reports", "bin", "cache" };

    /// <summary>排除项:个人数据文件(键位/视频设置、服务器列表、账号缓存),不随版本分发。</summary>
    private static readonly string[] ExcludedFiles =
    {
        "options.txt", "optionsof.txt", "optionsshaders.txt",
        "servers.dat", "servers.dat_old", "usercache.json",
        "launcher_profiles.json", "realms_pending_invitations.txt", "sync.ini",
    };

    internal static string RelPath(string root, string file)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fileFull = Path.GetFullPath(file);
        if (!fileFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(fileFull);
        return fileFull.Substring(rootFull.Length);
    }

    /// <summary>收集一个目录下全部相对路径(排除目录)。</summary>
    private static List<string> CollectFiles(string rootDir, string subDir)
    {
        var full = string.IsNullOrEmpty(subDir) ? rootDir : Path.Combine(rootDir, subDir);
        var result = new List<string>();
        if (!Directory.Exists(full)) return result;
        foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var rel = RelPath(full, f).Replace('\\', '/');
            var top = rel.Split('/')[0];
            if (ExcludedDirs.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(f);
        }
        return result;
    }

    /// <summary>① 完整版本 zip:版本目录 + 实例目录(含存档,不含个人数据/日志)+ 依赖库。</summary>
    public static Task<ExportResult> ExportFullZipAsync(
        string versionId, string versionsRoot, string instancesRoot, string librariesRoot,
        string targetZip, CancellationToken cancellation)
        // zip 写盘是纯 IO,挪到线程池避免占用调用方(UI)线程
        => Task.Run(() => ExportFullZipCore(versionId, versionsRoot, instancesRoot, librariesRoot, targetZip, cancellation));

    private static ExportResult ExportFullZipCore(
        string versionId, string versionsRoot, string instancesRoot, string librariesRoot,
        string targetZip, CancellationToken cancellation)
    {
        var tmp = targetZip + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetZip))!);
        using (var zipStream = new FileStream(tmp, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var count = 0;
            var bytes = 0L;

            void AddFile(string file, string entryName)
            {
                var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Fastest);
                using var source = File.OpenRead(file);
                using var es = entry.Open();
                source.CopyTo(es, 128 * 1024);
                count++;
                bytes += new FileInfo(file).Length;
            }

            void AddTree(string dir, string prefix)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(dir.Length + (dir.EndsWith(Path.DirectorySeparatorChar.ToString()) ? 0 : 1));
                    if (ExcludedDirs.Contains(rel.Split(Path.DirectorySeparatorChar)[0],
                            StringComparer.OrdinalIgnoreCase)) continue;
                    if (ExcludedFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)) continue;
                    AddFile(f, prefix + "/" + rel.Replace('\\', '/'));
                }
            }

            AddTree(Path.Combine(versionsRoot, versionId), "versions/" + versionId);
            AddTree(Path.Combine(instancesRoot, versionId), "instances/" + versionId);
            AddTree(librariesRoot, "libraries");
        }
        if (File.Exists(targetZip)) File.Delete(targetZip);
        File.Move(tmp, targetZip);

        var files = CountFiles(targetZip);
        return new ExportResult { TargetPath = targetZip, FileCount = files, TotalBytes = new FileInfo(targetZip).Length };
    }

    private static int CountFiles(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.Count;
    }

    /// <summary>清单中的模组条目(来自实例 mods.mcdl.json 安装记录)。</summary>
    public sealed class ManifestModItem
    {
        public string Url { get; init; } = "";
        public string FileName { get; init; } = "";
        public string Sha1 { get; init; } = "";
    }

    /// <summary>
    /// ② 下载清单 .mcdl.json:启动器可识别,拖回下载页面自动开始下载
    /// (MC 版本/加载器版本/模组;不含存档地图、个人数据)。
    /// </summary>
    public static string BuildDownloadManifest(
        string gameVersion, string? loaderVersion,
        string gameVersionJsonUrl, string gameVersionSha1, string? loaderUrl,
        IReadOnlyList<ManifestModItem> mods)
    {
        var items = new List<object>
        {
            new
            {
                type = "mc-version",
                versionId = gameVersion,
                url = gameVersionJsonUrl,
                sha1 = gameVersionSha1,
            }
        };
        if (!string.IsNullOrEmpty(loaderVersion))
            items.Add(new
            {
                type = "loader",
                loaderType = "fabric",
                loaderVersion,
                gameVersion,
                url = loaderUrl ?? "",
                fileName = "profile.json",
                sha1 = "",
            });
        foreach (var m in mods)
            items.Add(new { type = "mod", url = m.Url, fileName = m.FileName, sha1 = m.Sha1, gameVersion });
        return JsonConvert.SerializeObject(new { kind = "mcdl-manifest", version = 1, items }, Formatting.Indented);
    }
}
