using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Fuse.App;

/// <summary>
/// 同一版本的启动/运行占位。启动入口(大圆单击、版本列表 ▶)都经过这里,
/// 防止同一个版本被重复拉起多个游戏进程。
/// "正在启动"只占住启动管线这段时间;"运行中"一直占住到游戏进程退出。
/// </summary>
internal static class LaunchState
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Launching = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Process> Running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>占住启动位。返回 null 表示可以启动;否则返回拒绝原因。</summary>
    public static string? TryBegin(string versionId)
    {
        lock (Gate)
        {
            if (Launching.Contains(versionId)) return "该版本正在启动中，请稍候";
            if (Running.TryGetValue(versionId, out var proc))
            {
                if (IsAlive(proc)) return "该版本已经在运行了";
                Running.Remove(versionId);
            }
            Launching.Add(versionId);
            return null;
        }
    }

    /// <summary>启动成功:交给进程接管"运行中"占位,进程退出时自动释放。</summary>
    public static void MarkRunning(string versionId, Process process)
    {
        lock (Gate)
        {
            Launching.Remove(versionId);
            Running[versionId] = process;
        }

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => ReleaseAfterExit(versionId, process);
            // 极端情况:进程在挂事件之前就退出了,Exited 不会再来
            if (process.HasExited) ReleaseAfterExit(versionId, process);
        }
        catch (Exception)
        {
            // 无权监听/进程已消失:交给下一次 TryBegin 的存活检查兜底
        }
    }

    /// <summary>启动失败:释放占位,允许重试。</summary>
    public static void Abandon(string versionId)
    {
        lock (Gate) Launching.Remove(versionId);
    }

    public static bool IsLaunching(string versionId)
    {
        lock (Gate) return Launching.Contains(versionId);
    }

    /// <summary>该版本是否正在启动或正在运行(供列表/按钮显示状态)。</summary>
    public static bool IsBusy(string versionId)
    {
        lock (Gate)
        {
            if (Launching.Contains(versionId)) return true;
            if (!Running.TryGetValue(versionId, out var proc)) return false;
            if (IsAlive(proc)) return true;
            Running.Remove(versionId);
            return false;
        }
    }

    /// <summary>取运行中的游戏进程(用于把窗口叫到前台)。</summary>
    public static Process? GetRunning(string versionId)
    {
        lock (Gate)
        {
            if (!Running.TryGetValue(versionId, out var proc)) return null;
            if (IsAlive(proc)) return proc;
            Running.Remove(versionId);
            return null;
        }
    }

    private static void ReleaseAfterExit(string versionId, Process process)
    {
        lock (Gate)
        {
            if (Running.TryGetValue(versionId, out var current) && ReferenceEquals(current, process))
                Running.Remove(versionId);
        }
        // 不要在这里 Dispose:PlayTracker 还要从同一个 Process 读 ExitCode 结算游玩统计,
        // 结算完由 PlayTracker 统一 Dispose。
    }

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch (Exception) { return false; }
    }
}
