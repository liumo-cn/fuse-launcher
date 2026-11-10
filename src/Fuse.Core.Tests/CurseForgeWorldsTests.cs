using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Sources;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>地图(Worlds/classId 17)接入:类型映射、网页地址、分类列表解析。</summary>
[Collection("CurseForge")]
public class CurseForgeWorldsTests : IDisposable
{
    public void Dispose() => CurseForgeSource.Configure(null);

    private sealed class FakeTransport(string json) : ICurseForgeTransport
    {
        public Task<string> GetAsync(string pathAndQuery, CancellationToken ct)
            => Task.FromResult(json);
    }

    /// <summary>捕获实际请求的相对路径,用于断言搜索 URL 的形状。</summary>
    private sealed class CapturingTransport(string json) : ICurseForgeTransport
    {
        public string? LastPath;
        public Task<string> GetAsync(string pathAndQuery, CancellationToken ct)
        {
            LastPath = pathAndQuery;
            return Task.FromResult(json);
        }
    }

    private static readonly string EmptySearchJson =
        """{"data":[],"pagination":{"totalCount":0,"limit":20,"offset":0,"totalSize":0}}""";

    [Fact]
    public void World_MapsToClassId17()
    {
        Assert.Equal(17, CurseForgeSource.ClassIdFor("world"));
        Assert.Equal(CurseForgeSource.ClassWorlds, CurseForgeSource.ClassIdFor("world"));
        // 其它类型不受影响
        Assert.Equal(6, CurseForgeSource.ClassIdFor("mod"));
        Assert.Equal(6552, CurseForgeSource.ClassIdFor("shader"));
    }

    [Fact]
    public void World_PageUrlUsesWorldsSection()
    {
        Assert.Equal(
            "https://www.curseforge.com/minecraft/worlds/parkour-madness",
            CurseForgeSource.ProjectPageUrl("parkour-madness", "world"));
    }

    [Fact]
    public async Task CategoriesAsync_ParsesIdSlugName()
    {
        CurseForgeSource.Configure(new FakeTransport(
            """{"data":[{"id":4876,"slug":"parkour","name":"Parkour"},{"id":401,"slug":"survival","name":"Survival"}]}"""));

        var cats = await CurseForgeSource.CategoriesAsync(CurseForgeSource.ClassWorlds, CancellationToken.None);

        Assert.Equal(2, cats.Count);
        Assert.Equal(4876, cats[0].Id);
        Assert.Equal("parkour", cats[0].Slug);
        Assert.Equal("Parkour", cats[0].Name);
        Assert.Equal("Survival", cats[1].Name);
    }

    /// <summary>地图搜索的行为回归:classId=17、不带 gameVersion(大量地图不标注版本,带了滤空)、分类数字 id 直传、不带加载器。</summary>
    [Fact]
    public async Task WorldSearch_OmitsGameVersionAndPassesNumericCategory()
    {
        var transport = new CapturingTransport(EmptySearchJson);
        CurseForgeSource.Configure(transport);

        await CurseForgeSource.SearchAsync("parkour", "", "", "4876", 20, 20,
            CancellationToken.None, "world");

        Assert.NotNull(transport.LastPath);
        Assert.Contains("/mods/search", transport.LastPath);
        Assert.Contains("classId=17", transport.LastPath);
        Assert.DoesNotContain("gameVersion=", transport.LastPath);
        Assert.DoesNotContain("modLoaderType=", transport.LastPath);
        Assert.Contains("categoryId=4876", transport.LastPath);
        Assert.Contains("searchFilter=parkour", transport.LastPath);
        Assert.Contains("index=20", transport.LastPath);
    }

    /// <summary>普通模组搜索仍带 gameVersion 与 modLoaderType(地图分支不许影响其它页签)。</summary>
    [Fact]
    public async Task ModSearch_KeepsGameVersionAndLoader()
    {
        var transport = new CapturingTransport(EmptySearchJson);
        CurseForgeSource.Configure(transport);

        await CurseForgeSource.SearchAsync("sodium", "1.21.1", "fabric", "全部", 0, 20,
            CancellationToken.None, "mod");

        Assert.NotNull(transport.LastPath);
        Assert.Contains("classId=6", transport.LastPath);
        Assert.Contains("gameVersion=1.21.1", transport.LastPath);
        Assert.Contains("modLoaderType=4", transport.LastPath);
        Assert.DoesNotContain("categoryId=", transport.LastPath);
    }
}
