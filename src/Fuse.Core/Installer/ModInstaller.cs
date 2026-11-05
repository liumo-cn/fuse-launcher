using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Fuse.Core.Sources;

namespace Fuse.Core.Installer;

/// <summary>模组安装结果:本体 + 缺失前置的下载清单。</summary>
public sealed class ModInstallPlan
{
    /// <summary>待下载文件(本体 + 缺失前置),目标都在 mods 目录。</summary>
    public List<FileDownloadItem> Items { get; init; } = new();
    /// <summary>已存在(跳过)的前置文件名。</summary>
    public List<string> AlreadyPresent { get; init; } = new();
}

/// <summary>
/// 模组安装(含前置检测):点 ⬇ → 解析该模组 required 前置 →
/// 检查实例 mods 目录已有哪个 → 缺的一并下载,已有则只下本体。
/// </summary>
public static class ModInstaller
{
    public static async Task<ModInstallPlan> PlanAsync(
        HttpClient http, string projectId, string gameVersion, string loader,
        string modsDir, CancellationToken cancellation)
    {
        var plan = new ModInstallPlan();

        // 1. 本体
        var modFile = await ModrinthSource.GetLatestVersionFileAsync(
            http, projectId, gameVersion, loader, cancellation).ConfigureAwait(false);

        // 2. 前置检测(required 依赖逐个解析)
        var pending = new Queue<string>(modFile.RequiredDeps.Select(d => d.ProjectId));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectId };
        while (pending.Count > 0)
        {
            var depId = pending.Dequeue();
            if (!visited.Add(depId)) continue;

            var depFile = await ModrinthSource.GetProjectLatestFileAsync(
                http, depId, gameVersion, loader, cancellation).ConfigureAwait(false);

            var dest = Path.Combine(modsDir, depFile.FileName);
            var alreadyThere = File.Exists(dest) &&
                await Download.Hashing.VerifyFileAsync(dest, depFile.Sha1, HashAlgo.Sha1, cancellation)
                    .ConfigureAwait(false);

            if (alreadyThere)
                plan.AlreadyPresent.Add(depFile.FileName);
            else
                plan.Items.Add(new FileDownloadItem
                {
                    Url = depFile.Url,
                    DestPath = dest,
                    ExpectedHash = string.IsNullOrEmpty(depFile.Sha1) ? null : depFile.Sha1,
                });

            // 前置自己的前置继续入队
            foreach (var d in depFile.RequiredDeps.Select(x => x.ProjectId))
                if (!visited.Contains(d)) pending.Enqueue(d);
        }

        // 3. 本体(已有同名同哈希则跳过)
        var modDest = Path.Combine(modsDir, modFile.FileName);
        var modExists = File.Exists(modDest) &&
            await Download.Hashing.VerifyFileAsync(modDest, modFile.Sha1, HashAlgo.Sha1, cancellation)
                .ConfigureAwait(false);
        if (!modExists)
            plan.Items.Insert(0, new FileDownloadItem
            {
                Url = modFile.Url,
                DestPath = modDest,
                ExpectedHash = string.IsNullOrEmpty(modFile.Sha1) ? null : modFile.Sha1,
            });
        else
            plan.AlreadyPresent.Add(modFile.FileName);

        return plan;
    }
}
