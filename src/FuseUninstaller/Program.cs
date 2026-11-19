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
    /// <summary>游戏内容子目录:游戏根与安装目录重合时,卸载只删启动器本体,这些全部保留。</summary>
    private static readonly string[] GameContentDirs =
    {
        "versions", "instances", "saves", "mods", "config",
        "resourcepacks", "shaderpacks", "libraries", "assets",
    };

    private static bool RemoveInstallDir(string installDir)
    {
        // 便携模式下游戏目录在 <安装目录>\games:卸载只删启动器,不能把游戏/存档一起删掉
        var gamesDir = Path.Combine(installDir, "games");
        var protectGames = Directory.Exists(gamesDir);
        // 游戏根可能就是安装目录本身(用户把游戏下载目录设成了启动器文件夹):
        // 此时 versions/instances/saves 等全是游戏内容,与 games 一样保护
        var protectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "games" };
        if (!protectGames && LooksLikeGameRoot(installDir))
        {
            foreach (var n in GameContentDirs) protectNames.Add(n);
            Log("install dir looks like game root: protecting " + string.Join("/", GameContentDirs));
        }
        // 嵌套在安装目录里的游戏根(如用户自建的 .minecraft)整体保护
        var protectedRoots = FindGameRootDirs(installDir);

        for (var i = 0; i < 10; i++)
        {
            if (!Directory.Exists(installDir)) return true;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(installDir).ToList())
                {
                    var name = Path.GetFileName(entry);
                    if (protectNames.Contains(name) || protectedRoots.Contains(entry))
                        continue;
                    if (Directory.Exists(entry)) DeleteTreeSafe(entry);
                    else File.Delete(entry);
                }
                if (protectGames || protectedRoots.Count > 0 || protectNames.Count > 1)
                {
                    if (!Directory.EnumerateFileSystemEntries(installDir).Any())
                        Directory.Delete(installDir, false);
                }
                else
                {
                    DeleteTreeSafe(installDir);
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

    /// <summary>目录是否是游戏根:含 versions/&lt;id&gt;/&lt;id&gt;.json。</summary>
    private static bool LooksLikeGameRoot(string dir)
    {
        try
        {
            var versions = Path.Combine(dir, "versions");
            if (!Directory.Exists(versions)) return false;
            foreach (var versionDir in Directory.EnumerateDirectories(versions))
            {
                var id = Path.GetFileName(versionDir);
                if (File.Exists(Path.Combine(versionDir, id + ".json"))) return true;
            }
        }
        catch (Exception ex) { Log("scan game root: " + ex.Message); }
        return false;
    }

    /// <summary>安装目录下"看起来是游戏根"的子目录:含 versions/&lt;id&gt;/&lt;id&gt;.json。</summary>
    private static HashSet<string> FindGameRootDirs(string installDir)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(installDir))
            {
                var versions = Path.Combine(dir, "versions");
                if (!Directory.Exists(versions)) continue;
                foreach (var versionDir in Directory.EnumerateDirectories(versions))
                {
                    var id = Path.GetFileName(versionDir);
                    if (File.Exists(Path.Combine(versionDir, id + ".json")))
                    {
                        result.Add(dir);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) { Log("scan game roots: " + ex.Message); }
        return result;
    }

    /// <summary>
    /// reparse 感知的递归删除:遇到 junction/符号链接只摘链接本身,绝不进入目标。
    /// net48 的 Directory.Delete(recursive) 会穿进链接清空目标内容 ——
    /// 自动虚拟隔离在版本目录里建的 junction 一旦被这里跟随,共享根/启动器目录就被清空。
    /// </summary>
    private static void DeleteTreeSafe(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, false);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path).ToList())
        {
            if (Directory.Exists(entry) &&
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                DeleteTreeSafe(entry);
            else if (Directory.Exists(entry))
                Directory.Delete(entry, false);   // 链接:只摘本身
            else
                File.Delete(entry);
        }
        Directory.Delete(path, false);
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
