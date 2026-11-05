using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Fuse.Core.Config;

/// <summary>单个版本的游玩统计。</summary>
public sealed class PlayStatEntry
{
    public long TotalSeconds { get; set; }
    public int Launches { get; set; }
    public int Crashes { get; set; }
    public DateTime? FirstPlayedUtc { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public long LastDurationSeconds { get; set; }
}

/// <summary>一次已结束的会话(最近记录用)。</summary>
public sealed class PlaySession
{
    public string VersionId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public long Seconds { get; set; }
    public int ExitCode { get; set; }
}

/// <summary>正在进行的会话:启动器中途退出/被杀时,靠心跳补记时长。</summary>
public sealed class ActivePlaySession
{
    public string VersionId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public int Pid { get; set; }
    public DateTime ProcessStartedUtc { get; set; }
}

/// <summary>
/// 游玩时长/启动次数/崩溃次数统计(data/play-stats.json)。
/// 只记游戏进程生命周期;小于 5 秒的会话时长记 0(仍计一次启动),崩溃按退出码非 0 统计。
/// </summary>
public sealed class PlayStatsStore
{
    private sealed class Root
    {
        public Dictionary<string, PlayStatEntry>? Versions { get; set; }
        public List<PlaySession>? Recent { get; set; }
        public ActivePlaySession? Active { get; set; }
    }

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, PlayStatEntry> _versions = new(StringComparer.Ordinal);
    private List<PlaySession> _recent = new();

    public ActivePlaySession? Active { get; private set; }

    public PlayStatsStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var root = JsonConvert.DeserializeObject<Root>(File.ReadAllText(_path));
            if (root == null) return;
            _versions = root.Versions ?? new Dictionary<string, PlayStatEntry>(StringComparer.Ordinal);
            _recent = root.Recent ?? new List<PlaySession>();
            Active = root.Active;
        }
        catch (Exception)
        {
            // 统计文件损坏就重新开始,不影响启动
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(new Root
            {
                Versions = _versions,
                Recent = _recent,
                Active = Active,
            }, Formatting.Indented));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception)
        {
            // 写失败不影响游戏运行
        }
    }

    public PlayStatEntry Get(string versionId)
    {
        lock (_lock)
        {
            return _versions.TryGetValue(versionId, out var entry)
                ? new PlayStatEntry
                {
                    TotalSeconds = entry.TotalSeconds,
                    Launches = entry.Launches,
                    Crashes = entry.Crashes,
                    FirstPlayedUtc = entry.FirstPlayedUtc,
                    LastPlayedUtc = entry.LastPlayedUtc,
                    LastDurationSeconds = entry.LastDurationSeconds,
                }
                : new PlayStatEntry();
        }
    }

    public IReadOnlyList<PlaySession> RecentOf(string versionId, int take = 5)
    {
        lock (_lock)
        {
            return _recent.Where(s => s.VersionId == versionId).Take(take).ToList();
        }
    }

    public long TotalSecondsAll
    {
        get
        {
            lock (_lock) return _versions.Values.Sum(v => v.TotalSeconds);
        }
    }

    /// <summary>游戏启动成功:开始一次会话(已有未结束会话先按心跳补记)。</summary>
    public void Begin(string versionId, int pid, DateTime processStartedUtc)
    {
        lock (_lock)
        {
            if (Active != null) FinalizeLocked(Active.HeartbeatUtc, 0);
            var now = DateTime.UtcNow;
            Active = new ActivePlaySession
            {
                VersionId = versionId,
                StartedUtc = now,
                HeartbeatUtc = now,
                Pid = pid,
                ProcessStartedUtc = processStartedUtc,
            };
            SaveLocked();
        }
    }

    public void Heartbeat(DateTime utc)
    {
        lock (_lock)
        {
            if (Active == null) return;
            Active.HeartbeatUtc = utc;
            SaveLocked();
        }
    }

    /// <summary>游戏退出:结算本次会话。</summary>
    public bool End(string versionId, int exitCode, DateTime endUtc, out long seconds)
    {
        lock (_lock)
        {
            seconds = 0;
            if (Active == null || !string.Equals(Active.VersionId, versionId, StringComparison.Ordinal))
                return false;
            seconds = FinalizeLocked(endUtc, exitCode);
            SaveLocked();
            return true;
        }
    }

    /// <summary>启动器上次没等到游戏退出:用最后一次心跳作为结束时间补记。</summary>
    public void FinalizeOrphan()
    {
        lock (_lock)
        {
            if (Active == null) return;
            FinalizeLocked(Active.HeartbeatUtc, 0);
            SaveLocked();
        }
    }

    public void Clear(string versionId)
    {
        lock (_lock)
        {
            _versions.Remove(versionId);
            _recent.RemoveAll(s => s.VersionId == versionId);
            if (Active != null && Active.VersionId == versionId) Active = null;
            SaveLocked();
        }
    }

    private long FinalizeLocked(DateTime endUtc, int exitCode)
    {
        var session = Active;
        if (session == null) return 0;
        Active = null;
        var seconds = (long)Math.Max(0, (endUtc - session.StartedUtc).TotalSeconds);
        // 小于 5 秒的会话(误点/秒退)仍计一次启动,但时长按 0 记
        var recorded = seconds < 5 ? 0 : seconds;

        if (!_versions.TryGetValue(session.VersionId, out var entry))
        {
            entry = new PlayStatEntry();
            _versions[session.VersionId] = entry;
        }
        entry.TotalSeconds += recorded;
        entry.Launches++;
        if (exitCode != 0) entry.Crashes++;
        entry.FirstPlayedUtc ??= session.StartedUtc;
        entry.LastPlayedUtc = endUtc;
        entry.LastDurationSeconds = recorded;

        _recent.Insert(0, new PlaySession
        {
            VersionId = session.VersionId,
            StartedUtc = session.StartedUtc,
            Seconds = recorded,
            ExitCode = exitCode,
        });
        if (_recent.Count > 50) _recent.RemoveRange(50, _recent.Count - 50);
        return recorded;
    }

    public static string FormatLong(long seconds)
    {
        if (seconds <= 0) return "不到 1 分钟";
        if (seconds < 60) return seconds + " 秒";
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        if (hours == 0) return minutes + " 分钟";
        return minutes == 0 ? hours + " 小时" : hours + " 小时 " + minutes + " 分";
    }

    /// <summary>列表里的紧凑格式。</summary>
    public static string FormatShort(long seconds)
    {
        if (seconds < 60) return "<1 分";
        var hours = seconds / 3600;
        if (hours == 0) return (seconds / 60) + " 分";
        return hours + " 小时";
    }
}
