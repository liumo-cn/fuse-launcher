using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Fuse.Core.Installer;
using Fuse.Core.Sources;

namespace Fuse.Core.Download;

/// <summary>
/// 地图(世界存档 zip)安装:解压 → 定位 level.dat 所在的世界根 → 重名加后缀 → 移入 saves → 记录。
/// 和整合包不同,地图 zip 的世界可能在压缩包根目录,也可能套了一层(偶尔两层)目录。
/// </summary>
public static class MapInstaller
{
    /// <summary>
    /// 安装一张地图,返回世界最终在 saves 下的文件夹名(重名自动加 (2)(3) 后缀)。
    /// 失败时清理临时目录后抛出,任务系统把异常消息当「原因」展示。
    /// </summary>
    public static string Install(string zipPath, string savesDir, string instanceDir,
        string source, string projectId, string? sha1, string fallbackName,
        IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("地图压缩包不存在", zipPath);
        Directory.CreateDirectory(savesDir);
        // 临时目录放 saves 同盘,解压后的移动才是纯改名;点开头,存档列表会跳过
        var tempRoot = Path.Combine(savesDir, ".fuse-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            ModpackInstaller.ExtractAll(zipPath, tempRoot, ct);   // 通用安全解压(zip-slip 已挡)
            progress?.Report(0.4);
            ct.ThrowIfCancellationRequested();

            var worldRoot = FindWorldRoot(tempRoot)
                ?? throw new InvalidOperationException("压缩包里没有找到 level.dat，这可能不是一张 Minecraft 地图");
            var worldName = string.Equals(worldRoot, tempRoot, StringComparison.Ordinal)
                ? SanitizeName(Path.GetFileNameWithoutExtension(fallbackName))
                : new DirectoryInfo(worldRoot).Name;
            var target = UniqueWorldDir(savesDir, worldName);
            Directory.Move(worldRoot, target);
            progress?.Report(0.8);
            // 世界文件夹名解压后才知道,所以地图的安装记录在这里(队列侧)落盘
            ContentRecordStore.Record(instanceDir, source, projectId, Path.GetFileName(target), "map", sha1);
            progress?.Report(1.0);
            return Path.GetFileName(target);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    /// <summary>找 level.dat:先压缩包根,再逐层向下(最多 3 层),取最浅的第一个;跳过 __MACOSX 等垃圾目录。</summary>
    public static string? FindWorldRoot(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, "level.dat"))) return extractDir;
        var queue = new Queue<(string Dir, int Depth)>();
        foreach (var sub in SubWorlds(extractDir)) queue.Enqueue((sub, 1));
        var scanned = 0;
        while (queue.Count > 0 && scanned < 400)
        {
            scanned++;
            var (dir, depth) = queue.Dequeue();
            if (File.Exists(Path.Combine(dir, "level.dat"))) return dir;
            if (depth >= 3) continue;
            foreach (var sub in SubWorlds(dir)) queue.Enqueue((sub, depth + 1));
        }
        return null;
    }

    private static List<string> SubWorlds(string dir)
    {
        try
        {
            var list = new List<string>();
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub) ?? "";
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;       // 我们的临时目录/隐藏目录
                if (name.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(sub);
            }
            return list;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return new List<string>();
    }

    /// <summary>saves 下的目标目录:重名时加 (2)、(3)…(游戏里显示的是 level.dat 里的名字,文件夹名只是标识)。</summary>
    public static string UniqueWorldDir(string savesDir, string name)
    {
        var candidate = Path.Combine(savesDir, name);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(savesDir, name + " (" + i + ")");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException("saves 里同名存档过多，无法命名：「" + name + "」");
    }

    /// <summary>压缩包根就是世界时没有现成的文件夹名,从 zip 名/项目名推一个,净化非法字符。</summary>
    public static string SanitizeName(string raw)
    {
        var name = (raw ?? "").Trim();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? ' ' : ch);
        name = builder.ToString().Trim().TrimEnd('.').Trim();
        if (name.Length == 0) name = "地图";
        if (name.Length > 60) name = name.Substring(0, 60).Trim();
        return name;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
