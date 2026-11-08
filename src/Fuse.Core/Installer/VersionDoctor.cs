using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuse.Core.Versioning;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>单个版本的完整性体检结果。</summary>
public sealed class VersionAudit
{
    public string VersionId { get; init; } = "";
    /// <summary>人读的缺失项(如「客户端 jar」「3 个库文件」)。空 = 完好。</summary>
    public List<string> Issues { get; init; } = new();
    public bool NeedsRepair => Issues.Count > 0;
    /// <summary>修复方式:"vanilla" | "fabric" | "forge" | "neoforge"。</summary>
    public string Kind { get; init; } = "vanilla";
    /// <summary>底座 MC 版本(vanilla 即自身)。</summary>
    public string GameVersion { get; init; } = "";
    public string? LoaderVersion { get; init; }

    public string Summary => NeedsRepair ? string.Join("、", Issues) : "完整";
}

/// <summary>
/// 版本完整性体检(设置页「自动补全文件」):
/// 逐个已安装版本沿 inheritsFrom 合并出完整清单,检查客户端 jar / 依赖库 / 资源索引与对象,
/// 缺失的交给现有安装器重跑(安装器会跳过已存在的文件,只补缺口)。
/// </summary>
public static class VersionDoctor
{
    /// <summary>资源对象缺失超过这个数量就不再逐个统计,直接报「大量缺失」以控制耗时。</summary>
    private const int MaxMissingObjectsToReport = 512;

    public static List<VersionAudit> Audit(
        string versionsRoot, string librariesRoot, string assetsRoot)
    {
        var results = new List<VersionAudit>();
        if (!Directory.Exists(versionsRoot)) return results;

        foreach (var dir in Directory.GetDirectories(versionsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id)) continue;
            results.Add(AuditOne(id, versionsRoot, librariesRoot, assetsRoot));
        }
        return results;
    }

    private static VersionAudit AuditOne(
        string versionId, string versionsRoot, string librariesRoot, string assetsRoot)
    {
        var issues = new List<string>();
        McVersion version;
        try
        {
            version = VersionJsonLoader.Load(versionsRoot, versionId);
        }
        catch (Exception)
        {
            // 版本 json 丢了/坏了:从 id 猜底座与加载器(canonical id 形如 26.1-fabric-0.20.1),
            // 让补全至少能把官方 json 重新拉回来;猜不出的在 UI 里明确说"无法自动补全"。
            var inferred = InferFromVersionId(versionId);
            return new VersionAudit
            {
                VersionId = versionId,
                Issues = { "版本文件缺失或损坏(无法读取 version.json)" },
                Kind = inferred.Kind,
                GameVersion = inferred.GameVersion,
                LoaderVersion = inferred.LoaderVersion,
            };
        }

        // 1. 客户端 jar
        try
        {
            var jar = VanillaInstallPlanner.ClientJar(version, versionsRoot);
            if (!File.Exists(jar.DestPath)) issues.Add("客户端 jar");
        }
        catch (Exception)
        {
            // 没有 downloads.client 信息(非官方清单),跳过该项
        }

        // 2. 依赖库
        try
        {
            var libs = VanillaInstallPlanner.LibraryFiles(version, librariesRoot, RuleEnvironment.WindowsX64);
            var missingLibs = libs.Count(i => !File.Exists(i.DestPath));
            if (missingLibs > 0) issues.Add($"{missingLibs} 个库文件");
        }
        catch (Exception)
        {
            issues.Add("库清单解析失败");
        }

        // 3. 资源索引与对象
        if (version.AssetIndex != null && !string.IsNullOrEmpty(version.AssetIndex.Id))
        {
            var indexPath = Path.Combine(assetsRoot, "indexes", version.AssetIndex.Id + ".json");
            if (!File.Exists(indexPath))
            {
                issues.Add("资源索引");
            }
            else
            {
                var missingObjects = CountMissingObjects(indexPath, assetsRoot);
                if (missingObjects > MaxMissingObjectsToReport) issues.Add("大量资源文件");
                else if (missingObjects > 0) issues.Add($"{missingObjects} 个资源文件");
            }
        }

        var gameVersion = RootGameVersion(version, versionsRoot);
        if (string.IsNullOrEmpty(gameVersion))
            gameVersion = InferFromVersionId(versionId).GameVersion;
        var loader = DetectLoader(version, versionId, gameVersion);
        return new VersionAudit
        {
            VersionId = versionId,
            Issues = issues,
            Kind = loader.Kind,
            GameVersion = gameVersion,
            LoaderVersion = loader.LoaderVersion,
        };
    }

    private sealed record LoaderInfo(string Kind, string? LoaderVersion);

    /// <summary>
    /// 从合并后的 libraries 识别加载器与版本。Forge/NeoForge 的坐标在不同年代不一样
    /// (forge / fmlloader / neoforge / fancymodloader),识别不出时退回按 canonical id 解析。
    /// </summary>
    private static LoaderInfo DetectLoader(McVersion version, string versionId, string gameVersion)
    {
        var libs = version.Libraries;
        const string fabricPrefix = "net.fabricmc:fabric-loader:";
        var fabric = libs.FirstOrDefault(l => l.Name != null &&
            l.Name.StartsWith(fabricPrefix, StringComparison.OrdinalIgnoreCase));
        if (fabric != null)
            return new LoaderInfo("fabric", fabric.Name.Substring(fabricPrefix.Length));

        var neoforge = libs.FirstOrDefault(l => l.Name != null &&
            l.Name.StartsWith("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase));
        if (neoforge != null)
            return new LoaderInfo("neoforge", AfterLastColon(neoforge.Name));

        var forge = libs.FirstOrDefault(l => l.Name != null &&
            l.Name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase));
        if (forge != null)
        {
            // 1.12.2 这种老 Forge 的坐标是 1.12.2-14.23.5.2860,
            // 拿整串去拼 installer 路径会变成 1.12.2-1.12.2-… → 404
            var raw = AfterLastColon(forge.Name);
            var prefix = gameVersion + "-";
            if (!string.IsNullOrEmpty(gameVersion) &&
                raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(prefix.Length);
            return new LoaderInfo("forge", raw);
        }

        // 现代 Forge 没有 net.minecraftforge:forge 主条目:fmlloader 的版本是 "1.20.1-47.4.10"
        var fml = libs.FirstOrDefault(l => l.Name != null &&
            l.Name.StartsWith("net.minecraftforge:fmlloader:", StringComparison.OrdinalIgnoreCase));
        if (fml != null)
        {
            var raw = AfterLastColon(fml.Name);
            var dash = raw.IndexOf('-');
            return new LoaderInfo("forge", dash >= 0 ? raw.Substring(dash + 1) : raw);
        }

        // 库清单认不出(比如版本 json 只有 inheritsFrom):按 id 解析
        var inferred = InferFromVersionId(versionId);
        return inferred.Kind == "vanilla"
            ? new LoaderInfo("vanilla", null)
            : new LoaderInfo(inferred.Kind, inferred.LoaderVersion);
    }

    private static string AfterLastColon(string coord)
    {
        var idx = coord.LastIndexOf(':');
        return idx >= 0 && idx + 1 < coord.Length ? coord.Substring(idx + 1) : coord;
    }

    private sealed record InferredLoader(string Kind, string GameVersion, string? LoaderVersion);

    /// <summary>按常见版本 id 形态推断:<mc>-fabric-&lt;ver&gt; / -forge- / -neoforge-;纯版本号视为原版。</summary>
    private static InferredLoader InferFromVersionId(string versionId)
    {
        foreach (var (marker, kind) in new[]
                 {
                     ("-neoforge-", "neoforge"),
                     ("-fabric-", "fabric"),
                     ("-forge-", "forge"),
                 })
        {
            var idx = versionId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                var game = versionId.Substring(0, idx);
                var loader = versionId.Substring(idx + marker.Length);
                if (loader.Length > 0)
                    return new InferredLoader(kind, game, loader);
            }
        }
        // 纯版本号(26.3 / 1.20.1):当作原版;modpack 自定义 id 猜不出底座,GameVersion 留空
        var looksVanilla = versionId.Length > 0 &&
            (char.IsDigit(versionId[0]) || versionId.StartsWith("v", StringComparison.OrdinalIgnoreCase));
        return new InferredLoader("vanilla", looksVanilla ? versionId : "", null);
    }

    /// <summary>沿 inheritsFrom 找到最底层的原版版本号。</summary>
    private static string RootGameVersion(McVersion version, string versionsRoot)
    {
        var id = version.Id;
        for (var guard = 0; guard < 8; guard++)
        {
            string? parent;
            try
            {
                var json = VersionJsonLoader.LocateJson(versionsRoot, id);
                parent = JObject.Parse(File.ReadAllText(json)).Value<string>("inheritsFrom");
            }
            catch (Exception)
            {
                break;
            }
            if (string.IsNullOrEmpty(parent)) break;
            id = parent!;
        }
        return id;
    }

    /// <summary>统计 assets/objects 里缺失的对象文件数量(只 stat,不做哈希)。</summary>
    private static int CountMissingObjects(string indexPath, string assetsRoot)
    {
        try
        {
            var json = JObject.Parse(File.ReadAllText(indexPath));
            var objects = json["objects"] as JObject;
            if (objects == null) return 0;
            var missing = 0;
            foreach (var prop in objects.Properties())
            {
                var hash = prop.Value.Value<string>("hash");
                if (string.IsNullOrEmpty(hash) || hash!.Length < 2) continue;
                var path = Path.Combine(assetsRoot, "objects", hash.Substring(0, 2), hash);
                if (!File.Exists(path) && ++missing > MaxMissingObjectsToReport) break;
            }
            return missing;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
