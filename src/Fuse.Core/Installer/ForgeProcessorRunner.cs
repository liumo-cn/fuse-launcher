using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// 直接执行 Forge/NeoForge installer 里的 processors。
///
/// 为什么不跑官方 installer:1.20.4 起 installer.jar 带 META-INF/FORGE.SF 签名,
/// 一旦改动内部 install_profile.json(把下载源改到镜像)就被判定为篡改,
/// 输出 "A problem installing was detected";不改则启动时要拉 files.minecraftforge.net,
/// 国内经常直接挂起。所以按 installer 规范的 processors 自己跑:
///   · jar/classpath 都是 libraries 里的 maven 坐标(jar 的主类从 MANIFEST 读)
///   · args 里的 {KEY} 取自 install_profile.data(client 侧),[...] 是 maven 坐标 → 路径
///   · 输出文件按 outputs 校验存在性
/// </summary>
internal static class ForgeProcessorRunner
{
    private static readonly TimeSpan ProcessorTimeout = TimeSpan.FromMinutes(10);

    public static async Task RunAsync(
        byte[] installerBytes, string installerPath, string launcherRoot, string librariesRoot,
        string versionsRoot, string gameVersion, string javaExe, string dataDir,
        IProgress<double>? progress, CancellationToken ct)
    {
        var profile = ReadProfile(installerBytes)
                      ?? throw new InvalidOperationException("installer 里没有 install_profile.json");

        var data = ResolveData(profile, installerBytes, installerPath, launcherRoot, librariesRoot, versionsRoot, gameVersion, dataDir);
        var processors = profile["processors"] as JArray ?? new JArray();
        var total = Math.Max(processors.Count, 1);

        for (var i = 0; i < processors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var processor = processors[i] as JObject;
            if (processor == null) continue;

            // sided=true 的 processor 只服务某一侧,client 安装跳过 server
            var sided = processor.Value<bool?>("sided") == true;
            var side = data.TryGetValue("SIDE", out var s) ? s : "client";
            if (sided && side != "client") continue;

            // 输出都已存在 = 之前装过(或前一个 processor 已产出同一文件),跳过
            if (AllOutputsPresent(processor, data, librariesRoot, launcherRoot, versionsRoot, gameVersion))
            {
                progress?.Report(0.82 + 0.17 * (i + 1) / total);
                continue;
            }

            var jarCoord = processor.Value<string>("jar");
            if (string.IsNullOrEmpty(jarCoord)) continue;
            var jarPath = MavenPathOf(librariesRoot, jarCoord!);
            if (!File.Exists(jarPath))
                throw new FileNotFoundException($"processor 的 jar 不存在:{jarCoord}");

            var classpath = new List<string> { jarPath };
            if (processor["classpath"] is JArray cp)
                foreach (var c in cp.OfType<JValue>())
                {
                    var p = MavenPathOf(librariesRoot, c.Value<string>() ?? "");
                    if (p.Length > 0) classpath.Add(p);
                }

            var main = ReadMainClass(jarPath)
                       ?? throw new InvalidOperationException($"无法从 {Path.GetFileName(jarPath)} 的 MANIFEST 读取 Main-Class");

            var args = new List<string>();
            if (processor["args"] is JArray argArray)
                foreach (var a in argArray.OfType<JValue>())
                    args.Add(Expand(a.Value<string>() ?? "", data, librariesRoot, launcherRoot, versionsRoot, gameVersion));

            // 原版 client jar 是普通 jar(不是 bundler),BUNDLER_EXTRACT 会直接报错。
            // 官方 installer 处理的 client jar 其实也来自解包后的普通 jar,所以这里自己兜底:
            //   --libraries  → 没有内嵌库可解,跳过
            //   --jar-only   → 直接把输入 jar 当成 MC_UNPACKED 的输出
            var task = ArgValue(args, "--task");
            if (string.Equals(task, "BUNDLER_EXTRACT", StringComparison.OrdinalIgnoreCase))
            {
                var input = ArgValue(args, "--input");
                if (input != null && !IsBundler(input))
                {
                    var outPath = ArgValue(args, "--output");
                    if (outPath != null && args.Contains("--jar-only"))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                        File.Copy(input, outPath, overwrite: true);
                    }
                    progress?.Report(0.82 + 0.17 * (i + 1) / total);
                    continue;
                }
            }

            var argLine = string.Join(" ", args.Select(Quote));
            Log(dataDir, $"run {main} {argLine}");
            var (exit, output) = await RunJavaAsync(javaExe,
                $"-cp {Quote(string.Join(";", classpath))} {main} {argLine}",
                launcherRoot, ProcessorTimeout, ct).ConfigureAwait(false);

            Log(dataDir, $"exit {exit}");
            if (output.Length > 0) Log(dataDir, output.Length > 2000 ? output.Substring(output.Length - 2000) : output);

            if (exit != 0)
            {
                var tail = output.Length > 800 ? output.Substring(output.Length - 800) : output;
                throw new IOException($"processor {main} 退出码 {exit}:{tail}");
            }

            VerifyOutputs(processor, librariesRoot, launcherRoot, versionsRoot, gameVersion, data);
            progress?.Report(0.82 + 0.17 * (i + 1) / total);
        }

        // 关键产物必须存在,否则启动会以莫名其妙的 NoSuchElementException 失败
        foreach (var key in new[] { "MC_UNPACKED", "MC_SLIM", "MC_SRG", "PATCHED" })
        {
            if (!data.TryGetValue(key, out var produced)) continue;
            if (produced.Contains("{") || produced.Contains("[")) continue;
            if (!File.Exists(produced))
                throw new IOException($"processors 没有产出 {key}:{produced}(详见 {dataDir} 下的 processors.log)");
        }
    }

    private static void Log(string dataDir, string message)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.AppendAllText(Path.Combine(dataDir, "processors.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    // ---------- profile / 数据 ----------

    private static JObject? ReadProfile(byte[] installerBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(installerBytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("install_profile.json");
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        return JObject.Parse(reader.ReadToEnd());
    }

    private static Dictionary<string, string> ResolveData(
        JObject profile, byte[] installerBytes, string installerPath,
        string launcherRoot, string librariesRoot, string versionsRoot, string gameVersion, string dataDir)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SIDE"] = "client",
            ["ROOT"] = launcherRoot,
            ["INSTALLER"] = installerPath,
            ["LIBRARY_DIR"] = librariesRoot,
            ["MINECRAFT_VERSION"] = gameVersion,
            // 启动器自己的客户端 jar:客户端 processors 里 {MINECRAFT_JAR} 就指它
            ["MINECRAFT_JAR"] = Path.Combine(versionsRoot, gameVersion, gameVersion + ".jar"),
        };

        if (profile["data"] is JObject raw)
        {
            foreach (var kv in raw.Properties())
            {
                var value = kv.Value is JObject sided ? sided.Value<string>("client") : kv.Value.Type == JTokenType.String ? kv.Value.Value<string>() : null;
                if (string.IsNullOrEmpty(value)) continue;
                value = value!.Trim();
                if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
                    value = value.Substring(1, value.Length - 2); // SHA 值带引号

                if (value.StartsWith("/", StringComparison.Ordinal))
                    value = ExtractInstallerEntry(installerBytes, value.TrimStart('/'), dataDir);
                else if (value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal))
                    value = MavenPathOf(librariesRoot, value.Trim('[', ']'));

                data[kv.Name] = value;
            }
        }
        return data;
    }

    /// <summary>
    /// 把 installer 里的 data/xxx 资源释放到该版本专属目录并返回路径。
    /// 必须按版本隔离:每个 installer 的 data/client.lzma 是不同的补丁包,
    /// 共用缓存会让下一个加载器拿到上一个的补丁(表现为 binarypatcher 校验和对不上)。
    /// </summary>
    private static string ExtractInstallerEntry(byte[] installerBytes, string entryName, string dataDir)
    {
        var dir = dataDir;
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, entryName.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(target)) return target;
        using var zip = new ZipArchive(new MemoryStream(installerBytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName)
                    ?? throw new FileNotFoundException("installer 里缺少 " + entryName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = entry.Open();
        using var output = File.Create(target);
        input.CopyTo(output);
        return target;
    }

    // ---------- 路径/占位符 ----------

    private static string Expand(
        string arg, Dictionary<string, string> data, string librariesRoot,
        string launcherRoot, string versionsRoot, string gameVersion)
    {
        // [group:artifact:version:classifier@ext] → libraries 下的文件路径
        var result = System.Text.RegularExpressions.Regex.Replace(arg, @"\[([^\]]+)\]",
            m => MavenPathOf(librariesRoot, m.Groups[1].Value));

        // {KEY} → data 里的值(数据本身已在 ResolveData 里展开过)
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\{([A-Za-z0-9_]+)\}",
            m => data.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        return result
            .Replace("{ROOT}", launcherRoot)
            .Replace("{SIDE}", "client")
            .Replace("{MINECRAFT_VERSION}", gameVersion);
    }

    /// <summary>maven 坐标 → libraries 下的文件路径。</summary>
    private static string MavenPathOf(string librariesRoot, string coord)
    {
        var at = coord.IndexOf('@');
        var ext = "jar";
        if (at >= 0)
        {
            ext = coord.Substring(at + 1);
            coord = coord.Substring(0, at);
        }
        var parts = coord.Split(':');
        if (parts.Length < 3) return "";
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 && parts[3].Length > 0 ? "-" + parts[3] : "";
        var file = $"{artifact}-{version}{classifier}.{ext}";
        return Path.Combine(librariesRoot, group, artifact, version, file);
    }

    private static void VerifyOutputs(
        JObject processor, string librariesRoot, string launcherRoot,
        string versionsRoot, string gameVersion, Dictionary<string, string> data)
    {
        // outputs 形如 {"{MC_SRG}": "{MC_SRG_SHA}"}:第一个是产出文件,第二个是期望哈希(data 里的值)
        if (processor["outputs"] is not JObject outputs) return;
        foreach (var kv in outputs.Properties())
        {
            var path = Expand(kv.Name, data, librariesRoot, launcherRoot, versionsRoot, gameVersion);
            if (path.Contains("{") || path.Contains("[")) continue; // 未知占位符(如 SHA),不是路径
            if (!File.Exists(path))
                throw new IOException("processor 没有产出 " + Path.GetFileName(path));
        }
    }

    private static string? ReadMainClass(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
            if (manifest == null) return null;
            using var reader = new StreamReader(manifest.Open());
            string? line;
            var continued = "";
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith(" ", StringComparison.Ordinal)) { continued += line.Substring(1); continue; }
                if (continued.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                    return continued.Substring("Main-Class:".Length).Trim();
                continued = line;
            }
            if (continued.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                return continued.Substring("Main-Class:".Length).Trim();
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ArgValue(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return null;
        return args[i + 1];
    }

    /// <summary>client jar 是否 bundler 格式(内嵌库 + versions.list)。</summary>
    private static bool IsBundler(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            if (zip.GetEntry("META-INF/versions.list") != null) return true;
            var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
            if (manifest == null) return false;
            using var reader = new StreamReader(manifest.Open());
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (line.StartsWith("Bundler-Format:", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool AllOutputsPresent(
        JObject processor, Dictionary<string, string> data, string librariesRoot,
        string launcherRoot, string versionsRoot, string gameVersion)
    {
        if (processor["outputs"] is not JObject outputs || outputs.Count == 0) return false;
        foreach (var kv in outputs.Properties())
        {
            var path = Expand(kv.Name, data, librariesRoot, launcherRoot, versionsRoot, gameVersion);
            if (path.Contains("{") || path.Contains("[")) continue;
            if (!File.Exists(path)) return false;
        }
        return true;
    }

    private static string Quote(string s)
        => s.Length > 0 && s.IndexOf(' ') >= 0 && s[0] != '"' ? "\"" + s + "\"" : s;

    private static async Task<(int ExitCode, string Output)> RunJavaAsync(
        string javaExe, string arguments, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Java 进程");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (Exception) { } });

        var deadline = DateTime.UtcNow + timeout;
        while (!process.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                try { process.Kill(); } catch (Exception) { }
                ct.ThrowIfCancellationRequested();
            }
            if (DateTime.UtcNow > deadline)
            {
                try { process.Kill(); } catch (Exception) { }
                throw new TimeoutException("processor 运行超过 " + timeout.TotalMinutes + " 分钟,已终止");
            }
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }
}
