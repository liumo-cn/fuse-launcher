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
    /// <summary>本体文件名(不含前置)。用于安装后在 content.mcdl.json 里记一笔。</summary>
    public string MainFileName { get; set; } = "";
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
        // 从新到旧尝试主模组版本:最新的 Iris 可能钉了 Sodium 0.9.2(只支持 26.1.2),
        // 当前 26.1 上应自动回退到 Iris 1.11.3 + Sodium 0.9.1。
        var candidates = await ModrinthSource.GetVersionFilesAsync(
            http, projectId, gameVersion, loader, cancellation).ConfigureAwait(false);

        Exception? last = null;
        foreach (var modFile in candidates)
        {
            try
            {
                if (!await ModrinthSource.IsJarCompatibleWithMcAsync(
                        http, modFile, gameVersion, cancellation).ConfigureAwait(false))
                    continue;
                return await BuildPlanForAsync(http, projectId, modFile, gameVersion, loader, modsDir, cancellation)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException(
            last?.Message ?? "没有找到依赖能够同时满足当前 Minecraft 版本的模组版本", last);
    }

    private static async Task<ModInstallPlan> BuildPlanForAsync(
        HttpClient http, string projectId, ModrinthVersionFile modFile,
        string gameVersion, string loader, string modsDir, CancellationToken cancellation)
    {
        var plan = new ModInstallPlan();

        // 2. 前置检测(required 依赖逐个解析)
        var pending = new Queue<ModrinthDependency>(modFile.RequiredDeps);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectId };
        while (pending.Count > 0)
        {
            var dep = pending.Dequeue();
            var depId = dep.ProjectId;
            if (!visited.Add(depId)) continue;

            ModrinthVersionFile depFile;
            if (!string.IsNullOrEmpty(dep.VersionId))
            {
                // 上游钉死了依赖版本(Iris → Sodium 0.9.2):必须原样使用,
                // 并且它必须支持当前 MC,否则装上去必然报 Incompatible mods。
                depFile = await ModrinthSource.GetVersionFileByIdAsync(http, dep.VersionId, cancellation)
                    .ConfigureAwait(false);
                if (!await ModrinthSource.IsJarCompatibleWithMcAsync(http, depFile, gameVersion, cancellation)
                    .ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"这个模组固定依赖 {depId} 的 {depFile.FileName}，它只支持 Minecraft " +
                        string.Join(" / ", depFile.GameVersions) + "；当前实例是 " + gameVersion +
                        "。请升级游戏版本，或换一个支持当前版本的模组版本。");
                }
            }
            else
            {
                depFile = await ModrinthSource.GetProjectLatestFileAsync(
                    http, depId, gameVersion, loader, cancellation).ConfigureAwait(false);
            }

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

            // 前置自己的前置继续入队(保留上游钉死的版本)
            foreach (var d in depFile.RequiredDeps)
                if (!visited.Contains(d.ProjectId)) pending.Enqueue(d);
        }

        // 3. 本体(已有同名同哈希则跳过)
        plan.MainFileName = modFile.FileName;
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
