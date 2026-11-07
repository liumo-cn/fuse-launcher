using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Fuse.Core.Auth;

namespace Fuse.App;

/// <summary>微软正版登录窗（FR-06）：设备码流程 + 六步链，成功后凭据入 AccountStore。</summary>
public partial class MicrosoftLoginWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private DeviceCodeInfo? _deviceCode;
    private bool _finished;

    public MicrosoftLoginWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        RunLogin();
    }

    private async void RunLogin()
    {
        try
        {
            StatusText.Text = "正在申请设备码…";
            _deviceCode = await MicrosoftAccount.StartDeviceCodeAsync(
                AppServices.Config.MicrosoftClientId, _cts.Token);

            UriText.Text = _deviceCode.VerificationUri;
            CodeText.Text = _deviceCode.UserCode;
            StatusText.Text = "等待你在浏览器完成授权…";

            try
            {
                Process.Start(new ProcessStartInfo(_deviceCode.VerificationUri)
                {
                    UseShellExecute = true,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 无默认浏览器等场景：用户可手动复制链接
            }

            StatusText.Text = "等待授权中（可最小化本窗口，完成后自动继续）…";
            var (accessToken, refreshToken) = await MicrosoftAccount.WaitForAuthorizationAsync(
                AppServices.Config.MicrosoftClientId, _deviceCode, _cts.Token);

            StatusText.Text = "授权成功，正在拉取玩家档案（XBL → XSTS → MC 令牌 → 所属权）…";
            var result = await MicrosoftAccount.CompleteLoginAsync(accessToken, _cts.Token);

            // 刷新令牌必须用第二步返回的那个:CompleteLoginAsync 的结果里不含它
            // (曾经存了空串,启动时静默续期被微软 400 拒绝)
            AppServices.Accounts.SetMicrosoft(
                result.PlayerName, result.PlayerUuid.ToString(), refreshToken);
            StatusText.Text = $"登录成功：{result.PlayerName}";
            await Task.Delay(600);
            Finish(true);
        }
        catch (OperationCanceledException)
        {
            Finish(false);
        }
        catch (Exception ex)
        {
            StatusText.Text = "登录失败：" + ex.Message;
            // 窗口可能已被取消/关闭,这时不能再把它当 owner
            MessageBox.Show(IsVisible ? this : null, "登录失败：" + ex.Message, AppServices.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 收尾:取消/成功都走这里。用户先点了「取消登录」时窗口已经关闭,
    /// 再设置 DialogResult 会抛"只能在作为对话框显示之后设置"(曾经导致崩溃)。
    /// </summary>
    private void Finish(bool result)
    {
        if (_finished) return;
        _finished = true;
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            try { Close(); } catch (Exception) { }
        }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceCode == null) return;
        try
        {
            StatusText.Text = ClipboardHelper.TrySetText(_deviceCode.UserCode)
                ? "代码已复制，去浏览器粘贴即可。"
                : "复制失败（剪贴板被占用），请手动记录代码。";
        }
        catch (Exception)
        {
            // 剪贴板被占用等情况忽略
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        Finish(false);
    }
}
