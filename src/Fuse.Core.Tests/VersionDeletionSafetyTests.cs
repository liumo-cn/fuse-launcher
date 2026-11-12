using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuse.Core.Versioning;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 数据销毁级 bug 专项回归(safety-audit):沙盒内真实执行删除,
/// 前后做全树快照对比,断言"被删集合 == 预期集合"。沙盒 = 每测试独立的
/// %TEMP%\mcdl-safety-* 目录,与任何真实游戏目录/配置无关。
/// </summary>
public class VersionDeletionSafetyTests : IDisposable
{
    private readonly string _root;      // 模拟外部共享游戏根(PCL 布局)
    private readonly string _install;   // 模拟主安装目录
    private readonly string _base;      // 沙盒根(两个目录的公共父级)

    public VersionDeletionSafetyTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "mcdl-safety-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "game-root");
        _install = Path.Combine(_base, "install");
        Directory.CreateDirectory(Path.Combine(_root, "versions", "v1"));
        Directory.CreateDirectory(Path.Combine(_root, "versions", "v2"));
        Directory.CreateDirectory(Path.Combine(_root, "saves", "world"));
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "versions", "v1", "v1.json"),
            """{"id":"v1","type":"release","releaseTime":"2026-01-01T00:00:00"}""");
        File.WriteAllText(Path.Combine(_root, "versions", "v1", "v1.jar"), "jar");
        File.WriteAllText(Path.Combine(_root, "versions", "v2", "v2.json"),
            """{"id":"v2","type":"release","releaseTime":"2026-01-02T00:00:00"}""");
        File.WriteAllText(Path.Combine(_root, "saves", "world", "level.dat"), "data");
        File.WriteAllText(Path.Combine(_root, "mods", "a.jar"), "jar");
        File.WriteAllText(Path.Combine(_root, "options.txt"), "options");
    }

    public void Dispose()
    {
        try { SafeDirectory.DeleteTree(_root, _base); } catch (IOException) { }
        try { SafeDirectory.DeleteTree(_install, _base); } catch (IOException) { }
    }

    /// <summary>全树快照:相对路径集合(junction 记录自身,不进入目标)。根不存在 = 空快照。</summary>
    private static HashSet<string> Snapshot(string root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return set;
        void Walk(DirectoryInfo dir, string rel)
        {
            foreach (var e in dir.EnumerateFileSystemInfos())
            {
                var r = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    set.Add(r + "[link]");
                    continue;
                }
                set.Add(r);
                if (e is DirectoryInfo sub) Walk(sub, r);
            }
        }
        Walk(new DirectoryInfo(root), "");
        return set;
    }

    private static string Rel(string path, string root) =>
        Path.GetFullPath(path).Substring(Path.GetFullPath(root).Length + 1)
            .Replace('\\', '/');

    // ---------- S1:修复验证(真实执行 + 快照对比) ----------

    [Fact]
    public void SharedRoot_DeleteVersion_Snapshot_MatchesExpected()
    {
        // 共享根(未隔离):PlanDirectories 只允许删除 versions/v1
        var targets = VersionDeletion.PlanDirectories(_root, "v1", _root);
        var entry = Assert.Single(targets);
        Assert.Equal(Path.Combine(_root, "versions", "v1"), entry);

        var before = Snapshot(_root);
        foreach (var t in targets) SafeDirectory.DeleteTree(t, _root);
        var after = Snapshot(_root);

        // 快照对比:被删集合 == 预期集合(多一个少一个都算失败)
        var removed = before.Except(after).Select(p => p.Replace('\\', '/')).ToHashSet();
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "versions/v1", "versions/v1/v1.json", "versions/v1/v1.jar",
        };
        Assert.Equal(expected, removed);
        // 其它一切原样存在
        Assert.Contains("saves/world/level.dat", after);
        Assert.Contains("mods/a.jar", after);
        Assert.Contains("versions/v2/v2.json", after);
        Assert.Contains("options.txt", after);
    }

    [Fact]
    public void IsolatedStagedVersion_DeleteVersion_Snapshot_MatchesExpected()
    {
        // 虚拟隔离后的版本:instDir 解析为 versions/v1(隔离),删除仍只删版本目录
        IsolationStager.EnsureStaged(_root, "v1", _install);
        var instDir = InstanceLayout.ResolveExternal(_root, "v1");
        Assert.Equal(Path.Combine(_root, "versions", "v1"), instDir);

        var targets = VersionDeletion.PlanDirectories(_root, "v1", instDir);
        var before = Snapshot(_root);
        foreach (var t in targets) SafeDirectory.DeleteTree(t, _root);
        var after = Snapshot(_root);

        var removed = before.Except(after).Select(p => p.Replace('\\', '/')).ToHashSet();
        // junction 条目只摘链接([link]),共享内容不在被删集合
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "versions/v1", "versions/v1/v1.json", "versions/v1/v1.jar",
            "versions/v1/saves[link]", "versions/v1/mods[link]",
            "versions/v1/config", "versions/v1/resourcepacks", "versions/v1/shaderpacks",
            "versions/v1/options.txt",
        };
        Assert.Equal(expected, removed);
        // 共享内容完好
        Assert.Contains("saves/world/level.dat", after);
        Assert.Contains("mods/a.jar", after);
    }

    /// <summary>
    /// 对照验证:复刻修复前的算法(ResolveExternal 解析实例目录 + 整目录删),
    /// 在沙盒中证明它确实会摧毁整个游戏根——修复前后的行为差异由此测试固化。
    /// 这是唯一故意"破坏沙盒"的用例,破坏范围 = 沙盒自身。
    /// </summary>
    [Fact]
    public void Counterfactual_LegacyAlgorithm_DestroysEntireGameRoot()
    {
        var before = Snapshot(_root);
        Assert.Contains("saves/world/level.dat", before);

        // 修复前算法:InstDir() 对未隔离共享根的解析结果 = 游戏根本身
        var legacyInstanceDir = InstanceLayout.ResolveExternal(_root, "v1");
        Assert.Equal(_root, legacyInstanceDir);   // 解析结果就是根——这就是事故根源
        Directory.Delete(legacyInstanceDir, true); // 修复前的删除方式

        var after = Snapshot(_root);
        // 整个游戏根(存档/模组/两个版本/配置)全部消失 = mc联机 事故复现
        Assert.DoesNotContain("saves/world/level.dat", after);
        Assert.DoesNotContain("versions/v2/v2.json", after);
        Assert.Empty(after);
    }

    // ---------- S4:护栏(白名单根/保护根/逃逸) ----------

    [Fact]
    public void Guard_RejectsTargetOutsideAllowedRoot()
    {
        var ex = Assert.Throws<IOException>(() =>
            SafeDirectory.DeleteTree(@"D:\definitely-outside-sandbox\v", _root));
        Assert.Contains("允许的根目录", ex.Message);
        // 沙盒未被误删
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Guard_RejectsAllowedRootItself_AndAncestors()
    {
        Assert.Throws<IOException>(() => SafeDirectory.DeleteTree(_root, _root));
        // 祖先必然不在根内,同样被拒
        var parent = Path.GetDirectoryName(Path.GetFullPath(_root))!;
        Assert.Throws<IOException>(() => SafeDirectory.DeleteTree(parent, _root));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Guard_RejectsTraversalEscape()
    {
        // ".." 逃逸:FullPath 解析后落在允许根之外 → 拒绝
        var sneaky = Path.Combine(_root, "versions", "v1", "..", "..", "..", "escaped");
        var ex = Assert.Throws<IOException>(() => SafeDirectory.DeleteTree(sneaky, _root));
        Assert.Contains("允许的根目录", ex.Message);
    }

    [Fact]
    public void Guard_RejectsEmptyPath()
    {
        Assert.Throws<IOException>(() => SafeDirectory.DeleteTree("", _root));
        Assert.Throws<IOException>(() => SafeDirectory.DeleteTree("   ", _root));
        Assert.Throws<IOException>(() => SafeDirectory.DeleteTree(_root, ""));
    }

    [Fact]
    public void Guard_RejectsDriveRoot_AndProtectedRoots()
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(_root))!;   // 如 C:\
        Assert.Throws<IOException>(() => SafeDirectory.DeleteTree(driveRoot, _base));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Throws<IOException>(() =>
            SafeDirectory.DeleteTree(Path.Combine(profile, "some-game"), _base));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Throws<IOException>(() => SafeDirectory.DeleteTree(appData, _base));

        // AppData 子树(标准游戏目录位置)允许,前提是显式传入其父级为允许根
        var fakeGame = Path.Combine(appData, "mcdl-guard-probe", "sub");
        Directory.CreateDirectory(fakeGame);
        try
        {
            SafeDirectory.DeleteTree(fakeGame, Path.Combine(appData, "mcdl-guard-probe"));
            Assert.False(Directory.Exists(fakeGame));   // 合法子树删除成功
        }
        finally
        {
            try { Directory.Delete(Path.Combine(appData, "mcdl-guard-probe"), true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Guard_RootCheck_IsCaseInsensitive()
    {
        var upperRoot = _root.ToUpperInvariant();
        // 大小写不同的允许根:根内的目标仍可删
        var targets = VersionDeletion.PlanDirectories(upperRoot, "v2", upperRoot);
        Assert.Single(targets);
        SafeDirectory.DeleteTree(targets[0], upperRoot);
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "v2")));
        Assert.True(File.Exists(Path.Combine(_root, "saves", "world", "level.dat")));
    }
}
