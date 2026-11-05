using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Fuse.Core.Download;

namespace Fuse.App;

/// <summary>
/// 统一错误上报:写 crash.log + 弹"坏菜了"提示框(可一键导出完整日志)。
/// 启动失败、下载失败、任何未处理异常都走这里。
/// </summary>
public static class ErrorReporter
{
    private static bool _showing;

    /// <summary>最近一次错误的上下文,导出时写进 summary.txt。</summary>
    private static string _lastTitle = "";
    private static string _lastDetail = "";

    public static void Report(string title, Exception? ex, string? extraLogPath = null)
    {
        var detail = ex?.ToString() ?? "";
        try
        {
            var dir = Path.GetDirectoryName(AppServices.ConfigPath)!;
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}");
            if (!string.IsNullOrEmpty(detail)) sb.AppendLine(detail);
            if (!string.IsNullOrEmpty(extraLogPath)) sb.AppendLine("日志: " + extraLogPath);
            sb.AppendLine();
            Fuse.Core.Logging.LogSink.Append(Path.Combine(dir, "crash.log"), sb.ToString());
        }
        catch (Exception)
        {
        }

        _lastTitle = title;
        _lastDetail = detail + (string.IsNullOrEmpty(extraLogPath) ? "" : Environment.NewLine + "日志: " + extraLogPath);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            // 同一时间只弹一个,避免批量失败刷屏
            if (_showing) return;
            _showing = true;
            try { CrashDialog.Show(_lastTitle, ExportLogs); }
            finally { _showing = false; }
        }));
    }

    /// <summary>打包完整日志:crash.log / boot.log / 任务表 / 各实例的 latest-launch.log / 系统信息。</summary>
    private static void ExportLogs()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Fuse-错误日志-{stamp}.zip",
            Filter = "压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var appData = Path.GetDirectoryName(AppServices.ConfigPath)!;
            var packages = new List<(string Path, string Entry)>();
            void Add(string path, string entry)
            {
                if (File.Exists(path)) packages.Add((path, entry));
            }

            Add(Path.Combine(appData, "crash.log"), "crash.log");
            Add(Path.Combine(appData, "boot.log"), "boot.log");
            Add(Path.Combine(AppServices.InstallDirectory, "data", "tasks.json"), "tasks.json");

            var instances = Path.Combine(AppServices.InstallDirectory, "instances");
            if (Directory.Exists(instances))
            {
                foreach (var dir in Directory.EnumerateDirectories(instances))
                {
                    var name = Path.GetFileName(dir);
                    Add(Path.Combine(dir, "logs", "latest-launch.log"), $"instances/{name}/latest-launch.log");
                }
            }

            using (var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create))
            {
                var summary = zip.CreateEntry("summary.txt");
                using (var w = new StreamWriter(summary.Open(), Encoding.UTF8))
                {
                    w.WriteLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    w.WriteLine("错误: " + _lastTitle);
                    w.WriteLine();
                    w.WriteLine(_lastDetail);
                    w.WriteLine();
                    w.WriteLine("系统: " + Environment.OSVersion + " / " + (Environment.Is64BitProcess ? "x64" : "x86"));
                    w.WriteLine("CLR: " + Environment.Version);
                }

                var skipped = new List<string>();
                foreach (var (path, entry) in packages)
                {
                    // 游戏可能正在写 latest-launch.log:共享读方式拷入;被独占时重试几次,
                    // 仍然失败才跳过 —— 并且把原因写清楚,不能让"缺文件"变成无迹可寻。
                    var copied = false;
                    var reason = "";
                    for (var attempt = 0; attempt < 4 && !copied; attempt++)
                    {
                        try
                        {
                            using var src = new FileStream(path, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            var zipEntry = zip.CreateEntry(entry, CompressionLevel.Optimal);
                            using var dst = zipEntry.Open();
                            src.CopyTo(dst);
                            copied = true;
                        }
                        catch (IOException ex)
                        {
                            reason = ex.Message;
                            System.Threading.Thread.Sleep(300);
                        }
                    }

                    if (!copied)
                        skipped.Add($"{entry}  ({reason})");
                }

                if (skipped.Count > 0)
                {
                    var note = zip.CreateEntry("skipped.txt");
                    using var w = new StreamWriter(note.Open(), Encoding.UTF8);
                    w.WriteLine("以下文件在导出时被其它进程独占,重试 4 次后仍无法读取,已跳过:");
                    foreach (var line in skipped) w.WriteLine("  " + line);
                    w.WriteLine();
                    w.WriteLine("如果是 latest-launch.log 缺失:它正被游戏进程写入,关闭游戏后再导出即可拿到完整内容。");
                }
            }

            try { Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\""); }
            catch (Exception) { }
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, AppServices.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
