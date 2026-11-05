using System;
using System.IO;

namespace Fuse.Core.Logging;

/// <summary>
/// 日志统一入口:追加写入 + 超限裁剪。
/// boot.log / crash.log 是无限追加的,长期使用会涨到几十 MB——
/// 超过上限时只保留尾部,老日志直接丢弃(诊断价值随时间快速衰减,不值得归档)。
/// 所有写入点(App / WidgetWindow / AppLauncher / PanelWindow / MirrorProbe / Updater)都走这里。
/// </summary>
public static class LogSink
{
    /// <summary>超过 1.5MB 触发裁剪。</summary>
    private const long MaxBytes = 1_500_000;
    /// <summary>裁剪后保留尾部 256KB。</summary>
    private const long KeepBytes = 256 * 1024;

    private static readonly object Gate = new();

    public static void Append(string path, string content)
    {
        lock (Gate)
        {
            try
            {
                TrimIfNeeded(path);
                File.AppendAllText(path, content);
            }
            catch (Exception)
            {
                // 日志失败绝不影响主流程
            }
        }
    }

    private static void TrimIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes) return;

        // 二进制截尾:保留文件末尾 KeepBytes,避免按字符解码出现半个中文
        var bytes = File.ReadAllBytes(path);
        var keep = (int)Math.Min(KeepBytes, bytes.Length);
        var tail = new byte[keep];
        Array.Copy(bytes, bytes.Length - keep, tail, 0, keep);
        var tmp = path + ".trim";
        File.WriteAllBytes(tmp, tail);
        File.Delete(path);
        File.Move(tmp, path);
    }
}
