using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Fuse.Core.Versioning;

/// <summary>一次 staging 的结果明细(用于 boot.log 与测试)。</summary>
public sealed class IsolationStageResult
{
    public List<string> Junctions { get; } = new();
    public List<string> Created { get; } = new();
    public List<string> Copied { get; } = new();
    public List<string> Failed { get; } = new();
    public int AlreadyOk { get; internal set; }
    public string? Skipped { get; internal set; }
    public bool Changed => Junctions.Count + Created.Count + Copied.Count > 0;
}

/// <summary>
/// 外部共享根目录的自动虚拟隔离(2026-11-13 方案,liumo-guard/1.0):
/// 给未隔离的版本准备 versions/&lt;id&gt; 游戏目录,saves/mods/config 等用目录 junction
/// 指回根目录共享内容——游戏行为零变化,但"实例目录"从此永远是版本子目录,
/// 不再存在"实例目录=游戏根"的状态(删除类事故的结构性根治)。
/// 只增不改:原始共享内容从头到尾不动,撤销 = Unstage 摘掉 junction 即回原状。
/// </summary>
public static class IsolationStager
{
    /// <summary>需要隔离的内容目录(与 InstanceLayout.IsIsolated 的标记集对齐)。</summary>
    private static readonly string[] ContentDirs =
        { "saves", "mods", "config", "resourcepacks", "shaderpacks" };

    /// <summary>确保该版本拥有自己的游戏目录(幂等);PCL 等已隔离的版本自动跳过。</summary>
    public static IsolationStageResult EnsureStaged(string gameRoot, string versionId, string installDirectory)
    {
        var result = new IsolationStageResult();
        string root;
        try { root = Full(gameRoot); }
        catch (Exception) { result.Skipped = "game root invalid"; return result; }
        try
        {
            if (Same(root, Full(installDirectory))) { result.Skipped = "managed layout"; return result; }
        }
        catch (Exception) { }

        var versionDir = Path.Combine(root, "versions", versionId);
        if (!Directory.Exists(versionDir)) { result.Skipped = "version missing"; return result; }
        // 已经是隔离布局(自带标记目录/PCL Setup.ini)的版本不动:它缺的目录就该缺,
        // 把共享内容 junction 进去反而会张冠李戴
        if (InstanceLayout.IsIsolated(versionDir)) { result.Skipped = "already isolated"; return result; }

        foreach (var name in ContentDirs)
        {
            var inVersion = Path.Combine(versionDir, name);
            if (Directory.Exists(inVersion)) { result.AlreadyOk++; continue; }
            TryRemoveDanglingLink(inVersion);
            var shared = Path.Combine(root, name);
            if (Directory.Exists(shared))
            {
                if (CreateJunction(inVersion, shared)) result.Junctions.Add(name);
                else result.Failed.Add(name);
            }
            else
            {
                // 根目录也没有该内容:建真实空目录,凑齐隔离标记(游戏行为与共享时一致:都是空的)
                try { Directory.CreateDirectory(inVersion); result.Created.Add(name); }
                catch (Exception) { result.Failed.Add(name); }
            }
        }
        CopyIfMissing(Path.Combine(root, "options.txt"), Path.Combine(versionDir, "options.txt"), result);
        CopyIfMissing(Path.Combine(root, "servers.dat"), Path.Combine(versionDir, "servers.dat"), result);
        return result;
    }

    /// <summary>摘除版本目录里由 staging 建立的 junction(只摘链接,不动共享内容)。物理迁移前必须调用。</summary>
    public static void Unstage(string versionDir)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || !Directory.Exists(versionDir)) return;
        foreach (var name in ContentDirs)
        {
            var p = Path.Combine(versionDir, name);
            try
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(p, false);   // 只摘链接本身
            }
            catch (Exception)
            {
                // 不存在/非链接/被占用:都不是 staging 建的,留给调用方语义
            }
        }
    }

    /// <summary>创建目录 junction(免管理员权限);目标或链接已存在视为成功。</summary>
    public static bool CreateJunction(string linkPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(linkPath)) return true;
            var psi = new ProcessStartInfo("cmd.exe",
                "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>悬空 junction(Directory.Exists 为 false 但链接还在):摘掉再重建。</summary>
    private static void TryRemoveDanglingLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path, false);
        }
        catch (Exception)
        {
        }
    }

    private static void CopyIfMissing(string src, string dst, IsolationStageResult result)
    {
        try
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            File.Copy(src, dst);
            result.Copied.Add(Path.GetFileName(src));
        }
        catch (Exception)
        {
            result.Failed.Add(Path.GetFileName(src));
        }
    }

    private static string Full(string path)
        => Path.GetFullPath((path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static bool Same(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
