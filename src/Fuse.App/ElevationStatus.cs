using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Windows;

namespace Fuse.App;

/// <summary>PresentMon 的 ETW 权限检测与提权重启。</summary>
internal static class ElevationStatus
{
    private static readonly SecurityIdentifier PerfLogUsersSid =
        new("S-1-5-32-559"); // BUILTIN\Performance Log Users

    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static bool InPerformanceLogUsers
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.Groups == null) return false;
                foreach (var group in identity.Groups)
                    if (group is SecurityIdentifier sid && sid.Equals(PerfLogUsersSid)) return true;
            }
            catch (Exception)
            {
            }
            return false;
        }
    }

    /// <summary>PresentMon 能否读取帧率。</summary>
    public static bool CanTraceFps => IsAdministrator || InPerformanceLogUsers;

    /// <summary>以管理员身份重启启动器（用户的显式操作，不静默提权）。</summary>
    public static bool RestartAsAdministrator()
    {
        try
        {
            var exe = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(exe)) return false;
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception)
        {
            // 用户在 UAC 里点了“否”或系统策略拒绝
            return false;
        }
    }
}
