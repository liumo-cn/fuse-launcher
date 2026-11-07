using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Fuse.App;

/// <summary>
/// FPS 监控(PresentMon 2.x):
/// 用 --output_stdout 让 PresentMon 把 CSV 行实时写到标准输出,逐行解析 MsBetweenPresents。
/// (--output_file 是块缓冲的,游戏运行期间文件里没有换行,读不到实时数据。)
/// PresentMon 是外部 ETW 观察,不注入游戏(MIT,https://github.com/GameTechDev/PresentMon)。
/// 2.6 起参数只认双横线,且需要管理员或 Performance Log Users 组成员。
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    /// <summary>PresentMon.exe 的查找路径:启动器目录\tools\PresentMon-x64.exe。</summary>
    public static string ExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "PresentMon-x64.exe");

    public static bool Available => File.Exists(ExePath);

    private Process? _process;
    private int _msIndex = -1;

    public float Fps { get; private set; }

    /// <summary>PresentMon 报 "access denied" 时为 true:需要管理员或 Performance Log Users。</summary>
    public bool PermissionDenied { get; private set; }

    /// <summary>开始跟踪目标进程(PresentMon 按 PID 过滤)。</summary>
    public bool Start(int pid)
    {
        Stop();
        if (!Available) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                // 固定会话名 + 启动前清理自己的残留会话:
                // 被 Kill 的 PresentMon 会留下 ETW 会话,下次启动会报 already running。
                Arguments = "--process_id " + pid
                            + " --output_stdout --exclude_dropped --terminate_on_proc_exit"
                            + " --session_name FuseLauncher --stop_existing_session",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _process = Process.Start(psi);
            if (_process == null) return false;

            var proc = _process;
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                try { WidgetWindow.Boot("[presentmon] exited code " + proc.ExitCode); }
                catch (Exception) { }
            };
            var stderrLines = 0;
            proc.ErrorDataReceived += (_, e) =>
            {
                var line = e.Data ?? "";
                if (line.Length > 0 && stderrLines++ < 3)
                    WidgetWindow.Boot("[presentmon] stderr: " + line);
                if (line.IndexOf("access denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("administrative privileges", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("Performance Log Users", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!PermissionDenied)
                    {
                        PermissionDenied = true;
                        WidgetWindow.Boot("[presentmon] permission denied (need admin or Performance Log Users)");
                    }
                }
            };
            proc.OutputDataReceived += (_, e) => ParseLine(e.Data);
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>解析一行 CSV:首行定列,后续行取 MsBetweenPresents。</summary>
    private void ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        line = line!.TrimStart('\uFEFF');
        try
        {
            if (_msIndex < 0)
            {
                if (!line.StartsWith("Application", StringComparison.Ordinal)) return;
                var columns = line.Split(',');
                var index = Array.FindIndex(columns,
                    c => c.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    index = Array.FindIndex(columns,
                        c => c.Equals("MsBetweenDisplayChange", StringComparison.OrdinalIgnoreCase));
                _msIndex = index;
                return;
            }

            var parts = line.Split(',');
            if (_msIndex >= parts.Length) return;
            if (!double.TryParse(parts[_msIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0)
                return;
            Fps = (float)(1000.0 / ms);
        }
        catch (Exception)
        {
            // 半截行/异常字段:跳过
        }
    }

    public void Stop()
    {
        Fps = 0;
        _msIndex = -1;
        try
        {
            if (_process is { HasExited: false }) _process.Kill();
            _process?.Dispose();
        }
        catch (Exception)
        {
        }
        _process = null;
    }

    public void Dispose() => Stop();
}
