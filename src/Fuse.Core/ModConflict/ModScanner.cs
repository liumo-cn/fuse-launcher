using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.ModConflict;

/// <summary>一个已安装模组的元数据(从 fabric.mod.json 读取)。</summary>
public sealed class InstalledMod
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string Name { get; init; } = "";
    public string Environment { get; init; } = "";
    public Dictionary<string, string> Depends { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本模组提供的模块 id(Fabric API 用它提供大量 fabric-* 模块)。</summary>
    public List<string> Provides { get; init; } = new();
    public bool Disabled => FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
    public string Sha1 { get; init; } = "";
}

public static class ModScanner
{
    /// <summary>读取 mods 目录下的所有 jar(.jar 与 .jar.disabled)。</summary>
    public static List<InstalledMod> Scan(string modsDir)
    {
        var result = new List<InstalledMod>();
        if (!Directory.Exists(modsDir)) return result;
        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) continue;
            var mod = Read(path);
            if (mod != null) result.Add(mod);
        }
        return result;
    }

    public static InstalledMod? Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("fabric.mod.json");
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            var json = JObject.Parse(reader.ReadToEnd());
            var depends = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (json["depends"] is JObject deps)
            {
                foreach (var prop in deps.Properties())
                {
                    var value = prop.Value.Type == JTokenType.Array
                        ? string.Join(" ", prop.Value.Values<string>())
                        : prop.Value.ToString();
                    depends[prop.Name] = value;
                }
            }
            var provides = new List<string>();
            if (json["provides"] is JArray arr)
                provides.AddRange(arr.Values<string>().Where(v => !string.IsNullOrWhiteSpace(v)));
            // 现代 Fabric API 把模块放在嵌套 jar 里:顶层 fabric.mod.json 不写 provides,
            // 必须扫描 jar-in-jar,否则会把 fabric-rendering-fluids-v1 之类误判成缺失前置。
            foreach (var nested in zip.Entries.Where(e =>
                         e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && e.Length > 0))
            {
                try
                {
                    using var nestedStream = nested.Open();
                    using var nestedZip = new ZipArchive(nestedStream, ZipArchiveMode.Read);
                    var nestedEntry = nestedZip.GetEntry("fabric.mod.json");
                    if (nestedEntry == null) continue;
                    using var nestedReader = new StreamReader(nestedEntry.Open());
                    var nestedJson = JObject.Parse(nestedReader.ReadToEnd());
                    var nestedId = nestedJson.Value<string>("id");
                    if (!string.IsNullOrWhiteSpace(nestedId)) provides.Add(nestedId!);
                    if (nestedJson["provides"] is JArray nestedProvides)
                        provides.AddRange(nestedProvides.Values<string>()
                            .Where(v => !string.IsNullOrWhiteSpace(v)));
                }
                catch (Exception)
                {
                    // 嵌套 jar 解析失败不影响其余模组
                }
            }
            return new InstalledMod
            {
                FilePath = path,
                Sha1 = ComputeSha1(path),
                FileName = Path.GetFileName(path),
                Id = json.Value<string>("id") ?? "",
                Version = json.Value<string>("version") ?? "",
                Name = json.Value<string>("name") ?? json.Value<string>("id") ?? "",
                Environment = json.Value<string>("environment") ?? "",
                Depends = depends,
                Provides = provides,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ComputeSha1(string path)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception)
        {
            return "";
        }
    }
}
