using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Accounts;

/// <summary>外置登录(authlib-injector 协议)的一次会话。</summary>
public sealed record ExternalSession(
    string ApiRoot, string AccessToken, string ClientToken, string Uuid, string PlayerName);

/// <summary>
/// 第三方皮肤站登录(Blessing Skin / LittleSkin 等 authlib-injector 兼容站点)。
/// 只依赖标准 Yggdrasil 接口:站点根 → /api/yggdrasil → /authserver/authenticate。
/// </summary>
public static class ExternalAuth
{
    /// <summary>把用户输入的站点域名/地址规范化成 API 根,并确认站点确实支持 authlib-injector。</summary>
    public static async Task<string> ResolveApiRootAsync(HttpClient http, string input, CancellationToken ct)
    {
        var text = (input ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) throw new InvalidOperationException("请填写皮肤站地址");
        if (!text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;

        var candidates = new[]
        {
            text.EndsWith("/api/yggdrasil", StringComparison.OrdinalIgnoreCase) ? text : text + "/api/yggdrasil",
            text,
        };

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var resp = await http.GetAsync(candidate, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    last = new InvalidOperationException($"{(int)resp.StatusCode} {candidate}");
                    continue;
                }
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                // authlib-injector 站点根会返回 meta / skinDomains / signaturePublickey
                if (json["meta"] != null || json["skinDomains"] != null || json["signaturePublickey"] != null)
                    return candidate;
                last = new InvalidOperationException("该地址没有返回 authlib-injector 站点信息");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException(
            "该站点不是 authlib-injector 兼容站(检查地址是否正确)" + (last == null ? "" : ":" + last.Message));
    }

    /// <summary>用户名/密码登录,返回令牌与角色信息。密码错误会给出明确中文提示。</summary>
    public static async Task<ExternalSession> AuthenticateAsync(
        HttpClient http, string apiRoot, string username, string password, string clientToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new InvalidOperationException("请填写用户名");
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("请填写密码");

        var body = new JObject
        {
            ["agent"] = new JObject { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = username.Trim(),
            ["password"] = password,
            ["clientToken"] = clientToken,
            ["requestUser"] = false,
        };
        using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await http.PostAsync(apiRoot.TrimEnd('/') + "/authserver/authenticate", content, cts.Token)
            .ConfigureAwait(false);

        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string? message = null;
            try { message = JObject.Parse(text).Value<string>("errorMessage"); } catch (Exception) { }
            throw new InvalidOperationException(
                (int)resp.StatusCode == 403
                    ? "用户名或密码错误" + (message == null ? "" : "(" + message + ")")
                    : $"登录失败:{(int)resp.StatusCode} {message ?? text}".Trim());
        }

        var json = JObject.Parse(text);
        var profile = json["selectedProfile"];
        var name = profile?.Value<string>("name");
        var uuid = profile?.Value<string>("id");
        var access = json.Value<string>("accessToken");
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uuid))
            throw new InvalidOperationException("站点没有返回角色信息(账号可能还没有创建角色)");

        var token = access!;
        return new ExternalSession(apiRoot, token, json.Value<string>("clientToken") ?? clientToken,
            FormatUuid(uuid!), name!);
    }

    /// <summary>站点返回的 uuid 可能是不带连字符的 32 位串,统一成标准格式。</summary>
    public static string FormatUuid(string raw)
    {
        var hex = raw.Replace("-", "").ToLowerInvariant();
        if (hex.Length != 32) return raw;
        return $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20, 12)}";
    }
}
