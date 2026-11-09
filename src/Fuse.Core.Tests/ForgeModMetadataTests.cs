using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Fuse.Core.ModConflict;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>Forge / NeoForge 的 mods.toml 与 1.12.2 的 mcmod.info 解析。</summary>
public class ForgeModMetadataTests : IDisposable
{
    private readonly string _root;
    private readonly string _mods;

    public ForgeModMetadataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-forgemeta-" + Guid.NewGuid().ToString("N"));
        _mods = Path.Combine(_root, "mods");
        Directory.CreateDirectory(_mods);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void AddJar(string fileName, Action<ZipArchive> build)
    {
        using var zip = ZipFile.Open(Path.Combine(_mods, fileName), ZipArchiveMode.Create);
        build(zip);
    }

    private static void Write(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(text);
    }

    private static void WriteModsToml(ZipArchive zip, string path, string body) =>
        Write(zip, path,
            "modLoader=\"javafml\"" + Environment.NewLine +
            "loaderVersion=\"[1,)\"" + Environment.NewLine + body);

    [Fact]
    public void ReadsForgeModsTomlAndRequiredDependencies()
    {
        AddJar("example-1.2.3.jar", zip =>
        {
            Write(zip, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0" + Environment.NewLine +
                                               "Implementation-Version: 1.2.3" + Environment.NewLine);
            WriteModsToml(zip, "META-INF/mods.toml", @"
[[mods]]
modId=""examplemod""
version=""${file.jarVersion}""
displayName=""Example Mod""

[[dependencies.examplemod]]
modId=""forge""
mandatory=true
versionRange=""[14.23.5.2860,)""
side=""BOTH""

[[dependencies.examplemod]]
modId=""minecraft""
mandatory=true
versionRange=""[1.12.2,1.13)""
side=""BOTH""

[[dependencies.examplemod]]
modId=""optional-lib""
mandatory=false
versionRange=""[1.0,)""
side=""BOTH""
");
        });

        var mod = ModScanner.Read(Path.Combine(_mods, "example-1.2.3.jar"));
        Assert.NotNull(mod);
        Assert.Equal("examplemod", mod!.Id);
        Assert.Equal("1.2.3", mod.Version);
        Assert.Equal("Example Mod", mod.Name);
        Assert.True(mod.Depends.ContainsKey("forge"));
        Assert.Equal("[1.12.2,1.13)", mod.Depends["minecraft"]);
        Assert.False(mod.Depends.ContainsKey("optional-lib"));
    }

    [Fact]
    public void ReadsNeoForgeTypeRequiredAndSkipsServerSide()
    {
        AddJar("neomod.jar", zip => WriteModsToml(zip, "META-INF/neoforge.mods.toml", @"
[[mods]]
modId=""neomod""
version=""2.0.0""
displayName=""Neo Mod""

[[dependencies.neomod]]
modId=""neoforge""
type=""required""
versionRange=""[21.1,)""
side=""BOTH""

[[dependencies.neomod]]
modId=""optional-lib""
type=""optional""
versionRange=""[1.0,)""
side=""BOTH""

[[dependencies.neomod]]
modId=""server-lib""
type=""required""
side=""SERVER""
"));

        var mod = ModScanner.Read(Path.Combine(_mods, "neomod.jar"));
        Assert.NotNull(mod);
        Assert.Equal("neomod", mod!.Id);
        Assert.Equal("2.0.0", mod.Version);
        Assert.True(mod.Depends.ContainsKey("neoforge"));
        Assert.False(mod.Depends.ContainsKey("optional-lib"));
        Assert.False(mod.Depends.ContainsKey("server-lib"));
    }

    [Fact]
    public void ReadsLegacyMcmodInfo()
    {
        AddJar("oldmod.jar", zip => Write(zip, "mcmod.info",
            "[{\"modid\":\"oldmod\",\"name\":\"Old Mod\",\"version\":\"1.0\"," +
            "\"mcversion\":\"1.12.2\"," +
            "\"dependencies\":[\"codechickenlib\"]," +
            "\"requiredMods\":[\"forge@[14.23.5.2860,)\"]}]"));

        var mod = ModScanner.Read(Path.Combine(_mods, "oldmod.jar"));
        Assert.NotNull(mod);
        Assert.Equal("oldmod", mod!.Id);
        Assert.Equal("Old Mod", mod.Name);
        Assert.True(mod.Depends.ContainsKey("codechickenlib"));
        Assert.Equal("[14.23.5.2860,)", mod.Depends["forge"]);
    }

    [Fact]
    public void Analyzer_ForgeReportsMissingRequiredDependencyOnly()
    {
        AddJar("moda.jar", zip => WriteModsToml(zip, "META-INF/mods.toml", @"
[[mods]]
modId=""moda""
version=""1.0.0""
displayName=""Mod A""

[[dependencies.moda]]
modId=""libx""
mandatory=true
versionRange=""[1.0,2.0)""

[[dependencies.moda]]
modId=""forge""
mandatory=true
versionRange=""[14,)""

[[dependencies.moda]]
modId=""minecraft""
mandatory=true
versionRange=""[1.12.2,1.13)""
"));

        var report = ModConflictAnalyzer.Analyze(_mods, "1.12.2", "forge");
        var install = Assert.Single(report.Actions, a => a.Kind == ModFixKind.InstallDependency);
        Assert.Equal("libx", install.DependencyId);
        Assert.DoesNotContain(report.Actions, a => a.ModId.Equals("forge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyzer_ForgeDisablesDuplicateSameModId()
    {
        AddJar("lib-1.jar", zip => WriteModsToml(zip, "META-INF/mods.toml", @"
[[mods]]
modId=""libx""
version=""1.0.0""
displayName=""Lib X""
"));
        AddJar("lib-2.jar", zip => WriteModsToml(zip, "META-INF/mods.toml", @"
[[mods]]
modId=""libx""
version=""2.0.0""
displayName=""Lib X""
"));

        var report = ModConflictAnalyzer.Analyze(_mods, "1.12.2", "forge");
        var dup = Assert.Single(report.Actions, a => a.Kind == ModFixKind.DisableDuplicate);
        Assert.Equal("lib-1.jar", dup.FileName);
    }

    [Fact]
    public void Analyzer_ForgeMinecraftMismatchDoesNotAutoDisable()
    {
        AddJar("moda.jar", zip => WriteModsToml(zip, "META-INF/mods.toml", @"
[[mods]]
modId=""moda""
version=""1.0.0""
displayName=""Mod A""

[[dependencies.moda]]
modId=""minecraft""
mandatory=true
versionRange=""[1.12.2,1.13)""
"));

        var report = ModConflictAnalyzer.Analyze(_mods, "1.20.1", "forge");
        var action = Assert.Single(report.Actions, a => a.Kind == ModFixKind.ReplaceVersion);
        Assert.Equal("Mod A", action.ModName);
        Assert.False(action.DisableIfNoReplacement);
    }

    [Fact]
    public void Analyzer_ForgeJarInJarCountsAsProvided()
    {
        AddJar("outer.jar", zip =>
        {
            WriteModsToml(zip, "META-INF/mods.toml", @"
[[mods]]
modId=""outer""
version=""1.0.0""
displayName=""Outer""

[[dependencies.outer]]
modId=""innerlib""
mandatory=true
versionRange=""[1.0,)""
");
            using var ms = new MemoryStream();
            using (var nested = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                WriteModsToml(nested, "META-INF/mods.toml", @"
[[mods]]
modId=""innerlib""
version=""1.0.0""
displayName=""Inner""
");
            }
            ms.Position = 0;
            using var entry = zip.CreateEntry("META-INF/jarjar/innerlib.jar").Open();
            ms.CopyTo(entry);
        });

        var report = ModConflictAnalyzer.Analyze(_mods, "1.12.2", "forge");
        Assert.DoesNotContain(report.Actions, a => a.Kind == ModFixKind.InstallDependency);
    }

    [Fact]
    public void ReadsBareForgeVersionAsMinimum()
    {
        AddJar("cc.jar", zip => WriteModsToml(zip, "META-INF/mods.toml", @"
[[mods]]
modId=""example""
version=""1.0.0""

[[dependencies.example]]
modId=""creativecore""
mandatory=true
versionRange=""2.11.20""
"));

        var mod = ModScanner.Read(Path.Combine(_mods, "cc.jar"), "forge");
        Assert.Equal(">=2.11.20", mod!.Depends["creativecore"]);
    }

    [Fact]
    public void PrefersLoaderMetadataInMergedJar()
    {
        AddJar("merged.jar", zip =>
        {
            Write(zip, "fabric.mod.json",
                "{\"id\":\"fabricid\",\"version\":\"1.0.0\",\"depends\":{\"fabric-api\":\"*\"}}");
            WriteModsToml(zip, "META-INF/mods.toml", @"
[[mods]]
modId=""forgeid""
version=""1.0.0""
");
        });

        Assert.Equal("forgeid", ModScanner.Read(Path.Combine(_mods, "merged.jar"), "forge")!.Id);
        Assert.Equal("fabricid", ModScanner.Read(Path.Combine(_mods, "merged.jar"), "fabric")!.Id);
    }

    [Fact]
    public void ReadsBundleJarWithOnlyNestedMods()
    {
        AddJar("kotlinforforge-all.jar", zip =>
        {
            Write(zip, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0" + Environment.NewLine +
                                               "Implementation-Version: 4.12.0" + Environment.NewLine);
            using var ms = new MemoryStream();
            using (var nested = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                WriteModsToml(nested, "META-INF/mods.toml", @"
[[mods]]
modId=""kotlinforforge""
version=""4.12.0""
displayName=""Kotlin For Forge""
");
            }
            ms.Position = 0;
            using var entry = zip.CreateEntry("META-INF/jarjar/kffmod-4.12.0.jar").Open();
            ms.CopyTo(entry);
        });

        var mod = ModScanner.Read(Path.Combine(_mods, "kotlinforforge-all.jar"), "forge");
        Assert.NotNull(mod);
        Assert.Equal("kotlinforforge", mod!.Id);
        Assert.Equal("4.12.0", mod.Version);
    }

    [Fact]
    public void IgnoresWrongLoaderJar()
    {
        AddJar("fabriconly.jar", zip =>
            Write(zip, "fabric.mod.json", "{\"id\":\"fabricid\",\"version\":\"1.0.0\"}"));

        Assert.Null(ModScanner.Read(Path.Combine(_mods, "fabriconly.jar"), "forge"));
        Assert.NotNull(ModScanner.Read(Path.Combine(_mods, "fabriconly.jar"), "fabric"));
    }

    [Theory]
    [InlineData("1.20.1", "1.18 1.18.1 1.20.1", true)]
    [InlineData("1.19.4", "1.18 1.18.1 1.20.1", false)]
    [InlineData("1.12.2", "[1.12.2,1.13)", true)]
    [InlineData("1.13", "[1.12.2,1.13)", false)]
    [InlineData("1.12.2", "(,1.12.2]", true)]
    [InlineData("1.12.3", "(,1.12.2]", false)]
    [InlineData("1.20.1", "[1.20,)", true)]
    [InlineData("1.19.4", "[1.20,)", false)]
    [InlineData("1.20.1", "[1.12.2]", false)]
    [InlineData("1.20.1", "[1.20.1]", true)]
    [InlineData("1.19.2", "[1.18,1.19),[1.19.2,)", true)]
    public void VersionRange_MatchesMavenIntervals(string version, string range, bool expected)
    {
        Assert.Equal(expected, VersionRange.Matches(version, range));
    }
}
