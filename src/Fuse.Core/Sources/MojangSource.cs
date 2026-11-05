using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Fuse.Core.Versioning;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Sources;

/// <summary>官方版本清单中的一项。</summary>
public sealed record MojangVersionEntry(string Id, string Type, string ReleaseTime, string Url, string Sha1);

/// <summary>
/// Mojang 官方源（PRD 13.2 主源，文件溯源官方）：
/// 版本清单 + 按官方 SHA-1 校验落盘 version.json。
/// </summary>
public static class MojangSource
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public static async Task<List<MojangVersionEntry>> FetchManifestAsync(HttpClient http, CancellationToken ct)
    {
        // 多源重试:清单改写按 attempt 在 HKMC ↔ BMCLAPI 之间轮换(HKMC 挂掉时
        // 单次尝试会直接失败——2026-11-04 实测 HKMC 不可达导致下载页版本列表卡死)。
        // 每次尝试 12s 超时;3 次仍失败才抛,由调用方给重试入口。
        Exception? last = null;
        foreach (var attempt in new[] { 0, 1, 3 })   // 0=HKMC, 1=BMCLAPI, 3=BMCLAPI(偶数会回到 HKMC)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(12));
                using var response = await http.GetAsync(Download.UrlMirror.ForAttempt(ManifestUrl, attempt), HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                return json["versions"]!.Cast<JObject>()
                    .Select(v => new MojangVersionEntry(
                        v.Value<string>("id") ?? "",
                        v.Value<string>("type") ?? "",
                        v.Value<string>("releaseTime") ?? "",
                        v.Value<string>("url") ?? "",
                        v.Value<string>("sha1") ?? ""))
                    .Where(e => !string.IsNullOrEmpty(e.Id))
                    .ToList();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException("清单源超时(" + (attempt + 1) + "/3)");
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TimeoutException || ex is IOException)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException("清单获取失败(已尝试多个镜像源):" + last?.Message, last);
    }

    public static IReadOnlyList<MojangVersionEntry> Filter(
        IEnumerable<MojangVersionEntry> manifest, string query, string typeFilter)
    {
        return manifest
            .Where(v => typeFilter == "all" || v.Type == typeFilter)
            .Where(v => string.IsNullOrEmpty(query) || v.Id.IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(v => ParseReleaseDate(v.ReleaseTime))
            .Take(40)
            .ToList();
    }

    private static DateTime ParseReleaseDate(string? releaseTime)
    {
        // ISO 字符串可直接排序,但为防格式漂移,显式解析为日期
        return DateTime.TryParse(releaseTime, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTime.MinValue;
    }

    /// <summary>下载官方 version.json（SHA-1 强校验）到 versions/&lt;id&gt;/&lt;id&gt;.json 并解析。</summary>
    public static async Task<McVersion> DownloadVersionJsonAsync(
        HttpClient http, MojangVersionEntry entry, string versionsRoot, CancellationToken ct)
    {
        var dir = Path.Combine(versionsRoot, entry.Id);
        Directory.CreateDirectory(dir);
        var destPath = Path.Combine(dir, entry.Id + ".json");

        if (File.Exists(destPath))
        {
            if (await Hashing.VerifyFileAsync(destPath, entry.Sha1, HashAlgo.Sha1, ct).ConfigureAwait(false))
                return VersionJsonLoader.Load(versionsRoot, entry.Id);
            File.Delete(destPath);
        }

        // 清单可能来自镜像(HKMC 会把自己的 mirror.hkmc.online 写进 url 字段),
        // 那个地址下的 /v1/packages 并不是原始 json。用 sha1 重组官方规范地址再交给镜像改写。
        var jsonUrl = string.IsNullOrEmpty(entry.Sha1)
            ? entry.Url
            : $"https://piston-meta.mojang.com/v1/packages/{entry.Sha1}/{entry.Id}.json";

        // 版本 json 逐个源尝试:BMCLAPI /v1/packages 0.9s,官方 piston-meta 有时更快,
        // 任一失败换下一个,全失败才抛(内容最后用 sha1 强校验)
        HttpResponseMessage? response = null;
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            HttpResponseMessage? resp = null;
            try
            {
                // 单次 45 秒超时:镜像偶发"连接建立了但不发数据",没有超时会一直卡住
                // (表现为任务进度 0、json 文件被锁在半截,用户看到"下载不动")
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(45));
                var candidate = Download.UrlMirror.ForAttempt(jsonUrl, attempt);
                resp = await http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) { response = resp; break; }
                var status = (int)resp.StatusCode;
                resp.Dispose();
                resp = null;
                lastError = new HttpRequestException($"{status} {candidate}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                resp?.Dispose();
                lastError = ex;
            }
        }
        if (response == null)
            throw new HttpRequestException("下载版本 json 失败", lastError);
        response.EnsureSuccessStatusCode();
        // 先写 .tmp 再原子替换:别的任务可能同时在读这个 json(见加载器安装的并发场景)
        var tmpPath = destPath + ".tmp";
        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var target = File.Create(tmpPath))
        {
            // 响应体也要有上限:慢速涓流时 CopyToAsync 只受 HttpClient 默认超时(100s)约束
            using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyCts.CancelAfter(TimeSpan.FromSeconds(45));
            await stream.CopyToAsync(target, 64 * 1024, bodyCts.Token).ConfigureAwait(false);
        }
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmpPath, destPath);
        if (!await Hashing.VerifyFileAsync(destPath, entry.Sha1, HashAlgo.Sha1, ct).ConfigureAwait(false))
        {
            File.Delete(destPath);
            throw new IOException("官方版本 JSON 校验不通过（SHA-1 不匹配），可能被篡改");
        }
        return VersionJsonLoader.Load(versionsRoot, entry.Id);
    }
}
