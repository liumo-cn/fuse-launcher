using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Accounts;

/// <summary>LittleSkin OAuth 设备码流程(RFC 8628)。</summary>
public static class LittleSkinOAuth
{
    /// <summary>LittleSkin OAuth 应用 client_id(默认 1688,可用环境变量覆盖)。</summary>
    public static string ClientId { get; private set; } = "1688";

    /// <summary>允许用环境变量覆盖 client_id,避免每次重置应用都要重新打包。</summary>
    public static void Configure(string? clientId)
    {
        if (!string.IsNullOrWhiteSpace(clientId)) ClientId = clientId!.Trim();
    }
    public const string ApiRoot = "https://littleskin.cn/api/yggdrasil";
    private const string DeviceEndpoint = "https://open.littleskin.cn/oauth/device_code";
    private const string TokenEndpoint = "https://open.littleskin.cn/oauth/token";
    private const string ProfileEndpoint = "https://littleskin.cn/api/yggdrasil/sessionserver/session/minecraft/profile";
    private const string MinecraftTokenEndpoint = "https://littleskin.cn/api/yggdrasil/authserver/oauth";

    /// <summary>启动器需要的权限。Select 让用户在网页授权时直接选角色。</summary>
    public const string Scope =
        "openid Yggdrasil.PlayerProfiles.Select Yggdrasil.MinecraftToken.Create offline_access";

    public sealed record DeviceCode(
        string Code, string UserCode, string VerificationUri,
        string VerificationUriComplete, int ExpiresIn, int Interval);

    public sealed record OAuthToken(string AccessToken, string? RefreshToken, string? IdToken);

    public sealed record OAuthProfile(string Id, string Name);

    public static async Task<DeviceCode> StartDeviceAsync(HttpClient http, CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        });
        using var resp = await http.PostAsync(DeviceEndpoint, body, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var json = JObject.Parse(text);
        if (!resp.IsSuccessStatusCode)
        {
            var err = json.Value<string>("error") ?? "";
            var desc = json.Value<string>("error_description") ?? text;
            throw new InvalidOperationException(err == "invalid_client"
                ? "LittleSkin 应用尚未通过「设备代码流白名单」审核（invalid_client）。\n\n" +
                  "请按官方文档发送邮件工单申请，审核通过后再试。"
                : "LittleSkin 授权启动失败：" + desc);
        }
        return new DeviceCode(
            json.Value<string>("device_code") ?? throw new InvalidOperationException("缺少 device_code"),
            json.Value<string>("user_code") ?? "",
            json.Value<string>("verification_uri") ?? "https://open.littleskin.cn/oauth/link",
            json.Value<string>("verification_uri_complete") ?? "",
            json.Value<int?>("expires_in") ?? 600,
            Math.Max(3, json.Value<int?>("interval") ?? 5));
    }

    /// <summary>轮询授权结果;用户完成授权后返回令牌。</summary>
    public static async Task<OAuthToken> PollAsync(
        HttpClient http, DeviceCode device, Action<string>? status, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(device.ExpiresIn);
        var interval = device.Interval;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = ClientId,
                ["device_code"] = device.Code,
            });
            using var resp = await http.PostAsync(TokenEndpoint, body, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var json = JObject.Parse(text);
            if (resp.IsSuccessStatusCode)
            {
                var access = json.Value<string>("access_token");
                if (string.IsNullOrEmpty(access)) throw new InvalidOperationException("LittleSkin 未返回访问令牌");
                return new OAuthToken(access!, json.Value<string>("refresh_token"), json.Value<string>("id_token"));
            }
            var error = json.Value<string>("error") ?? "";
            switch (error)
            {
                case "authorization_pending":
                    status?.Invoke("等待你在浏览器中完成授权…");
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    throw new InvalidOperationException("授权码已过期，请重新发起登录。");
                default:
                    throw new InvalidOperationException(
                        "LittleSkin 授权失败：" + (json.Value<string>("error_description") ?? error));
            }
        }
        throw new InvalidOperationException("授权超时，请重新发起登录。");
    }

    public static async Task<OAuthToken> RefreshAsync(HttpClient http, string refreshToken, CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        });
        using var resp = await http.PostAsync(TokenEndpoint, body, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("LittleSkin 授权已过期，请重新拖入卡片登录。");
        var json = JObject.Parse(text);
        var access = json.Value<string>("access_token")
                     ?? throw new InvalidOperationException("LittleSkin 未返回访问令牌");
        return new OAuthToken(access, json.Value<string>("refresh_token"), json.Value<string>("id_token"));
    }

    /// <summary>从 ID Token 中取用户选定的角色(不验证签名,令牌来自 HTTPS 的官方端点)。</summary>
    public static OAuthProfile? SelectedProfile(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        try
        {
            var parts = idToken!.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var json = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var profile = json["selectedProfile"];
            if (profile == null) return null;
            var id = profile.Value<string>("id");
            var name = profile.Value<string>("name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;
            return new OAuthProfile(id!, name!);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>用 OAuth 访问令牌换取可启动游戏的 Yggdrasil Minecraft 令牌。</summary>
    public static async Task<ExternalSession> CreateMinecraftTokenAsync(
        HttpClient http, string oauthAccessToken, string uuid, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, MinecraftTokenEndpoint)
        {
            Content = new StringContent(new JObject { ["uuid"] = uuid.Replace("-", "") }.ToString(),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", oauthAccessToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("换取 Minecraft 令牌失败：" + text);
        var json = JObject.Parse(text);
        var access = json.Value<string>("accessToken")
                     ?? throw new InvalidOperationException("LittleSkin 未返回 Minecraft 令牌");
        var profile = json["selectedProfile"];
        var name = profile?.Value<string>("name") ?? "";
        var id = profile?.Value<string>("id") ?? uuid;
        return new ExternalSession(ApiRoot, access!, json.Value<string>("clientToken") ?? "",
            ExternalAuth.FormatUuid(id), name);
    }

    /// <summary>列出用户名下的角色(仅申请了 PlayerProfiles.Read 时可用)。</summary>
    public static async Task<List<OAuthProfile>> GetProfilesAsync(
        HttpClient http, string oauthAccessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProfileEndpoint);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", oauthAccessToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("获取角色列表失败：" + text);
        var list = new List<OAuthProfile>();
        foreach (var p in JArray.Parse(text))
            list.Add(new OAuthProfile(p.Value<string>("id") ?? "", p.Value<string>("name") ?? ""));
        return list;
    }
}
