using System.IO;

namespace Fuse.Core.ModConflict;

/// <summary>判断一个 jar 能不能用于指定 MC 版本/加载器(共享 mods 目录迁移时用)。</summary>
public static class ModCompatibility
{
    /// <summary>
    /// true=明确支持,false=明确不支持(别的加载器/版本范围不符),
    /// null=没有写 minecraft 依赖,无法判断(保守起见不要从共用目录删)。
    /// </summary>
    public static bool? IsCompatible(string jarPath, string gameVersion, string loader)
    {
        if (string.IsNullOrEmpty(gameVersion) || !File.Exists(jarPath)) return false;
        var mod = ModScanner.Read(jarPath, loader);
        if (mod == null) return false; // 不是当前加载器的模组
        if (!mod.Depends.TryGetValue("minecraft", out var range) || string.IsNullOrWhiteSpace(range))
            return null;
        return VersionRange.Matches(gameVersion, range);
    }
}
