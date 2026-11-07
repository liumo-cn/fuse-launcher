using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Sources;

/// <summary>
/// 「这条内容是这个实例里装过的」记录:<实例目录\content.mcdl.json。
/// 打勾状态不靠内存,而是靠"记录 + 文件仍在磁盘"这两个事实推导,重启/切版本都不会误报。
/// </summary>
public static class ContentRecordStore
{
    public sealed class Entry
    {
        public string Source { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Sha1 { get; set; }
        public string InstalledAt { get; set; } = "";
    }

    private sealed class Document
    {
        public List<Entry> Items { get; set; } = new();
    }

    private static readonly object Gate = new();

    public static string PathFor(string instanceDir) =>
        Path.Combine(instanceDir, "content.mcdl.json");

    /// <summary>记录一次安装(同 source+project+fileName 去重)。失败静默,不影响下载本身。</summary>
    public static void Record(string instanceDir, string source, string projectId,
        string fileName, string kind, string? sha1)
    {
        if (string.IsNullOrWhiteSpace(instanceDir) || string.IsNullOrWhiteSpace(fileName)) return;
        try
        {
            lock (Gate)
            {
                var doc = Load(instanceDir);
                doc.Items.RemoveAll(e =>
                    e.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                    e.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase) &&
                    e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
                doc.Items.Add(new Entry
                {
                    Source = source,
                    ProjectId = projectId,
                    FileName = fileName,
                    Kind = kind,
                    Sha1 = sha1,
                    InstalledAt = DateTime.UtcNow.ToString("o"),
                });
                Save(instanceDir, doc);
            }
        }
        catch (Exception)
        {
            // 记录失败只会导致少一个打勾,不影响内容
        }
    }

    /// <summary>
    /// 该项目是否仍安装在实例里:有记录且对应文件存在。
    /// mods 兼容 .disabled(被禁用的模组仍算"这个版本里有")。
    /// </summary>
    public static bool IsInstalled(string instanceDir, string kind, string projectId, string destDir)
    {
        if (string.IsNullOrWhiteSpace(instanceDir) || string.IsNullOrWhiteSpace(projectId)) return false;
        try
        {
            var doc = Load(instanceDir);
            foreach (var e in doc.Items)
            {
                if (!e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase)) continue;
                var path = Path.Combine(destDir, e.FileName);
                if (File.Exists(path)) return true;
                if (File.Exists(path + ".disabled")) return true;
                if (Directory.Exists(path)) return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Document Load(string instanceDir)
    {
        var path = PathFor(instanceDir);
        if (!File.Exists(path)) return new Document();
        try
        {
            return JsonConvert.DeserializeObject<Document>(File.ReadAllText(path)) ?? new Document();
        }
        catch (Exception)
        {
            return new Document();
        }
    }

    private static void Save(string instanceDir, Document doc)
    {
        var path = PathFor(instanceDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(doc, Formatting.Indented));
    }
}
