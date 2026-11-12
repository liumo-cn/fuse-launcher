using System;
using System.Collections.Generic;
using System.IO;

namespace Fuse.Core.Versioning;

/// <summary>
/// 删除版本时的目录安全规划(共享游戏根绝不入列)。
/// 事故教训(2026-11-12 mc联机):未开版本隔离的外部目录,实例目录解析结果就是游戏根本身,
/// 直接对它 Directory.Delete 会把整个游戏目录(其它版本/存档/资源)连根删掉(liumo-guard/1.0)。
/// </summary>
public static class VersionDeletion
{
    /// <summary>返回删除该版本允许移除的目录:去重,且全部严格位于 gameRoot 内部。</summary>
    public static IReadOnlyList<string> PlanDirectories(string gameRoot, string versionId, string instDir)
    {
        var root = Full(gameRoot);
        var targets = new List<string>();
        Add(targets, root, Path.Combine(root, "versions", versionId));
        Add(targets, root, instDir);
        return targets;
    }

    /// <summary>该路径是否就是游戏根目录本身(无论隔离与否都不允许整目录删)。</summary>
    public static bool IsGameRootPath(string path, string gameRoot)
    {
        try { return Same(Full(path), Full(gameRoot)); }
        catch (Exception) { return false; }
    }

    private static void Add(List<string> targets, string root, string path)
    {
        string p;
        try { p = Full(path); }
        catch (Exception) { return; }   // 非法路径直接放弃
        // 必须严格位于游戏根内部:拦下 gameRoot 本身、盘符根、以及 versionId 带 .. 逃逸出根的路径
        if (!p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var t in targets)
            if (Same(t, p)) return;
        targets.Add(p);
    }

    private static string Full(string path)
        => Path.GetFullPath((path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static bool Same(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
