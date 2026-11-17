using System;
using Fuse.Core.ModConflict;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 数据销毁专项后的版本比较器回归:Forge mods.toml versionRange 按 Maven 语义判定。
/// 测试向量来自 2026-11-15 暮色纪元 2.6.1 整合包的真实误报数据(用户提供)。
/// </summary>
public class VersionRangeMavenTests
{
    // ---------- 用户提供的 8 条向量(必须全过) ----------

    [Theory]
    [InlineData("1.20-2.14.13", "[1.20-2.5.0,)")]        // 数值感知:14 > 5,不是 "2.14" < "2.5" 的字典序
    [InlineData("2.4-Fix", "[2.4,)")]                     // qualifier ≥ release(未知限定符排在 release 后)
    [InlineData("2.4", "[2.4,)")]                         // 下边界闭
    [InlineData("1.20.1", "[1.20.1,1.21)")]               // 下边界闭
    [InlineData("1.20.1-1.2.8", "[1.20.1,1.21)")]         // Forge 合成版本串不按 '-' 拆
    [InlineData("0.3.28+mc1.20.1", "[0.3.19+mc1.20.1,)")] // + 元数据按 Maven 切分后数值比较
    public void RealWorldVectors_AreInRange(string version, string constraint)
    {
        Assert.True(VersionRange.Matches(version, constraint), $"{version} 应满足 {constraint}");
    }

    [Theory]
    [InlineData("1.21", "[1.20.1,1.21)")]                 // 上边界开
    [InlineData("0.3.9+mc1.20.1", "[0.3.19+mc1.20.1,)")]  // 数值比较:9 < 19,不是字典序("9" > "1")
    public void RealWorldVectors_AreOutOfRange(string version, string constraint)
    {
        Assert.False(VersionRange.Matches(version, constraint), $"{version} 不应满足 {constraint}");
    }

    // ---------- Maven 规范行为 ----------

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0", "1.0.0.0.0")]
    [InlineData("1-ga", "1.0")]
    [InlineData("1-final", "1.0")]
    [InlineData("1", "1-release")]
    public void MavenEquivalences_CompareEqual(string a, string b)
    {
        var va = MavenVersion.TryCreate(a, out var x) ? x : throw new InvalidOperationException(a);
        var vb = MavenVersion.TryCreate(b, out var y) ? y : throw new InvalidOperationException(b);
        Assert.Equal(0, va.CompareTo(vb));
        Assert.Equal(0, vb.CompareTo(va));
    }

    [Theory]
    [InlineData("1.0-alpha", "1.0")]       // alpha < release
    [InlineData("1.0-beta", "1.0")]
    [InlineData("1.0-milestone", "1.0")]
    [InlineData("1.0-rc", "1.0")]
    [InlineData("1.0-snapshot", "1.0")]
    [InlineData("1.0", "1.0-sp")]          // sp > release
    [InlineData("1.0", "1.0-fix")]         // 未知限定符 > release
    [InlineData("1.0.9", "1.0.10")]        // 数值感知:9 < 10(字典序会判反)
    public void Ordering_MatchesMavenSemantics(string smaller, string larger)
    {
        var va = MavenVersion.TryCreate(smaller, out var x) ? x : throw new InvalidOperationException(smaller);
        var vb = MavenVersion.TryCreate(larger, out var y) ? y : throw new InvalidOperationException(larger);
        Assert.True(va.CompareTo(vb) < 0, $"{smaller} 应小于 {larger}");
        Assert.True(vb.CompareTo(va) > 0, $"{larger} 应大于 {smaller}");
    }

    [Fact]
    public void QualifierOrdering_FullChain()
    {
        // alpha < beta < milestone < rc < snapshot < release == ga == final < sp < 未知
        var order = new[]
        {
            "1-alpha", "1-beta", "1-milestone", "1-rc", "1-snapshot",
            "1", "1-ga", "1-final", "1-sp", "1-fix",
        };
        for (var i = 0; i < order.Length - 1; i++)
        {
            var a = MavenVersion.TryCreate(order[i], out var x) ? x : throw new InvalidOperationException(order[i]);
            var b = MavenVersion.TryCreate(order[i + 1], out var y) ? y : throw new InvalidOperationException(order[i + 1]);
            var cmp = a.CompareTo(b);
            // ga / final 与 release 等价,不是严格递增
            if (order[i + 1] is "1-ga" or "1-final") Assert.Equal(0, cmp);
            else Assert.True(cmp < 0, $"{order[i]} 应小于 {order[i + 1]}");
        }
    }

    [Fact]
    public void KeepNewestDuplicate_UsesMavenSemantics()
    {
        // 重复 mod id 保留"最高版本":1.20-2.14.13 > 1.20-2.5.0(数值感知)
        var older = MavenVersion.TryCreate("1.20-2.5.0", out var o) ? o : throw new InvalidOperationException();
        var newer = MavenVersion.TryCreate("1.20-2.14.13", out var n) ? n : throw new InvalidOperationException();
        Assert.True(newer.CompareTo(older) > 0);
    }

    // ---------- 修复前模拟:替换引发的新违规 ----------

    private static Fuse.Core.ModConflict.ModDependencyGraph Mod(
        string id, string version, params (string DepId, string Constraint)[] depends)
        => new()
        {
            Id = id,
            Name = id,
            Version = version,
            Depends = depends.ToDictionary(d => d.DepId, d => d.Constraint, StringComparer.OrdinalIgnoreCase),
        };

    [Fact]
    public void Simulation_FlagsReplacementThatBreaksOtherMod()
    {
        // 真实场景:moonlight 换到 2.5.x 会让 supplementaries(要求更高版本)坏掉
        var graph = new[]
        {
            Mod("moonlight", "1.20.1-2.7.1", ("moonlight-lib", "[1.20,)")),
            Mod("supplementaries", "3.1.36", ("moonlight", "[1.20.1-2.7.0,)")),
        };

        var problems = Fuse.Core.ModConflict.ModConflictFixer.SimulateReplacementViolations(
            graph, new[] { ("moonlight", "1.20.1-2.5.0") });

        var problem = Assert.Single(problems);
        Assert.Contains("supplementaries", problem);
    }

    [Fact]
    public void Simulation_NoProblem_WhenReplacementSatisfiesAll()
    {
        var graph = new[]
        {
            Mod("moonlight", "1.20.1-1.2.8"),
            Mod("supplementaries", "3.1.36", ("moonlight", "[1.20.1-2.5.0,)")),
        };

        var problems = Fuse.Core.ModConflict.ModConflictFixer.SimulateReplacementViolations(
            graph, new[] { ("moonlight", "1.20.1-2.5.0") });

        Assert.Empty(problems);
    }
}
