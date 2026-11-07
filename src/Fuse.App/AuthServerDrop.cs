using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Fuse.App.Panels;
using Fuse.Core.Accounts;

namespace Fuse.App;

/// <summary>
/// 接收网页拖进来的 authlib-injector 认证服务器地址(LittleSkin 仪表盘的「外置登录」卡片可拖)。
/// 只负责识别/校验/打开登录页;凭据仍由用户在登录页输入。
/// </summary>
internal static class AuthServerDrop
{
    private static readonly Regex UrlPattern = new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase);

    /// <summary>
    /// 从拖拽数据里取第一个 https 地址。浏览器链接拖拽常见格式:
    /// text/uri-list / text/plain / UniformResourceLocator / text/html(<a href>)。
    /// </summary>
    public static bool TryGetUrl(IDataObject data) => TryGetUrl(data, out _);

    public static bool TryGetUrl(IDataObject data, out string url)
    {
        url = "";
        if (data == null) return false;
        if (data.GetDataPresent(DataFormats.FileDrop)) return false;   // 文件拖拽交给导入逻辑

        foreach (var format in new[]
                 {
                     "text/uri-list", "text/plain", DataFormats.UnicodeText, DataFormats.Text,
                     "UniformResourceLocatorW", "UniformResourceLocator", DataFormats.Html,
                 })
        {
            try
            {
                if (!data.GetDataPresent(format)) continue;
                var raw = data.GetData(format);
                var text = raw switch
                {
                    string s => s,
                    System.IO.MemoryStream ms => ReadAll(ms),
                    _ => raw?.ToString() ?? "",
                };
                if (TryExtractUrl(text, out url)) return true;
            }
            catch (Exception)
            {
                // 某个格式读不了就试下一个
            }
        }
        return false;
    }

    public static bool TryExtractUrl(string? text, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        string? first = null;
        string? hostBest = null;
        foreach (Match match in UrlPattern.Matches(text!))
        {
            var candidate = WebUtility.HtmlDecode(match.Value).TrimEnd('.', ',', ')', ']', ';');
            if (!candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            first ??= candidate;
            // 最高优先级:真正的 Yggdrasil API 根(卡片拖拽常把图片链接排在前面)
            if (candidate.IndexOf("/api/yggdrasil", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                url = candidate;
                return true;
            }
            // 次优先:站点主域(排除 cdn./img. 之类的子域)
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                var host = uri.Host.ToLowerInvariant();
                if (host == "littleskin.cn" || host == "www.littleskin.cn") hostBest ??= candidate;
            }
        }
        if (hostBest != null)
        {
            url = hostBest;
            return true;
        }
        if (first != null)
        {
            url = first;
            return true;
        }
        return false;
    }

    private static int _busy;

    /// <summary>拖放/剪贴板入口:校验服务器并打开 LittleSkin 风格的登录表单。</summary>
    public static async Task AddAsync(string rawUrl, Window owner, bool fromClipboard = false)
    {
        // 拖放事件可能从多个窗口/格式重复触发:同一时间只处理一次
        if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            await AddCoreAsync(rawUrl, fromClipboard).ConfigureAwait(true);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task AddCoreAsync(string rawUrl, bool fromClipboard)
    {
        // 文档站链接(manual.littlesk.in)不是认证服务器,直接换成官方 API 根,省掉 15s 超时
        if (rawUrl.IndexOf("manual.littlesk.in", StringComparison.OrdinalIgnoreCase) >= 0)
            rawUrl = "https://littleskin.cn/api/yggdrasil";
        if (ElevationStatus.IsAdministrator && !fromClipboard)
        {
            Show("无法接收拖拽", "当前以管理员身份运行，Windows 不允许普通浏览器把内容拖进来。\n\n" +
                                   "请复制 LittleSkin 的卡片地址，然后在登录页点「从剪贴板识别」。");
            return;
        }

        if (!TryValidateHost(rawUrl, out var error))
        {
            Show("地址不可用", error);
            return;
        }

        WidgetWindow.Boot("[authdrop] raw=" + rawUrl);
        ExternalServer server;
        try
        {
            server = await ExternalAuth.ProbeAsync(AppServices.Http, rawUrl, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception first)
        {
            // 拖拽来源可能带跳转/包装页:地址里出现 littleskin.cn 时退回官方 API 根再试一次
            if (rawUrl.IndexOf("littlesk", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    server = await ExternalAuth.ProbeAsync(AppServices.Http,
                        "https://littleskin.cn/api/yggdrasil", CancellationToken.None).ConfigureAwait(true);
                    WidgetWindow.Boot("[authdrop] fallback to littleskin api root");
                    OpenLogin(server);
                    return;
                }
                catch (Exception)
                {
                }
            }
            Show("无法添加认证服务器",
                first is TaskCanceledException
                    ? "连接认证服务器超时，请检查网络后重试。"
                    : first.Message);
            return;
        }

        if (server.ApiRoot.IndexOf("littleskin.cn", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            LoginLittleSkin(server);
            return;
        }
        OpenLogin(server);
    }

    private static void LoginLittleSkin(ExternalServer server)
    {
        var dialog = new LittleSkinLoginWindow();
        var ok = dialog.ShowDialog();
        if (ok == true && dialog.Session != null)
        {
            AppServices.Accounts.UseExternalOAuth(
                server.ApiRoot, dialog.ProfileName ?? "LittleSkin",
                dialog.Session, dialog.OAuthRefreshToken);
            Toast.ShowNearOrb("已登录 LittleSkin：" + dialog.ProfileName);
            return;
        }
        if (dialog.UsePasswordFallback) OpenLogin(server);
    }

    private static void OpenLogin(ExternalServer server)
    {

        var widget = AppServices.Widget;
        if (widget == null)
        {
            Show("暂时无法打开登录页", "启动器窗口还没准备好，请稍后重试。");
            return;
        }

        PanelWindow.ShowPanel("login", new Point(0, 0), false, widget);
        if (PanelWindow.OpenPanels.TryGetValue("login", out var panel) &&
            panel.PanelContent is LoginPanel login)
        {
            login.PrefillAuthServer(server.ApiRoot, server.ServerName);
        }
    }

    private static bool TryValidateHost(string rawUrl, out string error)
    {
        error = "";
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            error = "只接受 https 地址";
            return false;
        }
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("127.") || host.StartsWith("10.") || host.StartsWith("192.168.") || host.StartsWith("169.254.") ||
            host == "::1" || IsPrivate172(host))
        {
            error = "不接受本机或内网地址";
            return false;
        }
        return true;
    }

    private static bool IsPrivate172(string host)
    {
        if (!host.StartsWith("172.", StringComparison.Ordinal)) return false;
        var parts = host.Split('.');
        return parts.Length > 1 && int.TryParse(parts[1], out var second) && second >= 16 && second <= 31;
    }

    private static string ReadAll(System.IO.MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static void Show(string title, string message)
    {
        var window = new InfoWindow(title, message);
        window.ShowDialog();
    }
}
