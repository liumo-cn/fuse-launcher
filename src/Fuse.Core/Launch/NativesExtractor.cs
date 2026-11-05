using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Versioning;

namespace Fuse.Core.Launch;

/// <summary>
/// 从 natives 库 jar 中提取平台动态库（FR-07 节点 7）。
/// 支持两种官方布局：新式独立 classifier 库（1.19+，名称含 :natives-windows）与
/// 旧式 downloads.classifiers（≤1.18）。
/// </summary>
public static class NativesExtractor
{
    public static IReadOnlyList<string> Extract(
        McVersion version,
        string librariesDirectory,
        string nativesDirectory,
        string osName = "windows",
        string arch = "x64")
    {
        var env = new RuleEnvironment { OsName = osName, Arch = arch };
        Directory.CreateDirectory(nativesDirectory);
        var extracted = new List<string>();
        var jars = new List<string>();

        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.Evaluate(lib.Rules, env)) continue;

            if (lib.Artifact != null && lib.Name.EndsWith(":natives-" + osName, StringComparison.Ordinal))
                jars.Add(Path.Combine(librariesDirectory, lib.Artifact.Path));

            if (lib.Classifiers != null &&
                lib.Classifiers.TryGetValue("natives-" + osName, out var nativeArtifact) &&
                !string.IsNullOrEmpty(nativeArtifact.Path))
                jars.Add(Path.Combine(librariesDirectory, nativeArtifact.Path));
        }

        foreach (var jar in jars.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(jar)) continue; // 库文件缺失交由启动前自动修复节点处理
            using var archive = ZipFile.OpenRead(jar);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var destPath = Path.Combine(nativesDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                if (File.Exists(destPath) && new FileInfo(destPath).Length == entry.Length)
                    continue; // 已存在且尺寸一致，跳过
                using var source = entry.Open();
                using var target = File.Create(destPath);
                source.CopyTo(target);
                extracted.Add(destPath);
            }
        }
        return extracted;
    }
}
