using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Accounts;

/// <summary>
/// authlib-injector.jar 的获取与缓存。
/// 官方分发接口 https://authlib-injector.yushi.moe/artifact/latest.json 给出最新版下载地址;
/// 拿不到时退回 GitHub Releases。下载后按版本名缓存,后续启动直接复用。
/// </summary>
public static class AuthlibInjector
{
    private const string LatestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
    private const string GitHubApi = "https://api.github.com/repos/yushijinhun/authlib-injector/releases/latest";

    /// <summary>确保本地有 injector,返回 jar 的绝对路径。失败抛异常(调用方弹"坏菜了"并取消启动)。</summary>
    public static async Task<string> EnsureAsync(HttpClient http, string cacheDir, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir);

        // 1. 找一个可用的下载地址 + 版本号
        string? downloadUrl = null;
        string? version = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await http.GetAsync(LatestUrl, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                version = json.Value<string>("version");
                downloadUrl = json.Value<string>("download_url");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _ = ex;
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await http.GetAsync(GitHubApi, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            version = (json.Value<string>("tag_name") ?? "latest").TrimStart('v');
            downloadUrl = (json["assets"] as JArray)?
                .OfType<JObject>()
                .Select(a => a.Value<string>("browser_download_url"))
                .FirstOrDefault(u => u != null && u.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException("无法获取 authlib-injector 下载地址");

        // 2. 已缓存则直接用
        var fileName = $"authlib-injector-{(string.IsNullOrEmpty(version) ? "latest" : version)}.jar";
        var target = Path.Combine(cacheDir, fileName);
        if (File.Exists(target) && new FileInfo(target).Length > 100_000) return target;

        // 3. 下载(不限速、可重试;失败抛错让上层取消启动)
        var downloader = new SmallFileDownloader(http);
        var result = await downloader.DownloadSetAsync(new[]
        {
            new FileDownloadItem { Url = downloadUrl!, DestPath = target },
        }, new SmallFileOptions { Parallelism = 1 }, null, ct).ConfigureAwait(false);
        if (result.Failed.Count > 0 || !File.Exists(target))
            throw new IOException("authlib-injector 下载失败:" + (result.Failed.FirstOrDefault() ?? downloadUrl));
        return target;
    }
}
