using System;
using System.Collections.Generic;
using System.Linq;

namespace Fuse.Core.ModConflict;

public enum ModFixKind
{
    InstallDependency,
    DisableDuplicate,
    ReplaceVersion,
    DisableIncompatible,
}

public sealed class ModFixAction
{
    public ModFixKind Kind { get; init; }
    public string ModId { get; init; } = "";
    public string ModName { get; init; } = "";
    /// <summary>要禁用/替换的本地文件(InstallDependency 为空)。</summary>
    public string FileName { get; init; } = "";
    /// <summary>缺失的依赖 id(InstallDependency / ReplaceVersion)。</summary>
    public string DependencyId { get; init; } = "";
    public string DependencyName { get; init; } = "";
    public string Constraint { get; init; } = "";
    public string InstalledVersion { get; init; } = "";
    public string InstalledSha1 { get; init; } = "";
    /// <summary>MC 版本不兼容时:找不到适配新版本的构建就自动禁用,避免死循环。</summary>
    public bool DisableIfNoReplacement { get; init; }
    public string RequiredGameVersion { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool CanAutoFix { get; init; } = true;
}

public sealed class ModConflictReport
{
    public string GameVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public int ModCount { get; init; }
    /// <summary>模组要求的游戏版本(用于建议切换)。</summary>
    public string? SuggestedGameVersion { get; init; }
    public List<ModFixAction> Actions { get; init; } = new();
    public bool HasProblems => Actions.Count > 0;
}

/// <summary>
/// 静态冲突分析:扫描 jar 内 fabric.mod.json,找重复 mod id、缺失前置、
/// 版本不满足、模组不支持当前 MC 版本。不解析本地化报错文本,避免语言差异。
/// </summary>
public static class ModConflictAnalyzer
{
    private static bool IsNewer(string candidate, string current)
    {
        var a = VersionRange.Parse(candidate);
        var b = VersionRange.Parse(current);
        return a != null && b != null && a.CompareTo(b) > 0;
    }

    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "fabricloader", "fabric-loader", "java", "fabric", "quilt_loader", "quilt-loader",
    };

    public static ModConflictReport Analyze(string modsDir, string gameVersion, string loader,
        string? errorText = null)
    {
        // 目前只解析 fabric.mod.json。Forge/NeoForge 的元数据在 META-INF/mods.toml,
        // 硬按 Fabric 规则分析会把 Forge 整合包误判成缺一堆前置,甚至装进错误的 jar。
        if (!string.IsNullOrEmpty(loader) &&
            !loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
            return new ModConflictReport { GameVersion = gameVersion, Loader = loader };
        var all = ModScanner.Scan(modsDir);
        var enabled = all.Where(m => !m.Disabled && !string.IsNullOrEmpty(m.Id)).ToList();
        var actions = new List<ModFixAction>();

        // 1) 重复 mod id:保留版本最高的一个,其余禁用
        var effective = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in enabled.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            var keep = list.OrderByDescending(m => VersionRange.Parse(m.Version), Comparer<VersionRange.Version?>.Create(
                (a, b) => (a?.CompareTo(b) ?? (b == null ? 0 : -1)))).First();
            effective[keep.Id] = keep;
            foreach (var other in list.Where(m => !ReferenceEquals(m, keep)))
            {
                actions.Add(new ModFixAction
                {
                    Kind = ModFixKind.DisableDuplicate,
                    ModId = other.Id,
                    ModName = other.Name,
                    FileName = other.FileName,
                    InstalledVersion = other.Version,
                    Reason = $"重复安装:{other.FileName} 与 {keep.FileName} 是同一个模组，保留 {keep.Version}",
                });
            }
        }

        // Fabric API 等模组通过 provides 提供模块 id(如 fabric-rendering-fluids-v1),
        // 这些不是独立 jar,不能当成缺失前置。
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in effective.Values)
            foreach (var id in mod.Provides) provided.Add(id);

        // 2) 缺失前置 / 版本不满足 / MC 版本不兼容
        string? suggestedGameCandidate = null;
        foreach (var mod in effective.Values)
        {
            foreach (var dep in mod.Depends)
            {
                var depId = dep.Key;
                var constraint = dep.Value;
                if (BuiltIns.Contains(depId)) continue;

                if (!effective.TryGetValue(depId, out var installed))
                {
                    if (provided.Contains(depId)) continue;
                    if (depId.Equals("fabric-api", StringComparison.OrdinalIgnoreCase) &&
                        effective.Values.Any(m => m.Id.StartsWith("fabric-api", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    actions.Add(new ModFixAction
                    {
                        Kind = ModFixKind.InstallDependency,
                        ModId = depId,
                        ModName = depId,
                        DependencyId = depId,
                        DependencyName = depId,
                        Constraint = constraint,
                        Reason = $"{mod.Name} 需要 {depId} {VersionRange.Describe(constraint)}，但没有安装",
                    });
                    continue;
                }

                if (!VersionRange.Matches(installed.Version, constraint))
                {
                    actions.Add(new ModFixAction
                    {
                        Kind = ModFixKind.ReplaceVersion,
                        ModId = installed.Id,
                        ModName = installed.Name,
                        FileName = installed.FileName,
                        DependencyId = installed.Id,
                        DependencyName = installed.Name,
                        Constraint = constraint,
                        InstalledVersion = installed.Version,
                        InstalledSha1 = installed.Sha1,
                        Reason = $"{mod.Name} 需要 {installed.Name} {VersionRange.Describe(constraint)}，当前是 {installed.Version}",
                    });
                }
            }

            // MC 版本依赖单独判断(约束是 >= 或精确版本)
            if (mod.Depends.TryGetValue("minecraft", out var mcRange) &&
                !VersionRange.Matches(gameVersion, mcRange))
            {
                if (IsNewer(mcRange.TrimStart('>', '=', '^', '~', ' '), gameVersion))
                    suggestedGameCandidate ??= mcRange.TrimStart('>', '=', '^', '~', ' ');
                actions.Add(new ModFixAction
                {
                    Kind = ModFixKind.ReplaceVersion,
                    ModId = mod.Id,
                    ModName = mod.Name,
                    FileName = mod.FileName,
                    DependencyId = mod.Id,
                    DependencyName = mod.Name,
                    InstalledVersion = mod.Version,
                    InstalledSha1 = mod.Sha1,
                    Constraint = "",
                    RequiredGameVersion = gameVersion,
                    DisableIfNoReplacement = true,
                    CanAutoFix = true,
                    Reason = $"{mod.Name} {mod.Version} 只支持 Minecraft {mcRange}，当前是 {gameVersion}；" +
                             $"尝试升级到适配 {gameVersion} 的版本，没有则禁用",
                });
            }
        }

        // 3) 合并 Fabric 报错文本里的线索(中英双语):静态分析漏掉时兜底
        string? suggestedGame = suggestedGameCandidate;
        foreach (var hint in FabricErrorParser.Parse(errorText))
        {
            switch (hint.Kind)
            {
                case FabricErrorParser.HintKind.MissingDependency:
                    if (effective.ContainsKey(hint.ModId) || provided.Contains(hint.ModId)) break;
                    if (actions.Any(a => a.Kind == ModFixKind.InstallDependency && a.DependencyId.Equals(hint.ModId, StringComparison.OrdinalIgnoreCase)))
                        break;
                    actions.Add(new ModFixAction
                    {
                        Kind = ModFixKind.InstallDependency,
                        ModId = hint.ModId,
                        ModName = hint.ModName,
                        DependencyId = hint.ModId,
                        DependencyName = hint.ModName,
                        Constraint = hint.Constraint,
                        Reason = "Fabric 报告缺少前置:" + hint.ModName + " " + hint.Constraint,
                    });
                    break;

                case FabricErrorParser.HintKind.ReplaceVersion:
                    if (!effective.TryGetValue(hint.ModId, out var target)) break;
                    if (VersionRange.MatchesAny(target.Version, hint.Constraint)) break;
                    if (actions.Any(a => a.Kind == ModFixKind.ReplaceVersion && a.ModId.Equals(hint.ModId, StringComparison.OrdinalIgnoreCase)))
                        break;
                    actions.Add(new ModFixAction
                    {
                        Kind = ModFixKind.ReplaceVersion,
                        ModId = target.Id,
                        ModName = target.Name,
                        FileName = target.FileName,
                        DependencyId = target.Id,
                        DependencyName = target.Name,
                        Constraint = hint.Constraint,
                        InstalledVersion = target.Version,
                        InstalledSha1 = target.Sha1,
                        Reason = "Fabric 报告版本不匹配:" + target.Name + " 需要 " + hint.Constraint,
                    });
                    break;

                case FabricErrorParser.HintKind.MinecraftMismatch:
                    // 只建议"升级到更高版本";旧日志里的低版本提示不应触发降级按钮
                    if (IsNewer(hint.RequiredMinecraft, gameVersion))
                        suggestedGame ??= hint.RequiredMinecraft;
                    if (!effective.TryGetValue(hint.ModId, out var mcMod)) break;
                    if (actions.Any(a => a.Kind == ModFixKind.DisableIncompatible && a.ModId.Equals(hint.ModId, StringComparison.OrdinalIgnoreCase)))
                        break;
                    actions.Add(new ModFixAction
                    {
                        Kind = ModFixKind.ReplaceVersion,
                        ModId = mcMod.Id,
                        ModName = mcMod.Name,
                        FileName = mcMod.FileName,
                        DependencyId = mcMod.Id,
                        DependencyName = mcMod.Name,
                        InstalledVersion = mcMod.Version,
                        InstalledSha1 = mcMod.Sha1,
                        Constraint = "",
                        RequiredGameVersion = gameVersion,
                        DisableIfNoReplacement = true,
                        CanAutoFix = true,
                        Reason = mcMod.Name + " 需要 Minecraft " + hint.RequiredMinecraft +
                                 "，当前是 " + gameVersion + "；尝试升级到适配 " + gameVersion + " 的版本，没有则禁用",
                    });
                    break;
            }
        }

        return new ModConflictReport
        {
            GameVersion = gameVersion,
            Loader = loader,
            ModCount = enabled.Count,
            SuggestedGameVersion = suggestedGame,
            Actions = actions
                .GroupBy(a => a.Kind + "|" + a.ModId + "|" + a.FileName)
                .Select(g => g.First())
                .ToList(),
        };
    }
}
