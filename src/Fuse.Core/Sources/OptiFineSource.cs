using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Sources;

/// <summary>OptiFine 的一个可用版本。</summary>
public sealed record OptiFineVersion(
    string McVersion, string Type, string Patch, string FileName, string? ForgeVersion)
{
    /// <summary>显示用名称:OptiFine 1.20.1 HD_U I6。</summary>
    public string Display => $"OptiFine {McVersion} {Type} {Patch}";
}

/// <summary>
/// OptiFine 版本源(BMCLAPI)。官方 optifine.net 没有 API,只有网页表单,
/// 所以列表走 BMCLAPI 的接口;安装包也用它的下载地址。
/// </summary>
public static class OptiFineSource
{
    public const string ApiBase = "https://bmclapi2.bangbang93.com/optifine";

    /// <summary>某个 MC 版本可用的 OptiFine 列表(新→旧)。</summary>
    public static async Task<List<OptiFineVersion>> FetchVersionsAsync(
        HttpClient http, string mcVersion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/{mcVersion}");
        request.Headers.UserAgent.ParseAdd("FuseLauncher/1.0.0 Java/17.0.15 (Windows 10; x64; minecraft launcher)");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var arr = JArray.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        var parsed = arr.OfType<JObject>()
            .Select(o => new OptiFineVersion(
                o.Value<string>("mcversion") ?? mcVersion,
                o.Value<string>("type") ?? "HD_U",
                o.Value<string>("patch") ?? "",
                o.Value<string>("filename") ?? "",
                o.Value<string>("forge")))
            .Where(v => v.Patch.Length > 0 && v.Type.Length > 0)
            .ToList();

        // 接口是「旧→新」且正式版与 preview 混排;正式版全部排前面,各自的内部顺序反过来
        static bool IsPreview(OptiFineVersion v) =>
            v.Patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase);
        return parsed.Where(v => !IsPreview(v)).Reverse()
            .Concat(parsed.Where(IsPreview).Reverse())
            .ToList();
    }

    /// <summary>安装包直链。</summary>
    public static string DownloadUrl(OptiFineVersion v) => $"{ApiBase}/{v.McVersion}/{v.Type}/{v.Patch}";
}
