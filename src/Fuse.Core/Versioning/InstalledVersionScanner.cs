using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Versioning;

/// <summary>已安装版本的概要信息（版本管理页卡片 / 启动首页浮层）。</summary>
public sealed class InstalledVersionInfo
{
    public string Id { get; init; } = "";
    /// <summary>版本所在的游戏目录(多目录支持:启动/详情操作都用它定位)。</summary>
    public string RootDirectory { get; init; } = "";
    public int? JavaMajor { get; init; }
    public string? ReleaseTime { get; init; }
    public long SizeBytes { get; init; }
    public int ModsCount { get; init; }
    /// <summary>能否找到可启动的客户端 jar（派生版本用父版本的 jar）。</summary>
    public bool HasClientJar { get; init; }

    public string JavaDisplay => JavaMajor.HasValue ? "Java " + JavaMajor.Value : "Java 自动";
    public string SizeDisplay => SizeBytes >= 1L << 30
        ? (SizeBytes / (double)(1L << 30)).ToString("0.0") + " GB"
        : (SizeBytes / (double)(1L << 20)).ToString("0") + " MB";
    public string ModsDisplay => ModsCount + " 模组";
}

/// <summary>
/// 扫描 versions 目录列出已安装版本（FR-08）。
/// 体积 = 版本目录（含实例游戏目录）之和；模组数取 instances/&lt;id&gt;/mods 下未停用的 jar。
/// </summary>
public static class InstalledVersionScanner
{
    /// <summary>
    /// 扫描一个游戏目录的 versions。externalLayout = true 表示官方/第三方共享根布局
    /// (实例目录 = 根本身,mods 在 root\mods);false 是我们的 instances 隔离布局。
    /// </summary>
    public static IReadOnlyList<InstalledVersionInfo> Scan(
        string versionsRoot, string instancesRoot, string librariesRoot,
        bool externalLayout = false)
    {
        if (!Directory.Exists(versionsRoot)) return Array.Empty<InstalledVersionInfo>();
        var rootDir = Path.GetFullPath(Path.Combine(versionsRoot, ".."));

        var result = new List<InstalledVersionInfo>();
        foreach (var dir in Directory.EnumerateDirectories(versionsRoot))
        {
            var id = Path.GetFileName(dir);
            var jsonPath = Path.Combine(dir, id + ".json");
            if (!File.Exists(jsonPath)) continue;

            int? javaMajor = null;
            string? releaseTime = null;
            bool hasClientJar = false;
            try
            {
                // 必须走 inheritsFrom 合并：Forge/NeoForge/Fabric 的 version.json 自身不带
                // javaVersion 和 jar，Java 版本与客户端 jar 都在父版本里。
                var merged = VersionJsonLoader.Load(versionsRoot, id);
                var major = merged.JavaVersion?.MajorVersion ?? 0;
                if (major > 0) javaMajor = major;
                releaseTime = merged.ReleaseTime;
                var jarId = string.IsNullOrEmpty(merged.Jar) ? id : merged.Jar!;
                hasClientJar = File.Exists(Path.Combine(versionsRoot, jarId, jarId + ".jar"))
                    || File.Exists(Path.Combine(versionsRoot, jarId + ".jar"));
            }
            catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException
                                       || ex is InvalidOperationException || ex is ArgumentException)
            {
                // 继承链断裂/JSON 损坏时退回单文件解析，至少把自身声明的信息读出来
                try
                {
                    var obj = JObject.Parse(File.ReadAllText(jsonPath));
                    javaMajor = obj["javaVersion"]?["majorVersion"]?.Value<int?>();
                    releaseTime = obj.Value<string>("releaseTime");
                    hasClientJar = File.Exists(Path.Combine(versionsRoot, id, id + ".jar"));
                }
                catch (IOException)
                {
                }
                catch (Newtonsoft.Json.JsonException)
                {
                }
            }

            var size = DirectorySize(dir);
            // 外部布局:共享根时实例目录 = 根(mods/saves 在根下);版本隔离时 = versions/<id>
            var instanceDir = externalLayout
                ? InstanceLayout.ResolveExternal(rootDir, id)
                : Path.Combine(instancesRoot, id);
            if (Directory.Exists(instanceDir) &&
                !instanceDir.Equals(rootDir, StringComparison.OrdinalIgnoreCase))
            {
                size += DirectorySize(instanceDir);
            }
            else if (externalLayout)
            {
                // 共享根:体积仍要把根下的 mods 算上(否则列表体积会突然变小)
                var extMods = Path.Combine(rootDir, "mods");
                if (Directory.Exists(extMods)) size += DirectorySize(extMods);
            }

            var modsDir = Path.Combine(instanceDir, "mods");
            var modsCount = Directory.Exists(modsDir)
                ? Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                    .Count(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                : 0;

            result.Add(new InstalledVersionInfo
            {
                Id = id,
                RootDirectory = rootDir,
                JavaMajor = javaMajor,
                ReleaseTime = releaseTime,
                SizeBytes = size,
                ModsCount = modsCount,
                HasClientJar = hasClientJar,
            });
        }
        return result.OrderByDescending(v => v.ReleaseTime).ToList();
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f =>
                {
                    try { return new FileInfo(f).Length; }
                    catch (IOException) { return 0L; }
                });
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
