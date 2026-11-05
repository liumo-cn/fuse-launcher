using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Fuse.Core.Download;
using Fuse.Core.Logging;
using Newtonsoft.Json.Linq;

namespace Fuse.App;

/// <summary>
/// 启动器自更新:拉取远端清单(版本号 / 下载地址 / SHA-256),下载新版安装器到 %TEMP%,
/// 校验通过后以静默模式重装(payload 覆盖程序文件,保留用户 config),完成后自动重启。
///
/// 清单格式(JSON):
/// {
///   "version": "1.0.1",
///   "url": "https://cdn.example.com/fuse/FuseLauncher-Setup-1.0.1.exe",
///   "sha256": "…64 位小写…",
///   "sha1": "…(可选，仅为兼容旧客户端)…",
///   "size": 52428800,
///   "notes": "…"
/// }
///
/// 安全要点:
/// · **强制 https**:清单与安装包都必须是 https,且不允许重定向降到 http;
/// · **sha256 必填**:缺失即拒绝更新 —— 未启用代码签名时,哈希是唯一的完整性锚点;
///   sha1 只作为旧清单的兼容回退,命中时会写一条警告日志;
/// · **同源约束**:安装包地址必须与清单同源。没有签名证书的情况下,
///   这是"改清单也不能指向第三方域名"的唯一保障;
/// · **大小上限**:清单 64KB、安装包 300MB,超限直接中止(防塞满磁盘);
/// · **运行中的 exe 绝不被覆盖** —— 本进程退出后由隐藏的接力进程再跑安装器;
/// · 接力脚本的路径经**环境变量**传入,不再拼进 PowerShell 源码:
///   路径里带单引号(例如用户名 O'Brien)不会再破坏脚本,也没有拼接注入面。
/// </summary>
internal static class Updater
{
    private const int CheckTimeoutMs = 15_000;      // 清单拉取超时
    private const int DownloadTimeoutMs = 240_000;  // 安装包下载超时(给慢网络留余地)
    private const int ManifestMaxBytes = 64 * 1024; // 清单上限:正常清单不到 1KB
    private const long PackageMaxBytes = 300L * 1024 * 1024; // 安装包上限

    /// <summary>版本号合法形态:2~4 段纯数字(不允许预发布后缀,便于比较与拼文件名)。</summary>
    private static readonly Regex VersionRe = new Regex(@"^[0-9]+(\.[0-9]+){1,3}$", RegexOptions.Compiled);

    /// <summary>并发闸门:同一时间只允许一个检查/下载/安装流程。</summary>
    private static int _active;

    public static bool TryBeginUpdate() => Interlocked.Exchange(ref _active, 1) == 0;
    public static void EndUpdate() => Interlocked.Exchange(ref _active, 0);

    public sealed class Manifest
    {
        public Manifest(string version, string url, string? sha256, string? sha1, long size, string? notes, string sourceHost)
        {
            Version = version;
            Url = url;
            Sha256 = sha256;
            Sha1 = sha1;
            Size = size;
            Notes = notes;
            SourceHost = sourceHost;
        }

        public string Version { get; }
        public string Url { get; }
        public string? Sha256 { get; }
        public string? Sha1 { get; }
        public long Size { get; }
        public string? Notes { get; }
        /// <summary>清单所在主机的域名,用于校验安装包同源。</summary>
        public string SourceHost { get; }
    }

    /// <summary>当前配置的更新源;空/null 视为未配置。</summary>
    public static string? ConfiguredUrl
    {
        get
        {
            var url = AppServices.Config.UpdateManifestUrl;
            return string.IsNullOrWhiteSpace(url) ? null : url!.Trim();
        }
    }

    // ---------------- 版本号 ----------------

    /// <summary>版本号是否合法(2~4 段纯数字,可带 v 前缀)。</summary>
    public static bool IsValidVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        return VersionRe.IsMatch(NormalizeVersion(v)!);
    }

    /// <summary>
    /// 语义比较:remote 比 current 新才返回 true。
    /// 缺的位补 0 —— 否则 <see cref="Version.CompareTo"/> 会认为
    /// "1.0.1" 比 "1.0.1.0" 小,导致更新完仍然提示有新版(旧实现就有这个问题)。
    /// </summary>
    public static bool IsNewer(string? remote, string? current)
    {
        if (!TryParseVersion(remote, out var r)) return false;
        if (!TryParseVersion(current, out var c)) return false;
        return r.CompareTo(c) > 0;
    }

    private static bool TryParseVersion(string? raw, out Version result)
    {
        result = new Version(0, 0, 0, 0);
        if (!IsValidVersion(raw)) return false;
        var parts = NormalizeVersion(raw)!.Split('.');
        var nums = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return false;
        }
        result = new Version(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    private static string? NormalizeVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return v;
        v = v!.Trim();
        if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V')) v = v.Substring(1);
        return v;
    }

    // ---------------- URL 校验 ----------------

    private static Uri RequireHttps(string raw, string what)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(what + "不是合法地址:" + raw);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(what + "必须是 https(当前 " + uri.Scheme + ")");
        return uri;
    }

    /// <summary>跟随重定向之后必须仍是 https —— 否则 https→http 的降级会绕过校验。</summary>
    private static void EnsureFinalHttps(HttpResponseMessage resp, string what)
    {
        var final = resp.RequestMessage?.RequestUri;
        if (final == null) return;
        if (!string.Equals(final.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(what + "被重定向到非 https 地址(" + final.Scheme + "),已中止");
    }

    // ---------------- 清单 ----------------

    public static async Task<Manifest> CheckAsync()
    {
        var raw = ConfiguredUrl ?? throw new InvalidOperationException("未配置更新源");
        var uri = RequireHttps(raw, "更新清单地址");

        using var cts = new CancellationTokenSource(CheckTimeoutMs);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        // 清单必须现取:CND/代理缓存会在发版后让用户长时间看不到新版本
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

        using var resp = await AppServices.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        EnsureFinalHttps(resp, "更新清单");

        var body = await ReadTextCappedAsync(resp, ManifestMaxBytes, cts.Token).ConfigureAwait(false);
        var json = JObject.Parse(body);

        var version = ((string?)json["version"])?.Trim();
        var url = ((string?)json["url"])?.Trim();
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("更新清单缺少 version/url");
        if (!IsValidVersion(version))
            throw new InvalidOperationException("更新清单的 version 不合法:" + version + "(只允许 2~4 段数字)");

        var size = (long?)json["size"] ?? 0;
        return new Manifest(
            version!,
            url!,
            ((string?)json["sha256"])?.Trim(),
            ((string?)json["sha1"])?.Trim(),
            size,
            (string?)json["notes"],
            uri.Host);
    }

    private static async Task<string> ReadTextCappedAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
    {
        var declared = resp.Content.Headers.ContentLength;
        if (declared.HasValue && declared.Value > maxBytes)
            throw new InvalidOperationException("响应过大(" + declared.Value + " 字节,上限 " + maxBytes + "),已中止");

        using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > maxBytes)
                throw new InvalidOperationException("响应超过上限 " + maxBytes + " 字节,已中止");
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ---------------- 下载 ----------------

    public static async Task<string> DownloadAsync(Manifest m, Action<int>? progress)
    {
        if (!IsValidVersion(m.Version))
            throw new InvalidOperationException("清单版本号不合法,拒绝下载:" + m.Version);

        var uri = RequireHttps(m.Url, "更新包地址");

        // 同源约束:没有代码签名时,这是"改清单也不能把包指向别处"的唯一保障
        if (!string.Equals(uri.Host, m.SourceHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "更新包必须与清单同源(清单 " + m.SourceHost + ",包 " + uri.Host + "),已拒绝");

        if (string.IsNullOrWhiteSpace(m.Sha256) && string.IsNullOrWhiteSpace(m.Sha1))
            throw new InvalidOperationException("清单没有 sha256,无法校验完整性,已拒绝更新");

        // 版本号已通过正则校验,拼进文件名不会有路径分隔符或非法字符
        var target = Path.Combine(Path.GetTempPath(), "FuseLauncher-Setup-" + m.Version + ".exe");
        var temp = target + ".tmp";

        try
        {
            using var cts = new CancellationTokenSource(DownloadTimeoutMs);
            using (var resp = await AppServices.Http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                EnsureFinalHttps(resp, "更新包");

                var total = resp.Content.Headers.ContentLength ?? -1;
                if (total > PackageMaxBytes)
                    throw new InvalidOperationException("安装包过大(" + Mb(total) + "MB,上限 " + Mb(PackageMaxBytes) + "MB)");
                if (m.Size > 0 && total > 0 && m.Size != total)
                    Log("manifest size=" + m.Size + " 与实际 Content-Length=" + total + " 不一致(以哈希校验为准)");

                using var source = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false)) > 0)
                {
                    read += n;
                    if (read > PackageMaxBytes)
                        throw new InvalidOperationException("安装包超过上限 " + Mb(PackageMaxBytes) + "MB,已中止");
                    await file.WriteAsync(buffer, 0, n, cts.Token).ConfigureAwait(false);
                    if (progress != null && total > 0)
                        progress((int)Math.Min(100, 100 * read / total));
                }
            }

            // 优先 sha256;没有才退回 sha1(旧清单),并明确记一条警告
            await VerifyPackageAsync(temp, m, cts.Token).ConfigureAwait(false);

            File.Copy(temp, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception) { }
        }
        return target;
    }

    /// <summary>
    /// 校验下载到的安装包。复用 Fuse.Core 的 Hashing(与下载器同一套实现),
    /// 避免两处各写一份哈希逻辑。sha256 优先;只有清单确实没给 sha256 时才退回 sha1。
    /// </summary>
    private static async Task VerifyPackageAsync(string file, Manifest m, CancellationToken ct)
    {
        var hasSha256 = !string.IsNullOrWhiteSpace(m.Sha256);
        var algo = hasSha256 ? HashAlgo.Sha256 : HashAlgo.Sha1;
        var name = hasSha256 ? "SHA-256" : "SHA-1";
        var expected = (hasSha256 ? m.Sha256! : m.Sha1!).Trim();

        if (!hasSha256)
        {
            Log("warn: 清单没有 sha256,回退到 SHA-1 校验 —— 请尽快把发布脚本升级为生成 sha256");
        }

        var actual = await Hashing.ComputeFileAsync(file, algo, ct).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "更新包校验失败(" + name + " 不匹配,期望 " + Short(expected) + "…,实际 " + Short(actual) + "…)");
        }
    }

    private static string Short(string s) => s.Length <= 12 ? s : s.Substring(0, 12);
    private static long Mb(long bytes) => bytes / (1024 * 1024);

    // ---------------- 接力安装 ----------------

    /// <summary>
    /// 静默重装并重启。本进程还占着 exe 文件锁,所以挂一个隐藏的 PowerShell
    /// (-EncodedCommand,UTF-16 Base64,中文路径不会像 cmd/ASCII 那样变成 ????):
    /// 结束启动器进程 → 静默安装(保留用户 config)→ /RUN 自动重启,然后本进程退出。
    ///
    /// 路径全部走**环境变量**,不拼进脚本源码:
    /// 旧实现把路径直接拼进单引号字符串,路径里带一个 ' (例如用户名 O'Brien、
    /// 或游戏目录 D:\Bob's Games)就会把脚本语法打断,更新静默失败且没有原因。
    /// 用环境变量后这些路径是"数据",既不会再破坏语句,也没有拼接注入面。
    ///
    /// 结束进程用 **PID** 而不是进程名:按名字杀会误杀同名的其它实例。
    /// </summary>
    public static void ApplyAndRestart(string setupPath)
    {
        if (!File.Exists(setupPath))
            throw new FileNotFoundException("更新包不存在:" + setupPath);
        Log("apply: " + setupPath);

        var appDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!;
        var gamesDir = AppServices.Config.InstallDirectory;
        var pid = Process.GetCurrentProcess().Id;

        var script = string.Join("; ",
            "Start-Sleep -Milliseconds 900",
            "$p = Get-Process -Id $env:FUSE_PID -ErrorAction SilentlyContinue; if ($p) { $p | Stop-Process -Force }",
            "Start-Sleep -Milliseconds 300",
            "$arg = '/SILENT /INSTALL=\"' + $env:FUSE_APPDIR + '\" /GAMES=\"' + $env:FUSE_GAMES + '\" /RUN'",
            "[System.Diagnostics.Process]::Start($env:FUSE_SETUP, $arg)");

        var psi = new ProcessStartInfo("powershell.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["FUSE_SETUP"] = setupPath;
        psi.EnvironmentVariables["FUSE_APPDIR"] = appDir;
        psi.EnvironmentVariables["FUSE_GAMES"] = gamesDir ?? string.Empty;
        psi.EnvironmentVariables["FUSE_PID"] = pid.ToString(CultureInfo.InvariantCulture);
        psi.Arguments = "-NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Process.Start(psi);
        Application.Current.Shutdown();
    }

    public static void Log(string message)
    {
        var dir = Path.GetDirectoryName(AppServices.ConfigPath)!;
        LogSink.Append(Path.Combine(dir, "boot.log"),
            "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [update] " + message + Environment.NewLine);
    }
}
