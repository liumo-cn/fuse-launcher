using System;
using System.IO;
using Fuse.Core.Installer;
using Xunit;

namespace Fuse.Core.Tests;

public class InstanceMigratorTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcdl-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Migrate_CopiesSavesOptionsServers_AndSkipsExisting()
    {
        var root = NewTempRoot();
        try
        {
            var from = Path.Combine(root, "old");
            var to = Path.Combine(root, "new");
            Directory.CreateDirectory(Path.Combine(from, "saves", "World1"));
            File.WriteAllText(Path.Combine(from, "saves", "World1", "level.dat"), "old-world");
            File.WriteAllText(Path.Combine(from, "options.txt"), "fov:0.5");
            File.WriteAllText(Path.Combine(from, "servers.dat"), "server-bytes");
            Directory.CreateDirectory(Path.Combine(from, "mods"));
            File.WriteAllText(Path.Combine(from, "mods", "a.jar"), "should-not-copy");

            Directory.CreateDirectory(Path.Combine(to, "saves"));
            File.WriteAllText(Path.Combine(to, "options.txt"), "new-default");

            var copied = InstanceMigrator.Migrate(from, to);

            Assert.Equal(2, copied); // servers.dat + 存档里的 level.dat(options.txt 目标已存在,跳过)
            Assert.True(File.Exists(Path.Combine(to, "saves", "World1", "level.dat")));
            Assert.Equal("server-bytes", File.ReadAllText(Path.Combine(to, "servers.dat")));
            // 目标已有 options.txt:不覆盖
            Assert.Equal("new-default", File.ReadAllText(Path.Combine(to, "options.txt")));
            // mods 不迁移
            Assert.False(Directory.Exists(Path.Combine(to, "mods")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
