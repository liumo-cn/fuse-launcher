using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Fuse.Core.Monitoring;

/// <summary>一次系统性能采样结果。</summary>
public sealed class MetricsSample
{
    public float CpuPercent { get; init; }
    public float GpuPercent { get; init; }
    /// <summary>系统是否有可用的 GPU 3D 引擎计数器(区分"0%"和"读不到")。</summary>
    public bool GpuAvailable { get; init; }
    /// <summary>已用物理内存(MB)。</summary>
    public long MemoryUsedMb { get; init; }
    /// <summary>物理内存总量(MB)。</summary>
    public long MemoryTotalMb { get; init; }
    /// <summary>全网卡下行速率(字节/秒)。</summary>
    public long NetworkDownBps { get; init; }
    /// <summary>全网卡上行速率(字节/秒)。</summary>
    public long NetworkUpBps { get; init; }
}

/// <summary>
/// 系统性能采样:CPU/内存用 GlobalMemoryStatusEx 与进程时间差,
/// GPU/网速用 Windows 自带 PerformanceCounter(GPU Engine 是 Win10+ 内置)。
/// 500ms~1s 周期采样,全部后台线程,UI 只读快照。
/// </summary>
public sealed class SystemMetrics : IDisposable
{
    private readonly List<PerformanceCounter> _gpuCounters = new();
    private readonly List<PerformanceCounter> _netDown = new();
    private readonly List<PerformanceCounter> _netUp = new();
    private readonly object _lock = new();
    private bool _countersReady;
    private DateTime _lastSample = DateTime.UtcNow;
    private long _lastCpuTick;
    private MetricsSample _last = new();

    public SystemMetrics() => EnsureCounters();

    /// <summary>拉一次快照(非阻塞;计数器初始化失败时 GPU/网速为 0)。</summary>
    public MetricsSample Sample()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSample).TotalSeconds;
        if (elapsed < 0.3) return _last;
        _lastSample = now;

        var cpu = CpuPercent(ref _lastCpuTick, elapsed);
        var (memUsed, memTotal) = MemoryMb();
        var (gpu, gpuOk) = GpuPercent();
        var (down, up) = NetworkBps(elapsed);

        _last = new MetricsSample
        {
            CpuPercent = cpu,
            GpuPercent = gpu,
            GpuAvailable = gpuOk,
            MemoryUsedMb = memUsed,
            MemoryTotalMb = memTotal,
            NetworkDownBps = down,
            NetworkUpBps = up,
        };
        return _last;
    }

    // ---------- CPU(进程时间差:比 PDH 更轻,且无 500ms 强制刷新间隔) ----------

    private Process? _selfProcess;

    private float CpuPercent(ref long lastTick, double elapsed)
    {
        try
        {
            _selfProcess ??= Process.GetCurrentProcess();
            var total = _selfProcess.TotalProcessorTime.Ticks;
            var prev = System.Threading.Interlocked.Exchange(ref lastTick, total);
            if (prev == 0 || elapsed <= 0) return 0f;
            var deltaTicks = total - prev;
            var cores = Environment.ProcessorCount;
            var percent = (float)(deltaTicks / TimeSpan.TicksPerSecond / elapsed / cores * 100.0);
            // 进程时间只能反映启动器自身;系统 CPU 用全局 CPU 时间
            return SystemCpuPercent(percent);
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    private float _lastSystemCpu;

    /// <summary>系统级 CPU 占用:PDH \Processor(_Total)\% Processor Time。</summary>
    private float SystemCpuPercent(float fallback)
    {
        try
        {
            _systemCpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
            // PerformanceCounter.NextValue 第一次调用永远返回 0,间隔期由外部节流保证
            var value = _systemCpu.NextValue();
            if (value > 0 || _lastSystemCpu == 0) _lastSystemCpu = value;
            return _lastSystemCpu;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private PerformanceCounter? _systemCpu;

    // ---------- 内存 ----------

    private static (long Used, long Total) MemoryMb()
    {
        if (!TryGlobalMemoryStatus(out var status)) return (0, 0);
        var total = (long)status.ullTotalPhys / (1024 * 1024);
        var used = total - (long)status.ullAvailPhys / (1024 * 1024);
        return (used, total);
    }

    // ---------- GPU(Windows 10+ 内置 GPU Engine 计数器,按引擎求和/上限) ----------

    private void EnsureCounters()
    {
        lock (_lock)
        {
            if (_countersReady) return;
            _countersReady = true;
            EnsureGpuCountersLocked(force: true);
            try
            {
                var netCategory = new PerformanceCounterCategory("Network Interface");
                foreach (var name in netCategory.GetInstanceNames())
                {
                    if (name is "Loopback Pseudo-Interface 1" or "isatap" or "Teredo Tunneling Pseudo-Interface") continue;
                    try
                    {
                        _netDown.Add(new PerformanceCounter("Network Interface", "Bytes Received/sec", name));
                        _netUp.Add(new PerformanceCounter("Network Interface", "Bytes Sent/sec", name));
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
                // 计数器类别缺失(老系统/精简系统):GPU/网速恒为 0
            }
        }
    }

    private DateTime _gpuRefreshedAt = DateTime.MinValue;

    /// <summary>
    /// GPU Engine 的实例随进程出现/消失,不能只枚举一次;每 30 秒或列表为空时重建。
    /// 实例名必须原样传给 PerformanceCounter,不能再拼 "\utilization percentage"。
    /// </summary>
    private void EnsureGpuCountersLocked(bool force)
    {
        if (!force && _gpuCounters.Count > 0 &&
            (DateTime.UtcNow - _gpuRefreshedAt).TotalSeconds < 30) return;
        foreach (var c in _gpuCounters) c.Dispose();
        _gpuCounters.Clear();
        _gpuRefreshedAt = DateTime.UtcNow;
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var name in category.GetInstanceNames())
            {
                // 只取 engtype_3D(游戏主要用 3D 引擎);跨引擎求和会超过 100%
                if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    _gpuCounters.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", name));
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
            // 类别缺失(老系统/精简系统):GPU 显示 --
        }
    }

    private (float Percent, bool Available) GpuPercent()
    {
        try
        {
            lock (_lock) EnsureGpuCountersLocked(force: false);
            if (_gpuCounters.Count == 0) return (0f, false);
            var max = 0f;
            foreach (var counter in _gpuCounters)
            {
                var value = counter.NextValue();
                if (value > max) max = value;
            }
            return (max, true);
        }
        catch (Exception)
        {
            return (0f, false);
        }
    }

    private (long Down, long Up) NetworkBps(double elapsed)
    {
        try
        {
            long down = 0, up = 0;
            foreach (var c in _netDown) down += (long)c.NextValue();
            foreach (var c in _netUp) up += (long)c.NextValue();
            return (down, up);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    public void Dispose()
    {
        foreach (var c in _gpuCounters) c.Dispose();
        foreach (var c in _netDown) c.Dispose();
        foreach (var c in _netUp) c.Dispose();
        _systemCpu?.Dispose();
        _selfProcess?.Dispose();
        _gpuCounters.Clear();
        _netDown.Clear();
        _netUp.Clear();
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusExNative
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusExNative lpBuffer);

    private static bool TryGlobalMemoryStatus(out MemoryStatusExNative status)
    {
        status = new MemoryStatusExNative { dwLength = (uint)Marshal.SizeOf<MemoryStatusExNative>() };
        return GlobalMemoryStatusEx(ref status);
    }
}
