using System;
using System.IO;
using Fuse.Core.Versioning;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 外部游戏目录（官方或第三方）的实例目录与游戏版本解析。
/// 回归：版本隔离目录曾被当成共享根，导致 0 模组、模组搜索 400、本地/光影为空。
/// </summary>
public class InstanceLayoutTests : IDisposable
{
    private readonly string _root;

    public InstanceLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-instlayout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string NewVersion(string id)
    {
        var dir = Path.Combine(_root, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), "{\"id\":\"" + id + "\"}");
        return dir;
    }

    [Fact]
    public void Shared_root_when_no_isolation_markers()
    {
        var versionDir = NewVersion("1.20.1");
        Assert.False(InstanceLayout.IsIsolated(versionDir));
        Assert.Equal(_root, InstanceLayout.ResolveExternal(_root, "1.20.1"));
    }

    [Fact]
    public void Version_dir_when_isolation_flag_enabled()
    {
        var versionDir = NewVersion("26.1-Fabric 0.19.5");
        Directory.CreateDirectory(Path.Combine(versionDir, "Launcher"));
        File.WriteAllText(Path.Combine(versionDir, "Launcher", "Setup.ini"),
            "VersionArgumentIndie:1\r\nVersionArgumentIndieV2:True\r\n");

        Assert.True(InstanceLayout.IsIsolated(versionDir));
        Assert.Equal(versionDir, InstanceLayout.ResolveExternal(_root, "26.1-Fabric 0.19.5"));
    }

    [Fact]
    public void Version_dir_when_instance_content_exists()
    {
        var versionDir = NewVersion("1.20.1-forge-47.4.10");
        Directory.CreateDirectory(Path.Combine(versionDir, "mods"));
        File.WriteAllText(Path.Combine(versionDir, "options.txt"), "lang:zh_cn");

        Assert.True(InstanceLayout.IsIsolated(versionDir));
        Assert.Equal(versionDir, InstanceLayout.ResolveExternal(_root, "1.20.1-forge-47.4.10"));
    }

    [Fact]
    public void Missing_version_dir_is_shared_root()
    {
        Assert.Equal(_root, InstanceLayout.ResolveExternal(_root, "not-installed"));
    }

    [Fact]
    public void Game_version_prefers_client_version_for_self_contained_json()
    {
        // 自包含版本（部分启动器如此）：没有 inheritsFrom / jar，只有 clientVersion
        var dir = Path.Combine(_root, "versions", "26.1-Fabric 0.19.5");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "26.1-Fabric 0.19.5.json"), new JObject
        {
            ["id"] = "26.1-Fabric 0.19.5",
            ["clientVersion"] = "26.1",
            ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient",
        }.ToString());

        var versionsRoot = Path.Combine(_root, "versions");
        Assert.Equal("26.1", VersionJsonLoader.ResolveGameVersion(versionsRoot, "26.1-Fabric 0.19.5"));
    }

    [Fact]
    public void Game_version_follows_inherits_chain()
    {
        var versionsRoot = Path.Combine(_root, "versions");
        Directory.CreateDirectory(Path.Combine(versionsRoot, "1.20.1"));
        File.WriteAllText(Path.Combine(versionsRoot, "1.20.1", "1.20.1.json"), "{\"id\":\"1.20.1\"}");
        Directory.CreateDirectory(Path.Combine(versionsRoot, "1.20.1-fabric-0.15.0"));
        File.WriteAllText(Path.Combine(versionsRoot, "1.20.1-fabric-0.15.0", "1.20.1-fabric-0.15.0.json"),
            "{\"id\":\"1.20.1-fabric-0.15.0\",\"inheritsFrom\":\"1.20.1\"}");

        Assert.Equal("1.20.1", VersionJsonLoader.ResolveGameVersion(versionsRoot, "1.20.1-fabric-0.15.0"));
    }

    [Fact]
    public void Game_version_falls_back_to_id()
    {
        var dir = Path.Combine(_root, "versions", "custom-pack");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "custom-pack.json"), "{\"id\":\"custom-pack\"}");

        var versionsRoot = Path.Combine(_root, "versions");
        Assert.Equal("custom-pack", VersionJsonLoader.ResolveGameVersion(versionsRoot, "custom-pack"));
    }
}
