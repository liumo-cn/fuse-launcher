using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Fuse.Core.Download;

/// <summary>
/// 作者禁止第三方 API 下载的模组。记下 CurseForge 的项目/文件 ID,
/// 界面上可以一键打开对应网页,不用用户自己再搜。
/// </summary>
public sealed class ManualModDownload
{
    public string Name { get; set; } = "";
    public int ProjectId { get; set; }
    public int FileId { get; set; }

    public string PageUrl => ProjectId > 0 && FileId > 0
        ? "https://www.curseforge.com/projects/" + ProjectId + "/files/" + FileId
        : "";
}

/// <summary>
/// 从任务错误文本里回捞手动下载清单。老任务只有文字,新任务会直接带上结构化字段。
/// </summary>
public static class ManualModDownloads
{
    // A.jar (123/456)
    private static readonly Regex EntryPattern = new(
        @"(?<name>[^\s:：()（）/\\;；,，]+\.jar)\s*\((?<pid>\d+)/(?<fid>\d+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // · 123/456
    private static readonly Regex BarePattern = new(
        @"^[·•]\s*(?<pid>\d+)/(?<fid>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // 新版:清单已写到 <gameDir>\需手动下载的模组.txt
    private static readonly Regex NewDirPattern = new(
        @"清单已写到\s*(?<dir>.+?)\\需手动下载的模组\.txt",
        RegexOptions.Compiled);

    // 旧版:整合包已安装到 <gameDir>，但以下必需模组...
    private static readonly Regex OldDirPattern = new(
        @"整合包已安装到\s*(?<dir>.+?)[，,]",
        RegexOptions.Compiled);

    public static List<ManualModDownload> ParseFromMessage(string? message)
    {
        var result = new List<ManualModDownload>();
        if (string.IsNullOrEmpty(message)) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in EntryPattern.Matches(message))
            Add(result, seen, m.Groups["name"].Value,
                m.Groups["pid"].Value, m.Groups["fid"].Value);

        foreach (Match m in BarePattern.Matches(message))
            Add(result, seen, "", m.Groups["pid"].Value, m.Groups["fid"].Value);

        return result;
    }

    /// <summary>解析 BuildCurseForgeMrpack 返回的 "文件名 (项目/文件)" 或 "项目/文件"。</summary>
    public static List<ManualModDownload> ParseEntries(IEnumerable<string>? entries)
    {
        var result = new List<ManualModDownload>();
        if (entries == null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var m = EntryPattern.Match(raw);
            if (m.Success)
            {
                Add(result, seen, m.Groups["name"].Value,
                    m.Groups["pid"].Value, m.Groups["fid"].Value);
                continue;
            }
            var bare = Regex.Match(raw.Trim(), @"^(?<pid>\d+)/(?<fid>\d+)$");
            if (bare.Success)
                Add(result, seen, "", bare.Groups["pid"].Value, bare.Groups["fid"].Value);
        }
        return result;
    }

    /// <summary>手动模组该放进哪个 mods 文件夹;识别不出返回 null。</summary>
    public static string? ModsDirectoryFromMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = NewDirPattern.Match(message);
        if (!m.Success) m = OldDirPattern.Match(message);
        if (!m.Success) return null;
        var dir = m.Groups["dir"].Value.Trim().Trim('"');
        if (string.IsNullOrEmpty(dir)) return null;
        return Path.Combine(dir, "mods");
    }

    private static void Add(List<ManualModDownload> list, HashSet<string> seen,
        string name, string pidText, string fidText)
    {
        if (!int.TryParse(pidText, out var pid) || !int.TryParse(fidText, out var fid)) return;
        if (pid <= 0 || fid <= 0) return;
        if (!seen.Add(pid + "/" + fid)) return;
        list.Add(new ManualModDownload { Name = name, ProjectId = pid, FileId = fid });
    }
}
