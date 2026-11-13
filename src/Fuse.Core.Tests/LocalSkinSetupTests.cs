using System;
using System.IO;
using Fuse.Core.Installer;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>皮肤模块:CSL 停用/恢复与本地皮肤条目清理(正版进离线服没皮肤的根因回归)。</summary>
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

    private static void WriteConfig(string instanceDir, string loadlistJson)
    {
        var dir = Path.Combine(instanceDir, "CustomSkinLoader");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CustomSkinLoader.json"),
            "{\"loadlist\": " + loadlistJson + "}");
    }

    [Fact]
    public void Suspend_MovesOnlyCslJars_OthersStay()
    {
        var csl = MakeModsJar("CustomSkinLoader_Universal-15.1.jar");
        var other = MakeModsJar("sodium-fabric.jar");

        LocalSkinSetup.SuspendForExternal(_instance);

        Assert.False(File.Exists(csl));
        Assert.True(File.Exists(Path.Combine(_instance, "disabled-mods", "CustomSkinLoader_Universal-15.1.jar")));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void RestoreForExternal_MovesJarBack_AndCleansInvalidEntries()
    {
        var csl = MakeModsJar("CustomSkinLoader_Universal-15.1.jar");
        LocalSkinSetup.SuspendForExternal(_instance);
        WriteConfig(_instance,
            """[{"type":"Mojang"},{"type":"LocalSkin"},{"type":"Legacy","skin":"LocalSkin/skins/x.png"}]""");
        Directory.CreateDirectory(Path.Combine(_instance, "CustomSkinLoader", "ProfileCache"));

        LocalSkinSetup.RestoreForExternal(_instance);

        // jar 回到 mods,stash 清空
        Assert.True(File.Exists(csl));
        Assert.False(Directory.Exists(Path.Combine(_instance, "disabled-mods")));
        // 失效 LocalSkin 条目被就地清除,其余源(Mojang/Legacy)保留
        var cleaned = Newtonsoft.Json.Linq.JObject.Parse(
            File.ReadAllText(Path.Combine(_instance, "CustomSkinLoader", "CustomSkinLoader.json")));
        var types = cleaned["loadlist"]!.OfType<Newtonsoft.Json.Linq.JObject>()
            .Select(o => o.Value<string>("type")).ToList();
        Assert.DoesNotContain("LocalSkin", types);
        Assert.Contains("Mojang", types);
        Assert.Contains("Legacy", types);
        // 皮肤缓存一并清理
        Assert.False(Directory.Exists(Path.Combine(_instance, "CustomSkinLoader", "ProfileCache")));
    }

    [Fact]
    public void RestoreForExternal_KeepsNormalConfig()
    {
        var csl = MakeModsJar("CustomSkinLoader_Universal-15.1.jar");
        LocalSkinSetup.SuspendForExternal(_instance);
        WriteConfig(_instance,
            """[{"type":"Mojang"},{"type":"Legacy","skin":"LocalSkin/skins/x.png"}]""");

        LocalSkinSetup.RestoreForExternal(_instance);

        // 配置没有失效条目:保留(CSL 按 Mojang → Legacy 顺序加载)
        Assert.True(File.Exists(Path.Combine(_instance, "CustomSkinLoader", "CustomSkinLoader.json")));
        Assert.True(File.Exists(csl));
    }

    [Fact]
    public void RestoreForExternal_EmptyStash_IsNoOp()
    {
        // 从未停用过:不应创建任何东西、不应报错
        LocalSkinSetup.RestoreForExternal(_instance);
        Assert.False(Directory.Exists(Path.Combine(_instance, "disabled-mods")));
    }
}
