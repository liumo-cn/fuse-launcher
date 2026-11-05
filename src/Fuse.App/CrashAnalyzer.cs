using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Fuse.App;

/// <summary>
/// 崩溃分析:读最近一次 crash-report / latest.log,把日志里出现的模组 id/名称
/// 与 mods 目录里的 jar 元数据对上,给出"疑似模组"列表。
/// 只是启发式(不能替代逐个人工排查),但足以把"全部禁用 → 一个个开"简化为"先关掉可疑的几个"。
/// </summary>
internal static class CrashAnalyzer
{
    internal sealed class Suspect
    {
        public string FilePath { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    internal sealed class Result
    {
        public string SourceFile { get; set; } = "";
        public DateTime When { get; set; }
        public List<Suspect> Suspects { get; set; } = new();
        public string Excerpt { get; set; } = "";
    }

    public static Result? Analyze(string instanceDir, string modsDir)
    {
        FileInfo? source = null;
        var crashDir = Path.Combine(instanceDir, "crash-reports");
        if (Directory.Exists(crashDir))
            source = new DirectoryInfo(crashDir).GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        if (source == null)
        {
            var log = Path.Combine(instanceDir, "logs", "latest.log");
            if (File.Exists(log)) source = new FileInfo(log);
        }
        if (source == null) return null;

        var text = ReadTail(source.FullName, 256 * 1024);
        return new Result
        {
            SourceFile = source.FullName,
            When = source.LastWriteTime,
            Suspects = FindSuspects(text, modsDir),
            Excerpt = BuildExcerpt(text),
        };
    }

    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            var take = (int)Math.Min(length, maxBytes);
            fs.Seek(length - take, SeekOrigin.Begin);
            var buffer = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = fs.Read(buffer, read, take - read);
                if (n <= 0) break;
                read += n;
            }
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static List<Suspect> FindSuspects(string text, string modsDir)
    {
        var suspects = new List<Suspect>();
        if (string.IsNullOrEmpty(text) || !Directory.Exists(modsDir)) return suspects;

        foreach (var jar in Directory.GetFiles(modsDir, "*.jar"))
        {
            var (id, name) = ReadModMeta(jar);
            var keys = new List<string>();
            if (id.Length >= 3) keys.Add(id);
            if (name.Length >= 4) keys.Add(name);
            // jar 文件名也作为线索(很多日志直接打印文件名)
            var baseName = Path.GetFileNameWithoutExtension(jar);
            if (baseName.Length >= 4) keys.Add(baseName);

            var hit = keys.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hit) continue;
            suspects.Add(new Suspect { FilePath = jar, Id = id, Name = string.IsNullOrEmpty(name) ? baseName : name });
        }
        return suspects;
    }

    /// <summary>从 jar 里读 Fabric / Forge / 旧版 mcmod.info 的模组 id 与显示名。</summary>
    private static (string Id, string Name) ReadModMeta(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var fabric = zip.GetEntry("fabric.mod.json");
            if (fabric != null)
            {
                using var reader = new StreamReader(fabric.Open());
                var json = JObject.Parse(reader.ReadToEnd());
                return (json.Value<string>("id") ?? "", json.Value<string>("name") ?? "");
            }
            var toml = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase));
            if (toml != null)
            {
                using var reader = new StreamReader(toml.Open());
                var text = reader.ReadToEnd();
                var id = Regex.Match(text, "modId\\s*=\\s*\"([^\"]+)\"");
                var name = Regex.Match(text, "displayName\\s*=\\s*\"([^\"]+)\"");
                return (id.Success ? id.Groups[1].Value : "",
                        name.Success ? name.Groups[1].Value : "");
            }
            var info = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("mcmod.info", StringComparison.OrdinalIgnoreCase));
            if (info != null)
            {
                using var reader = new StreamReader(info.Open());
                var arr = JArray.Parse(reader.ReadToEnd());
                var first = arr.OfType<JObject>().FirstOrDefault();
                if (first != null)
                    return (first.Value<string>("modid") ?? "", first.Value<string>("name") ?? "");
            }
        }
        catch (Exception)
        {
            // 元数据读不出来就只按文件名匹配
        }
        return ("", "");
    }

    private static string BuildExcerpt(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var candidates = lines
            .Where(l => l.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        l.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        l.IndexOf("Caused by", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        l.IndexOf("Incompatible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        l.IndexOf("Requires", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        l.TrimStart().StartsWith("at ", StringComparison.Ordinal))
            .ToList();
        var picked = candidates.Skip(Math.Max(0, candidates.Count - 60));
        var excerpt = string.Join("\n", picked);
        return excerpt.Length > 6000 ? excerpt.Substring(excerpt.Length - 6000) : excerpt;
    }
}
