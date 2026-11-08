using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fuse.Core.Versioning;

/// <summary>启动配置：一个实例一次启动所需的全部输入。</summary>
public sealed class LaunchProfile
{
    public string GameDirectory { get; set; } = "";
    public string AssetsRoot { get; set; } = "";
    public string NativesDirectory { get; set; } = "";
    public string LibrariesDirectory { get; set; } = "";
    public string ClientJarPath { get; set; } = "";

    public string PlayerName { get; set; } = "Player";
    public Guid PlayerUuid { get; set; }
    /// <summary>离线模式用占位令牌即可，正版为微软访问令牌。</summary>
    public string AccessToken { get; set; } = "0";
    /// <summary>"msa"（正版）或 "legacy"（离线）。</summary>
    public string UserType { get; set; } = "legacy";

    public string LauncherName { get; set; } = "FuseLauncher";
    public string LauncherVersion { get; set; } = "0.1.0";

    public string VersionName { get; set; } = "";
    /// <summary>version_type 展示名（如 "原版 1.20.1"）；空则占位替换为空串。</summary>
    public string? VersionType { get; set; }

    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;
    /// <summary>启用后游戏参数才会带上分辨率（官方 features.has_custom_resolution 门控）。</summary>
    public bool CustomResolution { get; set; }
    public bool Fullscreen { get; set; }

    public long MaxHeapMb { get; set; } = 4096;
    /// <summary>所选 Java 的主版本号，驱动 GC 与编码参数。</summary>
    public int JavaMajor { get; set; } = 17;

    /// <summary>快速进服：host 或 host:port；null 表示单人启动。</summary>
    public string? ServerAddress { get; set; }

    /// <summary>log4j 配置文件落地路径（version.json logging.client 需要）。</summary>
    public string? LoggingConfigFile { get; set; }

    /// <summary>额外特性开关（如 is_demo_user）。</summary>
    public IDictionary<string, bool>? ExtraFeatures { get; set; }
}

/// <summary>
/// 依据官方 version.json 组装 JVM 与游戏参数：规则过滤 → 占位符替换 → GC/编码调优。
/// 组装顺序与官方启动器语义对齐（R7 风险项，由金样本测试守护）。
/// </summary>
public static class ArgumentBuilder
{
    private static readonly Regex PlaceholderPattern = new(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    public static RuleEnvironment EnvironmentOf(LaunchProfile p)
    {
        var env = new RuleEnvironment();
        if (p.CustomResolution) env.Features.Add("has_custom_resolution");
        // 官方语义：多人快速进服通过特性开关启用规则门控的 quickPlay 参数
        if (!string.IsNullOrEmpty(p.ServerAddress)) env.Features.Add("is_quick_play_multiplayer");
        if (p.ExtraFeatures != null)
            foreach (var kv in p.ExtraFeatures)
                if (kv.Value) env.Features.Add(kv.Key);
        return env;
    }

    /// <summary>规则过滤后 libraries 的 classpath（含 natives classifier 库），主 jar 在最后。</summary>
    public static string BuildClasspath(McVersion version, LaunchProfile profile, RuleEnvironment env)
    {
        var parts = new List<string>();
        // 派生版本(Forge 等)与原版的库清单大量重叠(jna/asm 等),合并后同一路径会出现两次。
        // Forge 1.17+ 的 BootstrapLauncher 构建 UnionFileSystem 时以路径为 key 收集,
        // 重复即抛 "Duplicate key" 直接退出码 1,所以必须按路径去重。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var forgeBootstrap = (version.MainClass ?? "").IndexOf("forge.bootstrap", StringComparison.OrdinalIgnoreCase) >= 0;

        // 新版 Forge/NeoForge 的 arguments.jvm 自带 -p(模块路径,例如 asm 9.8)。
        // 这些 jar 若同时出现在 classpath,JDK 会报
        // "Module named org.objectweb.asm was already on the JVMs module path"。
        // 按文件名把模块路径上的 jar 从 classpath 剔除(避开 ${library_directory} 占位符)。
        var onModulePath = ModulePathFileNames(version);
        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.Evaluate(lib.Rules, env)) continue;
            if (lib.Artifact == null || string.IsNullOrEmpty(lib.Artifact.Path)) continue;
            // Forge 1.21+ 的 forge-<ver>-client.jar 是补丁层(只有 32 个覆盖类),
            // 上 classpath 会和 official 客户端 jar 的包冲突,必须走 --patch-module
            if (forgeBootstrap && lib.Artifact.Path.EndsWith("-client.jar", StringComparison.OrdinalIgnoreCase)
                && lib.Name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (onModulePath.Contains(ArtifactKey(Path.GetFileName(lib.Artifact.Path)))) continue;
            var full = Path3(profile.LibrariesDirectory, lib.Artifact.Path);
            if (seen.Add(full)) parts.Add(full);
        }
        // Forge 1.21+ 改用 net.minecraftforge.bootstrap.ForgeBootstrap:它不走 -DlibraryDirectory,
        // 而是在安全的 classpath 里找 net/minecraft/client/Minecraft.class。生成的
        // client-<mc>-official.jar(processors 产物)不在 version.json 的库里,必须自己补进去。
        var mainClass = version.MainClass ?? "";
        if (mainClass.IndexOf("forge.bootstrap", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var mc = string.IsNullOrEmpty(version.Jar) ? version.Id : version.Jar!;
            var official = Path3(profile.LibrariesDirectory,
                $"net/minecraft/client/{mc}/client-{mc}-official.jar");
            if (File.Exists(official) && seen.Add(official)) parts.Add(official);
        }

        // Forge/NeoForge 1.17+ 由 BootstrapLauncher 启动:游戏类全来自 client-srg 库,
        // 启动器再塞原版 jar 会被当成自动模块(_1._19._4),与 srg jar 的 minecraft 模块撞包
        // ("module minecraft contains package ... module _1._19._4 exports ...")。
        // 官方 launcher 传的是版本目录自己的 <id>.jar(被 -DignoreList 排除),我们直接不传。
        var bootstrapped = version.MainClass != null &&
            version.MainClass.IndexOf("bootstraplauncher", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!bootstrapped && seen.Add(profile.ClientJarPath)) parts.Add(profile.ClientJarPath);
        return string.Join(";", parts);    }

    /// <summary>
    /// arguments.jvm 里 -p/--module-path 指向的 jar 文件名集合。
    /// 注意:JSON 里 "-p" 和它的值可能拆成两条参数对象,必须先把所有值摊平再配对。
    /// </summary>
    private static HashSet<string> ModulePathFileNames(McVersion version)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jvm = version.Arguments?.Jvm;
        if (jvm == null) return names;

        var flat = jvm.SelectMany(a => a.Values).ToList();
        for (var k = 0; k < flat.Count - 1; k++)
        {
            if (flat[k] != "-p" && flat[k] != "--module-path") continue;
            // 模块路径用 ${classpath_separator} 占位符串联(替换后才变成 ';')
            var value = flat[k + 1].Replace("${classpath_separator}", ";");
            foreach (var piece in value.Split(';'))
            {
                var name = piece.Replace((char)92, '/');
                var slash = name.LastIndexOf('/');
                if (slash >= 0) name = name.Substring(slash + 1);
                if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) names.Add(ArtifactKey(name));
            }
        }
        return names;
    }

    /// <summary>JVM 参数:官方 arguments.jvm(或旧版默认)+ 日志配置 + 内存 + GC + 编码。</summary>
    public static IReadOnlyList<string> BuildJvmArgs(McVersion version, LaunchProfile profile, RuleEnvironment env)
    {
        var args = new List<string>();
        var forgeBootstrapMain = (version.MainClass ?? "").IndexOf("forge.bootstrap", StringComparison.OrdinalIgnoreCase) >= 0;

        var modern = version.Arguments?.Jvm;
        if (modern is { Count: > 0 })
        {
            foreach (var arg in modern)
            {
                if (!RuleEvaluator.Evaluate(arg.Rules, env)) continue;
                args.AddRange(arg.Values);
            }
        }
        else
        {
            // 1.12- 无 arguments.jvm 时的默认 JVM 参数
            args.Add("-Djava.library.path=${natives_directory}");
            args.Add("-cp");
            args.Add("${classpath}");
        }

        if (!string.IsNullOrEmpty(version.LoggingClient?.Argument))
            args.Add(version.LoggingClient!.Argument);

        // Forge 1.21+:把补丁层叠加到 minecraft 模块上(等同于官方安装器的做法)
        if (forgeBootstrapMain)
        {
            var patch = FindForgeClientPatch(version, profile);
            if (patch != null)
            {
                args.Add("--patch-module");
                args.Add("minecraft=" + patch);
            }
        }

        args.Insert(0, $"-Xmx{profile.MaxHeapMb}M");
        // 老 mod 常用 HttpURLConnection 在主线程拉远程配置(MoBends 会连
        // raw.githubusercontent.com),站点被墙时按系统默认要等 21 秒,看起来就是卡死
        args.Add("-Dsun.net.client.defaultConnectTimeout=5000");
        args.Add("-Dsun.net.client.defaultReadTimeout=10000");
        args.AddRange(BuildGcArgs(profile.JavaMajor));
        args.AddRange(BuildEncodingArgs(profile.JavaMajor));
        return args;
    }

    /// <summary>游戏参数：官方 arguments.game 或旧版 minecraftArguments，另加进服/全屏。</summary>
    public static IReadOnlyList<string> BuildGameArgs(McVersion version, LaunchProfile profile, RuleEnvironment env)
    {
        var args = new List<string>();
        var modern = version.Arguments?.Game;
        var hasQuickPlay = false;

        if (modern is { Count: > 0 })
        {
            foreach (var arg in modern)
            {
                if (!RuleEvaluator.Evaluate(arg.Rules, env)) continue;
                args.AddRange(arg.Values);
            }
            // quickPlay 参数已由特性规则带入，占位符直接填值
            hasQuickPlay = args.Contains("${quickPlayMultiplayer}");
        }
        else if (!string.IsNullOrEmpty(version.MinecraftArguments))
        {
            args.AddRange(version.MinecraftArguments!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        if (profile.Fullscreen)
            args.Add("--fullscreen");

        if (!string.IsNullOrEmpty(profile.ServerAddress) && !hasQuickPlay)
        {
            // 版本未内置 quickPlay 规则（2023-04-04 之前）时手动追加 --server/--port
            var (host, port) = SplitServer(profile.ServerAddress!);
            args.Add("--server");
            args.Add(host);
            args.Add("--port");
            args.Add(port.ToString());
        }
        return args;
    }

    /// <summary>替换全部占位符；未知/未提供的占位符替换为空串（与官方启动器一致）。</summary>
    public static IReadOnlyList<string> ReplacePlaceholders(
        IEnumerable<string> args, McVersion version, LaunchProfile profile, RuleEnvironment env)
    {
        var dict = PlaceholderDictionary(version, profile);
        return args.Select(a => PlaceholderPattern.Replace(a, m => dict.TryGetValue(m.Groups[1].Value, out var v) ? v : ""))
            .ToList();
    }

    /// <summary>一步到位：返回 (JvmArgs, GameArgs)，全部完成占位符替换。</summary>
    public static (IReadOnlyList<string> Jvm, IReadOnlyList<string> Game) Build(
        McVersion version, LaunchProfile profile)
    {
        var env = EnvironmentOf(profile);
        var jvm = ReplacePlaceholders(BuildJvmArgs(version, profile, env), version, profile, env);
        var game = ReplacePlaceholders(BuildGameArgs(version, profile, env), version, profile, env);
        return (jvm, game);
    }

    /// <summary>
    /// 去掉版本号的构件名:"asm-9.6.jar"/"asm-9.8.jar" → "asm"。
    /// 模块路径上已有 asm 9.8 时,classpath 里再带 asm 9.6 也会撞自动模块名
    /// (org.objectweb.asm),所以必须按构件名而不是文件名比对。
    /// </summary>
    private static string ArtifactKey(string fileName)
    {
        return VersionSuffix.Replace(fileName, "");
    }

    private static readonly Regex VersionSuffix = new(@"-\d[\w.\-]*\.jar$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Forge 1.21+ 的 client 补丁层路径(不上 classpath,用 --patch-module 叠加)。</summary>
    private static string? FindForgeClientPatch(McVersion version, LaunchProfile profile)
    {
        foreach (var lib in version.Libraries)
        {
            if (lib.Artifact == null || string.IsNullOrEmpty(lib.Artifact.Path)) continue;
            if (!lib.Name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!lib.Artifact.Path.EndsWith("-client.jar", StringComparison.OrdinalIgnoreCase)) continue;
            return Path3(profile.LibrariesDirectory, lib.Artifact.Path);
        }
        return null;
    }

    // ---------- 内部 ----------

    private static IEnumerable<string> BuildGcArgs(int javaMajor)
    {
        if (javaMajor >= 21)
        {
            // 分代 ZGC（JDK 21+）
            yield return "-XX:+UseZGC";
            yield return "-XX:+ZGenerational";
        }
        else if (javaMajor >= 15)
        {
            yield return "-XX:+UseZGC";
        }
        else
        {
            // 调优 G1（常用调优参数）。G1NewSizePercent 等属实验参数,
            // Java 8-14 必须先用 UnlockExperimentalVMOptions 解锁,否则 JVM 拒绝启动
            // ("VM option 'G1NewSizePercent' is experimental ...")。
            yield return "-XX:+UnlockExperimentalVMOptions";
            yield return "-XX:+UseG1GC";
            yield return "-XX:+ParallelRefProcEnabled";
            // 老 mod 会主动 System.gc(),8G 堆上一次显式 Full GC 能卡好几秒
            yield return "-XX:+DisableExplicitGC";
            yield return "-XX:G1NewSizePercent=20";
            yield return "-XX:G1ReservePercent=20";
            yield return "-XX:MaxGCPauseMillis=50";
            yield return "-XX:G1HeapRegionSize=16M";
        }
    }

    private static IEnumerable<string> BuildEncodingArgs(int javaMajor)
    {
        if (javaMajor >= 19)
        {
            // JDK 18+ 默认 UTF-8；显式声明输出编码避免控制台乱码
            yield return "-Dstdout.encoding=UTF-8";
            yield return "-Dstderr.encoding=UTF-8";
        }
        else
        {
            // 老版本 JVM 在中文 Windows 下缺省 GBK，游戏聊天/日志需 GB18030 兼容
            yield return "-Dfile.encoding=GB18030";
        }
    }

    private static Dictionary<string, string> PlaceholderDictionary(McVersion version, LaunchProfile p)
    {
        var assetIndexId = version.AssetIndex?.Id ?? version.Assets ?? "";
        return new Dictionary<string, string>
        {
            ["launcher_name"] = p.LauncherName,
            ["launcher_version"] = p.LauncherVersion,
            ["classpath"] = BuildClasspath(version, p, EnvironmentOf(p)),
            ["classpath_separator"] = ";",
            ["library_directory"] = p.LibrariesDirectory,
            ["natives_directory"] = p.NativesDirectory,
            ["auth_player_name"] = p.PlayerName,
            ["auth_uuid"] = p.PlayerUuid.ToString("D"),
            ["auth_access_token"] = p.AccessToken,
            ["auth_session"] = p.AccessToken,
            ["auth_xuid"] = "",
            ["clientid"] = "",
            ["user_type"] = p.UserType,
            ["user_properties"] = "{}",
            ["version_name"] = p.VersionName,
            ["version_type"] = p.VersionType ?? "",
            ["game_directory"] = p.GameDirectory,
            ["assets_root"] = p.AssetsRoot,
            ["assets_index_name"] = assetIndexId,
            ["resolution_width"] = p.CustomResolution ? p.WindowWidth.ToString() : "",
            ["resolution_height"] = p.CustomResolution ? p.WindowHeight.ToString() : "",
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = p.ServerAddress ?? "",
            ["quickPlayRealms"] = "",
            ["path"] = p.LoggingConfigFile ?? "",
        };
    }

    private static (string Host, int Port) SplitServer(string address)
    {
        var idx = address.LastIndexOf(':');
        if (idx > 0 && int.TryParse(address[(idx + 1)..], out var port))
            return (address[..idx], port);
        return (address, 25565);
    }

    private static string Path3(string root, string relative)
        => string.IsNullOrEmpty(root) ? relative : System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
}

/// <summary>
/// 组装完整命令行：JVM 参数 → 主类 → 游戏参数（官方顺序，主类缺失是致命错误——
/// JVM 会把游戏参数当成自己的选项直接拒绝启动）。
/// -cp 的值（含分号的长路径串）强制加引号，防止路径含空格时断裂。
/// </summary>
public static class CommandLineBuilder
{
    public static string Build(McVersion version, IReadOnlyList<string> jvm, IReadOnlyList<string> game)
    {
        var sb = new System.Text.StringBuilder();
        AppendArgs(sb, jvm);
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(WindowsArgQuoter.Quote(version.MainClass));
        foreach (var arg in game)
        {
            sb.Append(' ');
            sb.Append(WindowsArgQuoter.Quote(arg));
        }
        return sb.ToString();
    }

    private static void AppendArgs(System.Text.StringBuilder sb, IEnumerable<string> args)
    {
        var forceQuoteNext = false;
        foreach (var arg in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(forceQuoteNext ? WindowsArgQuoter.Quote(arg, alwaysQuote: true) : WindowsArgQuoter.Quote(arg));
            forceQuoteNext = arg == "-cp";
        }
    }
}

/// <summary>Windows 命令行引号规则（CRT 解析），用于把参数列表合成单串（netstandard2.0 无 ArgumentList）。</summary>
public static class WindowsArgQuoter
{
    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(a => Quote(a)));

    public static string Quote(string arg, bool alwaysQuote = false)
    {
        if (arg.Length == 0) return "\"\"";
        if (!alwaysQuote && !arg.Contains(' ') && !arg.Contains('\t') && !arg.Contains('"'))
            return arg;

        var sb = new System.Text.StringBuilder();
        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in arg)
        {
            switch (ch)
            {
                case '\\':
                    backslashes++;
                    break;
                case '"':
                    sb.Append('\\', backslashes * 2 + 1);
                    backslashes = 0;
                    sb.Append('"');
                    break;
                default:
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                    sb.Append(ch);
                    break;
            }
        }
        // 结尾前的连续反斜杠在收引号时需要翻倍
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
