using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Fuse.Core.Java;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>FR-05：官方 java-runtime 挑选与清单规划（结构已对照真实官方清单实测）。</summary>
public class MojangJavaRuntimeTests
{
    private static JavaRuntimeEntry Entry(string component, string version) =>
        new(component, version, JavaLocator.ParseMajor(version),
            $"https://example.com/{component}.json", "sha1-" + component, "windows-x64");

    [Fact]
    public void Pick_ComponentExactMatchWins()
    {
        var entries = new[]
        {
            Entry("java-runtime-gamma", "17.0.15"),
            Entry("java-runtime-delta", "21.0.7"),
            Entry("jre-legacy", "8.0.412"),
        };
        var picked = MojangJavaRuntime.Pick(entries, "java-runtime-delta", 17);
        Assert.Equal("java-runtime-delta", picked!.Component); // 官方 component 优先于主版本
    }

    [Fact]
    public void Pick_FallsBackToSameMajor_ThenHigher()
    {
        var entries = new[]
        {
            Entry("java-runtime-delta", "21.0.7"),
            Entry("java-runtime-gamma", "17.0.15"),
        };
        // component 名对不上（如未来改名）→ 按主版本 17 挑到 gamma
        Assert.Equal("java-runtime-gamma", MojangJavaRuntime.Pick(entries, "java-runtime-unknown", 17)!.Component);
        // 17 缺失 → 取更高版本中最小的
        var no17 = new[] { Entry("java-runtime-delta", "21.0.7") };
        Assert.Equal("java-runtime-delta", MojangJavaRuntime.Pick(no17, null, 17)!.Component);
        // 8（jre-legacy 场景）缺失时也回退更高
        Assert.Equal("java-runtime-delta", MojangJavaRuntime.Pick(no17, null, 8)!.Component);
    }

    [Fact]
    public void PlanFiles_ParsesRawDownloads_SkipsDirectories()
    {
        var manifest = JObject.Parse("""
        {
          "files": {
            "bin/java.exe": {
              "type": "file",
              "executable": true,
              "downloads": {
                "raw": { "sha1": "aaa", "size": 216528, "url": "https://example.com/java.exe" },
                "lzma": { "sha1": "bbb", "size": 90000, "url": "https://example.com/java.exe.lzma" }
              }
            },
            "lib": { "type": "directory" },
            "release": {
              "type": "file",
              "downloads": { "raw": { "sha1": "ccc", "size": 417, "url": "https://example.com/release" } }
            }
          }
        }
        """);
        var items = MojangJavaRuntime.PlanFiles(manifest, @"C:\mc\runtimes\gamma");
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.DestPath.EndsWith(@"bin\java.exe") && i.ExpectedHash == "aaa");
        // 必须用 raw 渠道（不是 lzma）
        Assert.DoesNotContain(items, i => i.Url.Contains(".lzma"));
    }

    [Fact]
    public async Task Download_WritesMarkerAndScanFindsIt()
    {
        // 扫描识别标记（真下载由实机 doctor 验证）：root/<runtime>/{bin/java.exe, runtime-meta.json}
        var root = Path.Combine(Path.GetTempPath(), "mcdl-jre-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "java-runtime-gamma");
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bin", "java.exe"), "stub");
            // 真实运行时一定有 server JVM;扫描现在会校验它(缺了启动时会弹 missing server JVM)
            Directory.CreateDirectory(Path.Combine(dir, "bin", "server"));
            await File.WriteAllTextAsync(Path.Combine(dir, "bin", "server", "jvm.dll"), "stub");
            await File.WriteAllTextAsync(Path.Combine(dir, "runtime-meta.json"),
                new JObject { ["component"] = "java-runtime-gamma", ["version"] = "17.0.15", ["major"] = 17 }.ToString());

            var found = JavaLocator.ScanRuntimesRoot(root);
            Assert.Single(found);
            Assert.Equal(17, found[0].MajorVersion);
            Assert.Equal("17.0.15", found[0].VersionString);
            Assert.Contains("Mojang", found[0].Source);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
