using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Auth;

/// <summary>设备码登录第一步返回的信息（用户在浏览器里输入此码）。</summary>
/// <remarks>
/// UserCode 是给人看的短码；DeviceCode 是轮询令牌端点时必须回传的长串。
/// 两者混淆会让轮询永远返回 bad_verification_code（曾经踩过）。
/// </remarks>
public sealed record DeviceCodeInfo(
    string UserCode, string DeviceCode, string VerificationUri, int ExpiresInSeconds, int PollIntervalSeconds);

/// <summary>正版登录完成后的完整身份。</summary>
public sealed record MicrosoftAuthResult(string PlayerName, Guid PlayerUuid, string AccessToken, string RefreshToken);

/// <summary>
/// Microsoft 正版登录六步链（FR-06 Could 通道）：
/// 设备码 OAuth → XBL → XSTS → Minecraft 令牌 → 所属权校验 → 玩家档案。
/// 刷新令牌由 TokenStore 以 DPAPI 加密保存，有效期内静默复用。
/// </summary>
public static class MicrosoftAccount
{
    /// <summary>Minecraft 官方启动器的公共客户端 ID（旧版 MSA 端点专用），可在设置中覆盖。</summary>
    /// <remarks>
    /// 这个 ID 只对 login.live.com 的 oauth20 端点有效；拿它去请求
    /// login.microsoftonline.com/consumers/oauth2/v2.0/devicecode 会得到
    /// AADSTS700016（应用不存在）——正版登录曾经因此完全不可用。
    /// </remarks>
    public const string DefaultClientId = "00000000402b5328";

    private const string Scope = "XboxLive.signin offline_access";
    private const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    private static readonly HttpClient Http = new();

    private static HttpRequestMessage Post(string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JObject.FromObject(body).ToString(), Encoding.UTF8, "application/json"),
        };
        return request;
    }

    /// <summary>OAuth 端点只认表单编码(JSON 会被拒),设备码/令牌/刷新都走这里。</summary>
    private static HttpRequestMessage PostForm(string url, Dictionary<string, string> form)
        => new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };

    private static async Task<JObject> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"微软服务返回 {(int)response.StatusCode}：{Truncate(text)}");
        return JObject.Parse(text);
    }

    /// <summary>表单 POST,非 2xx 也把 body 解析出来(设备码轮询的 400 是正常状态)。</summary>
    private static async Task<(JObject Json, bool Ok)> SendFormAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        JObject json;
        try { json = JObject.Parse(text); }
        catch (Exception) { json = new JObject(); }
        return (json, response.IsSuccessStatusCode);
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    /// <summary>第一步：向旧版 MSA 端点申请设备码。</summary>
    public static async Task<DeviceCodeInfo> StartDeviceCodeAsync(string? clientId, CancellationToken ct)
    {
        var json = await SendAsync(PostForm(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = clientId ?? DefaultClientId,
            ["scope"] = Scope,
            ["response_type"] = "device_code",
        }), ct).ConfigureAwait(false);

        var deviceCode = json.Value<string>("device_code") ?? "";
        if (string.IsNullOrEmpty(deviceCode))
            throw new InvalidOperationException("微软未返回设备码：" + Truncate(json.ToString()));

        return new DeviceCodeInfo(
            json.Value<string>("user_code") ?? "",
            deviceCode,
            json.Value<string>("verification_uri") ?? "https://www.microsoft.com/link",
            json.Value<int?>("expires_in") ?? 900,
            json.Value<int?>("interval") ?? 5);
    }

    /// <summary>第二步：轮询令牌端点直至用户在浏览器完成授权。</summary>
    public static async Task<(string AccessToken, string RefreshToken)> WaitForAuthorizationAsync(
        string? clientId, DeviceCodeInfo deviceCode, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deviceCode.DeviceCode))
            throw new InvalidOperationException("设备码无效，请重新登录");

        var interval = TimeSpan.FromSeconds(Math.Max(2, deviceCode.PollIntervalSeconds));
        var deadline = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresInSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval, ct).ConfigureAwait(false);

            // 未授权时端点返回 400 + {"error":"authorization_pending"}，属于正常等待
            var (json, _) = await SendFormAsync(PostForm(TokenUrl, new Dictionary<string, string>
            {
                ["client_id"] = clientId ?? DefaultClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode.DeviceCode,
            }), ct).ConfigureAwait(false);

            var error = json.Value<string>("error");
            if (error == "authorization_pending") continue;
            if (error == "slow_down")
            {
                interval = TimeSpan.FromSeconds(interval.TotalSeconds + 5);
                continue;
            }
            if (error == "expired_token")
                throw new TimeoutException("设备码已过期，请重新登录");
            if (error != null)
                throw new InvalidOperationException(
                    "微软授权失败：" + (json.Value<string>("error_description") ?? error));

            var access = json.Value<string>("access_token");
            var refresh = json.Value<string>("refresh_token");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
                throw new InvalidOperationException("微软授权响应缺少令牌");
            return (access!, refresh!);
        }
        throw new TimeoutException("设备码已过期，请重新登录");
    }

    /// <summary>用刷新令牌换取新的访问令牌（无 UI，静默）。</summary>
    public static async Task<(string AccessToken, string RefreshToken)> RefreshAsync(
        string refreshToken, string? clientId, CancellationToken ct)
    {
        var json = await SendAsync(PostForm(TokenUrl, new Dictionary<string, string>
        {
            ["client_id"] = clientId ?? DefaultClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        }), ct).ConfigureAwait(false);
        var access = json.Value<string>("access_token");
        var refresh = json.Value<string>("refresh_token") ?? refreshToken;
        if (string.IsNullOrEmpty(access)) throw new InvalidOperationException("刷新登录态失败：响应缺少令牌");
        return (access!, refresh);
    }

    /// <summary>第三至六步：XBL → XSTS → MC 令牌 → 所属权校验 → 玩家档案。</summary>
    public static async Task<MicrosoftAuthResult> CompleteLoginAsync(string microsoftAccessToken, CancellationToken ct)
    {
        // XBL
        var xbl = await SendAsync(Post("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + microsoftAccessToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }), ct).ConfigureAwait(false);
        var xblToken = xbl.Value<string>("Token") ?? throw new InvalidOperationException("XBL 登录失败：响应缺少 Token");

        // XSTS
        var xsts = await SendAsync(Post("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        }), ct).ConfigureAwait(false);
        var xstsToken = xsts.Value<string>("Token");
        if (string.IsNullOrEmpty(xstsToken))
        {
            var xerr = xsts.Value<long?>("XErr") ?? 0;
            throw new InvalidOperationException(xerr switch
            {
                2148916233 => "该微软账号没有 Xbox 档案，请先在 xbox.com 创建",
                2148916238 => "该账号是未成年人，无法登录（需家长账号授权）",
                2148916235 => "所在地区不允许此账号登录 Xbox 服务",
                _ => $"XSTS 授权失败（XErr={xerr}）",
            });
        }
        var uhs = xsts["DisplayClaims"]?["xui"]?.First?["uhs"]?.Value<string>()
                  ?? throw new InvalidOperationException("XSTS 响应缺少 UHS");

        // Minecraft 令牌
        var mc = await SendAsync(Post("https://api.minecraftservices.com/authentication/login_with_xbox", new
        {
            identityToken = $"XBL3.0 x={uhs};{xstsToken}",
        }), ct).ConfigureAwait(false);
        var mcToken = mc.Value<string>("access_token")
                      ?? throw new InvalidOperationException("Minecraft 令牌获取失败");

        // 所属权校验
        using var entRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/entitlements/mcstore");
        entRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
        var entitlements = await SendAsync(entRequest, ct).ConfigureAwait(false);
        var itemCount = entitlements["items"]?.Count() ?? 0;
        if (itemCount == 0)
            throw new InvalidOperationException("该账号未拥有 Minecraft（未购买且不在 XGP 授权内）");

        // 玩家档案
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
        var profile = await SendAsync(profileRequest, ct).ConfigureAwait(false);
        var name = profile.Value<string>("name")
                   ?? throw new InvalidOperationException("尚未创建 Minecraft 玩家档案（先启动一次原版游戏创建）");
        var idRaw = profile.Value<string>("id") ?? throw new InvalidOperationException("档案缺少 UUID");
        var uuid = Guid.Parse(idRaw);
        return new MicrosoftAuthResult(name, uuid, mcToken, string.Empty);
    }

    /// <summary>刷新令牌换正版档案（管线登录节点用，无 UI）。</summary>
    public static async Task<MicrosoftAuthResult> LoginByRefreshTokenAsync(
        string refreshToken, string? clientId, CancellationToken ct)
    {
        var (access, newRefresh) = await RefreshAsync(refreshToken, clientId, ct).ConfigureAwait(false);
        var result = await CompleteLoginAsync(access, ct).ConfigureAwait(false);
        return result with { RefreshToken = newRefresh };
    }
}
