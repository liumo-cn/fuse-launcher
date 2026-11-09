using System;
using System.IO;
using Fuse.Core.Logging;
using Xunit;

namespace Fuse.Core.Tests;

public class LaunchLogAttributionTests : IDisposable
{
    private readonly string _dir;

    public LaunchLogAttributionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fuse-attrib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string WriteLog(string command)
    {
        var path = Path.Combine(_dir, "latest-launch.log");
        File.WriteAllText(path, command + Environment.NewLine + "[17:00:00] [main/INFO]: hello");
        return path;
    }

    [Fact]
    public void MatchesVersionArgument()
    {
        var log = WriteLog("[cmd] javaw.exe -Xmx4G --username Steve --version 1.20.1 --gameDir D:\\mc");
        Assert.True(LaunchLogAttribution.IsForVersion(log, "1.20.1"));
        Assert.False(LaunchLogAttribution.IsForVersion(log, "1.20.1-forge-47.4.22"));
    }

    [Fact]
    public void MatchesFuseVersionPropertyAndSpaces()
    {
        var log = WriteLog("[cmd] javaw.exe -Dfuse.version=26.1-Fabric 0.19.5 --version 26.1-Fabric 0.19.5");
        Assert.True(LaunchLogAttribution.IsForVersion(log, "26.1-Fabric 0.19.5"));
        Assert.False(LaunchLogAttribution.IsForVersion(log, "26.1-Fabric"));
    }

    [Fact]
    public void MissingFileOrVersionReturnsFalse()
    {
        var log = WriteLog("[cmd] javaw.exe --version 1.20.1");
        Assert.False(LaunchLogAttribution.IsForVersion(Path.Combine(_dir, "nope.log"), "1.20.1"));
        Assert.False(LaunchLogAttribution.IsForVersion(log, ""));
    }
}
