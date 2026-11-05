using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace FuseSetup;

public partial class App : Application
{
    private static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "fuse-setup.log"),
            "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + System.Environment.NewLine); }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("app startup");
        base.OnStartup(e);
        Log("base done");
        try
        {
            // /SILENT 时不显示向导窗口:构造函数里已经完成安装/卸载并请求退出。
            // 以前这里无条件 Show(),静默更新会闪出一个安装器窗口(还会多留一帧)。
            var silent = e.Args.Any(a =>
                a.TrimStart('/').Equals("SILENT", StringComparison.OrdinalIgnoreCase));
            var win = new MainWindow();
            Log("ctor done");
            if (silent)
            {
                Log("silent: window not shown");
                Shutdown(0);
                return;
            }
            win.Show();
            Log("window shown");
        }
        catch (Exception ex)
        {
            Log("window FAILED: " + ex);
            MessageBox.Show("启动器安装向导无法启动：" + ex.Message);
            Shutdown(-1);
        }
    }
}
