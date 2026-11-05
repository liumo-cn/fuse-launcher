using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Fuse.Core.Sources;

namespace Fuse.App;

/// <summary>
/// 本地模组更新检查:文件 SHA-1 → Modrinth 反查当前版本 → 取同 MC 版本/加载器的最新版本。
/// 只处理启用中的 .jar;.jar.disabled 不参与(禁用中的模组不该提醒更新)。
///
/// 关键:新版本如果 pin 了前置版本(如 Iris → 指定 Sodium),必须同时更新那个前置;
/// 前置与当前 MC 版本不兼容时整条更新跳过(否则会重演"游戏启动报不兼容")。
/// </summary>
internal static class ModUpdateChecker
{
    internal sealed class UpdateInfo
    {
        public string FilePath { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string Sha1 { get; set; } = "";
        public string NewFileName { get; set; } = "";
    }

    public static async Task<List<UpdateInfo>> CheckAsync(
        string modsDir, string gameVersion, string loader, CancellationToken ct)
    {
        var paths = Directory.Exists(modsDir)
            ? Directory.GetFiles(modsDir, "*.jar")
                .Where(p => !p.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();
        if (paths.Count == 0) return new List<UpdateInfo>();

        var hashes = await ComputeHashesAsync(paths, ct).ConfigureAwait(false);
        var lookup = paths.Select((path, i) => (Path: path, Hash: hashes[i]))
            .Where(x => !string.IsNullOrEmpty(x.Hash))
            .ToList();
        if (lookup.Count == 0) return new List<UpdateInfo>();

        var hashList = lookup.Select(x => x.Hash).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var current = await ModrinthSource.GetVersionsByHashesAsync(
            AppServices.Http, hashList, "sha1", ct).ConfigureAwait(false);
        var latest = await ModrinthSource.GetLatestVersionsByHashesAsync(
            AppServices.Http, hashList, "sha1", gameVersion, loader, ct).ConfigureAwait(false);

        // 本地已装模组按 project_id 建索引:校验新版本 pin 的前置时要用
        var localByProject = new Dictionary<string, (string Path, string VersionId, string VersionNumber)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in lookup)
        {
            if (!current.TryGetValue(hash, out var cur)) continue;
            if (string.IsNullOrEmpty(cur.ProjectId)) continue;
            localByProject[cur.ProjectId] = (path, cur.File.VersionId, cur.VersionNumber);
        }

        var updates = new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase); // key = 被替换的本地文件
        var depCache = new Dictionary<string, ModrinthVersionFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, hash) in lookup)
        {
            if (!current.TryGetValue(hash, out var cur)) continue;
            if (!latest.TryGetValue(hash, out var next)) continue;
            if (string.Equals(cur.File.VersionId, next.File.VersionId, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(next.File.FileName) || string.IsNullOrEmpty(next.File.Url)) continue;

            // 新版本 pin 的前置:本地有这个前置时,必须兼容当前 MC 版本,并把前置一起更新
            var compatible = true;
            var depUpdates = new List<UpdateInfo>();
            foreach (var dep in next.File.RequiredDeps)
            {
                if (string.IsNullOrEmpty(dep.VersionId)) continue;
                if (!localByProject.TryGetValue(dep.ProjectId, out var localDep)) continue;

                if (!depCache.TryGetValue(dep.VersionId!, out var depFile))
                {
                    try
                    {
                        depFile = await ModrinthSource.GetVersionFileByIdAsync(
                            AppServices.Http, dep.VersionId!, ct).ConfigureAwait(false);
                        depCache[dep.VersionId!] = depFile;
                    }
                    catch (Exception)
                    {
                        compatible = false;
                        break;
                    }
                }
                if (!SupportsGameVersion(depFile, gameVersion))
                {
                    // 这个新版本要求的配套版本不支持当前 MC → 整个更新跳过
                    compatible = false;
                    break;
                }
                if (string.Equals(depFile.VersionId, localDep.VersionId, StringComparison.Ordinal)) continue;
                depUpdates.Add(new UpdateInfo
                {
                    FilePath = localDep.Path,
                    CurrentVersion = localDep.VersionNumber,
                    LatestVersion = "配套版本",
                    DownloadUrl = depFile.Url,
                    Sha1 = depFile.Sha1,
                    NewFileName = depFile.FileName,
                });
            }
            if (!compatible) continue;

            updates[path] = new UpdateInfo
            {
                FilePath = path,
                CurrentVersion = cur.VersionNumber,
                LatestVersion = next.VersionNumber,
                DownloadUrl = next.File.Url,
                Sha1 = next.File.Sha1,
                NewFileName = next.File.FileName,
            };
            foreach (var depUpdate in depUpdates)
                updates[depUpdate.FilePath] = depUpdate;
        }

        return updates.Values.OrderBy(u => u.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<string[]> ComputeHashesAsync(List<string> paths, CancellationToken ct)
    {
        var hashes = new string[paths.Count];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(paths.Select(async (path, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                hashes[i] = await Hashing.ComputeFileAsync(path, HashAlgo.Sha1, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                hashes[i] = "";
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return hashes;
    }

    private static bool SupportsGameVersion(ModrinthVersionFile file, string gameVersion)
        => file.GameVersions.Count == 0 ||
           file.GameVersions.Any(v => string.Equals(v, gameVersion, StringComparison.OrdinalIgnoreCase));
}
