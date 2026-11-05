using System;
using System.IO;
using Fuse.Core.Installer;
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
}
