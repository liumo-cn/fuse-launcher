using System;
using System.IO;

namespace Fuse.Core.Versioning;

/// <summary>
/// junction/符号链接感知 + 白名单根校验的目录树删除(liumo-guard/1.0)。
///
/// DeleteTree 必须显式传入 allowedRoot(允许删除的根目录):
///   · 目标必须严格位于 allowedRoot 内部 —— 拒绝删除 allowedRoot 本身、其祖先、盘符根、.. 逃逸
///   · 绝对保护名单:盘符根、用户主目录、AppData、Program Files、Windows 目录自身及系统树一律拒绝
///     (UserProfile/AppData 的子树允许,标准 .minecraft 游戏目录就在那里)
/// 遇到 reparse point(junction/符号链接)只摘除链接本身,绝不进入目标内容——
/// net48 的 Directory.Delete(recursive) 会跟随 junction 删除目标内容,必须走本实现。
/// </summary>
public static class SafeDirectory
{
    /// <summary>
    /// App 层注入的回收站实现(netstandard2.0 没有 VB FileIO):收到目标路径即送回收站。
    /// null = 未注入(core-only/测试环境)→ 回收站重载回退为永久删除。
    /// </summary>
    public static Action<string>? RecycleSink { get; set; }

    /// <summary>带白名单根的目录树删除。目标必须严格位于 allowedRoot 内部,否则抛 IOException。</summary>
    public static void DeleteTree(string path, string allowedRoot)
    {
        var target = Validate(path, allowedRoot);
        DeleteTreeUnrooted(target);
    }

    /// <summary>
    /// 用户发起的目录删除:同样的白名单根/保护根校验,但目标送回收站(可在回收站还原)。
    /// 未注入 RecycleSink 时回退为永久删除。
    /// </summary>
    public static void DeleteTreeToRecycle(string path, string allowedRoot)
    {
        var target = Validate(path, allowedRoot);
        if (RecycleSink == null) { DeleteTreeUnrooted(target); return; }
        RecycleSink(target);
    }

    /// <summary>用户发起的单文件删除:同上,送回收站;未注入时永久删除。</summary>
    public static void DeleteFileToRecycle(string path, string allowedRoot)
    {
        var target = Validate(path, allowedRoot);
        if (RecycleSink == null) { TryClearReadOnly(new FileInfo(target)); File.Delete(target); return; }
        RecycleSink(target);
    }

    /// <summary>白名单根/保护根校验,返回规范化目标;不通过抛 IOException。</summary>
    private static string Validate(string path, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("删除目标为空,拒绝执行");
        if (string.IsNullOrWhiteSpace(allowedRoot)) throw new IOException("未指定允许删除的根目录,拒绝执行");
        var target = Full(path);
        var root = Full(allowedRoot);
        if (IsProtected(target)) throw new IOException("拒绝删除受保护路径:" + target);
        // 必须严格位于允许根内部:拦下 allowedRoot 本身、其祖先、.. 逃逸与盘符根
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"删除目标不在允许的根目录「{root}」内:{target}");
        return target;
    }

    private static void DeleteTreeUnrooted(string path)
    {
        if (IsReparse(path))
        {
            Directory.Delete(path, false);
            return;
        }
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return;
        Clean(dir);
        dir.Delete(false);
    }

    /// <summary>
    /// 无根校验的 reparse 感知删除。仅供"已通过白名单校验"的回收站失败回退使用;
    /// 常规删除请用 DeleteTree / DeleteTreeToRecycle。
    /// </summary>
    public static void DeleteTreeUnrootedPublic(string path) => DeleteTreeUnrooted(path);

    private static void Clean(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                entry.Delete();   // 只摘链接本身,不进入目标
                continue;
            }
            if (entry is DirectoryInfo sub)
            {
                Clean(sub);
                sub.Delete(false);
            }
            else
            {
                TryClearReadOnly(entry);
                entry.Delete();
            }
        }
    }

    /// <summary>绝对保护路径:删除目标命中即拒绝(盘符根/系统树自身;UserProfile 与 AppData 只拦根本身)。</summary>
    private static bool IsProtected(string target)
    {
        try
        {
            if (Same(target, Path.GetPathRoot(target) ?? "")) return true;   // 盘符根
            foreach (var p in ProtectedRoots())
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var f = Full(p);
                if (Same(target, f)) return true;                            // 保护根本身
                if (IsSystemTree(f) &&
                    target.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;                                             // 系统树内部一律不删
            }
            return false;
        }
        catch (Exception) { return true; }   // 保护性判断失败时按"拒绝"处理
    }

    /// <summary>系统树(Program Files/Windows 等)内部没有任何合法游戏目录,整棵禁删;
    /// UserProfile/AppData 只拦根本身,子树里的游戏目录(如 Roaming\.minecraft)合法。</summary>
    private static bool IsSystemTree(string protectedRoot)
    {
        var sys = Full(Environment.GetFolderPath(Environment.SpecialFolder.System));
        var win = Full(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var pf = Full(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var pf86 = Full(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        return Same(protectedRoot, sys) || Same(protectedRoot, win) ||
               Same(protectedRoot, pf) || Same(protectedRoot, pf86);
    }

    private static string[] ProtectedRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    };

    private static bool IsReparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return false; }
    }

    private static void TryClearReadOnly(FileSystemInfo entry)
    {
        try
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                entry.Attributes &= ~FileAttributes.ReadOnly;
        }
        catch (Exception)
        {
        }
    }

    private static string Full(string path)
        => Path.GetFullPath((path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static bool Same(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
