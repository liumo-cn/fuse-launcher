using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Fuse.Core.Download;
using Fuse.Core.Sources;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>地图安装:定位 level.dat、重名加后缀、无 level.dat 报错、zip-slip 仍被挡、安装记录落盘。</summary>
public class MapInstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _saves;
    private readonly string _instance;

    public MapInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mcdl-map-" + Guid.NewGuid().ToString("N"));
        _saves = Path.Combine(_dir, "saves");
        _instance = Path.Combine(_dir, "instances", "demo");
        Directory.CreateDirectory(_saves);
        Directory.CreateDirectory(_instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string MakeZip(params string[] entryNames)
    {
        var zip = Path.Combine(_dir, "map-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var name in entryNames)
        {
            using var w = new StreamWriter(archive.CreateEntry(name).Open());
            w.Write(name.EndsWith("level.dat", StringComparison.Ordinal) ? "fake-level" : "fake");
        }
        return zip;
    }

    private string Install(string zip, string fallbackName = "fallback map.zip") =>
        MapInstaller.Install(zip, _saves, _instance, "curseforge", "12345",
            "abc", fallbackName, null, CancellationToken.None);

    [Fact]
    public void NestedWorld_InstallsUnderFolderName()
    {
        var zip = MakeZip("Cool Map/level.dat", "Cool Map/region/r.0.0.mca");

        var name = Install(zip);

        Assert.Equal("Cool Map", name);
        Assert.True(Directory.Exists(Path.Combine(_saves, "Cool Map")));
        Assert.True(File.Exists(Path.Combine(_saves, "Cool Map", "level.dat")));
        // 解压临时目录清理干净:saves 下只剩世界本身
        Assert.Equal(
            new[] { "Cool Map" },
            Directory.GetDirectories(_saves).Select(d => Path.GetFileName(d)!).ToArray());
    }

    [Fact]
    public void RootWorld_UsesFallbackName()
    {
        var zip = MakeZip("level.dat", "region/r.0.0.mca");

        var name = Install(zip, "My Map v1.2.zip");

        Assert.Equal("My Map v1.2", name);
        Assert.True(File.Exists(Path.Combine(_saves, "My Map v1.2", "level.dat")));
    }

    [Fact]
    public void DeepNested_FindsWorldWithinThreeLevels()
    {
        var zip = MakeZip("Pack v2/saves/Demo/level.dat", "Pack v2/saves/Demo/region/r.0.0.mca");

        var name = Install(zip);

        Assert.Equal("Demo", name);
        Assert.True(Directory.Exists(Path.Combine(_saves, "Demo")));
    }

    [Fact]
    public void SameNameTwice_GetsNumberedSuffix()
    {
        var zip = MakeZip("Cool Map/level.dat", "Cool Map/region/r.0.0.mca");

        var first = Install(zip);
        var second = Install(zip);

        Assert.Equal("Cool Map", first);
        Assert.Equal("Cool Map (2)", second);
        Assert.True(Directory.Exists(Path.Combine(_saves, "Cool Map (2)")));
    }

    [Fact]
    public void NoLevelDat_FailsAndLeavesSavesClean()
    {
        var zip = MakeZip("readme.txt", "screenshots/pic.png");

        var ex = Assert.Throws<InvalidOperationException>(() => Install(zip));
        Assert.Contains("level.dat", ex.Message);
        Assert.Empty(Directory.GetDirectories(_saves));
    }

    [Fact]
    public void ZipSlipEntry_StillRejected()
    {
        var zip = MakeZip("Cool Map/level.dat", "../evil.txt");

        Assert.Throws<IOException>(() => Install(zip));
        Assert.Empty(Directory.GetDirectories(_saves));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact]
    public void Install_WritesContentRecordWithMapKind()
    {
        var zip = MakeZip("Cool Map/level.dat");

        Install(zip);

        Assert.True(ContentRecordStore.IsInstalled(_instance, "map", "12345", _saves));
        var doc = Path.Combine(_instance, "content.mcdl.json");
        Assert.True(File.Exists(doc));
        Assert.Contains("\"map\"", File.ReadAllText(doc));
    }

    [Fact]
    public void SanitizeName_StripsInvalidCharacters()
    {
        Assert.Equal("A B", MapInstaller.SanitizeName("A<B>"));
        Assert.Equal("地图", MapInstaller.SanitizeName("  "));
        Assert.Equal(60, MapInstaller.SanitizeName(new string('长', 80)).Length);
    }
}
