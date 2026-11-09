using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuse.Core.ModConflict;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// 把外部共享目录(没开版本隔离)的某个版本迁移成独立实例:
/// 复制存档/配置/资源包,把只支持当前版本的模组从共用 mods 移进 versions/&lt;id&gt;/mods。
/// 支持其它共用版本的模组保持原位,避免把别人要用的文件移走。
/// </summary>
public static class SharedInstanceMigrator
{
    public sealed class IsolationResult
    {
        public List<string> CopiedMods { get; } = new();
        public List<string> MovedMods { get; } = new();
        public int SharedModsBefore { get; set; }
        public int SharedModsRemaining { get; set; }
    }

    private static readonly string[] SharedDirs =
    {
        "saves", "config", "resourcepacks", "shaderpacks",
        "defaultconfigs", "scripts", "kubejs",
    };

    private static readonly string[] SharedFiles =
    {
        "options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat",
    };

    public static IsolationResult Enable(
        string gameRoot, string versionId, string gameVersion, string loader,
        IReadOnlyCollection<string> otherSharedVersionIds)
    {
        var result = new IsolationResult();
        var versionDir = Path.Combine(gameRoot, "versions", versionId);
        var modsDir = Path.Combine(versionDir, "mods");
        Directory.CreateDirectory(modsDir);

        foreach (var name in SharedDirs)
            CopyTree(Path.Combine(gameRoot, name), Path.Combine(versionDir, name));
        foreach (var name in SharedFiles)
        {
            var src = Path.Combine(gameRoot, name);
            var dst = Path.Combine(versionDir, name);
            if (File.Exists(src) && !File.Exists(dst))
            {
                try { File.Copy(src, dst); } catch (IOException) { }
            }
        }

        var sharedMods = Path.Combine(gameRoot, "mods");
        if (!Directory.Exists(sharedMods)) return result;
        var files = Directory.GetFiles(sharedMods)
            .Where(IsModFile)
            .ToList();
        result.SharedModsBefore = files.Count;

        var otherGameVersions = otherSharedVersionIds
            .Select(id => ResolveGameVersion(gameRoot, id))
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            var compatible = ModCompatibility.IsCompatible(file, gameVersion, loader);
            if (compatible == false) continue; // 明确不是当前版本的模组,留在共用目录

            var dest = Path.Combine(modsDir, Path.GetFileName(file));
            var copied = false;
            if (!File.Exists(dest))
            {
                try
                {
                    File.Copy(file, dest);
                    copied = true;
                    result.CopiedMods.Add(Path.GetFileName(file));
                }
                catch (IOException)
                {
                }
            }

            // 只有明确只支持当前版本(其它共用版本都不支持)才从共用目录移走
            if (copied && compatible == true &&
                otherGameVersions.All(v =>
                    ModCompatibility.IsCompatible(file, v, "") == false))
            {
                try
                {
                    File.Delete(file);
                    result.MovedMods.Add(Path.GetFileName(file));
                }
                catch (IOException)
                {
                }
            }
        }

        result.SharedModsRemaining = Directory.Exists(sharedMods)
            ? Directory.GetFiles(sharedMods).Count(IsModFile)
            : 0;
        return result;
    }

    private static bool IsModFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyTree(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var dest = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(dest)) continue;
            try { File.Copy(file, dest); } catch (IOException) { }
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".restore-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".old-", StringComparison.OrdinalIgnoreCase)) continue;
            CopyTree(dir, Path.Combine(target, name));
        }
    }

    /// <summary>沿 inheritsFrom 找到根版本号(clientVersion 优先)。</summary>
    private static string ResolveGameVersion(string gameRoot, string versionId)
    {
        var versionsRoot = Path.Combine(gameRoot, "versions");
        var id = versionId;
        for (var guard = 0; guard < 8; guard++)
        {
            var jsonPath = Path.Combine(versionsRoot, id, id + ".json");
            if (!File.Exists(jsonPath)) return "";
            try
            {
                var json = JObject.Parse(File.ReadAllText(jsonPath));
                var client = json.Value<string>("clientVersion");
                if (!string.IsNullOrWhiteSpace(client)) return client!;
                var parent = json.Value<string>("inheritsFrom");
                if (string.IsNullOrWhiteSpace(parent)) return id;
                id = parent!;
            }
            catch (Exception)
            {
                return "";
            }
        }
        return "";
    }
}
