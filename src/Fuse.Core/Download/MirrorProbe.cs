using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Download;

/// <summary>
/// 下载源测速:启动时并发探一次官方与各镜像的响应时间,之后所有加载器请求直接按实测最快的源发起,
/// 不再"先等官方 8 秒超时再换镜像"。测速结果按资源类型分开缓存(forge / neoforge / fabric)。
/// </summary>
public static class MirrorProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, List<string>> Ranked = new(StringComparer.OrdinalIgnoreCase);
    private static Task? _probing;

    /// <summary>每类资源待测的源(第一个是官方)。</summary>
    private static readonly (string Key, string[] Sources, string ProbePath)[] Targets =
    {
        ("forge", new[]
        {
            "https://maven.minecraftforge.net",
            "https://bmclapi2.bangbang93.com/maven",
            "https://mirrors4.qlu.edu.cn/bmclapi/maven",
        }, "/net/minecraftforge/forge/maven-metadata.xml"),

        ("neoforge", new[]
        {
            "https://maven.neoforged.net/releases",
            "https://bmclapi2.bangbang93.com/maven",
            "https://mirrors4.qlu.edu.cn/bmclapi/maven",
        }, "/net/neoforged/neoforge/maven-metadata.xml"),

        ("fabric", new[]
        {
            "https://meta.fabricmc.net",
            "https://bmclapi2.bangbang93.com/fabric-meta",
        }, "/v2/versions/loader/1.20.1"),
    };

    /// <summary>启动测速(幂等,重复调用不会重测)。</summary>
    public static void StartProbe(HttpClient http)
    {
        lock (Sync)
        {
            if (_probing != null) return;
            _probing = Task.Run(() => ProbeAllAsync(http));
        }
    }

    /// <summary>测速是否已有结果。</summary>
    public static bool HasResult(string key)
    {
        lock (Sync) return Ranked.ContainsKey(key);
    }

    /// <summary>
    /// 按实测速度返回源列表(最快在前)。榜单里没有的源按原始顺序追加在后面,
    /// 保证"测速失败也能正常下载"。
    /// </summary>
    public static IReadOnlyList<string> Order(string key, IEnumerable<string> sources)
    {
        var all = sources.ToList();
        lock (Sync)
        {
            if (!Ranked.TryGetValue(key, out var ranked)) return all;
            var ordered = new List<string>();
            foreach (var url in ranked)
            {
                // 榜单里存的是"基础域名"(如 .../fabric-meta),调用方传的是带路径的完整地址
                // (如 .../fabric-meta/v2/versions/loader),必须按前缀匹配,否则永远匹配不上,
                // 排序形同虚设 → 每次仍先撞官方慢源(实测官方 30s+,镜像 0.4s)。
                var baseUrl = url.TrimEnd('/');
                var match = all.FirstOrDefault(s =>
                {
                    var t = s.TrimEnd('/');
                    return string.Equals(t, baseUrl, StringComparison.OrdinalIgnoreCase)
                           || t.StartsWith(baseUrl + "/", StringComparison.OrdinalIgnoreCase);
                });
                if (match != null && !ordered.Contains(match)) ordered.Add(match);
            }
            foreach (var url in all)
                if (!ordered.Contains(url)) ordered.Add(url);
            return ordered;
        }
    }

    /// <summary>当前各资源的最快源(调试/日志用)。</summary>
    public static string? Fastest(string key)
    {
        lock (Sync)
            return Ranked.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : null;
    }

    private static async Task ProbeAllAsync(HttpClient http)
    {
        using var client = new HttpClient { Timeout = ProbeTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FuseLauncher/1.0.0 Java/17.0.15 (Windows 10; x64; minecraft launcher)");

        // 全并发会互相抢带宽(实测 neoforge 三项全超时),限制并发 4 更接近真实单请求速度
        using var gate = new SemaphoreSlim(4);
        var tasks = Targets.Select(async t =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { return await MeasureAsync(client, t).ConfigureAwait(false); }
            finally { gate.Release(); }
        }).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        lock (Sync)
        {
            foreach (var (key, ranked) in results)
            {
                if (ranked.Count == 0) continue;
                Ranked[key] = ranked;
                Log($"{key}: {string.Join(" > ", ranked)}");
            }
        }
    }

    /// <summary>测速结果写 boot.log,便于确认"到底选了哪个源"。</summary>
    private static void Log(string message)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuseLauncher");
            System.IO.Directory.CreateDirectory(dir);
            Fuse.Core.Logging.LogSink.Append(System.IO.Path.Combine(dir, "boot.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [probe] {message}" + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }

    private static async Task<(string Key, List<string> Ranked)> MeasureAsync(
        HttpClient client, (string Key, string[] Sources, string ProbePath) target)
    {
        var timings = new List<(string Url, long Ms)>();

        var probes = target.Sources.Select(async src =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync(src + target.ProbePath).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return ((string Url, long Ms)?)null;
                // 读完响应体,避免只测到连接建立时间
                await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                sw.Stop();
                return (src, sw.ElapsedMilliseconds);
            }
            catch (Exception)
            {
                return null;
            }
        }).ToArray();

        var done = await Task.WhenAll(probes).ConfigureAwait(false);
        foreach (var item in done)
            if (item.HasValue) timings.Add(item.Value);

        return (target.Key, timings.OrderBy(t => t.Ms).Select(t => t.Url).ToList());
    }
}
