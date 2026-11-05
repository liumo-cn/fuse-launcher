using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// 实例备份 / 还原 / 复制。
/// 备份存放在 &lt;游戏目录&gt;/backups/&lt;版本id&gt;/*.zip,包含实例目录(存档、模组、配置、光影、材质)
/// 和版本 json;日志/崩溃报告/缓存不备份。还原采用"解压到临时目录 → 原子替换实例目录",
/// 失败不会破坏原实例;还原前由调用方再自动备份一次当前状态。
/// </summary>
public static class InstanceBackup
{
    /// <summary>顶层不参与备份的目录(日志/缓存/临时)。</summary>
    private static readonly HashSet<string> ExcludedTopDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "crash-reports", "cache", "bin",
    };

    public sealed class BackupInfo
    {
        public string Path { get; set; } = "";
        public DateTime Created { get; set; }
        public long Size { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public static string BackupRoot(string gameRoot, string versionId)
        => Path.Combine(gameRoot, "backups", versionId);

    public static List<BackupInfo> List(string gameRoot, string versionId)
    {
        var dir = BackupRoot(gameRoot, versionId);
        if (!Directory.Exists(dir)) return new List<BackupInfo>();
        var list = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.zip"))
        {
            var info = new FileInfo(file);
            list.Add(new BackupInfo
            {
                Path = file,
                Created = info.LastWriteTime,
                Size = info.Length,
                DisplayName = Path.GetFileNameWithoutExtension(file),
            });
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    public static string Create(string gameRoot, string versionsRoot, string instanceDir,
        string versionId, string displayName)
    {
        var dir = BackupRoot(gameRoot, versionId);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(dir, "backup-" + stamp + ".zip");
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var meta = new JObject
            {
                ["versionId"] = versionId,
                ["displayName"] = displayName,
                ["created"] = DateTime.Now.ToString("o"),
            };
            WriteText(zip, "__meta__/backup.json", meta.ToString());
            AddDirectory(zip, instanceDir, "", isTop: true);
            var versionJson = Path.Combine(versionsRoot, versionId, versionId + ".json");
            if (File.Exists(versionJson))
                zip.CreateEntryFromFile(versionJson, "__meta__/version.json", CompressionLevel.Optimal);
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        return path;
    }

    /// <summary>把备份还原进实例目录(先解压到临时目录,成功后再替换)。</summary>
    public static void Restore(string backupPath, string instanceDir)
    {
        var fullInstance = Path.GetFullPath(instanceDir).TrimEnd('\\');
        var parent = Path.GetDirectoryName(fullInstance)
                     ?? throw new InvalidOperationException("实例目录无效");
        var temp = fullInstance + ".restore-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(temp);
        try
        {
            ExtractInto(backupPath, temp);
            var old = fullInstance + ".old-" + Guid.NewGuid().ToString("N")[..8];
            if (Directory.Exists(fullInstance)) Directory.Move(fullInstance, old);
            Directory.Move(temp, fullInstance);
            try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch (IOException) { }
        }
        catch
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>复制实例:返回新版本 id(实例目录 instances/&lt;新id&gt; 与版本 json 都是副本)。</summary>
    public static string Clone(string gameRoot, string versionsRoot, string instancesRoot,
        string sourceVersionId, string newDisplayName)
    {
        var newId = UniqueVersionId(gameRoot, sourceVersionId + "-copy");
        var srcInstance = Path.Combine(instancesRoot, sourceVersionId);
        var dstInstance = Path.Combine(instancesRoot, newId);
        if (!Directory.Exists(srcInstance))
            throw new DirectoryNotFoundException("源实例目录不存在:" + srcInstance);

        CopyDirectory(srcInstance, dstInstance);

        var srcJson = Path.Combine(versionsRoot, sourceVersionId, sourceVersionId + ".json");
        if (!File.Exists(srcJson))
            throw new FileNotFoundException("源版本 json 不存在:" + srcJson);
        var obj = JObject.Parse(File.ReadAllText(srcJson));
        obj["id"] = newId;
        var dstDir = Path.Combine(versionsRoot, newId);
        Directory.CreateDirectory(dstDir);
        File.WriteAllText(Path.Combine(dstDir, newId + ".json"), obj.ToString());
        return newId;
    }

    private static string UniqueVersionId(string gameRoot, string baseId)
    {
        var id = baseId;
        var n = 2;
        while (File.Exists(Path.Combine(gameRoot, "versions", id, id + ".json")) ||
               Directory.Exists(Path.Combine(gameRoot, "instances", id)))
            id = baseId + "-" + n++;
        return id;
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static void AddDirectory(ZipArchive zip, string root, string relative, bool isTop)
    {
        var dir = relative.Length == 0 ? root : Path.Combine(root, relative);
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = (relative.Length == 0 ? name : relative + "/" + name).Replace('\\', '/');
            zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (isTop && ExcludedTopDirs.Contains(name)) continue;
            if (name.StartsWith(".restore-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".old-", StringComparison.OrdinalIgnoreCase)) continue;
            AddDirectory(zip, root, relative.Length == 0 ? name : relative + "/" + name, isTop: false);
        }
    }

    private static void ExtractInto(string backupPath, string targetDir)
    {
        var rootFull = Path.GetFullPath(targetDir).TrimEnd('\\') + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(backupPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (entry.FullName.StartsWith("__meta__/", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.GetFullPath(Path.Combine(targetDir,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("备份包含非法路径:" + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".restore-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".old-", StringComparison.OrdinalIgnoreCase)) continue;
            CopyDirectory(dir, Path.Combine(dst, name));
        }
    }
}
