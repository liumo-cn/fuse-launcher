using System;
using System.IO;
using System.Linq;
using Fuse.Core.Versioning;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>版本继承链合并（R7 风险项）。</summary>
public class VersionInheritanceTests : IDisposable
{
    private readonly string _root;

    public VersionInheritanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcdl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "1.20.1"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vanilla-1.20.1.json"),
            Path.Combine(_root, "1.20.1", "1.20.1.json"));

        // Forge 风格子版本：继承 1.20.1，加库、换主类、追加 game 参数
        var child = new JObject
        {
            ["id"] = "forge-1.20.1",
            ["inheritsFrom"] = "1.20.1",
            ["releaseTime"] = "2023-09-10T00:00:00+00:00",
            ["type"] = "release",
            ["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher",
            ["arguments"] = new JObject
            {
                ["game"] = new JArray
                {
                    "--fml.forgeVersion", "47.2.0",
                    "--fml.mcVersion", "1.20.1",
                },
            },
            ["libraries"] = new JArray
            {
                new JObject
                {
                    ["name"] = "net.minecraftforge:forge:1.20.1-47.2.0",
                    ["downloads"] = new JObject
                    {
                        ["artifact"] = new JObject
                        {
                            ["path"] = "net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0.jar",
                            ["url"] = "https://example.com/forge.jar",
                            ["sha1"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                            ["size"] = 1,
                        },
                    },
                },
            },
        };
        Directory.CreateDirectory(Path.Combine(_root, "forge-1.20.1"));
        File.WriteAllText(Path.Combine(_root, "forge-1.20.1", "forge-1.20.1.json"), child.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Vanilla_LoadsDirectly()
    {
        var v = VersionJsonLoader.Load(_root, "1.20.1");
        Assert.Equal("net.minecraft.client.main.Main", v.MainClass);
        Assert.Equal(17, v.JavaVersion!.MajorVersion);
        Assert.Equal(88, v.Libraries.Count);
        Assert.Null(v.InheritsFrom);
        Assert.Equal("0c3ec587af28e5a785c0b4a7b8a30f9a8f78f838", v.ClientDownload!.Sha1);
    }

    [Fact]
    public void Child_MergesParentScalars()
    {
        var v = VersionJsonLoader.Load(_root, "forge-1.20.1");
        Assert.Null(v.InheritsFrom);
        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", v.MainClass);
        Assert.Equal(17, v.JavaVersion!.MajorVersion);           // 继承父版本
        Assert.Equal("5", v.AssetIndex!.Id);                     // 继承父版本
        Assert.Equal(88 + 1, v.Libraries.Count);                 // 子在前父在后
        Assert.Equal("net.minecraftforge:forge:1.20.1-47.2.0", v.Libraries[0].Name);
    }

    [Fact]
    public void Child_GameArgs_ConcatAfterParent()
    {
        var v = VersionJsonLoader.Load(_root, "forge-1.20.1");
        var game = v.Arguments!.Game;
        // 子版本参数在前
        Assert.Equal("--fml.forgeVersion", game[0].Values[0]);
        // 父版本 --username 仍在
        Assert.Contains("--username", game.Select(a => a.Values.FirstOrDefault()));
    }

    [Fact]
    public void LegacyMinecraftArguments_InheritedWhenChildAbsent()
    {
        // 旧版布局：父带 minecraftArguments，子不带 → 继承
        var parent = new JObject
        {
            ["id"] = "old-parent",
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["minecraftArguments"] = "--username ${auth_player_name} --version ${version_name}",
            ["libraries"] = new JArray(),
        };
        var child = new JObject
        {
            ["id"] = "old-child",
            ["inheritsFrom"] = "old-parent",
            ["mainClass"] = "net.minecraftforge.fml.common.launcher.FMLTweaker",
        };
        Directory.CreateDirectory(Path.Combine(_root, "old-parent"));
        File.WriteAllText(Path.Combine(_root, "old-parent", "old-parent.json"), parent.ToString());
        Directory.CreateDirectory(Path.Combine(_root, "old-child"));
        File.WriteAllText(Path.Combine(_root, "old-child", "old-child.json"), child.ToString());

        var v = VersionJsonLoader.Load(_root, "old-child");
        Assert.Equal("--username ${auth_player_name} --version ${version_name}", v.MinecraftArguments);
        Assert.Equal("net.minecraftforge.fml.common.launcher.FMLTweaker", v.MainClass);
    }

    [Fact]
    public void Cycle_Detects()
    {
        var a = new JObject { ["id"] = "cycle-a", ["inheritsFrom"] = "cycle-b" };
        var b = new JObject { ["id"] = "cycle-b", ["inheritsFrom"] = "cycle-a" };
        Directory.CreateDirectory(Path.Combine(_root, "cycle-a"));
        File.WriteAllText(Path.Combine(_root, "cycle-a", "cycle-a.json"), a.ToString());
        Directory.CreateDirectory(Path.Combine(_root, "cycle-b"));
        File.WriteAllText(Path.Combine(_root, "cycle-b", "cycle-b.json"), b.ToString());

        Assert.Throws<InvalidOperationException>(() => VersionJsonLoader.Load(_root, "cycle-a"));
    }
}
