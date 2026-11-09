using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using Fuse.Core.Installer;
using Fuse.Core.Java;
using Fuse.Core.Launch;
using Fuse.Core.ModConflict;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>审查修复批次的回归:编码/argfile/位数/排序/防护 fail-closed。</summary>
public class HardeningTests
{
    // ---------- 游戏日志编码 ----------

    [Fact]
    public void GameLogEncoding_MatchesJvmOutputProperties()
    {
        Assert.Equal(Encoding.UTF8, LaunchPipeline.GameLogEncoding(21));
        Assert.Equal(Encoding.UTF8, LaunchPipeline.GameLogEncoding(19));
        // 先调 GameLogEncoding(注册 CodePages),再用 GetEncoding 比对
        var gb = LaunchPipeline.GameLogEncoding(8);
        Assert.Equal(Encoding.GetEncoding("GB18030"), gb);
        Assert.Equal(Encoding.GetEncoding("GB18030"), LaunchPipeline.GameLogEncoding(18));
    }

    // ---------- @argfile ----------

    private static string ShortClasspath()
        => string.Join(";", Enumerable.Range(0, 220)
            .Select(i => @"C:\liumo\游戏目录\libraries\some\group\artifact" + i + @"\lib-" + i + @".jar"));

    private static string LongClasspath()
        => string.Join(";", Enumerable.Range(0, 440)
            .Select(i => @"C:\liumo\游戏目录\libraries\some\group\artifact" + i + @"\lib-" + i + @".jar"));

    [Fact]
    public void Argfile_ShortOrJava8_KeepsSingleString()
    {
        var shortArgs = "-cp \"" + ShortClasspath() + "\" Main";
        var (args1, file1) = LiumoArgfile.TryBuild(shortArgs, 21, Path.GetTempPath(), "t1");
        Assert.Null(file1);
        Assert.Equal(shortArgs, args1);

        var longArgs = "-cp \"" + string.Join(";", Enumerable.Range(0, 400)
            .Select(i => @"C:\libs\long\artifact" + i + @".jar")) + "\" Main";
        var (args2, file2) = LiumoArgfile.TryBuild(longArgs, 8, Path.GetTempPath(), "t2");
        Assert.Null(file2);
        Assert.Equal(longArgs, args2);
    }

    [Fact]
    public void Argfile_Java9Plus_LongArgs_MovesClasspathIntoArgfile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcdl-argfile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cp = LongClasspath();
            var fullArgs = "-cp \"" + cp + "\" net.minecraft.Main --gameDir X";
            var (args, file) = LiumoArgfile.TryBuild(fullArgs, 21, dir, "1.20.1-Forge 47.2.0");

            Assert.NotNull(file);
            Assert.True(File.Exists(file));
            Assert.Contains("@", args);
            Assert.DoesNotContain("lib-10.jar", args, StringComparison.Ordinal); // 原串里的 classpath 已搬走
            var content = File.ReadAllLines(file!);
            Assert.StartsWith("-cp", content[0]);
            Assert.Contains("lib-10.jar", content[0], StringComparison.Ordinal);
            // 反斜杠必须成对(否则 JVM 把 \a 当转义)
            Assert.Contains("some\\\\group", content[0], StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ---------- JVM 位数 ----------

    [Fact]
    public void Is64BitJava_ReadsPeHeader()
    {
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        if (File.Exists(system32))
            Assert.True(JavaLocator.Is64BitJava(system32));

        // SysWOW64 里的 cmd.exe 是 32 位(仅在 64 位系统上存在);
        // GetFolderPath(System) 返回小写 system32,不能用字符串替换拼
        var windowsDir = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.System));
        var wow64 = Path.Combine(windowsDir!, "SysWOW64", "cmd.exe");
        if (File.Exists(wow64))
            Assert.False(JavaLocator.Is64BitJava(wow64));
    }

    [Fact]
    public void ParseMajor_AcceptsUppercaseU()
    {
        Assert.Equal(8, JavaLocator.ParseMajor("8U51-cacert462b08"));
        Assert.Equal(8, JavaLocator.ParseMajor("8u421"));
        Assert.Equal(8, JavaLocator.ParseMajor("1.8.0_392"));
        Assert.Equal(17, JavaLocator.ParseMajor("17.0.10"));
    }

    // ---------- Mojang 运行时 ----------

    private static JavaRuntimeEntry Entry(string component, string version)
        => new(component, version, JavaLocator.ParseMajor(version),
            "https://example.com/" + version + ".json", "sha-" + version, "windows-x64");

    [Fact]
    public void JavaRuntimePick_OrdersVersionNamesNumerically()
    {
        var entries = new[] { Entry("java-runtime-gamma", "17.0.9"), Entry("java-runtime-gamma", "17.0.10") };
        Assert.Equal("17.0.10", MojangJavaRuntime.Pick(entries, "java-runtime-gamma", 17)!.VersionName);
    }

    [Fact]
    public void PlanFiles_MissingSha1_FailsClosed()
    {
        var manifest = JObject.Parse("""
        {
          "files": {
            "bin/java.exe": {
              "type": "file",
              "downloads": { "raw": { "size": 1, "url": "https://example.com/java.exe" } }
            }
          }
        }
        """);
        var ex = Assert.Throws<InvalidOperationException>(
            () => MojangJavaRuntime.PlanFiles(manifest, Path.GetTempPath()));
        Assert.Contains("liumo-guard", ex.Message);
    }

    [Fact]
    public void PlanFiles_EntryOutsideRoot_ThrowsGuard()
    {
        var manifest = JObject.Parse("""
        {
          "files": {
            "../evil.dll": {
              "type": "file",
              "downloads": { "raw": { "sha1": "aaa", "size": 1, "url": "https://example.com/e.dll" } }
            }
          }
        }
        """);
        Assert.Throws<IOException>(() => MojangJavaRuntime.PlanFiles(manifest, Path.GetTempPath()));
    }

    // ---------- 冲突修复 fail-closed ----------

    [Fact]
    public async System.Threading.Tasks.Task Fixer_MissingModsDir_RefusesWithoutBackup()
    {
        var missing = Path.Combine(Path.GetTempPath(), "mcdl-no-mods-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<IOException>(() => ModConflictFixer.ApplyAsync(
            missing, "1.20.1", "fabric",
            Array.Empty<ModFixAction>(), new HttpClient(), null, CancellationToken.None));
        Assert.Contains("liumo-guard", ex.Message);
    }

    // ---------- OptiFine 数值选版 ----------

    [Fact]
    public void OptiFinePatchRank_IsNumeric()
    {
        Assert.Equal(9, OptiFineInstaller.LiumoPatchRank("1.19.4_HD_U_I9"));
        Assert.Equal(10, OptiFineInstaller.LiumoPatchRank("1.19.4_HD_U_I10"));
        Assert.True(OptiFineInstaller.LiumoPatchRank("1.19.4_HD_U_I10")
                    > OptiFineInstaller.LiumoPatchRank("1.19.4_HD_U_I9"));
        Assert.Equal(4, OptiFineInstaller.LiumoPatchRank("1.19.4_HD_U_I4"));
    }
}
