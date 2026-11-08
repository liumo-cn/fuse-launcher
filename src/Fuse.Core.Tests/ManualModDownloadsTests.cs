using System.IO;
using Fuse.Core.Download;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>失败原因弹窗要一键开网页,先从历史错误文本里把项目和文件 ID 抠出来。</summary>
public class ManualModDownloadsTests
{
    [Fact]
    public void ParseFromMessage_OldStyle()
    {
        const string message =
            @"整合包已安装到 D:\liumo\Documents\mc联机\.minecraft\versions\rlcraft-rlcraft1.12.2-releasev2.9.3.zip，"
            + "但以下必需模组作者禁止第三方 API 下载，请手动下载后放进 mods："
            + "SRParasites-1.12.2v1.9.11.jar (348025/4436467)；"
            + "RTG-1.12.2-6.1.0.0-snapshot.1.jar (237989/2782568)";

        var mods = ManualModDownloads.ParseFromMessage(message);
        Assert.Equal(2, mods.Count);
        Assert.Equal("SRParasites-1.12.2v1.9.11.jar", mods[0].Name);
        Assert.Equal(348025, mods[0].ProjectId);
        Assert.Equal(4436467, mods[0].FileId);
        Assert.Equal("RTG-1.12.2-6.1.0.0-snapshot.1.jar", mods[1].Name);

        var dir = ManualModDownloads.ModsDirectoryFromMessage(message);
        Assert.Equal(
            Path.Combine(@"D:\liumo\Documents\mc联机\.minecraft\versions\rlcraft-rlcraft1.12.2-releasev2.9.3.zip", "mods"),
            dir);
    }

    [Fact]
    public void ParseFromMessage_NewStyleWithListFile()
    {
        const string message =
            "整合包已安装，但需手动下载 2 个模组（作者禁止第三方 API 下载）。"
            + "清单已写到 D:\\games\\inst\\需手动下载的模组.txt" + "\r\n"
            + "· SRParasites-1.12.2v1.9.11.jar (348025/4436467)" + "\r\n"
            + "· RTG-1.12.2-6.1.0.0-snapshot.1.jar (237989/2782568)";

        var mods = ManualModDownloads.ParseFromMessage(message);
        Assert.Equal(2, mods.Count);
        Assert.Equal(Path.Combine(@"D:\games\inst", "mods"),
            ManualModDownloads.ModsDirectoryFromMessage(message));
    }

    [Fact]
    public void ParseFromMessage_BareIds()
    {
        const string message =
            "清单已写到 D:\\games\\inst\\需手动下载的模组.txt\r\n· 348025/4436467\r\n· 237989/2782568";

        var mods = ManualModDownloads.ParseFromMessage(message);
        Assert.Equal(2, mods.Count);
        Assert.Equal("", mods[0].Name);

        var entries = ManualModDownloads.ParseEntries(new[] { "348025/4436467", "A.jar (1/2)" });
        Assert.Equal(2, entries.Count);
        Assert.Equal("A.jar", entries[1].Name);
    }

    [Fact]
    public void PageUrl_UsesSlugBecauseNumericFileUrlIs404()
    {
        var mod = new ManualModDownload
        {
            ProjectId = 348025,
            FileId = 4436467,
            Slug = "scape-and-run-parasites",
            ClassId = 6,
        };
        Assert.Equal(
            "https://www.curseforge.com/minecraft/mc-mods/scape-and-run-parasites/files/4436467",
            mod.PageUrl);

        var pack = new ManualModDownload
        {
            ProjectId = 1,
            FileId = 2,
            Slug = "rlcraft",
            ClassId = 4471,
        };
        Assert.Equal("https://www.curseforge.com/minecraft/modpacks/rlcraft/files/2", pack.PageUrl);
    }

    [Fact]
    public void PageUrl_PrefersWebsiteUrlAndFallsBackToProjectPage()
    {
        var mod = new ManualModDownload
        {
            ProjectId = 237989,
            FileId = 2782568,
            WebsiteUrl = "https://www.curseforge.com/minecraft/mc-mods/realistic-terrain-generation/",
        };
        Assert.Equal(
            "https://www.curseforge.com/minecraft/mc-mods/realistic-terrain-generation/files/2782568",
            mod.PageUrl);
        Assert.Equal("https://www.curseforge.com/projects/237989", mod.ProjectUrl);

        // 老任务解析出来只有 ID,slug 要靠接口补;补不到时 PageUrl 为空,界面走项目主页
        Assert.Equal("", new ManualModDownload { ProjectId = 1, FileId = 2 }.PageUrl);
    }

    [Fact]
    public void ParseFromMessage_PlainErrorReturnsEmpty()
    {
        Assert.Empty(ManualModDownloads.ParseFromMessage("connection reset by peer"));
        Assert.Null(ManualModDownloads.ModsDirectoryFromMessage("connection reset by peer"));
    }
}
