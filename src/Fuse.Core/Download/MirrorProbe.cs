using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Download;

/// <summary>
/// 下载源测速。先 Range 0-0 筛掉死源，再拉 256KB 算真实带宽，
/// 按带宽 90% + 延迟 10% 排序；结果缓存 3 分钟，过期后后台重测。
/// </summary>
public static class MirrorProbe
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(12);
    private const int SampleBytes = 256 * 1024;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, RankedEntry> Ranked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, RankedIndexEntry> UrlRanked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Refreshing = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ProbeGate = new(4);
    private static HttpClient? _http;
    private static Task? _probing;

    private sealed class RankedEntry
    {
        public List<string> Ordered = new();
        public DateTime At;
    }

    private sealed class RankedIndexEntry
    {
        public List<int> Order = new();
        public DateTime At;
    }

    /// <summary>每类资源待测的源(第一个是官方)。</summary>
    private static readonly (string Key, string[] Sources, string ProbePath)[] Targets =
    {
        ("forge", new[]
        {
            "https://maven.minecraftforge.net",
            "https://bmclapi2.bangbang93.com/maven",
            "https://mirrors.cernet.edu.cn/bmclapi",
            "https://mirrors4.qlu.edu.cn/bmclapi/maven",
        }, "/net/minecraftforge/forge/maven-metadata.xml"),

        ("neoforge", new[]
        {
            "https://maven.neoforged.net/releases",
            "https://bmclapi2.bangbang93.com/maven",
            "https://mirrors.cernet.edu.cn/bmclapi",
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
            _http = http;
            if (_probing != null) return;
            _probing = Task.Run(() => ProbeAllAsync());
        }
    }

    /// <summary>测速是否已有结果。</summary>
    public static bool HasResult(string key)
    {
        lock (Sync)
            return Ranked.TryGetValue(key, out var entry) && !IsStale(entry.At);
    }

    /// <summary>
    /// 按实测速度返回源列表(最快在前)。榜单里没有的源按原始顺序追加在后面,
    /// 保证"测速失败也能正常下载"。
    /// </summary>
    public static IReadOnlyList<string> Order(string key, IEnumerable<string> sources)
    {
        var all = sources.ToList();
        RankedEntry? entry;
        var refresh = false;
        lock (Sync)
        {
            Ranked.TryGetValue(key, out entry);
            if (entry == null || IsStale(entry.At))
                refresh = _http != null && Refreshing.Add(key);
        }
        if (refresh) _ = Task.Run(() => RefreshTargetAsync(key));
        return entry == null ? all : ApplyOrder(entry.Ordered, all);
    }

    /// <summary>当前各资源的最快源(调试/日志用)。</summary>
    public static string? Fastest(string key)
    {
        lock (Sync)
            return Ranked.TryGetValue(key, out var entry) && entry.Ordered.Count > 0 ? entry.Ordered[0] : null;
    }

    /// <summary>
    /// 按类别/主机缓存的实测顺序。取样用当前要下载的地址(不同文件路径不同),
    /// 3 分钟内复用;没有结果时返回 null,调用方走默认顺序。
    /// </summary>
    public static IReadOnlyList<string>? TryOrderForUrl(
        string key, IReadOnlyList<string> candidates)
    {
        if (candidates.Count <= 1) return candidates;
        RankedIndexEntry? entry;
        var refresh = false;
        lock (Sync)
        {
            UrlRanked.TryGetValue(key, out entry);
            if (entry == null || IsStale(entry.At))
                refresh = _http != null && Refreshing.Add(key);
        }
        if (refresh) _ = Task.Run(() => RefreshUrlAsync(key, candidates));
        if (entry == null || entry.Order.Count == 0) return null;

        var ordered = new List<string>(candidates.Count);
        foreach (var index in entry.Order)
            if (index >= 0 && index < candidates.Count && !ordered.Contains(candidates[index]))
                ordered.Add(candidates[index]);
        foreach (var url in candidates)
            if (!ordered.Contains(url)) ordered.Add(url);
        return ordered;
    }

    private static bool IsStale(DateTime at) => DateTime.UtcNow - at >= Ttl;

    private static IReadOnlyList<string> ApplyOrder(IReadOnlyList<string> ranked, List<string> all)
    {
        var ordered = new List<string>();
        foreach (var url in ranked)
        {
            // 榜单里存的是"基础域名"(如 .../fabric-meta),调用方传的是带路径的完整地址
            // (如 .../fabric-meta/v2/versions/loader),必须按前缀匹配,否则永远匹配不上。
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

    private static async Task ProbeAllAsync()
    {
        var tasks = Targets.Select(t => Task.Run(async () =>
        {
            lock (Sync)
            {
                if (!Refreshing.Add(t.Key)) return;
            }
            await RefreshTargetAsync(t.Key).ConfigureAwait(false);
        })).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task RefreshTargetAsync(string key)
    {
        var target = Targets.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (target.Key == null)
        {
            lock (Sync) Refreshing.Remove(key);
            return;
        }

        try
        {
            if (_http == null) return;
            var urls = target.Sources.Select(s => s + target.ProbePath).ToArray();
            var order = await MeasureAsync(urls).ConfigureAwait(false);
            var ordered = order.Select(i => target.Sources[i]).ToList();
            lock (Sync)
            {
                Ranked[key] = new RankedEntry { Ordered = ordered, At = DateTime.UtcNow };
            }
            if (ordered.Count > 1)
                Log($"{key}: " + string.Join(" > ", ordered));
        }
        catch (Exception ex)
        {
            Log($"{key}: probe failed {ex.Message}");
            lock (Sync)
            {
                Ranked[key] = new RankedEntry { At = DateTime.UtcNow };
            }
        }
        finally
        {
            lock (Sync) Refreshing.Remove(key);
        }
    }

    private static async Task RefreshUrlAsync(string key, IReadOnlyList<string> candidates)
    {
        try
        {
            if (_http == null) return;
            var order = await MeasureAsync(candidates.ToArray()).ConfigureAwait(false);
            lock (Sync)
            {
                // 全失败也记一条空结果,3 分钟内不要反复测同一个地址
                UrlRanked[key] = new RankedIndexEntry { Order = order, At = DateTime.UtcNow };
            }
            if (order.Count > 1)
                Log($"{key}: " + string.Join(" > ", order.Select(i => candidates[i])));
        }
        catch (Exception ex)
        {
            Log($"{key}: probe failed {ex.Message}");
            lock (Sync)
            {
                UrlRanked[key] = new RankedIndexEntry { At = DateTime.UtcNow };
            }
        }
        finally
        {
            lock (Sync) Refreshing.Remove(key);
        }
    }

    /// <summary>返回按得分降序的候选索引。先筛死源,再采样真实带宽。</summary>
    private static async Task<List<int>> MeasureAsync(string[] urls)
    {
        var http = _http;
        if (http == null || urls.Length == 0) return new List<int>();

        var latencies = new double?[urls.Length];
        var probes = urls.Select((url, index) => Task.Run(async () =>
        {
            await ProbeGate.WaitAsync().ConfigureAwait(false);
            try { latencies[index] = await PreflightAsync(http, url).ConfigureAwait(false); }
            finally { ProbeGate.Release(); }
        })).ToArray();
        await Task.WhenAll(probes).ConfigureAwait(false);

        var alive = new List<(int Index, double LatencyMs)>();
        for (var i = 0; i < urls.Length; i++)
            if (latencies[i].HasValue) alive.Add((i, latencies[i]!.Value));
        if (alive.Count == 0) return new List<int>();
        if (alive.Count == 1) return new List<int> { alive[0].Index };

        var samples = new (long Bytes, double Kbps)?[alive.Count];
        var workers = alive.Select((item, slot) => Task.Run(async () =>
        {
            await ProbeGate.WaitAsync().ConfigureAwait(false);
            try { samples[slot] = await SampleAsync(http, urls[item.Index]).ConfigureAwait(false); }
            finally { ProbeGate.Release(); }
        })).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        var ok = new List<(int Index, double LatencyMs, double Kbps)>();
        for (var slot = 0; slot < alive.Count; slot++)
            if (samples[slot].HasValue)
                ok.Add((alive[slot].Index, alive[slot].LatencyMs, samples[slot]!.Value.Kbps));
        if (ok.Count == 0) return alive.Select(a => a.Index).ToList();

        var maxKbps = Math.Max(1, ok.Max(s => s.Kbps));
        var minLatency = Math.Max(1, ok.Min(s => s.LatencyMs));
        return ok
            .Select(s => (
                s.Index,
                Score: 0.9 * (s.Kbps / maxKbps) + 0.1 * (minLatency / Math.Max(1, s.LatencyMs))))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Index)
            .ToList();
    }

    /// <summary>只读 1 字节,确认源活着并记下握手+首字节延迟。</summary>
    private static async Task<double?> PreflightAsync(HttpClient http, string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(PreflightTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            var watch = Stopwatch.StartNew();
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[1];
            await stream.ReadAsync(buffer, 0, 1, cts.Token).ConfigureAwait(false);
            watch.Stop();
            return Math.Max(1, watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>拉最多 256KB,算真实 KB/s;源不支持 Range 时也只读这么多就断开。</summary>
    private static async Task<(long Bytes, double Kbps)?> SampleAsync(HttpClient http, string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(SampleTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, SampleBytes - 1);
            var watch = Stopwatch.StartNew();
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (total < SampleBytes)
            {
                var want = (int)Math.Min(buffer.Length, SampleBytes - total);
                var read = await stream.ReadAsync(buffer, 0, want, cts.Token).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }
            watch.Stop();
            if (total == 0) return null;
            var ms = Math.Max(1, watch.Elapsed.TotalMilliseconds);
            return (total, total / ms);
        }
        catch (Exception)
        {
            return null;
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
}
