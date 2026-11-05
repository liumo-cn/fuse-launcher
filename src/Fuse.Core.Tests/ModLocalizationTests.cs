using Fuse.Core.Sources;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 中文名对照表:列表显示和中文搜索都依赖它。
/// Modrinth 的项目元数据只有英文,搜「钠」只会返回汉化包,所以必须映射成英文名再搜。
/// </summary>
public class ModLocalizationTests
{
    [Theory]
    [InlineData("钠", "Sodium")]
    [InlineData("机械动力", "Create")]
    [InlineData("虹膜", "Iris Shaders")]
    [InlineData("物品管理器", "Just Enough Items")]
    [InlineData("钠：附加选项", "Sodium Extra")]
    public void ToEnglishQuery_MapsChineseToEnglish(string input, string expected)
    {
        Assert.Equal(expected, ModLocalization.ToEnglishQuery(input));
    }

    [Theory]
    [InlineData("Sodium")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("某个没有收录的模组名")]
    public void ToEnglishQuery_FallsBackToNull(string? input)
    {
        Assert.Null(ModLocalization.ToEnglishQuery(input));
    }

    [Fact]
    public void ChineseName_ResolvesBySlug()
    {
        Assert.Equal("钠", ModLocalization.ChineseName("sodium"));
        Assert.Equal("虹膜光影", ModLocalization.ChineseName("iris"));
        Assert.Null(ModLocalization.ChineseName("not-in-table"));
        Assert.Null(ModLocalization.ChineseName(null));
    }

    [Fact]
    public void HasChinese_DetectsCjkOnly()
    {
        Assert.True(ModLocalization.HasChinese("钠"));
        Assert.True(ModLocalization.HasChinese("Create 机械"));
        Assert.False(ModLocalization.HasChinese("Create"));
        Assert.False(ModLocalization.HasChinese(""));
    }

    [Fact]
    public void SortedByZhLength_PrefersShortestMatch()
    {
        // "钠" 应该命中 Sodium 本体,而不是 "钠：附加选项"
        Assert.Equal("Sodium", ModLocalization.ToEnglishQuery("钠"));
        Assert.Equal("Sodium Extra", ModLocalization.ToEnglishQuery("附加选项"));
    }
}
