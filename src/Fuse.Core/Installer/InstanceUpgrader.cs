using System;
using System.IO;
using Newtonsoft.Json;

namespace Fuse.Core.Installer;

/// <summary>
/// 整合包原地升级的目录操作:先在 &lt;实例&gt;.upgrade-* 暂存目录里把新版本装好,
/// 再原子交换(旧实例改名成 .old-*,暂存目录变成实例目录)。失败自动还原;
/// 成功后保留 .old-* 目录,可通过 marker 回滚。
/// </summary>
public static class InstanceUpgrader
{
    public sealed class UpgradeRecord
    {
        public string OldDir { get; set; } = "";
        public string OldPackVersion { get; set; } = "";
        public string NewPackVersion { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
    }

    public static string MarkerPath(string gameRoot, string versionId)
        => Path.Combine(gameRoot, "backups", "upgrades", versionId + ".json");

    public static string StagingDir(string instanceDir, string stamp)
        => instanceDir.TrimEnd('\\', '/') + ".upgrade-" + stamp;

    /// <summary>旧实例改名为 .old-时间戳,暂存目录切换为实例目录;切换失败会还原旧目录。</summary>
    public static string SwapIn(string instanceDir, string stagingDir, string stamp)
    {
        var fullInstance = Path.GetFullPath(instanceDir).TrimEnd('\\', '/');
        var oldDir = fullInstance + ".old-" + stamp;
        if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);
        Directory.Move(fullInstance, oldDir);
        try
        {
            Directory.Move(stagingDir, fullInstance);
        }
        catch
        {
            // 切不过去就还原,保证实例还能用
            try { Directory.Move(oldDir, fullInstance); } catch (IOException) { }
            throw;
        }
        return oldDir;
    }

    public static void SaveRecord(string gameRoot, string versionId, UpgradeRecord record)
    {
        var path = MarkerPath(gameRoot, versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(record, Formatting.Indented));
    }

    public static UpgradeRecord? LoadRecord(string gameRoot, string versionId)
    {
        try
        {
            var path = MarkerPath(gameRoot, versionId);
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<UpgradeRecord>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void DeleteRecord(string gameRoot, string versionId)
    {
        try
        {
            var path = MarkerPath(gameRoot, versionId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>回滚:当前实例改名 .failed-*,旧目录换回来;失败会还原当前实例。</summary>
    public static void Rollback(string instanceDir, UpgradeRecord record)
    {
        var fullInstance = Path.GetFullPath(instanceDir).TrimEnd('\\', '/');
        if (!Directory.Exists(record.OldDir))
            throw new DirectoryNotFoundException("旧版本目录不存在，无法回滚:" + record.OldDir);
        var failed = fullInstance + ".failed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.Move(fullInstance, failed);
        try
        {
            Directory.Move(record.OldDir, fullInstance);
        }
        catch
        {
            try { Directory.Move(failed, fullInstance); } catch (IOException) { }
            throw;
        }
        try { Directory.Delete(failed, true); } catch (IOException) { }
    }
}
