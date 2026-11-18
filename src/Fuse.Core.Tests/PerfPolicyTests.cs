using Fuse.Core.Config;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>低特效自动判定:Tier 0/1 或远程桌面才自动开,正常机器不动。</summary>
public class PerfPolicyTests
{
    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    [InlineData(2, false, false)]
    [InlineData(3, false, false)]
    [InlineData(2, true, true)]
    [InlineData(0, true, true)]
    public void ShouldAutoEnableLowEffects(int tier, bool remote, bool expected)
        => Assert.Equal(expected, PerfPolicy.ShouldAutoEnableLowEffects(tier, remote));
}
