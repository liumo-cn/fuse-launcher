using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using Fuse.Core.ModConflict;
using Xunit;

namespace Fuse.Core.Tests;

public class ModConflictTests : IDisposable
{
    private readonly string _root;

    public ModConflictTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-modconflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void AddJar(string fileName, string id, string version, string dependsJson = "{}", string? name = null)
    {
        var path = Path.Combine(_root, "mods", fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("fabric.mod.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write($"{{\"id\":\"{id}\",\"version\":\"{version}\",\"name\":\"{name ?? id}\",\"depends\":{dependsJson}}}");
    }

    [Theory]
    [InlineData("1.0.9", ">=1.0.4", true)]
    [InlineData("1.0.3", ">=1.0.4", false)]
    [InlineData("0.9.2", "0.9.x", true)]
    [InlineData("0.8.9", "0.9.x", false)]
    [InlineData("0.6.10", ">=0.6.10 <1.0.0", true)]
    [InlineData("1.0.0", ">=0.6.10 <1.0.0", false)]
    [InlineData("26.1", "26.1.2", false)]
    [InlineData("26.1.2", "26.1.2", true)]
    [InlineData("26.1.2", ">=26.1", true)]
    [InlineData("1.2.3", "*", true)]
    public void Version_range_matches(string version, string constraint, bool expected)
    {
        Assert.Equal(expected, VersionRange.Matches(version, constraint));
    }

    [Theory]
    [InlineData("mc26.1.1-0.8.9-fabric", "0.9.x", false)]
    [InlineData("mc26.1.2-0.9.2-fabric", "0.9.x", true)]
    [InlineData("1.2.3", ">=1.0", true)]
    public void Modrinth_version_numbers_can_be_non_semver(string raw, string constraint, bool expected)
    {
        Assert.Equal(expected, VersionRange.MatchesAny(raw, constraint));
    }

    [Fact]
    public void Parses_chinese_fabric_error()
    {
        const string text = @"确定了一种可能的解决方法，这样做可能会解决你的问题：
	 - 安装 searchables，1.0.4 及以上版本。
	 - 将 模组 'Sodium' (sodium) 0.8.9+mc26.1.1 替换为 任意 0.9.x 版本。
	 - 模组 'MezzConfig' (mezz_config) 0.6.10 需要 'Minecraft' (minecraft) 的 26.1.2 版本，但已经安装了的版本 26.1 不对！";
        var hints = FabricErrorParser.Parse(text);
        Assert.Contains(hints, h => h.Kind == FabricErrorParser.HintKind.MissingDependency && h.ModId == "searchables");
        Assert.Contains(hints, h => h.Kind == FabricErrorParser.HintKind.ReplaceVersion && h.ModId == "sodium" && h.Constraint == "0.9.x");
        Assert.Contains(hints, h => h.Kind == FabricErrorParser.HintKind.MinecraftMismatch && h.ModId == "mezz_config" && h.RequiredMinecraft == "26.1.2");
    }

    [Fact]
    public void Analyzer_finds_duplicate_missing_mc_mismatch()
    {
        AddJar("sodium-0.8.9.jar", "sodium", "0.8.9+mc26.1.1", "{\"minecraft\":\">=26.1\"}");
        AddJar("sodium-0.9.2.jar", "sodium", "0.9.2+mc26.1.2", "{\"minecraft\":\">=26.1\"}");
        AddJar("iris.jar", "iris", "1.11.4", "{\"sodium\":\"0.9.x\",\"minecraft\":\">=26.1\"}");
        AddJar("controlling.jar", "controlling", "26.1.0.3", "{\"searchables\":\">=1.0.4\",\"minecraft\":\">=26.1\"}");
        AddJar("mezz.jar", "mezz_config", "0.6.10", "{\"minecraft\":\"26.1.2\"}");

        var report = ModConflictAnalyzer.Analyze(Path.Combine(_root, "mods"), "26.1", "fabric");

        Assert.Contains(report.Actions, a => a.Kind == ModFixKind.DisableDuplicate && a.FileName.Contains("0.8.9"));
        Assert.Contains(report.Actions, a => a.Kind == ModFixKind.InstallDependency && a.DependencyId == "searchables");
        Assert.Contains(report.Actions, a => a.Kind == ModFixKind.ReplaceVersion && a.ModId == "mezz_config" && a.DisableIfNoReplacement);
        // 去重后保留 0.9.2,iris 的 sodium 0.9.x 约束应已满足
        Assert.DoesNotContain(report.Actions, a => a.Kind == ModFixKind.ReplaceVersion && a.ModId == "sodium");
    }

    [Fact]
    public void Restore_backup_replaces_current_mods()
    {
        AddJar("a.jar", "alpha", "1.0");
        var mods = Path.Combine(_root, "mods");
        var backup = Path.Combine(_root, "mods-backup-test");
        Directory.CreateDirectory(backup);
        File.Copy(Path.Combine(mods, "a.jar"), Path.Combine(backup, "a.jar"));
        File.WriteAllText(Path.Combine(mods, "extra.jar"), "x");

        Assert.True(ModConflictFixer.RestoreBackup(mods, backup, out var error), error);
        Assert.False(File.Exists(Path.Combine(mods, "extra.jar")));
        Assert.True(File.Exists(Path.Combine(mods, "a.jar")));
    }

    [Fact]
    public void Fixer_disables_duplicate_and_creates_backup()
    {
        AddJar("old.jar", "sodium", "0.8.9");
        AddJar("new.jar", "sodium", "0.9.2");
        var report = ModConflictAnalyzer.Analyze(Path.Combine(_root, "mods"), "26.1", "fabric");
        var outcome = ModConflictFixer.ApplyAsync(
            Path.Combine(_root, "mods"), "26.1", "fabric",
            report.Actions.Where(a => a.Kind == ModFixKind.DisableDuplicate),
            new System.Net.Http.HttpClient(), null, default).GetAwaiter().GetResult();

        Assert.True(outcome.BackupCreated);
        Assert.Contains(Directory.GetFiles(Path.Combine(_root, "mods")), f => f.EndsWith(".disabled"));
        Assert.True(Directory.Exists(outcome.BackupDirectory));
    }
}
