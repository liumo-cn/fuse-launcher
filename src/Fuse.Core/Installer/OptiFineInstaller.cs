using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// OptiFine 安装器。
///
/// OptiFine 没有 maven、也没有可静态提取的 version.json —— 官方只提供 installer.jar,
/// 必须真的运行它才能把补丁打进客户端 jar。做法:
/// ① 下载 installer(BMCLAPI 直链,文件较大且源不稳定,超时给到 5 分钟);
/// ② 把 APPDATA 指到一个临时目录再跑 <c>java -jar installer.jar --installClient</c>,
///    这样它会装进 temp\.minecraft 而不是污染用户真正的 .minecraft;
/// ③ 从 temp\.minecraft\versions 里取出生成的 &lt;mc&gt;-OptiFine_&lt;ver&gt; 目录,复制到自己的 versionsRoot。
/// </summary>
public static class OptiFineInstaller
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>安装。返回生成的版本 id。</summary>
    public static async Task<string> InstallAsync(
        HttpClient http, string javaExe, string mcVersion, Sources.OptiFineVersion version,
        string versionsRoot, string librariesRoot, string tempRoot,
        IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(tempRoot);
        var jarPath = Path.Combine(tempRoot, version.FileName);

        // 1. 下载安装器(已存在且非空则复用)
        if (!File.Exists(jarPath) || new FileInfo(jarPath).Length < 100_000)
        {
            progress?.Report(0.05);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DownloadTimeout);
            using var response = await http.GetAsync(
                Sources.OptiFineSource.DownloadUrl(version), cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            // netstandard2.0 没有 WriteAllBytesAsync
            await Task.Run(() => File.WriteAllBytes(jarPath, bytes), ct).ConfigureAwait(false);
        }
        progress?.Report(0.55);

        // 2. 在临时 APPDATA 下静默安装
        var fakeAppData = Path.Combine(tempRoot, "appdata");
        var fakeMc = Path.Combine(fakeAppData, ".minecraft");
        Directory.CreateDirectory(Path.Combine(fakeMc, "versions"));
        // OptiFine 安装器要在 <appdata>\.minecraftersions 里找到原版底座才能打补丁,
        // 空的假 .minecraft 会直接失败,所以先把原版版本目录复制过去。
        var vanillaDir = Path.Combine(versionsRoot, mcVersion);
        if (Directory.Exists(vanillaDir))
        {
            var seedDir = Path.Combine(fakeMc, "versions", mcVersion);
            Directory.CreateDirectory(seedDir);
            foreach (var file in Directory.EnumerateFiles(vanillaDir, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(vanillaDir.Length).TrimStart(Path.DirectorySeparatorChar);
                var dest = Path.Combine(seedDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }
        var fakeProfiles = Path.Combine(fakeMc, "launcher_profiles.json");
        if (!File.Exists(fakeProfiles))
            File.WriteAllText(fakeProfiles, "{\"profiles\":{},\"settings\":{},\"version\":3}");
        var exitCode = await RunInstallerAsync(javaExe, jarPath, fakeAppData, ct).ConfigureAwait(false);
        if (exitCode != 0)
            throw new IOException($"OptiFine 安装器退出码 {exitCode}(可能缺少 Java 或安装器需要交互)");
        progress?.Report(0.85);

        // 3. 取产物
        var producedVersions = Path.Combine(fakeAppData, ".minecraft", "versions");
        if (!Directory.Exists(producedVersions))
            throw new DirectoryNotFoundException("OptiFine 安装器没有生成 versions 目录");

        var dir = Directory.GetDirectories(producedVersions)
            .FirstOrDefault(d => Path.GetFileName(d).IndexOf("OptiFine", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? throw new DirectoryNotFoundException("OptiFine 安装器没有生成版本目录");

        var id = Path.GetFileName(dir);
        var target = Path.Combine(versionsRoot, id);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = file.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        // 安装器把 OptiFine / launchwrapper-of 两个库放在假 .minecraft\libraries 下,
        // 必须一并搬到自己的库目录,否则启动时 classpath 缺 launchwrapper(KnotClient 式的 ClassNotFound)。
        var fakeLibs = Path.Combine(fakeAppData, ".minecraft", "libraries");
        if (Directory.Exists(fakeLibs))
        {
            foreach (var file in Directory.EnumerateFiles(fakeLibs, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(fakeLibs.Length).TrimStart(Path.DirectorySeparatorChar);
                var dest = Path.Combine(librariesRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }

        progress?.Report(1.0);
        return id;
    }

    private static async Task<int> RunInstallerAsync(
        string javaExe, string jarPath, string fakeAppData, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            // 注意:optifine 的 -jar 主类是 InstallerFrame(GUI,只认点击),
            // 静默安装入口是 optifine.Installer.main —— 它用工作目录/APPDATA 下的 .minecraft。
            Arguments = $"-Djava.awt.headless=true -cp \"{jarPath}\" optifine.Installer",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = fakeAppData,
        };
        // 关键:让安装器认为 .minecraft 在临时目录里
        psi.EnvironmentVariables["APPDATA"] = fakeAppData;

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("无法启动 Java 进程");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
        });

        // netstandard2.0 的 Process 没有 WaitForExitAsync,用轮询实现可取消的等待
        while (!process.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                try { process.Kill(); } catch (Exception) { }
                ct.ThrowIfCancellationRequested();
            }
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
        }
        var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0 && output.Length > 0)
            throw new IOException("OptiFine 安装器输出:" + output.Substring(0, Math.Min(400, output.Length)));
        return process.ExitCode;
    }
}
