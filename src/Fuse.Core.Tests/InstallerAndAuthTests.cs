using System;
using System.IO;
using System.Linq;
using Fuse.Core.Auth;
using Fuse.Core.Download;
using Fuse.Core.Installer;
using Fuse.Core.Versioning;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>原版安装规划器：以真实官方 1.20.1 JSON 为输入（R7/FR-03 守护）。</summary>
public class VanillaInstallPlannerTests
{
    private static McVersion LoadVanilla()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcdl-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "1.20.1"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vanilla-1.20.1.json"),
            Path.Combine(root, "1.20.1", "1.20.1.json"));
        return VersionJsonLoader.Load(root, "1.20.1");
    }

    [Fact]
    public void ClientJar_UsesOfficialUrlAndSha1()
    {
        var item = VanillaInstallPlanner.ClientJar(LoadVanilla(), @"C:\mc\versions");
        Assert.Equal("https://piston-data.mojang.com/v1/objects/0c3ec587af28e5a785c0b4a7b8a30f9a8f78f838/client.jar", item.Url);
        Assert.EndsWith(@"versions\1.20.1\1.20.1.jar", item.DestPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("0c3ec587af28e5a785c0b4a7b8a30f9a8f78f838", item.ExpectedHash);
    }

    [Fact]
    public void LibraryFiles_WindowsRulesFiltered_NoDuplicates()
    {
        var items = VanillaInstallPlanner.LibraryFiles(
            LoadVanilla(), @"C:\mc\libraries", RuleEnvironment.WindowsX64);
        // 1.20.1 共 88 个库条目；windows 规则过滤 + 路径去重后 64 个（其余是 linux/macos natives 变体与 osx 门控库）
        Assert.Equal(64, items.Count);
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.ExpectedHash)));
        Assert.Equal(items.Count, items.GroupBy(i => i.DestPath, StringComparer.OrdinalIgnoreCase).Count());
        // natives classifier 库在清单内
        Assert.Contains(items, i => i.DestPath.Contains("lwjgl-glfw-3.3.1-natives-windows.jar"));
        // osx 专属库被过滤
        Assert.DoesNotContain(items, i => i.DestPath.Contains("java-objc-bridge"));
    }

    [Fact]
    public void AssetObjects_UsesOfficialCdnLayout()
    {
        // 最小化 assets 索引夹具
        var dir = Path.Combine(Path.GetTempPath(), "mcdl-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var indexPath = Path.Combine(dir, "5.json");
        File.WriteAllText(indexPath, new JObject
        {
            ["objects"] = new JObject
            {
                ["minecraft/sounds/test.ogg"] = new JObject { ["hash"] = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890", ["size"] = 100 },
                ["dup.json"] = new JObject { ["hash"] = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890", ["size"] = 100 },
            },
        }.ToString());
        try
        {
            var items = VanillaInstallPlanner.AssetObjects(indexPath, @"C:\mc\assets");
            Assert.Single(items); // 同哈希去重
            Assert.Equal(@"C:\mc\assets\objects\ab\abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
                items[0].DestPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal("https://resources.download.minecraft.net/ab/abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
                items[0].Url);
            Assert.Equal(items[0].ExpectedHash, items[0].Url.Split('/')[^1]); // 哈希互为校验
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LogConfig_LandsInInstanceDirectory()
    {
        var item = VanillaInstallPlanner.LogConfig(LoadVanilla(), @"C:\mc\instances\1.20.1");
        Assert.NotNull(item);
        Assert.Equal(@"C:\mc\instances\1.20.1\log4jConfig.xml", item!.DestPath);
        Assert.Equal("bd65e7d2e3c237be76cfbef4c2405033d7f91521", item.ExpectedHash);
    }
}

public class AccountStoreTests : IDisposable
{
    private readonly string _path;

    public AccountStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "mcdl-acc-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public void RefreshToken_EncryptedAtRest()
    {
        var store = new AccountStore(_path);
        store.Load();
        store.SetMicrosoft("Steve", "5627dd98-e6be-3c21-b8a8-e92344183641", "super-secret-refresh-token");
        store.Save();

        // 落盘内容不得包含明文令牌
        var raw = File.ReadAllText(_path);
        Assert.DoesNotContain("super-secret-refresh-token", raw);
        Assert.Contains("MsRefreshTokenEncrypted", raw);

        // 重开后可解出
        var reopened = new AccountStore(_path);
        reopened.Load();
        Assert.Equal("super-secret-refresh-token", reopened.GetMicrosoftRefreshToken());
        Assert.Equal("Steve", reopened.Data.MsPlayerName);
        Assert.Equal("msa", reopened.Data.ActiveType);
    }

    [Fact]
    public void SignOut_ClearsToken()
    {
        var store = new AccountStore(_path);
        store.Load();
        store.SetMicrosoft("Steve", "5627dd98-e6be-3c21-b8a8-e92344183641", "tok");
        store.SignOutMicrosoft();
        Assert.Null(store.GetMicrosoftRefreshToken());
        Assert.Equal("offline", store.Data.ActiveType);
    }

    [Fact]
    public void CorruptStore_BootsAsOffline()
    {
        File.WriteAllText(_path, "broken!!");
        var store = new AccountStore(_path);
        store.Load();
        Assert.Equal("offline", store.Data.ActiveType);
        Assert.Null(store.GetMicrosoftRefreshToken());
    }
}
