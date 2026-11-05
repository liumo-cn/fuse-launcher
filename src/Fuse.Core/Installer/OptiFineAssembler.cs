using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Fuse.Core.Sources;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// "可装模组的 OptiFine 环境"组装(方案 A):
///   ① 装 Fabric 底座(独立版本,继承原版,不动现有 OptiFine 版本)
///   ② 从 Modrinth 装 OptiFabric 桥接模组 —— 查不到就直接失败(由上层取消整个任务)
///   ③ 把 OptiFine 本体 jar 复制进该实例的 mods 目录
/// 结果版本形如 1.19.4-fabric-0.16.9,模组页就是普通 Fabric 环境。
/// </summary>
public static class OptiFineAssembler
{
    public static async Task<string> AssembleAsync(
        HttpClient http, string mcVersion, string loaderVersion,
        string versionsRoot, string librariesRoot, string assetsRoot, string instancesRoot,
        IProgress<double>? progress, CancellationToken ct)
    {
        var fabricId = $"{mcVersion}-fabric-{loaderVersion}";
        var instanceDir = Path.Combine(instancesRoot, fabricId);

        // 1. Fabric 底座(独立版本,继承原版)
        progress?.Report(0.02);
        await FabricInstaller.InstallAsync(http, mcVersion, loaderVersion,
            versionsRoot, librariesRoot, assetsRoot, instanceDir,
            new ScaledProgress(progress, 0.02, 0.60), ct).ConfigureAwait(false);
        progress?.Report(0.62);

        // 2. OptiFine 本体当 mod:安装器已经把它落在 libraries/optifine/OptiFine/<ver>/
        //    OptiFabric 不在 Modrinth/GitHub Release(仅 CurseForge),按用户选择走"半自动":
        //    这里只把 OptiFine jar 放进 mods,OptiFabric 由界面提示用户手动放入。
        var modsDir = Path.Combine(instanceDir, "mods");
        Directory.CreateDirectory(modsDir);
        var optifineJar = FindOptiFineJar(librariesRoot, mcVersion);
        if (optifineJar == null)
            throw new FileNotFoundException("找不到 OptiFine 本体 jar(请先安装 OptiFine)");
        var dest = Path.Combine(modsDir, Path.GetFileName(optifineJar));
        if (!File.Exists(dest)) File.Copy(optifineJar, dest);
        progress?.Report(0.95);

        progress?.Report(1.0);
        return fabricId;
    }

    /// <summary>在 libraries/optifine/OptiFine/*/ 里找与本 MC 版本对应的 OptiFine jar。</summary>
    private static string? FindOptiFineJar(string librariesRoot, string mcVersion)
    {
        var root = Path.Combine(librariesRoot, "optifine", "OptiFine");
        if (!Directory.Exists(root)) return null;
        // 版本目录名形如 1.19.4_HD_U_I4:按 MC 版本前缀匹配,取最新(字符串序最后)
        return Directory.EnumerateDirectories(root)
            .Where(d => Path.GetFileName(d).StartsWith(mcVersion, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.jar"))
            .FirstOrDefault();
    }

    /// <summary>把子进度映射到整体区间。</summary>
    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double>? _inner;
        private readonly double _offset;
        private readonly double _weight;
        public ScaledProgress(IProgress<double>? inner, double offset, double weight)
        {
            _inner = inner;
            _offset = offset;
            _weight = weight;
        }
        public void Report(double value) => _inner?.Report(_offset + _weight * value);
    }
}
