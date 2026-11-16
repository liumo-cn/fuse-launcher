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

    [Fact]
    public void NewEntries_MapByChineseName()
    {
        // 2026-11-15 新增批次的中文对照名(数据安全专项后补)
        Assert.Equal("Inventory Profiles Next", ModLocalization.ToEnglishQuery("R键整理"));
        Assert.Equal("Carry On", ModLocalization.ToEnglishQuery("便携搬运"));
        Assert.Equal("Corpse", ModLocalization.ToEnglishQuery("尸体"));
        Assert.Equal("Emotecraft", ModLocalization.ToEnglishQuery("表情动作"));
        Assert.Equal("Carpet", ModLocalization.ToEnglishQuery("地毯"));
        Assert.Equal("Chipped", ModLocalization.ToEnglishQuery("雕琢方块"));
        Assert.Equal("Lootr", ModLocalization.ToEnglishQuery("Lootr 独立战利品"));
        Assert.Equal("Fast Leaf Decay", ModLocalization.ToEnglishQuery("树叶快速腐烂"));
        Assert.Equal("Shulker Box Tooltip", ModLocalization.ToEnglishQuery("潜影盒预览"));
        Assert.Equal("Traveler's Backpack", ModLocalization.ToEnglishQuery("旅行背包"));
    }

    [Fact]
    public void ZhMatching_IsCaseInsensitive_ForNamesWithLetters()
    {
        // 中文名带英文字母(如「R键整理」)时,大小写变体必须都能命中
        Assert.Equal("Inventory Profiles Next", ModLocalization.ToEnglishQuery("r键整理"));
        Assert.Equal("Inventory Profiles Next", ModLocalization.ToEnglishQuery("R键整理"));
        Assert.Equal("Inventory Profiles Next", ModLocalization.ToEnglishQuery("R键整理 1.2"));
        Assert.Equal("KubeJS 脚本", ModLocalization.ChineseName("kubejs"));
    }
}
