using System;
using System.Linq;
using Fuse.Core.Versioning;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 完整命令行组装：主类必须在 JVM 参数与游戏参数之间（缺失时 JVM 会把
/// --username 等游戏参数当成自己的选项，报 Unrecognized option 直接退出）。
/// v0.2.1 实机故障回归测试。
/// </summary>
public class CommandLineBuilderTests
{
    private static McVersion Vanilla() => new()
    {
        Id = "1.20",
        MainClass = "net.minecraft.client.main.Main",
    };

    [Fact]
    public void MainClass_SitsBetweenJvmAndGameArgs()
    {
        var jvm = new[] { "-Xmx4096M", "-cp", @"C:\mc\libraries\a.jar;C:\mc\versions\1.20\1.20.jar" };
        var game = new[] { "--username", "Doctor", "--version", "1.20" };

        var line = CommandLineBuilder.Build(Vanilla(), jvm, game);

        var mainIdx = line.IndexOf("net.minecraft.client.main.Main", StringComparison.Ordinal);
        var cpIdx = line.IndexOf("-cp ", StringComparison.Ordinal);
        var usernameIdx = line.IndexOf("--username", StringComparison.Ordinal);
        Assert.True(mainIdx > cpIdx, "主类应在 -cp 之后");
        Assert.True(usernameIdx > mainIdx, "游戏参数必须在主类之后（否则 JVM 拒绝启动）");
    }

    [Fact]
    public void Classpath_IsAlwaysQuoted()
    {
        var jvm = new[] { "-cp", @"C:\mc\libraries\a.jar;C:\mc\versions\1.20\1.20.jar" };
        var line = CommandLineBuilder.Build(Vanilla(), jvm, Array.Empty<string>());
        // -cp 的值必须带引号（即使不含空格），防路径含空格断裂
        Assert.Contains("-cp \"C:\\mc\\libraries\\a.jar;C:\\mc\\versions\\1.20\\1.20.jar\"", line);
    }

    [Fact]
    public void SpacePath_IsQuotedAndRoundTrips()
    {
        var jvm = new[] { "-cp", @"C:\My Games\mc\a.jar" };
        var line = CommandLineBuilder.Build(Vanilla(), jvm, new[] { "--gameDir", @"C:\My Games\inst" });
        Assert.Contains("\"C:\\My Games\\mc\\a.jar\"", line);
        Assert.Contains("\"C:\\My Games\\inst\"", line);
    }
}
