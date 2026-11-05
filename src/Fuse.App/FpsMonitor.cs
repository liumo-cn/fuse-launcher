using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.App;

/// <summary>
/// FPS 监控(PresentMon 可选组件):把 Intel 开源的 PresentMon-x64.exe 指到游戏进程,
/// 用 -output_csv 逐秒产出帧率数据,我们解析最新值。找不到 exe 或进程退出时 FPS 显示 --。
/// PresentMon 是外部进程观察(ETW),不注入游戏,反作弊零风险(MIT 协议,来源 https://github.com/GameTechDev/PresentMon)。
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    /// <summary>PresentMon.exe 的查找路径:启动器目录\tools\PresentMon-x64.exe。</summary>
    public static string ExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "PresentMon-x64.exe");

    public static bool Available => File.Exists(ExePath);

    private Process? _process;
    private readonly string _csvPath;
    private Timer? _reader;

    public float Fps { get; private set; }

    public FpsMonitor()
    {
        _csvPath = Path.Combine(Path.GetTempPath(), "fuse-presentmon-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".csv");
    }

    /// <summary>开始跟踪目标进程(PresentMon 按 PID 过滤)。</summary>
    public bool Start(int pid)
    {
        if (!Available) return false;
        Stop();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "-process_id " + pid + " -output_file \"" + _csvPath + "\" -exclude_dropped -terminate_on_timeout 36000",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _process = Process.Start(psi);
            if (_process == null) return false;
            _reader = new Timer(_ => ReadLatest(), null, 1500, 1000);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>读 CSV 最后两行的 MsBetweenPresents 均值 → FPS。</summary>
    private void ReadLatest()
    {
        try
        {
            if (!File.Exists(_csvPath)) return;
            using var fs = new FileStream(_csvPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string? line = null, previous = null, header = null;
            while ((line = reader.ReadLine()) != null)
            {
                if (header == null && line.StartsWith("Application")) header = line;
                previous = line;
            }
            if (previous == null || header == null) return;
            var columns = header.Split(',');
            var msIndex = -1;
            for (var i = 0; i < columns.Length; i++)
            {
                // 优先 MsBetweenPresents(呈现间隔),退回 MsBetweenDisplayChange
                if (columns[i].Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase))
                {
                    msIndex = i;
                    break;
                }
                if (msIndex < 0 && columns[i].Equals("MsBetweenDisplayChange", StringComparison.OrdinalIgnoreCase))
                    msIndex = i;
            }
            if (msIndex < 0) return;
            var parts = previous.Split(',');
            if (msIndex >= parts.Length) return;
            if (!double.TryParse(parts[msIndex], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ms) || ms <= 0) return;
            Fps = (float)(1000.0 / ms);
        }
        catch (Exception)
        {
            // CSV 半截写入/被占用:下个周期重试
        }
    }

    public void Stop()
    {
        _reader?.Dispose();
        _reader = null;
        try
        {
            if (_process is { HasExited: false }) _process.Kill();
            _process?.Dispose();
        }
        catch (Exception)
        {
        }
        _process = null;
        try { if (File.Exists(_csvPath)) File.Delete(_csvPath); } catch (IOException) { }
    }

    public void Dispose() => Stop();
}
