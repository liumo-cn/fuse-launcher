using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Fuse.Core.Java;

/// <summary>本机发现的一个 Java 运行时。</summary>
public sealed class JavaRuntime
{
    public string Home { get; init; } = "";
    public string JavaExecutable => Path.Combine(Home, "bin", "java.exe");
    /// <summary>javaw.exe(无控制台窗口),游戏启动用它,桌面不弹命令行。</summary>
    public string JavaWExecutable => Path.Combine(Home, "bin", "javaw.exe");
    public string VersionString { get; init; } = "";
    public int MajorVersion { get; init; }
    public string Source { get; init; } = "";
}

/// <summary>
/// 扫描本机已安装 Java：环境变量、注册表、常见安装目录。
/// FR-05：版本需求以官方 javaVersion.majorVersion 为准，由调用方比对 MajorVersion。
/// </summary>
public static class JavaLocator
{
    public static IReadOnlyList<JavaRuntime> FindInstalled()
    {
        var homes = new List<(string Home, string Source)>();

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome)) homes.Add((javaHome, "JAVA_HOME"));

        homes.AddRange(FromRegistry(Registry.LocalMachine, @"SOFTWARE\JavaSoft\JDK", "注册表 JDK"));
        homes.AddRange(FromRegistry(Registry.LocalMachine, @"SOFTWARE\JavaSoft\Java Runtime Environment", "注册表 JRE"));

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
                homes.Add((dir, "常见目录"));
        }

        // 去重 + 只留完整可用的 Java(有 java.exe,也有 server JVM)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtimes = new List<JavaRuntime>();
        foreach (var (home, source) in homes)
        {
            var full = Path.GetFullPath(home);
            if (!seen.Add(full)) continue;
            if (!HasServerJvm(full)) continue;

            var ver = ReadVersionFromReleaseFile(full);
            runtimes.Add(new JavaRuntime
            {
                Home = full,
                VersionString = ver,
                MajorVersion = ParseMajor(ver),
                Source = source,
            });
        }
        return runtimes
            .OrderByDescending(r => r.MajorVersion)
            .ToList();
    }

    /// <summary>
    /// Java 是否完整可启动:bin\java.exe + server JVM(Java 8 JDK 在 jre\bin\server\jvm.dll)。
    /// 缺 jvm.dll 的运行时会让 java.exe 弹 "missing server JVM" 窗口,必须在选择阶段排除。
    /// </summary>
    public static bool HasServerJvm(string home)
    {
        if (string.IsNullOrWhiteSpace(home)) return false;
        try
        {
            var full = Path.GetFullPath(home);
            if (!File.Exists(Path.Combine(full, "bin", "java.exe"))) return false;
            return File.Exists(Path.Combine(full, "bin", "server", "jvm.dll"))
                   || File.Exists(Path.Combine(full, "jre", "bin", "server", "jvm.dll"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>按官方 javaVersion.majorVersion 挑选：优先精确匹配，其次更高版本中最小的。</summary>
    public static JavaRuntime? Pick(IReadOnlyList<JavaRuntime> runtimes, int requiredMajor)
    {
        var exact = runtimes.FirstOrDefault(r => r.MajorVersion == requiredMajor);
        if (exact != null) return exact;
        return runtimes
            .Where(r => r.MajorVersion > requiredMajor)
            .OrderBy(r => r.MajorVersion)
            .FirstOrDefault();
    }

    /// <summary>"17.0.1" → 17；"1.8.0_392" → 8；解析失败返回 0。</summary>
    public static int ParseMajor(string version)
    {
        if (string.IsNullOrEmpty(version)) return 0;
        var parts = version.Split('.', '_');
        if (parts.Length > 0 && parts[0] == "1" && parts.Length > 1 && int.TryParse(parts[1], out var legacy))
            return legacy;
        // Mojang 的 Java 8 运行时版本号是 "8u51-cacert462b08" 这种形式
        var head = parts[0];
        var u = head.IndexOf('u');
        if (u > 0 && int.TryParse(head.Substring(0, u), out var oldStyle))
            return oldStyle;
        return int.TryParse(head, out var major) ? major : 0;
    }

    /// <summary>读取指定 JavaHome 的 release 文件中的 JAVA_VERSION（无则空串）。</summary>
    public static string ReadVersion(string home) => ReadVersionFromReleaseFile(home);

    /// <summary>扫描运行时根目录（Mojang java-runtime 下载产物）：优先 runtime-meta.json 标记。</summary>
    public static IReadOnlyList<JavaRuntime> ScanRuntimesRoot(string runtimesRoot)
    {
        var result = new List<JavaRuntime>();
        if (!Directory.Exists(runtimesRoot)) return result;
        foreach (var dir in Directory.EnumerateDirectories(runtimesRoot))
        {
            if (!HasServerJvm(dir)) continue;
            var versionString = "";
            var major = 0;
            try
            {
                var metaPath = Path.Combine(dir, "runtime-meta.json");
                if (File.Exists(metaPath))
                {
                    var meta = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(metaPath));
                    versionString = meta.Value<string>("version") ?? "";
                    major = meta.Value<int?>("major") ?? 0;
                }
            }
            catch (IOException)
            {
            }
            catch (Newtonsoft.Json.JsonException)
            {
            }
            if (major == 0)
            {
                versionString = ReadVersionFromReleaseFile(dir);
                major = ParseMajor(versionString);
            }
            result.Add(new JavaRuntime
            {
                Home = dir,
                VersionString = versionString,
                MajorVersion = major,
                Source = "Mojang java-runtime",
            });
        }
        return result;
    }

    private static IEnumerable<(string Home, string Source)> FromRegistry(RegistryKey root, string keyPath, string source)
    {
        var found = new List<(string Home, string Source)>();
        try
        {
            using var key = root.OpenSubKey(keyPath);
            if (key != null)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var item = key.OpenSubKey(sub);
                    var home = item?.GetValue("JavaHome") as string;
                    if (!string.IsNullOrEmpty(home)) found.Add((home!, source));
                }
            }
        }
        catch (System.Security.SecurityException)
        {
        }
        catch (IOException)
        {
        }
        return found;
    }

    private static string ReadVersionFromReleaseFile(string home)
    {
        try
        {
            var release = Path.Combine(home, "release");
            if (!File.Exists(release)) return "";
            foreach (var line in File.ReadLines(release))
            {
                if (!line.StartsWith("JAVA_VERSION=")) continue;
                return line.Substring("JAVA_VERSION=".Length).Trim('"');
            }
        }
        catch (IOException)
        {
        }
        return "";
    }
}
