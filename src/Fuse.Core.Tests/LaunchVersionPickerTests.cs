using System.Collections.Generic;
using Fuse.Core.Launch;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>大圆一键启动:上次版本失效时回退到第一个已安装版本。</summary>
public class LaunchVersionPickerTests
{
    private static readonly string[] Installed = { "1.20.1-forge", "1.21.1-fabric" };

    [Fact]
    public void Pick_KeepsValidLast()
    {
        Assert.Equal("1.21.1-fabric", LaunchVersionPicker.Pick("1.21.1-fabric", Installed, out var repaired));
        Assert.False(repaired);
    }

    [Fact]
    public void Pick_FallsBackWhenLastMissing()
    {
        // 改名/乱码/删除后配置里还是旧 id
        Assert.Equal("1.20.1-forge", LaunchVersionPicker.Pick("香草纪元-旧名字", Installed, out var repaired));
        Assert.True(repaired);
    }

    [Fact]
    public void Pick_UsesFirstWhenNoLast()
    {
        Assert.Equal("1.20.1-forge", LaunchVersionPicker.Pick(null, Installed, out var repaired));
        Assert.False(repaired);
        Assert.Equal("1.20.1-forge", LaunchVersionPicker.Pick("", Installed, out repaired));
        Assert.False(repaired);
    }

    [Fact]
    public void Pick_ReturnsNullWhenNothingInstalled()
    {
        Assert.Null(LaunchVersionPicker.Pick("x", new List<string>(), out var repaired));
        Assert.False(repaired);
    }
}
