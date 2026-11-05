using System;
using System.IO;

namespace Fuse.Core.Versioning;

/// <summary>
/// 版本实例目录解析。三种布局:
///   ① 我们的布局：根\instances\&lt;id&gt;（版本隔离，assets/libraries 共享）
///   ② 外部共享布局（官方或第三方 未开隔离）：实例目录 = 根本身，mods/saves 都在根下
///   ③ 外部隔离布局（开了版本隔离）：实例目录 = 根\versions\&lt;id&gt;
/// ③ 以前被当成 ② 处理，导致版本列表 0 模组、本地模组为空、光影前置检测不到、启动时模组不加载。
/// </summary>
public static class InstanceLayout
{
    /// <summary>实例目录下的专属内容标记：出现任意一个就说明该版本是隔离的。</summary>
    private static readonly string[] IsolatedMarkers =
    {
        "mods", "saves", "config", "resourcepacks", "shaders", "logs",
        "options.txt", "servers.dat", "screenshots", "crash-reports", "command_history.txt",
    };

    /// <summary>外部（官方或第三方）根目录下某版本的实例目录。</summary>
    public static string ResolveExternal(string gameRoot, string versionId)
    {
        var versionDir = Path.Combine(gameRoot, "versions", versionId);
        return IsIsolated(versionDir) ? versionDir : gameRoot;
    }

    /// <summary>该版本目录是否是「实例目录」而不是「版本文件目录」。</summary>
    public static bool IsIsolated(string versionDir)
    {
        try
        {
            if (!Directory.Exists(versionDir)) return false;

            // 第三方启动器写在版本目录内的隔离标记（<启动器>\Setup.ini 的 VersionArgumentIndie）。
            // 不绑定具体目录名：任何启动器按这个约定写的都能识别。
            foreach (var sub in Directory.EnumerateDirectories(versionDir))
            {
                var setup = Path.Combine(sub, "Setup.ini");
                if (!File.Exists(setup)) continue;
                try
                {
                    var text = File.ReadAllText(setup);
                    if (text.IndexOf("VersionArgumentIndieV2:True", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        text.IndexOf("VersionArgumentIndie:1", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                catch (Exception)
                {
                    // 读不了就看下面的文件标记
                }
            }

            foreach (var marker in IsolatedMarkers)
            {
                var path = Path.Combine(versionDir, marker);
                if (Directory.Exists(path) || File.Exists(path)) return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
