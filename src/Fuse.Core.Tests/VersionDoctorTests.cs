using System;
using System.IO;
using Fuse.Core.Installer;
using Xunit;

namespace Fuse.Core.Tests;

public class VersionDoctorTests : IDisposable
{
    private readonly string _root;
    private readonly string _versions;
    private readonly string _libraries;
    private readonly string _assets;

    public VersionDoctorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-doctor-" + Guid.NewGuid().ToString("N"));
        _versions = Path.Combine(_root, "versions");
        _libraries = Path.Combine(_root, "libraries");
        _assets = Path.Combine(_root, "assets");
        Directory.CreateDirectory(_versions);
        Directory.CreateDirectory(_libraries);
        Directory.CreateDirectory(_assets);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private void WriteVersion(string id, string json)
    {
        var dir = Path.Combine(_versions, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), json);
    }

    private VersionAudit AuditOne(string id)
    {
        var all = VersionDoctor.Audit(_versions, _libraries, _assets);
        foreach (var a in all)
            if (a.VersionId == id) return a;
        throw new InvalidOperationException("audit missing " + id);
    }

    [Fact]
    public void DetectsFabricLoaderAndRootVersion()
    {
        WriteVersion("26.1", "{\"id\":\"26.1\",\"type\":\"release\",\"libraries\":[]}");
        WriteVersion("26.1-fabric-0.20.1",
            "{\"id\":\"26.1-fabric-0.20.1\",\"inheritsFrom\":\"26.1\",\"type\":\"release\"," +
            "\"libraries\":[{\"name\":\"net.fabricmc:fabric-loader:0.20.1\"}]}");
        var audit = AuditOne("26.1-fabric-0.20.1");
        Assert.Equal("fabric", audit.Kind);
        Assert.Equal("26.1", audit.GameVersion);
        Assert.Equal("0.20.1", audit.LoaderVersion);
    }

    [Fact]
    public void DetectsModernForgeFromFmlloader()
    {
        WriteVersion("1.20.1", "{\"id\":\"1.20.1\",\"type\":\"release\",\"libraries\":[]}");
        WriteVersion("1.20.1-forge-47.4.10",
            "{\"id\":\"1.20.1-forge-47.4.10\",\"inheritsFrom\":\"1.20.1\",\"type\":\"release\"," +
            "\"libraries\":[{\"name\":\"net.minecraftforge:fmlloader:1.20.1-47.4.10\"}]}");
        var audit = AuditOne("1.20.1-forge-47.4.10");
        Assert.Equal("forge", audit.Kind);
        Assert.Equal("1.20.1", audit.GameVersion);
        Assert.Equal("47.4.10", audit.LoaderVersion);
    }

    [Fact]
    public void DetectsNeoForge()
    {
        WriteVersion("1.20.4", "{\"id\":\"1.20.4\",\"type\":\"release\",\"libraries\":[]}");
        WriteVersion("1.20.4-neoforge-20.4.253",
            "{\"id\":\"1.20.4-neoforge-20.4.253\",\"inheritsFrom\":\"1.20.4\",\"type\":\"release\"," +
            "\"libraries\":[{\"name\":\"net.neoforged:neoforge:20.4.253\"}]}");
        var audit = AuditOne("1.20.4-neoforge-20.4.253");
        Assert.Equal("neoforge", audit.Kind);
        Assert.Equal("1.20.4", audit.GameVersion);
        Assert.Equal("20.4.253", audit.LoaderVersion);
    }

    [Fact]
    public void BrokenVanillaJsonInfersGameVersionFromId()
    {
        // 目录存在但 version.json 丢了:不能让 GameVersion 为空,否则补全任务直接跳过
        Directory.CreateDirectory(Path.Combine(_versions, "26.3"));
        var audit = AuditOne("26.3");
        Assert.True(audit.NeedsRepair);
        Assert.Equal("vanilla", audit.Kind);
        Assert.Equal("26.3", audit.GameVersion);
        Assert.Null(audit.LoaderVersion);
    }

    [Fact]
    public void BrokenModpackJsonHasNoGuessableGameVersion()
    {
        Directory.CreateDirectory(Path.Combine(_versions, "my-custom-pack"));
        var audit = AuditOne("my-custom-pack");
        Assert.True(audit.NeedsRepair);
        Assert.Equal("", audit.GameVersion); // UI 会明确提示无法自动补全
    }

    [Fact]
    public void BrokenCanonicalLoaderIdInfersLoader()
    {
        Directory.CreateDirectory(Path.Combine(_versions, "1.20.1-forge-47.4.10"));
        var audit = AuditOne("1.20.1-forge-47.4.10");
        Assert.Equal("forge", audit.Kind);
        Assert.Equal("1.20.1", audit.GameVersion);
        Assert.Equal("47.4.10", audit.LoaderVersion);
    }
}
