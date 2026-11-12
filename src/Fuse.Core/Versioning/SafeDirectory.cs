using System;
using System.IO;

namespace Fuse.Core.Versioning;

/// <summary>
/// junction/符号链接感知的目录树删除(liumo-guard/1.0)。
/// net48 的 Directory.Delete(recursive) 会跟随 reparse point 删除目标内容——
/// 版本目录里存在指向共享内容的 junction 时,递归删除会把共享存档/模组一起带走。
/// 本实现遇到 reparse point 只摘除链接本身,绝不进入。
/// </summary>
public static class SafeDirectory
{
    /// <summary>删除整棵目录树;路径本身是 junction 时只摘除链接。</summary>
    public static void DeleteTree(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
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
}
