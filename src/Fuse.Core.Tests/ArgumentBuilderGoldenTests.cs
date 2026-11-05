using System;
using System.IO;
using System.Linq;
using Fuse.Core.Versioning;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 启动参数组装金样本测试：用真实官方 1.20.1 version.json（piston-meta 拉取，SHA-1
/// fff64d6821a97cf12ce5f16df58dd5db0ed210b3）验证规则求值、占位符替换与 GC 策略（R7 守护）。
/// </summary>
public class ArgumentBuilderGoldenTests : IDisposable
{
    private readonly string _root;
    private readonly McVersion _vanilla;
    private readonly LaunchProfile _profile;

    public ArgumentBuilderGoldenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcdl-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "1.20.1"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vanilla-1.20.1.json"),
            Path.Combine(_root, "1.20.1", "1.20.1.json"));
        _vanilla = VersionJsonLoader.Load(_root, "1.20.1");

        _profile = new LaunchProfile
        {
            GameDirectory = @"D:\mc\.minecraft",
            AssetsRoot = @"D:\mc\.minecraft\assets",
            NativesDirectory = @"D:\mc\.minecraft\bin\1.20.1",
            LibrariesDirectory = @"D:\mc\.minecraft\libraries",
            ClientJarPath = @"D:\mc\.minecraft\versions\1.20.1\1.20.1.jar",
            PlayerName = "Steve",
            PlayerUuid = new Guid("5627dd98-e6be-3c21-b8a8-e92344183641"),
            AccessToken = "token123",
            UserType = "legacy",
            VersionName = "1.20.1",
            VersionType = "release",
            MaxHeapMb = 4096,
            JavaMajor = 17,
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void JvmArgs_ContainMemoryGcEncodingAndClasspath()
    {
        var (jvm, _) = ArgumentBuilder.Build(_vanilla, _profile);

        Assert.Equal("-Xmx4096M", jvm[0]);
        // Java 17 落在 常用的 ZGC 区间（15–20）
        Assert.Contains("-XX:+UseZGC", jvm);
        Assert.DoesNotContain("-XX:+UseG1GC", jvm);
        Assert.Contains("-Dfile.encoding=GB18030", jvm);            // 17 → 中文兼容编码
        Assert.Contains("-Djava.library.path=D:\\mc\\.minecraft\\bin\\1.20.1", jvm);
        Assert.Contains("-Dlog4j.configurationFile=", jvm.Single(a => a.StartsWith("-Dlog4j.configurationFile=")));
    }

    [Fact]
    public void JvmArgs_UsesGenerationalZGCOnJava21()
    {
        _profile.JavaMajor = 21;
        var (jvm, _) = ArgumentBuilder.Build(_vanilla, _profile);
        Assert.Contains("-XX:+UseZGC", jvm);
        Assert.Contains("-XX:+ZGenerational", jvm);
        Assert.DoesNotContain("-XX:+UseG1GC", jvm);
    }

    [Fact]
    public void JvmArgs_UsesG1OnOldJava()
    {
        _profile.JavaMajor = 8;
        var (jvm, _) = ArgumentBuilder.Build(_vanilla, _profile);
        Assert.Contains("-XX:+UseG1GC", jvm);
        Assert.Contains("-XX:MaxGCPauseMillis=50", jvm);
        Assert.DoesNotContain("-XX:+UseZGC", jvm);
    }

    [Fact]
    public void Classpath_ContainsLibrariesAndClientJar()
    {
        var cp = ArgumentBuilder.BuildClasspath(_vanilla, _profile, RuleEnvironment.WindowsX64);

        Assert.Contains(@"D:\mc\.minecraft\libraries\org\lwjgl\lwjgl\3.3.1\lwjgl-3.3.1.jar", cp);
        Assert.Contains(@"D:\mc\.minecraft\libraries\com\google\guava\guava\31.1-jre\guava-31.1-jre.jar", cp);
        // 新式 natives 库也进 classpath（与官方启动器一致）
        Assert.Contains(@"lwjgl-glfw-3.3.1-natives-windows.jar", cp);
        Assert.EndsWith(@"versions\1.20.1\1.20.1.jar", cp, StringComparison.OrdinalIgnoreCase);
        // macOS 专属库不得出现
        Assert.DoesNotContain("java-objc-bridge", cp); // 注意：该库无规则，实际允许；见下一条排除
    }

    [Fact]
    public void GameArgs_VanillaPlaceholdersReplaced()
    {
        var (_, game) = ArgumentBuilder.Build(_vanilla, _profile);
        var text = string.Join(" ", game);

        Assert.Contains("--username Steve", text);
        Assert.Contains("--uuid 5627dd98-e6be-3c21-b8a8-e92344183641", text);
        Assert.Contains("--accessToken token123", text);
        Assert.Contains("--userType legacy", text);
        Assert.Contains("--versionType release", text);
        Assert.Contains("--assetsDir D:\\mc\\.minecraft\\assets", text);
        Assert.Contains("--assetIndex 5", text);
        Assert.Contains("--gameDir D:\\mc\\.minecraft", text);
        // 未提供的 quickPlay 占位符应替换为空串（相邻参数保留但值空）
        Assert.DoesNotContain("${", text);
    }

    [Fact]
    public void GameArgs_QuickPlayJoinServer()
    {
        _profile.ServerAddress = "mc.example.com:25565";
        var (_, game) = ArgumentBuilder.Build(_vanilla, _profile);
        var idx = game.ToList().IndexOf("--quickPlayMultiplayer");
        Assert.True(idx >= 0, "1.20.1 应使用 QuickPlay 多人进服");
        Assert.Equal("mc.example.com:25565", game[idx + 1]);
    }

    [Fact]
    public void GameArgs_LegacyServerArgs()
    {
        _profile.ServerAddress = "mc.example.com";
        var legacy = new McVersion
        {
            Id = "1.12.2",
            MainClass = "net.minecraft.client.main.Main",
            MinecraftArguments = "--username ${auth_player_name} --version ${version_name}",
        };
        var (_, game) = ArgumentBuilder.Build(legacy, _profile);
        var text = string.Join(" ", game);
        Assert.Contains("--username Steve", text);
        Assert.Contains("--server mc.example.com", text);
        Assert.Contains("--port 25565", text);
    }

    [Fact]
    public void CustomResolution_GatedByFeature()
    {
        _profile.CustomResolution = true;
        var (_, game) = ArgumentBuilder.Build(_vanilla, _profile);
        var text = string.Join(" ", game);
        Assert.Contains("--width 854", text);
        Assert.Contains("--height 480", text);
    }

    [Fact]
    public void WindowsQuoter_HandlesSpacesQuotesAndTrailingBackslashes()
    {
        Assert.Equal("plain", WindowsArgQuoter.Quote("plain"));
        Assert.Equal("\"a b\"", WindowsArgQuoter.Quote("a b"));
        Assert.Equal("\"\"", WindowsArgQuoter.Quote(""));
        Assert.Equal("\"a\\\"b\"", WindowsArgQuoter.Quote("a\"b"));
        // 无空格的反斜杠无需引号（CRT 仅在引号前解析反斜杠）
        Assert.Equal("a\\", WindowsArgQuoter.Quote("a\\"));
        // 引号包裹时结尾反斜杠必须翻倍
        Assert.Equal("\"a b\\\\\"", WindowsArgQuoter.Quote("a b\\"));
    }
}
