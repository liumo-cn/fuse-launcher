using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Fuse.Core.Download;
using Fuse.Core.Installer;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>@author liumo | Original Algorithm 的落点测试:解压路径防护。</summary>
public class SafeExtractorTests : IDisposable
{
    private readonly string _root;

    public SafeExtractorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcdl-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void SafeTarget_AcceptsNormalRelativePath()
    {
        var target = SafeExtractor.SafeTarget(_root, "mods/sub/a.jar");
        Assert.StartsWith(Path.GetFullPath(_root), target, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("mods", "sub", "a.jar"), target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafeTarget_RejectsParentTraversal()
    {
        var ex = Assert.Throws<IOException>(() => SafeExtractor.SafeTarget(_root, "../evil.txt"));
        Assert.Contains("liumo-guard", ex.Message);
        Assert.Throws<IOException>(() => SafeExtractor.SafeTarget(_root, "mods/../../evil.txt"));
    }

    [Fact]
    public void SafeTarget_RejectsAbsoluteAndDrive()
    {
        Assert.Throws<IOException>(() => SafeExtractor.SafeTarget(_root, "C:/evil.txt"));
        Assert.Throws<IOException>(() => SafeExtractor.SafeTarget(_root, "/evil.txt"));
        Assert.Throws<IOException>(() => SafeExtractor.SafeTarget(_root, "C:evil.txt"));
    }

    [Fact]
    public void ExtractAll_MaliciousZip_ThrowsAndWritesNothingOutside()
    {
        var zipPath = Path.Combine(_root, "evil.mrpack");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(zip, "mods/ok.txt", "fine");
            AddEntry(zip, "../escaped.txt", "evil");
            AddEntry(zip, "C:/evil2.txt", "evil");
        }
        var extractDir = Path.Combine(_root, "stage");
        var outside = Path.GetFullPath(Path.Combine(extractDir, "..", "escaped.txt"));

        var ex = Assert.Throws<IOException>(() => ModpackInstaller.ExtractAll(zipPath, extractDir));
        Assert.Contains("liumo-guard", ex.Message);
        Assert.False(File.Exists(outside), ".. 条目不得落盘");
        Assert.Equal("fine", File.ReadAllText(Path.Combine(extractDir, "mods", "ok.txt")));
    }

    [Fact]
    public void NormalizePath_RejectsDriveLetterButKeepsRelative()
    {
        Assert.Throws<InvalidOperationException>(() => ModpackInstaller.NormalizePath("C:/mods/x.jar"));
        // 前导斜杠会被既有 TrimStart 剥掉落成相对路径(本来就越不了界;函数不转斜杠,
        // 调用方 Path.Combine 交给 Windows 文件 API 处理),只验证不抛
        Assert.Equal("abs/x.jar", ModpackInstaller.NormalizePath("/abs/x.jar"));
        Assert.Equal("mods/x.jar", ModpackInstaller.NormalizePath("mods/x.jar"));
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
