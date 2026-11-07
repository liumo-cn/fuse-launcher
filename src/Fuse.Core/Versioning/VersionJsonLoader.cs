using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Versioning;

/// <summary>
/// 从版本目录加载 version.json 并沿 inheritsFrom 递归合并出完整版本。
/// 支持两种布局：&lt;root&gt;/&lt;id&gt;/&lt;id&gt;.json 与 &lt;root&gt;/&lt;id&gt;.json。
/// </summary>
public static class VersionJsonLoader
{
    public static McVersion Load(string versionsRoot, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
            throw new ArgumentException("版本 id 不能为空", nameof(versionId));
        return Resolve(versionsRoot, versionId, new HashSet<string>(StringComparer.Ordinal));
    }

    public static string LocateJson(string versionsRoot, string versionId)
    {
        var nested = Path.Combine(versionsRoot, versionId, versionId + ".json");
        if (File.Exists(nested)) return nested;
        var flat = Path.Combine(versionsRoot, versionId + ".json");
        if (File.Exists(flat)) return flat;
        throw new FileNotFoundException($"找不到版本 {versionId} 的 version.json（查找过 {nested} 与 {flat}）");
    }

    private static McVersion Resolve(string versionsRoot, string versionId, ISet<string> visiting)
    {
        if (!visiting.Add(versionId))
            throw new InvalidOperationException("版本继承链存在循环：" + string.Join(" -> ", visiting.Concat(new[] { versionId })));

        var path = LocateJson(versionsRoot, versionId);
        var json = File.ReadAllText(path);
        var obj = JObject.Parse(json);
        if (obj.Value<string>("id") == null) obj["id"] = versionId;
        var version = McVersion.FromJObject(obj);

        if (string.IsNullOrEmpty(version.InheritsFrom))
            return version;

        var parent = Resolve(versionsRoot, version.InheritsFrom!, visiting);
        return Merge(parent, version);
    }

    /// <summary>
    /// 找出该版本对应的真实 MC 版本号（Modrinth 等接口要的是这个，不是本地版本 id）。
    /// 顺序：clientVersion（自包含版本会写）→ 合并后的 jar（Forge/Fabric 指向父版本）→ id。
    /// </summary>
    public static string ResolveGameVersion(string versionsRoot, string versionId)
    {
        try
        {
            var merged = Load(versionsRoot, versionId);
            if (!string.IsNullOrWhiteSpace(merged.ClientVersion)) return merged.ClientVersion!.Trim();
            if (!string.IsNullOrWhiteSpace(merged.Jar) &&
                !string.Equals(merged.Jar, versionId, StringComparison.OrdinalIgnoreCase))
                return merged.Jar!.Trim();
        }
        catch (Exception)
        {
        }
        return versionId;
    }

    /// <summary>子版本覆盖标量、追加列表；库顺序子在前（同名查找优先子版本）。</summary>
    public static McVersion Merge(McVersion parent, McVersion child)
    {
        return new McVersion
        {
            Id = child.Id,
            InheritsFrom = null,
            MainClass = !string.IsNullOrEmpty(child.MainClass) ? child.MainClass : parent.MainClass,
            // jar 缺省指向**父版本**:原版 jar 在父目录里,派生版本(如 fabric)不另带 jar
            Jar = child.Jar ?? parent.Jar ?? parent.Id,
            ClientVersion = child.ClientVersion ?? parent.ClientVersion,
            Assets = child.Assets ?? parent.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
            Type = child.Type ?? parent.Type,
            ClientDownload = child.ClientDownload ?? parent.ClientDownload,
            LoggingClient = child.LoggingClient ?? parent.LoggingClient,

            // 官方语义：子版本声明 minecraftArguments 时完全覆盖；否则沿父链
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,

            Arguments = MergeArguments(parent.Arguments, child.Arguments),
            Libraries = MergeLibraries(parent.Libraries, child.Libraries),
        };
    }

    /// <summary>
    /// 子版本优先:同一 group:artifact[:classifier] 只保留子版本那份。
    /// Fabric 0.20 带 ASM 9.11,原版 1.21.10 带 ASM 9.6;两份都进 classpath 会被
    /// Fabric loader 判成 duplicate ASM classes 直接退出。
    /// </summary>
    private static List<McLibrary> MergeLibraries(
        IReadOnlyList<McLibrary> parent, IReadOnlyList<McLibrary> child)
    {
        var merged = new List<McLibrary>(child.Count + parent.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lib in child)
            if (seen.Add(LibraryKey(lib))) merged.Add(lib);
        foreach (var lib in parent)
            if (seen.Add(LibraryKey(lib))) merged.Add(lib);
        return merged;
    }

    private static string LibraryKey(McLibrary lib)
    {
        var name = lib.Name ?? "";
        var at = name.IndexOf('@');
        var extension = at >= 0 ? name.Substring(at) : "";
        if (at >= 0) name = name.Substring(0, at);
        var parts = name.Split(':');
        if (parts.Length < 2) return lib.Name ?? "";
        var key = parts[0] + ":" + parts[1];
        // classifier 不同的是不同文件(native 各平台),不能互相覆盖
        if (parts.Length >= 4 && parts[3].Length > 0) key += ":" + parts[3];
        return key + extension;
    }

    private static McArguments? MergeArguments(McArguments? parent, McArguments? child)
    {
        if (parent == null) return child;
        if (child == null) return parent;
        return new McArguments
        {
            Jvm = child.Jvm.Concat(parent.Jvm).ToList(),
            Game = child.Game.Concat(parent.Game).ToList(),
        };
    }
}
