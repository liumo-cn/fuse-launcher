using System;
using System.IO;
using System.Linq;
using Fuse.Core.Installer;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 皮肤模块:CSL 恢复、站点源排序与失效条目清理。
/// 回归点:皮肤站账号进离线服没皮肤——旧实现把 CSL 停用了,只剩 authlib-injector。
/// </summary>
public class LocalSkinSetupTests : IDisposable
{
    private readonly string _instance;

    public LocalSkinSetupTests()
    {
        _instance = Path.Combine(Path.GetTempPath(), "mcdl-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_instance, "mods"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_instance, true); } catch (IOException) { }
    }

    private string MakeModsJar(string name)
    {
        var p = Path.Combine(_instance, "mods", name);
        File.WriteAllText(p, "jar:" + name);
        return p;
    }

    /// <summary>模拟旧版本留下的停用状态(jar 在 disabled-mods)。</summary>
    private void StashCsl(string name)
    {
        var stash = Path.Combine(_instance, "disabled-mods");
        Directory.CreateDirectory(stash);
        File.Move(Path.Combine(_instance, "mods", name), Path.Combine(stash, name));
    }

    private static void WriteConfig(string instanceDir, string loadlistJson)
    {
        var dir = Path.Combine(instanceDir, "CustomSkinLoader");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CustomSkinLoader.json"),
            "{\"version\":\"15.1\",\"buildNumber\":41,\"loadlist\": " + loadlistJson + "}");
    }

    private JObject ReadConfig()
        => JObject.Parse(File.ReadAllText(
            Path.Combine(_instance, "CustomSkinLoader", "CustomSkinLoader.json")));

    private string[] EntryTypes()
        => ReadConfig()["loadlist"]!.OfType<JObject>()
            .Select(o => o.Value<string>("type") ?? "").ToArray();

    private static string DefaultList(string littleSkinRoot = "https://littleskin.cn/csl/")
        => """
           [
             {"name":"GameProfile","type":"GameProfile"},
             {"name":"Mojang","type":"MojangAPI","apiRoot":"https://api.mojang.com/"},
             {"name":"LittleSkin","type":"CustomSkinAPI","root":"%ROOT%"},
             {"name":"LocalSkin","type":"Legacy","skin":"LocalSkin/skins/{USERNAME}.png"}
           ]
           """.Replace("%ROOT%", littleSkinRoot);

    // ---------- 站点源排序 ----------

    [Fact]
    public void EnsureSiteSourceFirst_LittleSkin_GoesBeforeMojang()
    {
        WriteConfig(_instance, DefaultList());

        var applied = LocalSkinSetup.EnsureSiteSourceFirst(_instance, "https://littleskin.cn/api/yggdrasil");

        Assert.True(applied);
        var list = ReadConfig()["loadlist"]!.OfType<JObject>().ToList();
        // GameProfile 永远第一(服务端下发的 textures 优先)
        Assert.Equal("GameProfile", list[0].Value<string>("type"));
        Assert.Equal("LittleSkin", list[1].Value<string>("name"));
        Assert.Equal("Mojang", list[2].Value<string>("name"));
        // 旧位置那一条被删掉,不能出现重复源
        Assert.Single(list, o => o.Value<string>("name") == "LittleSkin");
    }

    [Fact]
    public void EnsureSiteSourceFirst_KeepsProfileCacheCleared()
    {
        WriteConfig(_instance, DefaultList());
        var cache = Path.Combine(_instance, "CustomSkinLoader", "ProfileCache");
        Directory.CreateDirectory(cache);

        LocalSkinSetup.EnsureSiteSourceFirst(_instance, "https://littleskin.cn/api/yggdrasil");

        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void EnsureSiteSourceFirst_CustomSite_UsesCslRoot()
    {
        WriteConfig(_instance, DefaultList());

        var applied = LocalSkinSetup.EnsureSiteSourceFirst(_instance, "https://skin.example.com/api/yggdrasil/");

        Assert.True(applied);
        var list = ReadConfig()["loadlist"]!.OfType<JObject>().ToList();
        Assert.Equal("skin.example.com", list[1].Value<string>("name"));
        Assert.Equal("CustomSkinAPI", list[1].Value<string>("type"));
        Assert.Equal("https://skin.example.com/csl/", list[1].Value<string>("root"));
        Assert.Equal("Mojang", list[2].Value<string>("name"));
    }

    [Fact]
    public void EnsureSiteSourceFirst_NoConfig_WritesExtraList()
    {
        var applied = LocalSkinSetup.EnsureSiteSourceFirst(_instance, "https://skin.example.com/api/yggdrasil");

        Assert.True(applied);
        var extra = Path.Combine(_instance, "CustomSkinLoader", "ExtraList", "FuseSkinSite.json");
        Assert.True(File.Exists(extra));
        var src = JObject.Parse(File.ReadAllText(extra));
        Assert.Equal("CustomSkinAPI", src.Value<string>("type"));
        Assert.Equal("https://skin.example.com/csl/", src.Value<string>("root"));
        // 没有配置时不去猜 CSL 的完整结构
        Assert.False(File.Exists(Path.Combine(_instance, "CustomSkinLoader", "CustomSkinLoader.json")));
    }

    [Fact]
    public void EnsureSiteSourceFirst_AlreadyFirst_NoDuplicate()
    {
        WriteConfig(_instance, """
            [
              {"name":"GameProfile","type":"GameProfile"},
              {"name":"LittleSkin","type":"CustomSkinAPI","root":"https://littleskin.cn/csl/"},
              {"name":"Mojang","type":"MojangAPI"}
            ]
            """);

        LocalSkinSetup.EnsureSiteSourceFirst(_instance, "https://littleskin.cn/api/yggdrasil");

        Assert.Single(EntryTypes(), t => t == "CustomSkinAPI");
        Assert.Equal(new[] { "GameProfile", "CustomSkinAPI", "MojangAPI" }, EntryTypes());
    }

    [Fact]
    public void EnsureSiteSourceFirst_NoApiRoot_NoOp()
    {
        WriteConfig(_instance, DefaultList());

        Assert.False(LocalSkinSetup.EnsureSiteSourceFirst(_instance, ""));
        Assert.False(LocalSkinSetup.EnsureSiteSourceFirst(_instance, null));
        Assert.Equal(new[] { "GameProfile", "MojangAPI", "CustomSkinAPI", "Legacy" }, EntryTypes());
    }

    [Fact]
    public void EnsureSiteSourceFirst_BrokenConfig_FallsBackToExtraList()
    {
        var dir = Path.Combine(_instance, "CustomSkinLoader");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CustomSkinLoader.json"), "{ not json");

        Assert.True(LocalSkinSetup.EnsureSiteSourceFirst(_instance, "https://littleskin.cn/api/yggdrasil"));
        Assert.True(File.Exists(Path.Combine(dir, "ExtraList", "FuseSkinSite.json")));
    }

    [Fact]
    public void DisableCslBootstrap_MovesCoreAside_AndIsIdempotent()
    {
        var core = Path.Combine(_instance, "CustomSkinLoader", "Core");
        Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(core, "CustomSkinLoader-Common.jar"), "jar");

        Assert.True(LocalSkinSetup.DisableCslBootstrap(_instance));
        Assert.False(Directory.Exists(core));
        Assert.True(Directory.Exists(Path.Combine(_instance, "CustomSkinLoader", "Core.disabled")));
        // 再跑一次没有 Core 可移,不算失败
        Assert.False(LocalSkinSetup.DisableCslBootstrap(_instance));
    }

    // ---------- Forge 上的 CSL 版本选择 ----------

    [Theory]
    [InlineData("CustomSkinLoader_Universal-15.1.jar", true)]
    [InlineData("CustomSkinLoader_Universal-15.0.1.jar", true)]
    [InlineData("CustomSkinLoader_Universal-16.0.jar", true)]
    [InlineData("CustomSkinLoader_Universal-14.28.jar", false)]
    [InlineData("CustomSkinLoader_ForgeV2-14.28.jar", false)]
    [InlineData("sodium-fabric.jar", false)]
    public void IsIncompatibleForgeCsl(string file, bool expected)
        => Assert.Equal(expected, LocalSkinSetup.IsIncompatibleForgeCsl(file));

    // ---------- 恢复 CSL / 清理条目 ----------

    [Fact]
    public void RestoreForExternal_MovesJarBack_AndCleansInvalidEntries()
    {
        var jar = MakeModsJar("CustomSkinLoader_Universal-15.1.jar");
        StashCsl("CustomSkinLoader_Universal-15.1.jar");
        WriteConfig(_instance,
            """[{"type":"MojangAPI"},{"type":"LocalSkin"},{"type":"Legacy","skin":"LocalSkin/skins/x.png"}]""");
        Directory.CreateDirectory(Path.Combine(_instance, "CustomSkinLoader", "ProfileCache"));

        LocalSkinSetup.RestoreForExternal(_instance);

        // jar 回到 mods,stash 清空
        Assert.True(File.Exists(jar));
        Assert.False(Directory.Exists(Path.Combine(_instance, "disabled-mods")));
        // 失效 LocalSkin 条目被就地清除,其余源(Mojang/Legacy)保留
        var types = EntryTypes();
        Assert.DoesNotContain("LocalSkin", types);
        Assert.Contains("MojangAPI", types);
        Assert.Contains("Legacy", types);
        // 皮肤缓存一并清理
        Assert.False(Directory.Exists(Path.Combine(_instance, "CustomSkinLoader", "ProfileCache")));
    }

    [Fact]
    public void RestoreForExternal_KeepsNormalConfig()
    {
        var jar = MakeModsJar("CustomSkinLoader_Universal-15.1.jar");
        StashCsl("CustomSkinLoader_Universal-15.1.jar");
        WriteConfig(_instance,
            """[{"type":"MojangAPI"},{"type":"Legacy","skin":"LocalSkin/skins/x.png"}]""");

        LocalSkinSetup.RestoreForExternal(_instance);

        // 配置没有失效条目:保留(CSL 按 Mojang → Legacy 顺序加载)
        Assert.True(File.Exists(Path.Combine(_instance, "CustomSkinLoader", "CustomSkinLoader.json")));
        Assert.True(File.Exists(jar));
    }

    [Fact]
    public void RestoreForExternal_EmptyStash_IsNoOp()
    {
        // 从未停用过:不应创建任何东西、不应报错
        LocalSkinSetup.RestoreForExternal(_instance);
        Assert.False(Directory.Exists(Path.Combine(_instance, "disabled-mods")));
    }
}
