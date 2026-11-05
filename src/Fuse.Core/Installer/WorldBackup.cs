using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Fuse.Core.Installer;

/// <summary>
/// 单个存档(世界)的备份/还原。
/// 备份存放在 &lt;游戏目录&gt;/backups/worlds/&lt;版本id&gt;/&lt;世界名&gt;/backup-时间.zip,
/// 排除 session.lock(游戏运行时会被占用)。还原先解压到临时目录再替换世界目录,失败不破坏原存档。
/// </summary>
public static class WorldBackup
{
    public sealed class BackupInfo
    {
        public string Path { get; set; } = "";
        public string WorldName { get; set; } = "";
        public DateTime Created { get; set; }
        public long Size { get; set; }
    }

    public static string BackupRoot(string gameRoot, string versionId)
        => Path.Combine(gameRoot, "backups", "worlds", versionId);

    public static string Create(string savesDir, string worldName, string backupRoot)
    {
        var worldDir = Path.Combine(savesDir, worldName);
        if (!Directory.Exists(worldDir))
            throw new DirectoryNotFoundException("存档不存在:" + worldDir);

        var dir = Path.Combine(backupRoot, SafeName(worldName));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var meta = zip.CreateEntry("__meta__/world.txt", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(meta.Open(), Encoding.UTF8)) writer.Write(worldName);

            foreach (var file in Directory.EnumerateFiles(worldDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = file.Substring(worldDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                zip.CreateEntryFromFile(file, rel, CompressionLevel.Fastest);
            }
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        return path;
    }

    public static List<BackupInfo> List(string backupRoot)
    {
        var list = new List<BackupInfo>();
        if (!Directory.Exists(backupRoot)) return list;
        foreach (var file in Directory.EnumerateFiles(backupRoot, "*.zip", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            list.Add(new BackupInfo
            {
                Path = file,
                WorldName = Path.GetFileName(Path.GetDirectoryName(file) ?? ""),
                Created = info.LastWriteTime,
                Size = info.Length,
            });
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    /// <summary>把备份还原到 saves 下对应的世界目录(先临时解压再替换)。</summary>
    public static void Restore(string backupPath, string savesDir)
    {
        string worldName;
        using (var probe = ZipFile.OpenRead(backupPath))
        {
            var meta = probe.GetEntry("__meta__/world.txt");
            if (meta != null)
            {
                using var reader = new StreamReader(meta.Open(), Encoding.UTF8);
                worldName = reader.ReadToEnd().Trim();
            }
            else
            {
                worldName = Path.GetFileName(Path.GetDirectoryName(backupPath) ?? "");
            }
        }
        if (string.IsNullOrEmpty(worldName))
            throw new InvalidOperationException("备份缺少世界名");

        Directory.CreateDirectory(savesDir);
        var worldDir = Path.Combine(savesDir, worldName);
        var temp = worldDir + ".restore-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(temp);
        try
        {
            var rootFull = Path.GetFullPath(temp).TrimEnd('\\') + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(backupPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (entry.FullName.StartsWith("__meta__/", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.GetFullPath(Path.Combine(temp,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("备份包含非法路径:" + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
            var old = worldDir + ".old-" + Guid.NewGuid().ToString("N")[..8];
            if (Directory.Exists(worldDir)) Directory.Move(worldDir, old);
            Directory.Move(temp, worldDir);
            try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch (IOException) { }
        }
        catch
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { }
            throw;
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "world" : result;
    }
}
