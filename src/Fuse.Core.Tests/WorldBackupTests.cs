using System;
using System.IO;
using System.Linq;
using Fuse.Core.Installer;
using Xunit;

namespace Fuse.Core.Tests;

public class WorldBackupTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcdl-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void CreateAndRestore_RoundTripsWorld_AndSkipsSessionLock()
    {
        var root = NewTempRoot();
        try
        {
            var saves = Path.Combine(root, "saves");
            var world = Path.Combine(saves, "World");
            Directory.CreateDirectory(world);
            File.WriteAllText(Path.Combine(world, "level.dat"), "v1");
            File.WriteAllText(Path.Combine(world, "session.lock"), "lock");

            var backupRoot = Path.Combine(root, "backups");
            var zip = WorldBackup.Create(saves, "World", backupRoot);
            Assert.True(File.Exists(zip));

            File.WriteAllText(Path.Combine(world, "level.dat"), "v2");
            WorldBackup.Restore(zip, saves);

            Assert.Equal("v1", File.ReadAllText(Path.Combine(saves, "World", "level.dat")));
            Assert.False(File.Exists(Path.Combine(saves, "World", "session.lock")));

            var list = WorldBackup.List(backupRoot);
            Assert.Single(list);
            Assert.Equal("World", list[0].WorldName);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
