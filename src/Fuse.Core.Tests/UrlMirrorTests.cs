using System;
using Fuse.Core.Download;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>选源分组和固定回退顺序:测速结果不可用时行为必须和旧版一致。</summary>
public class UrlMirrorTests
{
    [Theory]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", UrlMirror.SourceCategory.Meta)]
    [InlineData("https://launchermeta.mojang.com/v1/packages/x/1.20.1.json", UrlMirror.SourceCategory.Meta)]
    [InlineData("https://meta.fabricmc.net/v2/versions/loader/1.20.1", UrlMirror.SourceCategory.Meta)]
    [InlineData("https://piston-data.mojang.com/v1/objects/aa/client.jar", UrlMirror.SourceCategory.Objects)]
    [InlineData("https://launcher.mojang.com/v1/objects/aa/client.jar", UrlMirror.SourceCategory.Objects)]
    [InlineData("https://libraries.minecraft.net/com/google/guava/guava/31.1-jre/guava-31.1-jre.jar", UrlMirror.SourceCategory.Maven)]
    [InlineData("https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar", UrlMirror.SourceCategory.Maven)]
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/20.4.237/neoforge-20.4.237-installer.jar", UrlMirror.SourceCategory.Maven)]
    [InlineData("https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.15.11/fabric-loader-0.15.11.jar", UrlMirror.SourceCategory.Maven)]
    [InlineData("https://resources.download.minecraft.net/86/868c7a7c4d9358d631fa2f9abfc6bbf05d88292d", UrlMirror.SourceCategory.Assets)]
    [InlineData("https://api.modrinth.com/v2/project/sodium", UrlMirror.SourceCategory.Other)]
    public void CategoryOf_MapsKnownHosts(string url, UrlMirror.SourceCategory expected)
    {
        Assert.Equal(expected, UrlMirror.CategoryOf(url));
    }

    [Fact]
    public void Candidates_Libraries_OfficialFirst()
    {
        const string url = "https://libraries.minecraft.net/com/google/guava/guava/31.1-jre/guava-31.1-jre.jar";
        var list = UrlMirror.Candidates(url);

        Assert.Equal(url, list[0]);
        Assert.Contains("https://mirrors.ustc.edu.cn/bmclapi/com/google/guava/guava/31.1-jre/guava-31.1-jre.jar", list);
        Assert.Contains("https://bmclapi2.bangbang93.com/maven/com/google/guava/guava/31.1-jre/guava-31.1-jre.jar", list);
        Assert.Contains("https://mirrors4.qlu.edu.cn/bmclapi/com/google/guava/guava/31.1-jre/guava-31.1-jre.jar", list);
    }

    [Fact]
    public void Candidates_LoaderMaven_UstcFirst()
    {
        const string url = "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar";
        var list = UrlMirror.Candidates(url);

        Assert.StartsWith("https://mirrors.ustc.edu.cn/bmclapi/", list[0]);
        Assert.StartsWith("https://bmclapi2.bangbang93.com/maven/", list[1]);
        Assert.StartsWith("https://mirrors4.qlu.edu.cn/bmclapi/", list[2]);
        Assert.Equal(url, list[3]);
    }

    [Fact]
    public void Candidates_MetaAndObjects_BmclFirst()
    {
        const string meta = "https://piston-meta.mojang.com/v1/packages/aa/1.20.1.json";
        Assert.Equal("https://bmclapi2.bangbang93.com/v1/packages/aa/1.20.1.json", UrlMirror.Candidates(meta)[0]);
        Assert.Equal(meta, UrlMirror.Candidates(meta)[1]);

        const string obj = "https://piston-data.mojang.com/v1/objects/aa/client.jar";
        Assert.Equal("https://bmclapi2.bangbang93.com/v1/objects/aa/client.jar", UrlMirror.Candidates(obj)[0]);
        Assert.Equal(obj, UrlMirror.Candidates(obj)[1]);
    }

    [Fact]
    public void Candidates_Assets_OfficialFirst()
    {
        const string asset = "https://resources.download.minecraft.net/86/868c7a7c4d9358d631fa2f9abfc6bbf05d88292d";
        var list = UrlMirror.Candidates(asset);

        Assert.Equal(asset, list[0]);
        Assert.Equal("https://bmclapi2.bangbang93.com/assets/86/868c7a7c4d9358d631fa2f9abfc6bbf05d88292d", list[1]);
    }

    [Fact]
    public void Candidates_Other_OnlyOriginal()
    {
        const string url = "https://api.modrinth.com/v2/project/sodium";
        var list = UrlMirror.Candidates(url);

        Assert.Single(list);
        Assert.Equal(url, list[0]);
    }

    [Fact]
    public void ForAttempt_FallbackOrderMatchesOldBehavior()
    {
        const string lib = "https://libraries.minecraft.net/com/google/guava/guava/31.1-jre/guava-31.1-jre.jar";
        Assert.Equal(lib, UrlMirror.ForAttempt(lib, 0));
        Assert.StartsWith("https://mirrors.ustc.edu.cn/bmclapi/", UrlMirror.ForAttempt(lib, 1));
        Assert.StartsWith("https://bmclapi2.bangbang93.com/maven/", UrlMirror.ForAttempt(lib, 2));
        Assert.StartsWith("https://mirrors4.qlu.edu.cn/bmclapi/", UrlMirror.ForAttempt(lib, 3));

        const string forge = "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar";
        Assert.StartsWith("https://mirrors.ustc.edu.cn/bmclapi/", UrlMirror.ForAttempt(forge, 0));
        Assert.StartsWith("https://bmclapi2.bangbang93.com/maven/", UrlMirror.ForAttempt(forge, 1));
        Assert.StartsWith("https://mirrors4.qlu.edu.cn/bmclapi/", UrlMirror.ForAttempt(forge, 2));
        Assert.Equal(forge, UrlMirror.ForAttempt(forge, 3));

        const string meta = "https://piston-meta.mojang.com/v1/packages/aa/1.20.1.json";
        Assert.StartsWith("https://bmclapi2.bangbang93.com/", UrlMirror.ForAttempt(meta, 0));
        Assert.Equal(meta, UrlMirror.ForAttempt(meta, 1));

        const string asset = "https://resources.download.minecraft.net/86/868c7a7c4d9358d631fa2f9abfc6bbf05d88292d";
        Assert.Equal(asset, UrlMirror.ForAttempt(asset, 0));
        Assert.StartsWith("https://bmclapi2.bangbang93.com/assets/", UrlMirror.ForAttempt(asset, 1));

        const string other = "https://api.modrinth.com/v2/project/sodium";
        Assert.Equal(other, UrlMirror.ForAttempt(other, 0));
        Assert.Equal(other, UrlMirror.ForAttempt(other, 9));
    }

    [Fact]
    public void Order_WithoutProbe_ReturnsOriginalList()
    {
        var sources = new[] { "https://a.example", "https://b.example" };
        var ordered = MirrorProbe.Order("no-such-group-" + Guid.NewGuid().ToString("N"), sources);
        Assert.Equal(sources, ordered);
    }
}
