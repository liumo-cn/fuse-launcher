using System;
using System.IO;

namespace Fuse.Core.Installer;

/// <summary>
/// 整合包升级时的"个人数据迁移":把旧实例的存档/设置/服务器列表/光影/材质复制到新实例。
/// 只复制目标不存在的文件,不覆盖新整合包自带的内容;mods/config 不迁移(由新整合包决定)。
/// </summary>
public static class InstanceMigrator
{
    private static readonly string[] SingleFiles = { "options.txt", "servers.dat" };
    private static readonly string[] Directories = { "saves", "shaderpacks", "resourcepacks" };

    /// <summary>返回复制的文件数。</summary>
    public static int Migrate(string fromDir, string toDir)
    {
        if (!Directory.Exists(fromDir)) return 0;
        Directory.CreateDirectory(toDir);
        var count = 0;

        foreach (var name in SingleFiles)
        {
            var src = Path.Combine(fromDir, name);
            var dst = Path.Combine(toDir, name);
            if (!File.Exists(src) || File.Exists(dst)) continue;
            File.Copy(src, dst, overwrite: false);
            count++;
        }

        foreach (var dirName in Directories)
        {
            var srcDir = Path.Combine(fromDir, dirName);
            if (!Directory.Exists(srcDir)) continue;
            foreach (var srcFile in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = srcFile.Substring(srcDir.Length).TrimStart('\\', '/');
                var dst = Path.Combine(toDir, dirName, rel);
                if (File.Exists(dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(srcFile, dst, overwrite: false);
                count++;
            }
        }
        return count;
    }
}
