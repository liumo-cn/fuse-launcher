using System;
using System.Diagnostics;
using System.Management;

namespace Fuse.App;

/// <summary>
/// LaunchState 只在内存里,启动器重启后不知道游戏还在跑。
/// 启动时给游戏带 -Dfuse.version 标记,这里按标记反查 javaw 命令行把游戏认领回来。
/// </summary>
internal static class RunningGameFinder
{
    public static Process? FindByMarker(string versionId)
    {
        try
        {
            var marker = "-Dfuse.version=" + versionId;
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'javaw.exe'");
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    if (o["CommandLine"] is not string cmd) continue;
                    if (!HasToken(cmd, marker)) continue;
                    if (o["ProcessId"] is not uint pid || pid == 0) continue;
                    try
                    {
                        var p = Process.GetProcessById((int)pid);
                        if (!p.HasExited) return p;
                    }
                    catch (Exception)
                    {
                        // 进程刚好退出了,继续找下一个
                    }
                }
            }
        }
        catch (Exception)
        {
            // WMI 不可用就当没找到,走正常启动
        }
        return null;
    }

    /// <summary>整词匹配,避免 26.1 误认领 26.1-fabric-0.20.1。</summary>
    private static bool HasToken(string text, string token)
    {
        var idx = 0;
        while ((idx = text.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = idx + token.Length;
            if (end >= text.Length) return true;
            var c = text[end];
            if (char.IsWhiteSpace(c) || c == '"') return true;
            idx = end;
        }
        return false;
    }
}
