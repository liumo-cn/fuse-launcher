using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Accounts;
using Fuse.Core.Download;
using Fuse.Core.Java;
using Fuse.Core.Pipeline;
using Fuse.Core.Versioning;

namespace Fuse.Core.Launch;

/// <summary>启动入参（FR-07）。</summary>
public sealed class LaunchOptions
{
    /// <summary>版本目录（含各版本 version.json），如 .minecraft\versions。</summary>
    public required string VersionsRoot { get; init; }
    public required string VersionId { get; init; }

    /// <summary>游戏根目录（.minecraft）；资源与存档根。</summary>
    public required string GameDirectory { get; init; }
    /// <summary>库目录（libraries）。</summary>
    public required string LibrariesDirectory { get; init; }
    /// <summary>资源目录（assets），可全局共享。</summary>
    public required string AssetsRoot { get; init; }

    /// <summary>显式指定 Java；null 则自动按官方 javaVersion.majorVersion 挑选。</summary>
    public string? JavaHomeOverride { get; init; }
    /// <summary>Java 运行时下载根目录（FR-05）；非 null 时允许缺 Java 自动从官方 java-runtime 清单安装。</summary>
    public string? JavaRuntimesRoot { get; init; }
    /// <summary>缺 Java 时是否自动下载官方运行时（PRD：经用户确认；外壳传用户设置）。</summary>
    public bool AutoInstallJava { get; init; } = true;
    /// <summary>诊断开关：无视本机已装 Java，强制走官方运行时下载路径。</summary>
    public bool IgnoreInstalledJava { get; init; }
    public long MaxHeapMb { get; init; } = 4096;
    public string? ServerAddress { get; init; }
    public bool Fullscreen { get; init; }
    public int WindowWidth { get; init; } = 854;
    public int WindowHeight { get; init; } = 480;

    /// <summary>等待游戏窗口出现的超时秒数（以窗口出现为启动成功判据）。</summary>
    public int WindowTimeoutSeconds { get; init; } = 120;

    /// <summary>额外 JVM 参数(如外置登录的 -javaagent:authlib-injector.jar=apiRoot),插在主类之前。</summary>
    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = Array.Empty<string>();

    /// <summary>启动前自动修复回调（缺失/损坏文件静默补齐），null 表示跳过。</summary>
    public Func<McVersion, Task>? AutoRepair { get; init; }
}

/// <summary>启动结果：以「游戏窗口出现」为成功判据（FR-07 节点 10）。</summary>
public sealed record LaunchResult(Process GameProcess, IReadOnlyList<string> JvmArgs, IReadOnlyList<string> GameArgs, JavaRuntime Java);

/// <summary>
/// 启动管线（PRD 9.2）：预检查 → 选 Java → 登录 → 自动修复 → 提取 natives →
/// 组装参数 → 拉起进程 → 等待窗口。离线与正版共用本管线，仅登录节点不同。
/// </summary>
public sealed class LaunchPipeline
{
    private readonly HttpClient _http;

    /// <summary>
    /// 打开启动日志。上一局刚关掉时游戏进程可能还占着 latest-launch.log,
    /// 直接 File.Create 会抛"正由另一进程使用"并被判成启动崩溃 —— 这里重试,
    /// 仍不行就换带时间戳的文件名;用 FileShare.ReadWrite 打开,导出日志时也能读。
    /// </summary>
    private static FileStream OpenLaunchLog(string path)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(250);
            }
        }
        var fallback = Path.Combine(Path.GetDirectoryName(path)!, $"launch-{DateTime.Now:HHmmss}.log");
        return new FileStream(fallback, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
    }

    public LaunchPipeline(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public IAuthProvider AuthProvider { get; set; } = new OfflineAuthProvider("Player");

    public async Task<LaunchResult> LaunchAsync(
        LaunchOptions options,
        IProgress<LoaderProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var combo = new LoaderCombo<Context>()
            .Then("预检查", 1, (ctx, ct) =>
            {
                ctx.Version = VersionJsonLoader.Load(options.VersionsRoot, options.VersionId);
                if (string.IsNullOrEmpty(ctx.Version.MainClass))
                    throw new InvalidOperationException($"版本 {options.VersionId} 缺少 mainClass");
                Directory.CreateDirectory(options.GameDirectory);
                Directory.CreateDirectory(NativesDir(options));
                return Task.CompletedTask;
            })
            .Then("选择 Java", 1, async (ctx, ct) =>
            {
                var required = ctx.Version!.JavaVersion?.MajorVersion ?? 8;

                // 1. 手动指定优先
                if (!string.IsNullOrEmpty(options.JavaHomeOverride))
                {
                    // 只有 java.exe、缺 server/jvm.dll 的运行时会在启动时弹
                    // "missing server JVM" 系统窗口,这里提前给明确错误
                    if (!JavaLocator.HasServerJvm(options.JavaHomeOverride!))
                        throw new InvalidOperationException(
                            "手动指定的 Java 不完整(缺少 bin\\server\\jvm.dll):" + options.JavaHomeOverride);
                    var ver = JavaLocator.ReadVersion(options.JavaHomeOverride!);
                    ctx.Java = new JavaRuntime
                    {
                        Home = options.JavaHomeOverride!,
                        VersionString = ver,
                        MajorVersion = JavaLocator.ParseMajor(ver),
                        Source = "手动指定",
                    };
                    return;
                }

                // 2. 本机扫描（已装 Java + 已下载的官方运行时）
                var found = new List<JavaRuntime>();
                if (!options.IgnoreInstalledJava)
                    found.AddRange(JavaLocator.FindInstalled());
                if (options.JavaRuntimesRoot != null)
                    found.AddRange(JavaLocator.ScanRuntimesRoot(options.JavaRuntimesRoot));
                var picked = JavaLocator.Pick(found, required);

                // 3. FR-05：缺 Java 且允许时，自动下载 Mojang 官方 java-runtime（溯源官方）
                if (picked == null && options.JavaRuntimesRoot != null && options.AutoInstallJava)
                {
                    var entries = await MojangJavaRuntime.FetchCatalogAsync(_http, ct).ConfigureAwait(false);
                    var entry = MojangJavaRuntime.Pick(entries, ctx.Version!.JavaVersion?.Component, required)
                                ?? throw new InvalidOperationException($"官方清单中没有 Java {required} 的运行时");
                    var dest = Path.Combine(options.JavaRuntimesRoot, entry.Component);
                    picked = await MojangJavaRuntime.DownloadAsync(_http, entry, dest,
                        new CallbackProgress(p => progress?.Report(new LoaderProgress("选择 Java", p, $"下载 {entry.VersionName}"))),
                        ct).ConfigureAwait(false);
                }

                ctx.Java = picked
                    ?? throw new InvalidOperationException($"本机未找到 Java {required}，请先在设置中安装或手动指定");
            })
            .Then("登录", 1, async (ctx, ct) =>
            {
                ctx.Auth = await AuthProvider.GetAuthAsync(ct).ConfigureAwait(false);
            });

        if (options.AutoRepair != null)
        {
            combo = combo.Then("自动修复", 2, async (ctx, ct) =>
            {
                if (options.AutoRepair != null) await options.AutoRepair(ctx.Version!).ConfigureAwait(false);
            });
        }

        combo = combo
            .Then("提取 Natives", 1, (ctx, ct) =>
            {
                NativesExtractor.Extract(ctx.Version!, options.LibrariesDirectory, NativesDir(options));
                return Task.CompletedTask;
            })
            .Then("组装参数", 1, (ctx, ct) =>
            {
                var version = ctx.Version!;
                var profile = BuildProfile(options, version, ctx.Auth!);
                profile.JavaMajor = ctx.Java!.MajorVersion; // GC/编码参数按实际所选 Java 生成
                var nativesDir = NativesDir(options);
                profile.NativesDirectory = nativesDir;
                // 安装器把 log4j 配置落在实例目录；无论清单是否有 path 字段都指向该位置
                profile.LoggingConfigFile = version.LoggingClient != null
                    ? Path.Combine(options.GameDirectory, "log4jConfig.xml")
                    : null;
                var (jvm, game) = ArgumentBuilder.Build(version, profile);
                if (options.ExtraJvmArgs.Count > 0)
                    jvm = jvm.Concat(options.ExtraJvmArgs).ToList();
                ctx.JvmArgs = jvm;
                ctx.GameArgs = game;
                ctx.Profile = profile;
                return Task.CompletedTask;
            })
            .Then("启动游戏", 1, (ctx, ct) =>
            {
                // 优先 javaw.exe(无控制台窗口),桌面不弹命令行
                var javaExe = File.Exists(ctx.Java!.JavaWExecutable)
                    ? ctx.Java.JavaWExecutable
                    : ctx.Java.JavaExecutable;
                if (!File.Exists(javaExe))
                    throw new FileNotFoundException($"所选 Java 不存在：{javaExe}");

                var fullArgs = CommandLineBuilder.Build(ctx.Version!, ctx.JvmArgs!, ctx.GameArgs!);
                var launchLogPath = Path.Combine(options.GameDirectory, "logs", "latest-launch.log");
                Directory.CreateDirectory(Path.GetDirectoryName(launchLogPath)!);
                ctx.LaunchLog = new StreamWriter(OpenLaunchLog(launchLogPath)) { AutoFlush = true };
                ctx.LaunchLog.WriteLine($"[cmd] {javaExe} {fullArgs}");

                var psi = new ProcessStartInfo
                {
                    FileName = javaExe,
                    Arguments = fullArgs,
                    WorkingDirectory = options.GameDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // 不要把启动器的密钥类环境变量继承给游戏进程(模组能读到)
                try
                {
                    psi.EnvironmentVariables.Remove("CURSEFORGE_API_KEY");
                }
                catch (Exception)
                {
                    // 非关键路径
                }
                ctx.GameProcess = Process.Start(psi)
                    ?? throw new InvalidOperationException("游戏进程启动失败（Process.Start 返回 null）");
                ctx.GameProcess.OutputDataReceived += (_, e) => { if (e.Data != null) ctx.LaunchLog.WriteLine(e.Data); };
                ctx.GameProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) ctx.LaunchLog.WriteLine(e.Data); };
                ctx.GameProcess.BeginOutputReadLine();
                ctx.GameProcess.BeginErrorReadLine();
                return Task.CompletedTask;
            })
            .Then("等待游戏窗口", 3, async (ctx, ct) =>
            {
                var process = ctx.GameProcess!;
                var deadline = TimeSpan.FromSeconds(options.WindowTimeoutSeconds);
                var start = Stopwatch.StartNew();
                while (start.Elapsed < deadline)
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"游戏进程在 {start.Elapsed.TotalSeconds:0} 秒后退出（退出码 {process.ExitCode}）。" +
                            $"游戏输出见：{Path.Combine(options.GameDirectory, "logs", "latest-launch.log")}");
                    }
                    if (HasVisibleWindow(process))
                        return;
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                throw new TimeoutException(
                    $"等待游戏窗口超时（{options.WindowTimeoutSeconds}s）。游戏输出见：" +
                    Path.Combine(options.GameDirectory, "logs", "latest-launch.log"));
            });

        var context = new Context();
        try
        {
            await combo.RunAsync(context, progress, cancellation).ConfigureAwait(false);
            return new LaunchResult(context.GameProcess!, context.JvmArgs!, context.GameArgs!, context.Java!);
        }
        finally
        {
            // 游戏进程接管后无需再写启动日志；失败路径保留日志供排查
            if (context.LaunchLog != null && context.GameProcess is { HasExited: true })
                context.LaunchLog.Dispose();
        }
    }

    private static string NativesDir(LaunchOptions options)
        => Path.Combine(options.GameDirectory, "bin", options.VersionId);

    private static LaunchProfile BuildProfile(LaunchOptions options, McVersion version, AuthResult auth)
    {
        var jarId = version.Jar ?? version.Id;
        var clientJar = Path.Combine(options.VersionsRoot, jarId, jarId + ".jar");
        return new LaunchProfile
        {
            GameDirectory = options.GameDirectory,
            AssetsRoot = options.AssetsRoot,
            LibrariesDirectory = options.LibrariesDirectory,
            ClientJarPath = clientJar,
            PlayerName = auth.PlayerName,
            PlayerUuid = auth.PlayerUuid,
            AccessToken = auth.AccessToken,
            UserType = auth.UserType,
            VersionName = version.Id,
            VersionType = version.Type,
            MaxHeapMb = options.MaxHeapMb,
            ServerAddress = options.ServerAddress,
            Fullscreen = options.Fullscreen,
            WindowWidth = options.WindowWidth,
            WindowHeight = options.WindowHeight,
        };
    }

    /// <summary>netstandard2.0 的 Process 未必暴露 MainWindowHandle，反射探测并缓存。</summary>
    private static bool HasVisibleWindow(Process process)
    {
        try
        {
            var handle = process.MainWindowHandle;
            return handle != IntPtr.Zero;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            // 平台不支持窗口句柄（非 GUI 环境）：退化为「进程存活即视为就绪」
            return true;
        }
    }

    private sealed class Context
    {
        public McVersion? Version;
        public JavaRuntime? Java;
        public AuthResult? Auth;
        public IReadOnlyList<string>? JvmArgs;
        public IReadOnlyList<string>? GameArgs;
        public LaunchProfile? Profile;
        public Process? GameProcess;
        public StreamWriter? LaunchLog;
    }
}
