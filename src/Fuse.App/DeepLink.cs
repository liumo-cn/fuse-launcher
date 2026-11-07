using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Fuse.App;

/// <summary>
/// fuse-launcher:// 深度链接:注册表注册 + Named Pipe 转发给已运行的实例。
/// 主实例开管道服务,第二个实例把 URL 写进管道后退出(单实例互斥体拦住的那种"双开")。
/// </summary>
internal static class DeepLink
{
    public const string Scheme = "fuse-launcher";
    private const string PipeName = "FuseLauncher.DeepLink";

    /// <summary>收到好友邀请。</summary>
    public static event Action<string>? InviteReceived;

    /// <summary>注册 HKCU 协议关联(exe 路径变了也会自动纠正;HKCU 不需要管理员)。</summary>
    public static void Register()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            root.SetValue("", "URL:Fuse Launcher 联机邀请");
            root.SetValue("URL Protocol", "");
            using var cmd = root.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", "\"" + exe + "\" \"%1\"");
        }
        catch (Exception ex)
        {
            WidgetWindow.Boot("[deeplink] register failed: " + ex.Message);
        }
    }

    /// <summary>启动参数里有没有邀请链接。</summary>
    public static string? FindInviteInArgs(string[] args)
    {
        foreach (var a in args)
            if (a.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
                return a;
        return null;
    }

    /// <summary>我是第二个实例:把 URL 发给主实例后返回(主实例弹窗)。</summary>
    public static bool TryForwardToRunningInstance(string url)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(url);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>主实例:开管道服务监听后续邀请(进程生命周期内常驻,断线自动重开)。</summary>
    public static void StartServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var url = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(url))
                        InviteReceived?.Invoke(url.Trim());
                }
                catch (Exception)
                {
                    // 管道被破坏/系统关机:歇一下重开
                    Thread.Sleep(1000);
                }
            }
        })
        {
            IsBackground = true,
            Name = "FuseDeepLink",
        };
        thread.Start();
    }

    /// <summary>把邀请交给 UI 线程弹窗。</summary>
    public static void Dispatch(string url)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                MultiplayerJoinWindow.ShowForInvite(url);
            }
            catch (Exception ex)
            {
                WidgetWindow.Boot("[deeplink] join window failed: " + ex.Message);
            }
        }));
    }
}
