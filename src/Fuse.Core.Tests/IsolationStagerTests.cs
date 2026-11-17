using System;
using System.IO;
using Fuse.Core.Versioning;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>自动虚拟隔离:staging 幂等、检测自动翻转、SafeDeleteTree 不伤共享内容。</summary>
public class IsolationStagerTests : IDisposable
{
    private readonly string _root;      // 外部共享游戏根
    private readonly string _install;   // 主安装目录(独立路径,保证外部布局)

    public IsolationStagerTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "mcdl-stage-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "game");
        _install = Path.Combine(baseDir, "install");
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(Path.Combine(_root, "versions", "v1"));
        File.WriteAllText(Path.Combine(_root, "versions", "v1", "v1.json"),
            """{"id":"v1","type":"release","releaseTime":"2026-01-01T00:00:00"}""");
    }

    public void Dispose()
    {
        try { SafeDirectory.DeleteTree(_root, Path.Combine(_root, "..")); } catch (IOException) { }
        try { Directory.Delete(_install, true); } catch (IOException) { }
    }

    private void MakeSharedContent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "saves", "world"));
        File.WriteAllText(Path.Combine(_root, "saves", "world", "level.dat"), "data");
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "mods", "a.jar"), "jar");
        File.WriteAllText(Path.Combine(_root, "options.txt"), "sound:1.0");
    }

    [Fact]
    public void Stage_SharedRoot_CreatesJunctions_AndFlipsDetection()
    {
        MakeSharedContent();

        var r = IsolationStager.EnsureStaged(_root, "v1", _install);

        Assert.True(r.Changed);
        Assert.Contains("saves", r.Junctions);
        Assert.Contains("mods", r.Junctions);
        Assert.Contains("config", r.Created);
        Assert.Contains("options.txt", r.Copied);
        // junction 穿透可见共享内容,游戏行为不变
        Assert.True(File.Exists(Path.Combine(_root, "versions", "v1", "saves", "world", "level.dat")));
        // 检测自动翻转:从此 InstDir() 是版本目录而不是游戏根
        Assert.True(InstanceLayout.IsIsolated(Path.Combine(_root, "versions", "v1")));
        // 原始共享内容一个字节都没动
        Assert.True(File.Exists(Path.Combine(_root, "saves", "world", "level.dat")));
    }

    [Fact]
    public void Stage_IsIdempotent()
    {
        MakeSharedContent();
        IsolationStager.EnsureStaged(_root, "v1", _install);

        // Fuse 已 stage 的版本第二轮走增量补链:没有新内容就没有任何改动
        var second = IsolationStager.EnsureStaged(_root, "v1", _install);

        Assert.Null(second.Skipped);
        Assert.False(second.Changed);
        Assert.Empty(second.Failed);
        // 共享内容与 junction 都保持原状
        Assert.True(File.Exists(Path.Combine(_root, "saves", "world", "level.dat")));
        Assert.True(Directory.Exists(Path.Combine(_root, "versions", "v1", "saves")));
    }

    [Fact]
    public void Stage_LinksGenericContentDirs_SkipsLauncherOwnedAndHidden()
    {
        MakeSharedContent();
        WriteAllUnder(Path.Combine(_root, "kubejs", "startup_scripts"), "main.js", "vefc");
        WriteAllUnder(Path.Combine(_root, "schematics"), "house.schema", "x");
        Directory.CreateDirectory(Path.Combine(_root, "tools"));
        Directory.CreateDirectory(Path.Combine(_root, ".zcode"));

        var r = IsolationStager.EnsureStaged(_root, "v1", _install);

        Assert.Contains("kubejs", r.Junctions);
        Assert.Contains("schematics", r.Junctions);
        // 游戏透过 junction 看见共享 kubejs 脚本
        Assert.True(File.Exists(Path.Combine(_root, "versions", "v1", "kubejs", "startup_scripts", "main.js")));
        // 启动器自有目录与隐藏目录不进版本目录
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "v1", "tools")));
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "v1", ".zcode")));
    }

    [Fact]
    public void Stage_StagedVersion_TopsUpRootDirsAddedLater()
    {
        // 香草纪元事故回归:隔离之后整合包才把 kubejs 落到共享根,下一次启动必须补上
        MakeSharedContent();
        IsolationStager.EnsureStaged(_root, "v1", _install);
        WriteAllUnder(Path.Combine(_root, "kubejs", "startup_scripts"), "main.js", "vefc");

        var second = IsolationStager.EnsureStaged(_root, "v1", _install);

        Assert.Contains("kubejs", second.Junctions);
        Assert.True(File.Exists(Path.Combine(_root, "versions", "v1", "kubejs", "startup_scripts", "main.js")));
    }

    [Fact]
    public void Unstage_RemovesGenericJunctions_LeavesSharedContent()
    {
        MakeSharedContent();
        WriteAllUnder(Path.Combine(_root, "kubejs"), "main.js", "vefc");
        var versionDir = Path.Combine(_root, "versions", "v1");
        IsolationStager.EnsureStaged(_root, "v1", _install);

        IsolationStager.Unstage(versionDir);

        Assert.False(Directory.Exists(Path.Combine(versionDir, "kubejs")));
        Assert.False(Directory.Exists(Path.Combine(versionDir, "saves")));
        // 共享内容完好
        Assert.True(File.Exists(Path.Combine(_root, "kubejs", "main.js")));
        Assert.True(File.Exists(Path.Combine(_root, "mods", "a.jar")));
    }

    private static void WriteAllUnder(string dir, string fileName, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    [Fact]
    public void Stage_AlreadyIsolatedVersion_IsSkipped()
    {
        // PCL 式隔离版本:自带 mods 目录,缺 saves 是"该缺的",不能把共享 saves 链进去
        Directory.CreateDirectory(Path.Combine(_root, "versions", "v1", "mods"));
        Directory.CreateDirectory(Path.Combine(_root, "saves", "world"));

        var r = IsolationStager.EnsureStaged(_root, "v1", _install);

        Assert.Equal("already isolated", r.Skipped);
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "v1", "saves")));
    }

    [Fact]
    public void Stage_VanillaOnlyRoot_CreatesEmptyMarkers_NoJunctions()
    {
        var r = IsolationStager.EnsureStaged(_root, "v1", _install);

        Assert.Empty(r.Junctions);
        Assert.Contains("saves", r.Created);
        Assert.True(InstanceLayout.IsIsolated(Path.Combine(_root, "versions", "v1")));
    }

    [Fact]
    public void SafeDeleteTree_OnStagedVersion_PreservesSharedContent()
    {
        MakeSharedContent();
        IsolationStager.EnsureStaged(_root, "v1", _install);
        var versionDir = Path.Combine(_root, "versions", "v1");

        SafeDirectory.DeleteTree(versionDir, _root);

        Assert.False(Directory.Exists(versionDir));
        // 共享内容完好: junction 只摘链接,不进目标
        Assert.True(File.Exists(Path.Combine(_root, "saves", "world", "level.dat")));
        Assert.True(File.Exists(Path.Combine(_root, "mods", "a.jar")));
    }

    [Fact]
    public void SafeDeleteTree_NormalTree_DeletesFully()
    {
        var tree = Path.Combine(_root, "tree", "sub");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "f.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "tree", "top.txt"), "x");

        SafeDirectory.DeleteTree(Path.Combine(_root, "tree"), _root);

        Assert.False(Directory.Exists(Path.Combine(_root, "tree")));
    }

    [Fact]
    public void SafeDeleteTree_LinkAtRoot_RemovesLinkOnly()
    {
        var target = Path.Combine(_root, "real");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "f.txt"), "x");
        var link = Path.Combine(_root, "link");
        Assert.True(IsolationStager.CreateJunction(link, target));

        SafeDirectory.DeleteTree(link, _root);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "f.txt")));
    }

    [Fact]
    public void Scanner_DoesNotCountThroughJunctions()
    {
        MakeSharedContent();
        IsolationStager.EnsureStaged(_root, "v1", _install);
        // 版本目录自身的真实文件 5 字节;junction 目标里的 level.dat 不该被计入
        File.WriteAllText(Path.Combine(_root, "versions", "v1", "real.bin"), "12345");

        var versions = InstalledVersionScanner.Scan(
            Path.Combine(_root, "versions"), Path.Combine(_root, "instances"),
            Path.Combine(_root, "libraries"), externalLayout: true);

        var v1 = Assert.Single(versions);
        Assert.True(v1.SizeBytes < 1024, "expected only real files counted, got " + v1.SizeBytes);
    }

    [Fact]
    public void Stage_ManagedInstallDirectory_IsSkipped()
    {
        Directory.CreateDirectory(Path.Combine(_install, "versions", "v1"));
        File.WriteAllText(Path.Combine(_install, "versions", "v1", "v1.json"), "{}");

        var r = IsolationStager.EnsureStaged(_install, "v1", _install);

        Assert.Equal("managed layout", r.Skipped);
    }
}
