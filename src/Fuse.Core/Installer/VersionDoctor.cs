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
    /// <summary>修复方式:"vanilla" | "fabric"。</summary>
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
            return new VersionAudit
            {
                VersionId = versionId,
                Issues = { "版本文件缺失或损坏(无法读取 version.json)" },
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

        var loader = DetectLoader(version);
        return new VersionAudit
        {
            VersionId = versionId,
            Issues = issues,
            Kind = loader != null ? "fabric" : "vanilla",
            GameVersion = RootGameVersion(version, versionsRoot),
            LoaderVersion = loader,
        };
    }

    /// <summary>从合并后的 libraries 里找 fabric-loader 版本号。</summary>
    private static string? DetectLoader(McVersion version)
    {
        const string prefix = "net.fabricmc:fabric-loader:";
        foreach (var lib in version.Libraries)
        {
            if (lib.Name != null && lib.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return lib.Name.Substring(prefix.Length);
        }
        return null;
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
