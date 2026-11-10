using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Sources;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>地图(Worlds/classId 17)接入:类型映射、网页地址、分类列表解析。</summary>
public class CurseForgeWorldsTests : IDisposable
{
    public void Dispose() => CurseForgeSource.Configure(null);

    private sealed class FakeTransport(string json) : ICurseForgeTransport
    {
        public Task<string> GetAsync(string pathAndQuery, CancellationToken ct)
            => Task.FromResult(json);
    }

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
}
