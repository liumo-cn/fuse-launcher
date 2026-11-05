using System;
using System.IO;
using Fuse.Core.Config;
using Xunit;

namespace Fuse.Core.Tests;

public class PlayStatsStoreTests
{
    private static string NewPath()
        => Path.Combine(Path.GetTempPath(), "mcdl-stats-" + Guid.NewGuid().ToString("N"), "play-stats.json");

    [Fact]
    public void End_AddsDurationLaunchesAndCrashes()
    {
        var path = NewPath();
        var store = new PlayStatsStore(path);
        store.Begin("1.20.1", 1234, DateTime.UtcNow);
        var ok = store.End("1.20.1", 1, DateTime.UtcNow.AddSeconds(120), out var seconds);

        Assert.True(ok);
        Assert.InRange(seconds, 118, 121);
        var entry = store.Get("1.20.1");
        Assert.Equal(1, entry.Launches);
        Assert.Equal(1, entry.Crashes);
        Assert.Equal(seconds, entry.TotalSeconds);
        Assert.Single(store.RecentOf("1.20.1"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ShortSession_CountsLaunchButZeroTime()
    {
        var path = NewPath();
        var store = new PlayStatsStore(path);
        store.Begin("short", 1, DateTime.UtcNow);
        store.End("short", 0, DateTime.UtcNow.AddSeconds(2), out var seconds);

        Assert.Equal(0, seconds);
        var entry = store.Get("short");
        Assert.Equal(1, entry.Launches);
        Assert.Equal(0, entry.TotalSeconds);
    }

    [Fact]
    public void OrphanSession_FinalizesAtHeartbeat_AndClearRemoves()
    {
        var path = NewPath();
        var store = new PlayStatsStore(path);
        store.Begin("orphan", 1, DateTime.UtcNow);
        store.Heartbeat(DateTime.UtcNow.AddSeconds(90));
        store.FinalizeOrphan();

        var entry = store.Get("orphan");
        Assert.Equal(1, entry.Launches);
        Assert.InRange(entry.TotalSeconds, 88, 92);
        Assert.Null(store.Active);

        store.Clear("orphan");
        Assert.Equal(0, store.Get("orphan").Launches);
        Assert.Empty(store.RecentOf("orphan"));
    }
}
