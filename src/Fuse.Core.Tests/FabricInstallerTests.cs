using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fuse.Core.Installer;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>Fabric 安装器规划(用户确认的命名规则:原版号-fabric-加载器号)。</summary>
public class FabricInstallerTests
{
    // 真实 meta v2 的 launcherMeta.libraries 只列间接依赖(asm/mixin 等),
    // 不含 fabric-loader 本体 —— 载荷必须由安装器自己补,否则 mainClass 找不到。
    private const string MetaJson = """
    {
      "loader": { "version": "0.16.14", "stable": true },
      "intermediary": { "version": "1.20.1", "url": "https://maven.fabricmc.net/net/fabricmc/intermediary/1.20.1/intermediary-1.20.1-v2.jar" },
      "launcherMeta": {
        "version": 1,
        "libraries": {
          "client": [
            { "name": "org.ow2.asm:asm:9.6", "url": "https://maven.fabricmc.net/" }
          ],
          "common": [
            { "name": "org.ow2.asm:asm:9.6", "url": "https://maven.fabricmc.net/" },
            { "name": "net.fabricmc:sponge-mixin:0.15.0+mixin.0.8.7", "url": "https://maven.fabricmc.net/" }
          ],
          "server": []
        },
        "mainClass": { "client": "net.fabricmc.loader.impl.launch.knot.KnotClient", "server": "net.fabricmc.loader.impl.launch.knot.KnotServer" }
      }
    }
    """;

    [Fact]
    public void BuildVersionJson_InheritsVanillaAndNamesCorrectly()
    {
        var vanilla = new JObject
        {
            ["id"] = "1.20.1",
            ["mainClass"] = "net.minecraft.client.main.Main",
        };
        var meta = JObject.Parse(MetaJson);
        var (json, libs) = FabricInstaller.BuildVersionJson(
            vanilla, "1.20.1", "0.16.14", meta, @"C:\mc\libraries");

        Assert.Equal("1.20.1-fabric-0.16.14", json["id"]!.ToString());
        Assert.Equal("1.20.1", json["inheritsFrom"]!.ToString());
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", json["mainClass"]!.ToString());

        // 库去重(client/common 各有一条相同 asm)
        var names = json["libraries"]!.Cast<JObject>().Select(l => l["name"]!.ToString()).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("net.fabricmc:intermediary:1.20.1", names);
        // meta 不给 loader 本体,安装器必须自己补上(否则 KnotClient 缺失,启动失败)
        Assert.Contains("net.fabricmc:fabric-loader:0.16.14", names);

        // 下载清单:Maven 坐标转路径
        Assert.Contains(libs, i => i.Url.StartsWith("https://maven.fabricmc.net/"));
        var loaderItem = libs.First(i => i.Url.Contains("fabric-loader"));
        Assert.Contains(@"net\fabricmc\fabric-loader\0.16.14\fabric-loader-0.16.14.jar", loaderItem.DestPath);
    }

    [Fact]
    public void MavenPath_ConvertsCoordinates()
    {
        Assert.Equal("net/fabricmc/fabric-loader/0.16.14/fabric-loader-0.16.14.jar",
            FabricInstaller.MavenPath("net.fabricmc:fabric-loader:0.16.14", ""));
        Assert.Equal("org/ow2/asm/asm-analysis/9.6/asm-analysis-9.6.jar",
            FabricInstaller.MavenPath("org.ow2.asm:asm-analysis:9.6", ""));
        Assert.Equal("net/fabricmc/intermediary/1.20.1/intermediary-1.20.1-v2.jar",
            FabricInstaller.MavenPath("net.fabricmc:intermediary:1.20.1:v2", ""));
    }
}
