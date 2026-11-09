using System;
using System.IO;
using System.Text;

namespace Fuse.Core.Logging;

/// <summary>
/// 共享游戏目录(多个版本都用根目录)时,logs/latest-launch.log 只保留最后一次启动。
/// 从首行命令行里的 --version / -Dfuse.version 判断这份日志到底属于哪个版本。
/// </summary>
public static class LaunchLogAttribution
{
    public static bool IsForVersion(string logPath, string? versionId)
    {
        var id = (versionId ?? "").Trim();
        if (id.Length == 0 || !File.Exists(logPath)) return false;
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[64 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return ContainsVersion(new string(buffer, 0, read), id);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool ContainsVersion(string text, string versionId)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(versionId)) return false;
        var id = versionId.Trim();

        // 版本 id 本身可能带空格(26.1-Fabric 0.19.5),所以 --version 取到下一个 " --" 为止
        var fromArg = ReadValue(text, "--version ", untilArgMarker: true);
        if (string.Equals(fromArg, id, StringComparison.OrdinalIgnoreCase)) return true;

        // 属性值也可能带空格,同样取到下一个 " --" 或行尾
        var fromProperty = ReadValue(text, "-Dfuse.version=", untilArgMarker: true);
        return string.Equals(fromProperty, id, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadValue(string text, string marker, bool untilArgMarker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var start = index + marker.Length;
        if (start >= text.Length) return "";

        var quote = text[start] == '"' || text[start] == '\'' ? text[start] : '\0';
        if (quote != '\0') start++;

        var end = start;
        if (quote != '\0')
        {
            while (end < text.Length && text[end] != quote) end++;
        }
        else if (untilArgMarker)
        {
            var nextArg = text.IndexOf(" --", start, StringComparison.Ordinal);
            var nextLine = text.IndexOf('\n', start);
            var nextCarriage = text.IndexOf('\r', start);
            end = text.Length;
            if (nextArg >= 0) end = Math.Min(end, nextArg);
            if (nextLine >= 0) end = Math.Min(end, nextLine);
            if (nextCarriage >= 0) end = Math.Min(end, nextCarriage);
        }
        else
        {
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        }
        return text.Substring(start, Math.Max(0, end - start)).Trim();
    }

    /// <summary>启动日志的创建时间(没有就退最近写入时间减一小时);用于判断崩溃报告是不是这次启动产生的。</summary>
    public static DateTime LaunchStartedAt(string launchLogPath)
    {
        try
        {
            var info = new FileInfo(launchLogPath);
            if (!info.Exists) return DateTime.MinValue;
            var created = info.CreationTime;
            if (created.Year > 2000) return created;
            return info.LastWriteTime.AddHours(-1);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }
}
