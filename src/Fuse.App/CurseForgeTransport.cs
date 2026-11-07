using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Sources;

namespace Fuse.App;

/// <summary>
/// Phase 1 传输层:Key 内嵌在程序集里,每次请求临时解包,用完清零。
/// 以后换代理只需改 Configure 的实现,业务代码不动。
/// </summary>
internal sealed class EmbeddedKeyTransport : ICurseForgeTransport
{
    private readonly HttpClient _http;

    public EmbeddedKeyTransport(HttpClient http) => _http = http;

    /// <summary>构建时是否注入了 Key。</summary>
    public bool Available
    {
        get
        {
            var key = CfKey.Unwrap();
            if (key == null) return false;
            Array.Clear(key, 0, key.Length);
            return true;
        }
    }

    public async Task<string> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var key = CfKey.Unwrap()
                  ?? throw new InvalidOperationException("CurseForge API 未启用（构建时未注入 Key）");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                CurseForgeSource.ApiBase + pathAndQuery);
            request.Headers.TryAddWithoutValidation("x-api-key", Encoding.ASCII.GetString(key));
            request.Headers.UserAgent.ParseAdd("FuseLauncher/" + AppServices.AppVersion);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if ((int)response.StatusCode == 429)
                throw new InvalidOperationException("CurseForge 请求过于频繁，请稍后重试");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }
    }
}
