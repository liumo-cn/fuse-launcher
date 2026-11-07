using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Fuse.App;

/// <summary>
/// PresentMon 组件部署：安装包自带 tools\PresentMon-x64.exe；缺失时（便携版/精简包）
/// 从官方 GitHub Release 拉取 2.6.0，校验 SHA-256 后落盘。失败静默，FPS 显示 --。
/// </summary>
internal static class PresentMonProvisioner
{
    private const string DownloadUrl =
        "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
    private const string ExpectedSha256 =
        "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";

    private static int _started;

    /// <summary>只尝试一次（成功或失败都不再打扰）。</summary>
    public static void EnsureOnce()
    {
        if (FpsMonitor.Available) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(EnsureAsync);
    }

    public static async Task<bool> EnsureAsync()
    {
        var target = FpsMonitor.ExePath;
        if (File.Exists(target)) return true;
        var temp = target + ".download";
        try
        {
            var dir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(dir);

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var output = File.Create(temp);
                await input.CopyToAsync(output).ConfigureAwait(false);
            }

            var actual = ComputeSha256(temp);
            if (!actual.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(temp); } catch (IOException) { }
                return false;
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            WidgetWindow.Boot("[presentmon] downloaded and verified");
            return true;
        }
        catch (Exception ex)
        {
            WidgetWindow.Boot("[presentmon] download failed: " + ex.Message);
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
