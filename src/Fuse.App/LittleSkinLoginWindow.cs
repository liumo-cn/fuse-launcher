using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fuse.Core.Accounts;

namespace Fuse.App;

/// <summary>
/// LittleSkin OAuth 设备码授权窗口:显示授权码 → 打开浏览器 → 轮询令牌。
/// 用户不需要在启动器里输入邮箱密码。
/// </summary>
internal sealed class LittleSkinLoginWindow : Window
{
    private readonly TextBlock _code;
    private readonly TextBlock _status;
    private readonly Button _open;
    private readonly Button _password;
    private readonly CancellationTokenSource _cts = new();

    public ExternalSession? Session { get; private set; }
    public string? OAuthRefreshToken { get; private set; }
    public string? ProfileName { get; private set; }
    /// <summary>用户选择“改用密码登录”时为 true。</summary>
    public bool UsePasswordFallback { get; private set; }

    private string _verification = "https://open.littleskin.cn/oauth/link";

    public LittleSkinLoginWindow()
    {
        Title = "LittleSkin 授权登录";
        DialogKit.ApplyWindow(this, 400);
        var stack = new StackPanel();
        var head = DialogKit.Title("LittleSkin 授权登录");
        stack.Children.Add(head);
        stack.Children.Add(DialogKit.Hint(
            "启动器不会接触你的密码。请在浏览器里完成授权（已登录的话只需点一下）。"));
        stack.Children.Add(DialogKit.Hint("授权码"));

        _code = new TextBlock
        {
            Text = "……",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)Application.Current.Resources["AccentTextBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 8),
        };
        stack.Children.Add(_code);

        _status = new TextBlock
        {
            Text = "正在向 LittleSkin 申请授权码…",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 8),
        };
        stack.Children.Add(_status);

        _open = DialogKit.Primary("打开浏览器授权", () => OpenBrowser());
        _open.IsEnabled = false;
        _password = DialogKit.Secondary("改用密码登录", () =>
        {
            UsePasswordFallback = true;
            DialogResult = false;
        });
        var cancel = DialogKit.Secondary("取消", () => DialogResult = false);
        stack.Children.Add(DialogKit.Buttons(_password, _open));
        Content = DialogKit.Card(stack);
        DialogKit.WireDrag(this, head);
        DialogKit.WireKeys(this, () => DialogResult = false);

        Closed += (_, _) =>
        {
            try { _cts.Cancel(); } catch (Exception) { }
            _cts.Dispose();
        };
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            var device = await LittleSkinOAuth.StartDeviceAsync(AppServices.Http, _cts.Token);
            _code.Text = device.UserCode;
            _verification = string.IsNullOrEmpty(device.VerificationUriComplete)
                ? device.VerificationUri
                : device.VerificationUriComplete;
            _open.IsEnabled = true;
            _status.Text = "已打开浏览器。若没有自动打开，点下方按钮，输入授权码 " + device.UserCode + "。";
            OpenBrowser();

            var token = await LittleSkinOAuth.PollAsync(
                AppServices.Http, device,
                msg => Dispatcher.BeginInvoke(new Action(() => _status.Text = msg)),
                _cts.Token);

            var profile = LittleSkinOAuth.SelectedProfile(token.IdToken);
            if (profile == null)
            {
                // 没申请 Select 或 ID Token 缺失时,退回到角色列表
                var profiles = await LittleSkinOAuth.GetProfilesAsync(
                    AppServices.Http, token.AccessToken, _cts.Token);
                if (profiles.Count == 0) throw new InvalidOperationException("账号下没有角色，请先创建角色");
                profile = profiles[0];
            }

            var session = await LittleSkinOAuth.CreateMinecraftTokenAsync(
                AppServices.Http, token.AccessToken, profile.Id, _cts.Token);
            Session = session;
            OAuthRefreshToken = token.RefreshToken;
            ProfileName = session.PlayerName;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // 用户关闭窗口
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            _open.IsEnabled = true;
        }
    }

    private void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_verification) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Text = "无法打开浏览器：" + ex.Message + "\n请手动访问 " + _verification;
        }
    }
}
