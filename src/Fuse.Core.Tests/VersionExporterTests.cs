using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Installer;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 完整版本 zip 的内容规则(用户确认):**含存档 saves**,不含个人数据
/// (视频/键位设置、服务器列表、账号缓存)与日志。
/// </summary>
public class VersionExporterTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcdl-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task ExportFullZip_IncludesSaves_ExcludesPersonalDataAndLogs()
    {
        var root = NewTempRoot();
        try
        {
            var versionsRoot = Path.Combine(root, "versions");
            var instancesRoot = Path.Combine(root, "instances");
            var librariesRoot = Path.Combine(root, "libraries");

            var verDir = Path.Combine(versionsRoot, "1.20");
            var instDir = Path.Combine(instancesRoot, "1.20");
            var libDir = Path.Combine(librariesRoot, "com", "example");
            Directory.CreateDirectory(verDir);
            Directory.CreateDirectory(Path.Combine(instDir, "saves", "world"));
            Directory.CreateDirectory(Path.Combine(instDir, "logs"));
            Directory.CreateDirectory(libDir);

            File.WriteAllText(Path.Combine(verDir, "1.20.json"), "{}");
            File.WriteAllText(Path.Combine(verDir, "1.20.jar"), "jar");
            File.WriteAllText(Path.Combine(instDir, "saves", "world", "level.dat"), "level");
            File.WriteAllText(Path.Combine(instDir, "options.txt"), "fov");
            File.WriteAllText(Path.Combine(instDir, "servers.dat"), "servers");
            File.WriteAllText(Path.Combine(instDir, "logs", "latest.log"), "log");
            File.WriteAllText(Path.Combine(libDir, "example.jar"), "lib");

            var zipPath = Path.Combine(root, "out.zip");
            var result = await VersionExporter.ExportFullZipAsync(
                "1.20", versionsRoot, instDir, librariesRoot, zipPath, CancellationToken.None);

            Assert.True(File.Exists(zipPath));
            Assert.True(result.FileCount > 0);

            using var archive = ZipFile.OpenRead(zipPath);
            var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

            // 存档要带上
            Assert.Contains("instances/1.20/saves/world/level.dat", names);
            // 版本与依赖库
            Assert.Contains("versions/1.20/1.20.jar", names);
            Assert.Contains("versions/1.20/1.20.json", names);
            Assert.Contains("libraries/com/example/example.jar", names);
            // 个人数据与日志不带
            Assert.DoesNotContain(names, n => n.EndsWith("options.txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.EndsWith("servers.dat", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("/logs/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task ExportFullZip_SharedRootDoesNotPackageGlobalAssets()
    {
        var root = NewTempRoot();
        try
        {
            var versionsRoot = Path.Combine(root, "versions");
            var librariesRoot = Path.Combine(root, "libraries");
            Directory.CreateDirectory(Path.Combine(versionsRoot, "1.20"));
            File.WriteAllText(Path.Combine(versionsRoot, "1.20", "1.20.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "mods"));
            File.WriteAllText(Path.Combine(root, "mods", "example.jar"), "jar");
            Directory.CreateDirectory(Path.Combine(root, "saves", "world"));
            File.WriteAllText(Path.Combine(root, "saves", "world", "level.dat"), "level");
            // 共享根里还有全局资源,不能被打进 instances/1.20
            Directory.CreateDirectory(Path.Combine(root, "assets", "objects"));
            File.WriteAllText(Path.Combine(root, "assets", "objects", "obj"), "asset");
            Directory.CreateDirectory(Path.Combine(librariesRoot, "com", "example"));
            File.WriteAllText(Path.Combine(librariesRoot, "com", "example", "example.jar"), "lib");
            Directory.CreateDirectory(Path.Combine(root, "backups", "1.20"));
            File.WriteAllText(Path.Combine(root, "backups", "1.20", "old.zip"), "zip");

            var zipPath = Path.Combine(root, "shared.zip");
            await VersionExporter.ExportFullZipAsync(
                "1.20", versionsRoot, root, librariesRoot, zipPath, CancellationToken.None);

            using var archive = ZipFile.OpenRead(zipPath);
            var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            Assert.Contains("instances/1.20/mods/example.jar", names);
            Assert.Contains("instances/1.20/saves/world/level.dat", names);
            Assert.DoesNotContain(names, n => n.StartsWith("instances/1.20/assets/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.StartsWith("instances/1.20/libraries/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.StartsWith("instances/1.20/backups/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
