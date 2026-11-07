using System;
using System.IO;
using Fuse.Core.Sources;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>内容安装记录:打勾状态必须由"记录 + 文件仍在磁盘"共同决定。</summary>
public sealed class ContentRecordStoreTests : IDisposable
{
    private readonly string _root;

    public ContentRecordStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        Directory.CreateDirectory(Path.Combine(_root, "shaderpacks"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Installed_only_when_file_exists()
    {
        var fileName = "apple.jar";
        var mods = Path.Combine(_root, "mods");
        File.WriteAllText(Path.Combine(mods, fileName), "x");

        ContentRecordStore.Record(_root, "modrinth", "P1", fileName, "mod", null);
        Assert.True(ContentRecordStore.IsInstalled(_root, "mod", "P1", mods));

        File.Delete(Path.Combine(mods, fileName));
        Assert.False(ContentRecordStore.IsInstalled(_root, "mod", "P1", mods));
    }

    [Fact]
    public void Disabled_mod_still_counts_as_present()
    {
        var fileName = "jei.jar";
        var mods = Path.Combine(_root, "mods");
        File.WriteAllText(Path.Combine(mods, fileName + ".disabled"), "x");

        ContentRecordStore.Record(_root, "modrinth", "P2", fileName, "mod", null);
        Assert.True(ContentRecordStore.IsInstalled(_root, "mod", "P2", mods));
    }

    [Fact]
    public void Kind_is_part_of_the_key()
    {
        var fileName = "pack.zip";
        var packs = Path.Combine(_root, "shaderpacks");
        File.WriteAllText(Path.Combine(packs, fileName), "x");

        ContentRecordStore.Record(_root, "modrinth", "P3", fileName, "shader", null);
        Assert.True(ContentRecordStore.IsInstalled(_root, "shader", "P3", packs));
        Assert.False(ContentRecordStore.IsInstalled(_root, "resourcepack", "P3", packs));
        Assert.False(ContentRecordStore.IsInstalled(_root, "mod", "P3", packs));
    }

    [Fact]
    public void Project_page_urls_are_well_formed()
    {
        Assert.Equal("https://modrinth.com/mod/sodium",
            ModrinthSource.ProjectPageUrl("sodium", "mod"));
        Assert.Equal("https://modrinth.com/shader/iris",
            ModrinthSource.ProjectPageUrl("iris", "shader"));
        Assert.Equal("https://modrinth.com/resourcepack/faithful",
            ModrinthSource.ProjectPageUrl("faithful", "resourcepack"));

        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/jei",
            CurseForgeSource.ProjectPageUrl("jei", "mod"));
        Assert.Equal("https://www.curseforge.com/minecraft/texture-packs/faithful",
            CurseForgeSource.ProjectPageUrl("faithful", "resourcepack"));
    }
}
