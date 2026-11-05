using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Fuse.Core.Launcher;

/// <summary>扫描到的一个候选 MC 游戏目录。</summary>
public sealed class FoundGameFolder
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>识别依据(官方/第三方/我们的布局),给用户看的。</summary>
    public string Kind { get; init; } = "";
    public int VersionCount { get; init; }
}

/// <summary>
/// 搜索本机已存在的 MC 游戏目录(官方/第三方/我们的布局)。
/// 搜索范围:用户目录/文档/下载/桌面、C 盘用户目录、各固定盘根(非系统盘更深)、
/// 各盘的「用户名 / Documents / Downloads」等搬家后的用户目录,
/// 以及浅层扫到的 MC 相关名字目录(如 D:\Games\mc)的二次深挖。
/// 识别规则:
///   ① 目录名是 .minecraft;或
///   ② 里面有 versions/&lt;id&gt;/&lt;id&gt;.json(版本目录结构);或
///   ③ 里面有 launcher_profiles.json(官方启动器特征)。
/// </summary>
public static class McFolderFinder
{
    /// <summary>单次二次深挖的层数(名字像 MC 的目录往下再找几层)。</summary>
    private const int CandidateDepth = 3;

    public static List<FoundGameFolder> Find(IReadOnlyCollection<string> knownPaths, CancellationToken ct = default)
    {
        var known = new HashSet<string>(
            knownPaths.Select(p => Normalize(p)),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<FoundGameFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        // 同一个根可能被多条规则命中(例如 D:\Documents 与 D:\ 根),按路径去重并保留更深的深度
        var roots = new Dictionary<string, (string Path, int Depth)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, depth) in EnumerateRoots())
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var key = Normalize(path);
            if (key.Length == 0) continue;
            if (!roots.TryGetValue(key, out var existing) || depth > existing.Depth)
                roots[key] = (path, depth);
        }

        foreach (var entry in roots.Values)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                ScanRoot(entry.Path, entry.Depth, known, seen, candidates, result, ct);
            }
            catch (Exception)
            {
                // 单个根失败(权限/不存在)不影响其他
            }
        }

        // 第二轮:浅层扫到但没预算继续走的 MC 名字目录,再往下深挖
        // (典型:D:\Games\mc 本身不是游戏目录,里面的 .minecraft 才是)
        var deepSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) break;
            if (!deepSeen.Add(Normalize(candidate))) continue;
            try
            {
                ScanRoot(candidate, CandidateDepth, known, seen, null, result, ct);
            }
            catch (Exception)
            {
                // 单个候选失败不影响其他
            }
        }

        return result
            .OrderByDescending(f => f.VersionCount)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>常见安装位置的搜索根(路径, 深度)。小白会下到任何地方,用户目录/下载/文档放最深搜索。</summary>
    private static IEnumerable<(string Path, int Depth)> EnumerateRoots()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var downloads = Path.Combine(profile, "Downloads");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        // 精确位置(几乎必中,深度浅)
        if (appData.Length > 0) yield return (appData, 2);
        if (docs.Length > 0) yield return (docs, 3);
        if (Directory.Exists(downloads)) yield return (downloads, 3);
        if (desktop.Length > 0) yield return (desktop, 2);

        // C 盘用户目录全扫(每个用户 profile 下的常见落点):小白可能装在别的用户/别的位置
        var usersRoot = Path.GetDirectoryName(profile);
        if (usersRoot is { Length: > 0 } && Directory.Exists(usersRoot))
        {
            yield return (usersRoot, 4);
        }

        // 各盘根 + 搬家后的用户目录:自定义安装常见 D:\.minecraft、D:\games\.minecraft、
        // 以及把「文档」挪到 D:\liumo\Documents 的情况(Windows 的 MyDocuments 仍指 C 盘时不会走到)
        var systemDrive = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "";
        var userName = Path.GetFileName(profile);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = drive.RootDirectory.FullName;
            var isSystem = string.Equals(
                Path.GetPathRoot(root), systemDrive, StringComparison.OrdinalIgnoreCase);

            // 系统盘已经由 usersRoot/docs/appData 覆盖,保持浅一点避免全盘深扫;
            // 其他盘放深一层,覆盖 D:\a\b\mc\.minecraft 这类路径
            yield return (root, isSystem ? 3 : 4);

            var altProfile = Path.Combine(root, userName);
            if (userName.Length > 0 && !string.Equals(altProfile, profile, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(altProfile))
            {
                yield return (altProfile, 4);
            }

            foreach (var sub in new[] { "Users", "Documents", "Downloads", "文档", "下载" })
            {
                var p = Path.Combine(root, sub);
                if (Directory.Exists(p)) yield return (p, 4);
            }
        }
    }

    private static void ScanRoot(
        string root, int depth, HashSet<string> known, HashSet<string> seen,
        List<string>? candidates, List<FoundGameFolder> result, CancellationToken ct)
    {
        if (depth < 0 || ct.IsCancellationRequested) return;
        if (IsBlocked(root)) return;

        if (IsGameFolder(root))
        {
            var normalized = Normalize(root);
            if (seen.Add(normalized) && !known.Contains(normalized))
                result.Add(Describe(root));
            // .minecraft 里面通常不再嵌套别的 .minecraft,但部分启动器的目录会;继续往下走一层找嵌套
        }

        if (depth == 0)
        {
            // 名字像 MC、但本身不是游戏目录 → 记下来,主扫描结束后再深挖
            // (例如 mc整合包 这种外层文件夹,真正游戏目录是里面的 .minecraft)
            if (candidates != null && IsMcRelatedName(Path.GetFileName(root)))
                candidates.Add(root);
            return;
        }
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (ct.IsCancellationRequested) return;
                // 跳过明显的系统/超大目录
                var name = Path.GetFileName(dir);
                if (IsSkipName(name)) continue;
                ScanRoot(dir, depth - 1, known, seen, candidates, result, ct);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 无权限的目录直接跳过
        }
    }

    /// <summary>识别一个目录是否是 MC 游戏目录。</summary>
    public static bool IsGameFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            if (Path.GetFileName(dir).Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
                return true;
            if (File.Exists(Path.Combine(dir, "launcher_profiles.json"))) return true;

            var versions = Path.Combine(dir, "versions");
            if (!Directory.Exists(versions)) return false;
            foreach (var versionDir in Directory.EnumerateDirectories(versions))
            {
                var id = Path.GetFileName(versionDir);
                if (File.Exists(Path.Combine(versionDir, id + ".json"))) return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static FoundGameFolder Describe(string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
        var name = Path.GetFileName(full);
        if (name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(full));
            if (parent.Length > 0) name = parent;
        }

        var versionCount = 0;
        string kind;
        if (File.Exists(Path.Combine(full, "launcher_profiles.json")))
            kind = "官方启动器";
        else if (Directory.Exists(Path.Combine(full, ".versions")))
            kind = "第三方启动器";
        else
            kind = "通用";

        var versionsDir = Path.Combine(full, "versions");
        if (Directory.Exists(versionsDir))
        {
            foreach (var versionDir in Directory.EnumerateDirectories(versionsDir))
            {
                var id = Path.GetFileName(versionDir);
                if (File.Exists(Path.Combine(versionDir, id + ".json"))) versionCount++;
            }
        }
        return new FoundGameFolder { Path = full, Name = name, Kind = kind, VersionCount = versionCount };
    }

    private static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim().TrimEnd('\\', '/'));
            var root = Path.GetPathRoot(full) ?? "";
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception)
        {
            return (path ?? "").Trim();
        }
    }

    private static bool IsBlocked(string dir)
    {
        var blocked = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            AppDomain.CurrentDomain.BaseDirectory,
        };
        var full = Normalize(dir);
        foreach (var b in blocked)
        {
            if (string.IsNullOrEmpty(b)) continue;
            var rootFull = Path.GetFullPath(b).TrimEnd('\\') + Path.DirectorySeparatorChar;
            if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // 明确无意义的巨坑目录
        var name = Path.GetFileName(full);
        if (name is "WindowsApps" or "$Recycle.Bin" or "System Volume Information" or "node_modules")
            return true;
        if (name.Equals("AppData", StringComparison.OrdinalIgnoreCase) &&
            full.IndexOf("\\Local\\", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

    private static bool IsMcRelatedName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // "mc"/"MC整合包"/"mc整合包" 这类外层文件夹:以 mc 开头最容易命中
        if (name.StartsWith("mc", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.IndexOf("minecraft", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        foreach (var hint in new[] { "我的世界", "启动器", "整合包" })
        {
            if (name.IndexOf(hint, StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    private static bool IsSkipName(string name)
    {
        // 这些目录里不可能有 MC,跳过能把用户目录的扫描时间从分钟级压到秒级
        var skip = new[]
        {
            "Windows", "Program Files", "Program Files (x86)", "WindowsApps",
            "$Recycle.Bin", "System Volume Information", "node_modules",
            ".git", ".gradle", ".cache", ".vscode", "AppData", "Cisco", "Intel",
            "AMD", "NVIDIA", "Microsoft", "OneDrive", "Packages", "PubCache",
            "Steam", "steamapps", "Epic Games",
            "Google Drive", "Dropbox", "iCloudDrive", "Recycle",
        };
        foreach (var s in skip)
            if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("$")) return true;
        return false;
    }
}
