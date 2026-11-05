using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FuseUninstaller;

/// <summary>
/// Fuse Launcher 卸载助手。
/// 由安装器释放到 &lt;安装目录&gt;\Uninstall Fuse Launcher.exe,体积很小、和安装器外观不同。
/// 关键点:从安装目录启动时先把自己复制到 %TEMP% 再重启,否则运行时无法删除自身所在的目录
/// (旧版"删不掉、要重启"就是因为卸载程序自己锁着安装目录里的 exe)。
/// 支持 /SILENT 静默卸载(自动化测试/批量部署用)。
/// </summary>
internal static class Program
{
    private const string AppName = "Fuse Launcher";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Fuse Launcher";
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "fuse-uninstall.log");

    [STAThread]
    private static void Main(string[] args)
    {
        var silent = args.Any(a => a.TrimStart('/').Equals("SILENT", StringComparison.OrdinalIgnoreCase));
        // 从安装目录重定位到临时目录时会带上 /CONFIRMED:确认过一次就不再弹第二次
        var confirmed = silent || args.Any(a =>
            a.TrimStart('/').Equals("CONFIRMED", StringComparison.OrdinalIgnoreCase));
        try
        {
            var installDir = ResolveInstallDir(args);
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
            {
                Fail(silent, "找不到安装目录:" + (installDir ?? "(未知)"));
                return;
            }
            Log("uninstall begin: " + installDir + " silent=" + silent + " confirmed=" + confirmed);

            if (!confirmed && MessageBox.Show(
                    "确定要卸载 " + AppName + " 吗？\n\n游戏目录(版本 / 存档)不会被删除。",
                    "卸载 " + AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            var self = Process.GetCurrentProcess().MainModule?.FileName;
            if (self != null && IsInside(self, installDir))
            {
                // 从安装目录运行:复制到临时目录重跑,把自己从"要删除的目录"里摘出去
                var temp = Path.Combine(Path.GetTempPath(),
                    "FuseUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(self, temp, overwrite: true);
                var relaunch = "/UNINSTALL=\"" + installDir + "\"" + (silent ? " /SILENT" : " /CONFIRMED");
                Process.Start(new ProcessStartInfo(temp, relaunch) { UseShellExecute = true });
                Log("relocated -> " + temp);
                return;
            }

            KillLauncher();
            DeleteShortcuts();
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
            catch (Exception ex) { Log("registry: " + ex.Message); }

            var removed = RemoveInstallDir(installDir);
            Log(removed ? "install dir removed" : "install dir will be removed after exit");

            if (!silent)
                MessageBox.Show("卸载完成。", "卸载 " + AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            ScheduleSelfDelete(self);
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            if (!silent)
                MessageBox.Show("卸载失败：" + ex.Message, "卸载 " + AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string ResolveInstallDir(string[] args)
    {
        foreach (var raw in args)
        {
            var a = raw.TrimStart('/');
            if (a.StartsWith("UNINSTALL=", StringComparison.OrdinalIgnoreCase))
                return a.Substring(10).Trim('"').TrimEnd('\\', '/');
        }
        // 没带参数(双击 / 开始菜单快捷方式)= 自己所在的目录就是安装目录
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrEmpty(self) ? null : Path.GetDirectoryName(self);
    }

    private static bool IsInside(string path, string dir)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void KillLauncher()
    {
        var me = Process.GetCurrentProcess();
        foreach (var name in new[] { "Fuse Launcher", "Uninstall Fuse Launcher" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == me.Id) continue;
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                    Log("killed " + name + " pid=" + p.Id);
                }
                catch (Exception ex) { Log("kill " + name + ": " + ex.Message); }
            }
        }
        Thread.Sleep(400);
    }

    private static void DeleteShortcuts()
    {
        DeleteFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        DeleteFile(Path.Combine(startMenu, AppName + ".lnk"));
        DeleteFile(Path.Combine(startMenu, "卸载 " + AppName + ".lnk"));
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log("delete " + path + ": " + ex.Message); }
    }

    /// <summary>删除安装目录:先重试(后台进程句柄释放需要时间),还不行就交给退出后的 cmd。</summary>
    private static bool RemoveInstallDir(string installDir)
    {
        // 便携模式下游戏目录在 <安装目录>\games:卸载只删启动器,不能把游戏/存档一起删掉
        var gamesDir = Path.Combine(installDir, "games");
        var protectGames = Directory.Exists(gamesDir);
        for (var i = 0; i < 10; i++)
        {
            if (!Directory.Exists(installDir)) return true;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(installDir).ToList())
                {
                    if (protectGames &&
                        Path.GetFileName(entry).Equals("games", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
                if (protectGames)
                {
                    if (!Directory.EnumerateFileSystemEntries(installDir).Any())
                        Directory.Delete(installDir, false);
                }
                else
                {
                    Directory.Delete(installDir, true);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("delete dir attempt " + (i + 1) + ": " + ex.Message);
                Thread.Sleep(300);
            }
        }
        Log(protectGames ? "install dir kept (games preserved)" : "install dir cleanup failed");
        return false;
    }

    private static void ScheduleSelfDelete(string self)
    {
        if (string.IsNullOrEmpty(self) || !File.Exists(self)) return;
        // 临时副本:退出后延迟删除(自己删自己会被锁)
        var psi = new ProcessStartInfo("cmd.exe",
            "/c ping -n 2 127.0.0.1 > nul & del /f /q \"" + self + "\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        try { Process.Start(psi); } catch (Exception ex) { Log("self delete: " + ex.Message); }
    }

    private static void Fail(bool silent, string message)
    {
        Log(message);
        if (!silent)
            MessageBox.Show(message, "卸载 " + AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.ExitCode = 1;
    }

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath,
                "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响卸载
        }
    }
}
