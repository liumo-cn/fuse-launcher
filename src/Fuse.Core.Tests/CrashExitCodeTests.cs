using System;
using Fuse.Core.Monitoring;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>崩溃退出码分类:NTSTATUS 负数(uint 溢出)映射与未知码回退。</summary>
public class CrashExitCodeTests
{
    [Fact]
    public void KnownNtStatus_AreDescribed()
    {
        Assert.Contains("0xC0000005", CrashExitCode.Describe(unchecked((int)0xC0000005)));
        Assert.Contains("CTRL", CrashExitCode.Describe(unchecked((int)0xC000013A)));
        Assert.Contains("栈溢出", CrashExitCode.Describe(unchecked((int)0xC00000FD)));
    }

    [Fact]
    public void ForcedKill_IsDescribed()
    {
        Assert.Contains("强制杀死", CrashExitCode.Describe(-1));
    }

    [Fact]
    public void UnknownCode_ReturnsEmptySuffix()
    {
        Assert.Equal("", CrashExitCode.Describe(0));
        Assert.Equal("", CrashExitCode.Describe(1));
        Assert.Equal("", CrashExitCode.Describe(-123456789));
    }
}
