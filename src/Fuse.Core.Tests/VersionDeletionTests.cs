using System.IO;
using System.Linq;
using Fuse.Core.Versioning;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>删除版本的目录规划:共享游戏根绝不入列,逃逸路径被拒,去重。</summary>
public class VersionDeletionTests
{
    private const string Root = @"D:\g\mc联机\.minecraft";

    [Fact]
    public void SharedRoot_InstanceDirEqualsRoot_IsNeverIncluded()
    {
        // mc联机事故:未开隔离的外部共享根,InstDir() 解析为游戏根本身
        var plan = VersionDeletion.PlanDirectories(Root, "1.21.5-fabric", Root);

        var entry = Assert.Single(plan);
        Assert.Equal(Path.Combine(Root, "versions", "1.21.5-fabric"), entry);
    }

    [Fact]
    public void ManagedInstance_IsIncluded()
    {
        var plan = VersionDeletion.PlanDirectories(@"D:\games", "v1", @"D:\games\instances\v1");

        Assert.Equal(2, plan.Count);
        Assert.Contains(@"D:\games\instances\v1", plan);
    }

    [Fact]
    public void IsolatedVersionsDir_IsDeduplicated()
    {
        var plan = VersionDeletion.PlanDirectories(Root, "v1", Path.Combine(Root, "versions", "v1"));

        var entry = Assert.Single(plan);
        Assert.Equal(Path.Combine(Root, "versions", "v1"), entry);
    }

    [Fact]
    public void TraversalVersionId_CannotEscapeRoot()
    {
        var plan = VersionDeletion.PlanDirectories(Root, @"..\..\evil", Root);

        Assert.All(plan, p => Assert.StartsWith(Root + Path.DirectorySeparatorChar, p));
        Assert.DoesNotContain(plan, p => p.Contains("evil"));
    }

    [Fact]
    public void OutsideRoot_InstanceDir_IsRejected()
    {
        var plan = VersionDeletion.PlanDirectories(Root, "v1", @"D:\other-game");

        var entry = Assert.Single(plan);
        Assert.Equal(Path.Combine(Root, "versions", "v1"), entry);
    }

    [Fact]
    public void Compare_IsCaseInsensitive_AndToleratesTrailingSlash()
    {
        Assert.True(VersionDeletion.IsGameRootPath(Root + "\\", Root));
        Assert.True(VersionDeletion.IsGameRootPath(Root.ToLowerInvariant(), Root.ToUpperInvariant()));
        Assert.False(VersionDeletion.IsGameRootPath(Path.Combine(Root, "versions"), Root));
    }
}
