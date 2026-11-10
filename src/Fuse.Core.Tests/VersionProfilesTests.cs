using System;
using System.IO;
using Fuse.Core.Config;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>每版本配置持久化:默认值、写盘重读、删除、损坏文件重建。</summary>
public class VersionProfilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mcdl-profiles-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "profiles.json");

    public VersionProfilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Get_ReturnsDefaultProfileAndCachesIt()
    {
        var profiles = new VersionProfiles(FilePath);

        var p = profiles.Get("1.21.7");

        Assert.Equal("auto", p.JavaPolicy);
        Assert.Equal(0, p.MaxHeapMb);
        Assert.Null(p.DisplayName);
    }

    [Fact]
    public void Set_PersistsAndReopenReadsItBack()
    {
        var profiles = new VersionProfiles(FilePath);
        profiles.Set("1.21.7", new VersionProfile
        {
            DisplayName = "整合包主档",
            JavaPolicy = "manual",
            JavaHomeOverride = @"D:\Java\21",
            MaxHeapMb = 8192,
            JvmArgs = "-XX:+UseG1GC",
            AutoJoinServer = "mc.example.com:25565",
        });

        var reopened = new VersionProfiles(FilePath).Get("1.21.7");

        Assert.Equal("整合包主档", reopened.DisplayName);
        Assert.Equal("manual", reopened.JavaPolicy);
        Assert.Equal(@"D:\Java\21", reopened.JavaHomeOverride);
        Assert.Equal(8192, reopened.MaxHeapMb);
        Assert.Equal("-XX:+UseG1GC", reopened.JvmArgs);
        Assert.Equal("mc.example.com:25565", reopened.AutoJoinServer);
    }

    [Fact]
    public void Remove_DeletesEntryAndPersists()
    {
        var profiles = new VersionProfiles(FilePath);
        profiles.Set("a", new VersionProfile { DisplayName = "A" });
        profiles.Set("b", new VersionProfile { DisplayName = "B" });

        profiles.Remove("a");
        var reopened = new VersionProfiles(FilePath);

        Assert.Equal("B", reopened.Get("b").DisplayName);
        Assert.Equal("auto", reopened.Get("a").JavaPolicy);   // 已删,拿到的是全新默认值
    }

    [Fact]
    public void CorruptJson_RebuildsWithoutThrowing()
    {
        File.WriteAllText(FilePath, "{ 这不是 JSON ");

        var profiles = new VersionProfiles(FilePath);

        Assert.Equal("auto", profiles.Get("1.21.7").JavaPolicy);
    }

    [Fact]
    public void Set_IsAtomic_TmpFileNotLeftBehind()
    {
        var profiles = new VersionProfiles(FilePath);
        profiles.Set("v", new VersionProfile { DisplayName = "one" });
        profiles.Set("v", new VersionProfile { DisplayName = "two" });

        Assert.Equal("two", new VersionProfiles(FilePath).Get("v").DisplayName);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
