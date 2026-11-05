using System;
using System.Diagnostics;
using System.Threading;

namespace Fuse.App;

/// <summary>
/// 游玩会话跟踪:游戏启动成功后开始计时,进程退出时结算;
/// 每分钟写一次心跳,启动器中途退出/被杀时下次启动按心跳补记。
/// </summary>
internal static class PlayTracker
{
    private static readonly object Sync = new();
    private static Process? _process;
    private static string? _versionId;
    private static Timer? _timer;

    public static void Start(string versionId, Process process)
    {
        try
        {
            AppServices.PlayStats.Begin(versionId, process.Id, process.StartTime.ToUniversalTime());
            Track(versionId, process);
        }
        catch (Exception)
        {
            // 统计失败绝不影响游戏
        }
    }

    /// <summary>启动器重启后:上次的游戏进程可能还在跑,能接上就继续计时,否则按心跳结算。</summary>
    public static void ResumeOnStartup()
    {
        var active = AppServices.PlayStats.Active;
        if (active == null) return;
        try
        {
            Process process;
            try
            {
                process = Process.GetProcessById(active.Pid);
            }
            catch (ArgumentException)
            {
                AppServices.PlayStats.FinalizeOrphan();
                return;
            }

            var sameProcess = false;
            try
            {
                sameProcess = !process.HasExited &&
                              Math.Abs((process.StartTime.ToUniversalTime() - active.ProcessStartedUtc).TotalSeconds) <= 2;
            }
            catch (Exception)
            {
                // 拿不到启动时间就按不同进程处理
            }

            if (!sameProcess)
            {
                try { process.Dispose(); } catch (Exception) { }
                AppServices.PlayStats.FinalizeOrphan();
                return;
            }
            Track(active.VersionId, process);
        }
        catch (Exception)
        {
            try { AppServices.PlayStats.FinalizeOrphan(); } catch (Exception) { }
        }
    }

    private static void Track(string versionId, Process process)
    {
        lock (Sync)
        {
            _process = process;
            _versionId = versionId;
            try { process.EnableRaisingEvents = true; } catch (Exception) { }
            process.Exited -= OnExited;
            process.Exited += OnExited;
            _timer?.Dispose();
            _timer = new Timer(_ => Tick(), null, 60_000, 60_000);
        }
        if (SafeHasExited(process)) OnExited(process, EventArgs.Empty);
    }

    private static void OnExited(object? sender, EventArgs e)
    {
        lock (Sync)
        {
            var process = _process;
            var versionId = _versionId;
            if (process == null || versionId == null) return;
            _process = null;
            _versionId = null;
            _timer?.Dispose();
            _timer = null;

            var exitCode = 0;
            try { exitCode = process.ExitCode; } catch (Exception) { }
            try { AppServices.PlayStats.End(versionId, exitCode, DateTime.UtcNow, out _); } catch (Exception) { }
            try { process.Dispose(); } catch (Exception) { }
        }
    }

    private static void Tick()
    {
        lock (Sync)
        {
            if (_process == null) return;
            try
            {
                if (_process.HasExited) OnExited(_process, EventArgs.Empty);
                else AppServices.PlayStats.Heartbeat(DateTime.UtcNow);
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception) { return true; }
    }
}
