using System;
using System.IO;
using System.IO.Compression;
using Fuse.Core.Installer;
using Xunit;

namespace Fuse.Core.Tests;

public class SharedInstanceMigratorTests : IDisposable
{
    private readonly string _root;

    public SharedInstanceMigratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-isolate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        Directory.CreateDirectory(Path.Combine(_root, "saves", "world"));
        File.WriteAllText(Path.Combine(_root, "saves", "world", "level.dat"), "level");
        Directory.CreateDirectory(Path.Combine(_root, "versions", "1.20.1"));
        File.WriteAllText(Path.Combine(_root, "versions", "1.20.1", "1.20.1.json"), "{\"id\":\"1.20.1\"}");
        Directory.CreateDirectory(Path.Combine(_root, "versions", "1.21.1"));
        File.WriteAllText(Path.Combine(_root, "versions", "1.21.1", "1.21.1.json"), "{\"id\":\"1.21.1\"}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void AddForgeJar(string name, string mcRange)
    {
        var id = Path.GetFileNameWithoutExtension(name);
        using var zip = ZipFile.Open(Path.Combine(_root, "mods", name), ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("META-INF/mods.toml").Open());
        writer.Write("modLoader=\"javafml\"\nloaderVersion=\"[1,)\"\n" +
                     "[[mods]]\nmodId=\"" + id + "\"\nversion=\"1.0.0\"\n" +
                     "[[dependencies." + id + "]]\nmodId=\"minecraft\"\nmandatory=true\nversionRange=\"" +
                     mcRange + "\"\n");
    }

    private void AddFabricJar(string name, string mcRange)
    {
        using var zip = ZipFile.Open(Path.Combine(_root, "mods", name), ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
        writer.Write("{\"id\":\"" + Path.GetFileNameWithoutExtension(name) +
                     "\",\"version\":\"1.0.0\",\"depends\":{\"minecraft\":\"" + mcRange + "\"}}");
    }

    [Fact]
    public void EnableMovesOnlyCurrentVersionModsAndCopiesSaves()
    {
        AddForgeJar("jei.jar", "[26.2,26.3)");
        AddForgeJar("shared.jar", "[26.2,26.3),[1.20.1,1.20.2)");
        AddFabricJar("fabric-only.jar", "[1.21.1,1.21.2)");

        var result = SharedInstanceMigrator.Enable(
            _root, "26.2", "26.2", "neoforge", new[] { "1.20.1", "1.21.1" });

        var versionMods = Path.Combine(_root, "versions", "26.2", "mods");
        Assert.True(File.Exists(Path.Combine(versionMods, "jei.jar")));
        Assert.True(File.Exists(Path.Combine(versionMods, "shared.jar")));
        Assert.False(File.Exists(Path.Combine(versionMods, "fabric-only.jar")));

        // 只支持 26.2 的移走,同时支持 1.20.1 的留在共用目录
        Assert.False(File.Exists(Path.Combine(_root, "mods", "jei.jar")));
        Assert.True(File.Exists(Path.Combine(_root, "mods", "shared.jar")));
        Assert.True(File.Exists(Path.Combine(_root, "mods", "fabric-only.jar")));

        Assert.True(File.Exists(Path.Combine(_root, "versions", "26.2", "saves", "world", "level.dat")));
        Assert.Contains("jei.jar", result.MovedMods);
    }
}
