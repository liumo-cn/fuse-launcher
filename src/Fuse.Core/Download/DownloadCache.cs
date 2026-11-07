using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Fuse.Core.Download;

/// <summary>
/// 跨游戏目录的只读对象缓存。按 SHA 命名,命中时同盘硬链接、跨盘复制,
/// 多个 .minecraft 目录装同一批 assets/libraries 时不用重复下载。
/// 缓存失败不影响下载本身。
/// </summary>
public static class DownloadCache
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(
        string newFileName, string existingFileName, IntPtr securityAttributes);

    private static readonly object Sync = new();
    private static string? _root;

    /// <summary>缓存根目录(通常是启动器目录\cache\objects);null = 关闭缓存。</summary>
    public static string? Root
    {
        get
        {
            lock (Sync) return _root;
        }
        set
        {
            lock (Sync) _root = string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public static bool TryGet(string? hash, HashAlgo algo, out string path)
    {
        path = "";
        var root = Root;
        if (root == null || !IsHash(hash)) return false;
        var candidate = PathFor(root, hash!, algo);
        if (!File.Exists(candidate)) return false;
        path = candidate;
        return true;
    }

    /// <summary>把刚下好并校验通过的文件放进缓存;同盘硬链接不占额外空间。</summary>
    public static void Store(string sourcePath, string? hash, HashAlgo algo)
    {
        var root = Root;
        if (root == null || !IsHash(hash) || !File.Exists(sourcePath)) return;
        try
        {
            var dest = PathFor(root, hash!, algo);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) return;
            if (!CreateHardLinkW(dest, sourcePath, IntPtr.Zero))
                File.Copy(sourcePath, dest);
        }
        catch (Exception)
        {
            // 缓存只影响速度,失败就算了
        }
    }

    /// <summary>缓存命中:硬链接到目标路径;跨盘或硬链接失败时退回复制。</summary>
    public static bool LinkOrCopy(string cachedPath, string destPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
            if (File.Exists(destPath)) File.Delete(destPath);
            if (CreateHardLinkW(destPath, cachedPath, IntPtr.Zero)) return true;
            File.Copy(cachedPath, destPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string PathFor(string root, string hash, HashAlgo algo)
    {
        var value = hash.Trim().ToLowerInvariant();
        return Path.Combine(root, algo.ToString().ToLowerInvariant(), value.Substring(0, 2), value);
    }

    private static bool IsHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length < 8) return false;
        foreach (var c in hash)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
