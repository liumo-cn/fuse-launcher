using System;
using System.IO;
using Fuse.Core.Installer;
using Fuse.Core.Versioning;
using Xunit;

namespace Fuse.Core.Tests;

public class InstanceUpgraderTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcdl-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void SwapIn_KeepsOldDir_AndRollbackRestores()
    {
        var root = NewTempRoot();
        try
        {
            var live = Path.Combine(root, "instance");
            var staging = Path.Combine(root, "instance.upgrade-x");
            Directory.CreateDirectory(live);
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(live, "level.dat"), "old");
            File.WriteAllText(Path.Combine(staging, "level.dat"), "new");

            var oldDir = InstanceUpgrader.SwapIn(live, staging, "20260101-000000");

            Assert.False(Directory.Exists(staging));
            Assert.True(Directory.Exists(oldDir));
            Assert.Equal("new", File.ReadAllText(Path.Combine(live, "level.dat")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(oldDir, "level.dat")));

            var record = new InstanceUpgrader.UpgradeRecord
            {
                OldDir = oldDir,
                OldPackVersion = "1.0",
                NewPackVersion = "2.0",
                CreatedUtc = DateTime.UtcNow,
            };
            InstanceUpgrader.SaveRecord(root, "ver", record);
            Assert.NotNull(InstanceUpgrader.LoadRecord(root, "ver"));

            InstanceUpgrader.Rollback(live, record);

            Assert.Equal("old", File.ReadAllText(Path.Combine(live, "level.dat")));
            Assert.False(Directory.Exists(oldDir));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 事故回归(2026-11-20):实例目录被自动虚拟隔离后含 junction,升级换目录时
    /// 清理 .old/.failed 绝不能跟随链接清空目标内容(net48 裸递归删除的行为)。
    /// </summary>
    [Fact]
    public void SwapIn_StaleOldDirWithJunction_DoesNotDeleteLinkTarget()
    {
        var root = NewTempRoot();
        try
        {
            var live = Path.Combine(root, "instance");
            var staging = Path.Combine(root, "instance.upgrade-x");
            var shared = Path.Combine(root, "shared-mods");
            Directory.CreateDirectory(live);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(staging, "level.dat"), "new");
            File.WriteAllText(Path.Combine(shared, "precious.jar"), "keep");

            // 上一次升级留下的 .old 目录,里面有一个指向共享内容的 junction
            var stale = Path.Combine(root, "instance.old-20260101-000000");
            Directory.CreateDirectory(stale);
            Assert.True(IsolationStager.CreateJunction(Path.Combine(stale, "mods"), shared));

            InstanceUpgrader.SwapIn(live, staging, "20260101-000000");

            // SwapIn 会把 live 改名成同名 .old:目录存在,但里面的旧 junction 已被安全清除
            Assert.True(Directory.Exists(stale));
            Assert.False(Directory.Exists(Path.Combine(stale, "mods")));
            Assert.True(File.Exists(Path.Combine(shared, "precious.jar")));   // junction 目标内容完好
        }
        finally
        {
            try { SafeDirectory.DeleteTree(root, Path.Combine(root, "..")); } catch (IOException) { }
        }
    }

    [Fact]
    public void Rollback_FailedDirWithJunction_DoesNotDeleteLinkTarget()
    {
        var root = NewTempRoot();
        try
        {
            var live = Path.Combine(root, "instance");
            var oldDir = Path.Combine(root, "instance.old-x");
            var shared = Path.Combine(root, "shared-saves");
            Directory.CreateDirectory(live);
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(oldDir, "level.dat"), "old");
            File.WriteAllText(Path.Combine(live, "level.dat"), "new");
            File.WriteAllText(Path.Combine(shared, "world.dat"), "keep");
            // 当前实例(升坏了的)里有指向共享存档的 junction
            Assert.True(IsolationStager.CreateJunction(Path.Combine(live, "saves"), shared));

            var record = new InstanceUpgrader.UpgradeRecord { OldDir = oldDir };
            InstanceUpgrader.Rollback(live, record);

            Assert.Equal("old", File.ReadAllText(Path.Combine(live, "level.dat")));
            Assert.Empty(Directory.GetDirectories(root, "instance.failed-*"));   // .failed 已清理
            Assert.True(File.Exists(Path.Combine(shared, "world.dat")));   // junction 目标完好
        }
        finally
        {
            try { SafeDirectory.DeleteTree(root, Path.Combine(root, "..")); } catch (IOException) { }
        }
    }
}
