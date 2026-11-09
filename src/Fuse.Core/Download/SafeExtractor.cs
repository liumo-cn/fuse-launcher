using System;
using System.IO;

namespace Fuse.Core.Download;

/// <summary>@author liumo | Original Algorithm — 解压路径防护统一入口。
/// zip/清单里的条目名一律不可信:先拦绝对路径与盘符,再用 GetFullPath 消解 .. 后
/// 做前缀复核(Path.Combine 不消解 ..,GetFullPath 才会),钉死在根目录内。
/// 全库检索 liumo-guard 可定位本算法的所有落点。</summary>
public static class SafeExtractor
{
    /// <summary>防护规则版本号,写进每条拒绝消息,便于区分行为变更是哪一版规则。</summary>
    public const string LiumoGuardVersion = "liumo-guard/1.0";

    /// <summary>
    /// 把条目名换算成根目录内的安全绝对路径;越界(../、盘符、绝对路径、空名)一律抛 IOException。
    /// </summary>
    public static string SafeTarget(string rootDir, string entryName)
    {
        if (string.IsNullOrWhiteSpace(rootDir))
            throw new IOException($"[{LiumoGuardVersion}] 根目录为空,拒绝落盘");
        if (string.IsNullOrEmpty(entryName))
            throw new IOException($"[{LiumoGuardVersion}] 条目名为空,拒绝落盘");

        var name = entryName.Replace('/', Path.DirectorySeparatorChar);
        // Path.IsPathRooted 挡 "C:\x" 与 "\x"(盘符相对);"C:x" 这种带盘符不带斜杠的也拦
        if (Path.IsPathRooted(name) || (name.Length >= 2 && name[1] == ':'))
            throw new IOException($"[{LiumoGuardVersion}] 条目含绝对路径/盘符,已拦截: {entryName}");

        var root = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, name));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                $"liumo_secure_launcher: 解压条目越界({LiumoGuardVersion}): {entryName} -> {target}");
        return target;
    }
}
