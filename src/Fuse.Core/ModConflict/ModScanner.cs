using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.ModConflict;

/// <summary>一个已安装模组的元数据(从 fabric.mod.json / mods.toml / mcmod.info 读取)。</summary>
public sealed class InstalledMod
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string Name { get; init; } = "";
    public string Environment { get; init; } = "";
    public Dictionary<string, string> Depends { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本模组提供的模块 id(Fabric API 的模块、Forge 的 jar-in-jar 子模组)。</summary>
    public List<string> Provides { get; init; } = new();
    public bool Disabled => FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
    public string Sha1 { get; init; } = "";
}

public static class ModScanner
{
    /// <summary>读取 mods 目录下的所有 jar(.jar 与 .jar.disabled)。</summary>
    public static List<InstalledMod> Scan(string modsDir, string? loader = null)
    {
        var result = new List<InstalledMod>();
        if (!Directory.Exists(modsDir)) return result;
        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) continue;
            var mod = Read(path, loader);
            if (mod != null) result.Add(mod);
        }
        return result;
    }

    public static InstalledMod? Read(string path, string? loader = null)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var fileName = Path.GetFileName(path);
            var sha1 = ComputeSha1(path);

            var fabric = FindEntry(zip, "fabric.mod.json");
            var modsToml = FindEntry(zip, "META-INF/mods.toml");
            var neoToml = FindEntry(zip, "META-INF/neoforge.mods.toml");
            var info = FindEntry(zip, "mcmod.info");

            var forgeLike = IsLoader(loader, "forge") || IsLoader(loader, "neoforge");
            var fabricLike = IsLoader(loader, "fabric") || IsLoader(loader, "quilt");

            if (forgeLike)
            {
                // 合并 jar 同时带 fabric.mod.json 和 mods.toml:Forge 只认后者
                var first = IsLoader(loader, "neoforge") ? (neoToml ?? modsToml) : (modsToml ?? neoToml);
                var second = ReferenceEquals(first, modsToml) ? neoToml : modsToml;
                if (first != null)
                {
                    var mod = FromToml(zip, first, path, fileName, sha1);
                    if (mod != null) return mod;
                }
                if (second != null && !ReferenceEquals(second, first))
                {
                    var mod = FromToml(zip, second, path, fileName, sha1);
                    if (mod != null) return mod;
                }
                if (info != null) return FromMcmodInfo(zip, info, path, fileName, sha1);
                // 只有 fabric.mod.json 的 jar 在 Forge 里不会被加载,不算 Forge 模组
                return TryFromBundle(zip, path, fileName, sha1);
            }

            if (fabricLike)
            {
                if (fabric != null) return FromFabric(zip, fabric, path, fileName, sha1);
                // 只有 Forge 元数据的 jar 在 Fabric 里同样不加载
                return TryFromBundle(zip, path, fileName, sha1);
            }

            if (fabric != null) return FromFabric(zip, fabric, path, fileName, sha1);
            var toml = neoToml ?? modsToml;
            if (toml != null) return FromToml(zip, toml, path, fileName, sha1);
            if (info != null) return FromMcmodInfo(zip, info, path, fileName, sha1);
            return TryFromBundle(zip, path, fileName, sha1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsLoader(string? loader, string name) =>
        string.Equals(loader, name, StringComparison.OrdinalIgnoreCase);

    private static InstalledMod FromFabric(
        ZipArchive zip, ZipArchiveEntry entry, string path, string fileName, string sha1)
    {
        var json = ReadJson(entry);
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
        var id = json.Value<string>("id") ?? "";

        CollectNestedIds(zip, provides);
        return new InstalledMod
        {
            FilePath = path,
            Sha1 = sha1,
            FileName = fileName,
            Id = id,
            Version = json.Value<string>("version") ?? "",
            Name = json.Value<string>("name") ?? id,
            Environment = json.Value<string>("environment") ?? "",
            Depends = depends,
            Provides = CleanProvides(provides, id),
        };
    }

    private static InstalledMod? FromToml(
        ZipArchive zip, ZipArchiveEntry entry, string path, string fileName, string sha1)
    {
        var blocks = ParseToml(ReadText(entry));
        var mods = blocks.Where(b => b.Header.Equals("mods", StringComparison.OrdinalIgnoreCase)).ToList();
        if (mods.Count == 0) return null;

        var primary = mods[0].Values;
        var id = Value(primary, "modId");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var version = ResolveJarVersion(zip, Value(primary, "version"));
        var name = Value(primary, "displayName");
        if (string.IsNullOrWhiteSpace(name)) name = id;

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        var provides = new List<string>();
        foreach (var extra in mods.Skip(1))
        {
            var extraId = Value(extra.Values, "modId");
            if (string.IsNullOrWhiteSpace(extraId)) continue;
            declared.Add(extraId);
            provides.Add(extraId);
        }

        var depends = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            if (!block.Header.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase)) continue;
            var owner = block.Header.Substring("dependencies.".Length).Trim();
            if (!declared.Contains(owner)) continue;

            var depId = Value(block.Values, "modId");
            if (string.IsNullOrWhiteSpace(depId)) continue;
            if (Value(block.Values, "side").Equals("SERVER", StringComparison.OrdinalIgnoreCase)) continue;
            var mandatory = Value(block.Values, "mandatory");
            var type = Value(block.Values, "type");
            var required = mandatory.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                           type.Equals("required", StringComparison.OrdinalIgnoreCase);
            if (!required) continue;
            depends[depId] = NormalizeForgeRange(Value(block.Values, "versionRange"));
        }

        CollectNestedIds(zip, provides);
        return new InstalledMod
        {
            FilePath = path,
            Sha1 = sha1,
            FileName = fileName,
            Id = id,
            Version = version,
            Name = name,
            Environment = "",
            Depends = depends,
            Provides = CleanProvides(provides, id),
        };
    }

    private static InstalledMod? FromMcmodInfo(
        ZipArchive zip, ZipArchiveEntry entry, string path, string fileName, string sha1)
    {
        var root = ReadJson(entry);
        var array = root as JArray ?? root["modList"] as JArray;
        if (array == null) return null;
        var mods = array.OfType<JObject>().ToList();
        if (mods.Count == 0) return null;

        var first = mods[0];
        var id = first.Value<string>("modid") ?? first.Value<string>("modId") ?? "";
        if (string.IsNullOrWhiteSpace(id)) return null;

        var depends = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var provides = new List<string>();
        foreach (var mod in mods)
        {
            foreach (var dep in mod["dependencies"] as JArray ?? new JArray())
                AddMcmodDependency(depends, dep.ToString());
            foreach (var dep in mod["requiredMods"] as JArray ?? new JArray())
                AddMcmodDependency(depends, dep.ToString());
            var extraId = mod.Value<string>("modid") ?? mod.Value<string>("modId") ?? "";
            if (!string.IsNullOrWhiteSpace(extraId) && !extraId.Equals(id, StringComparison.OrdinalIgnoreCase))
                provides.Add(extraId);
        }

        CollectNestedIds(zip, provides);
        return new InstalledMod
        {
            FilePath = path,
            Sha1 = sha1,
            FileName = fileName,
            Id = id,
            Version = first.Value<string>("version") ?? "",
            Name = first.Value<string>("name") ?? id,
            Environment = "",
            Depends = depends,
            Provides = CleanProvides(provides, id),
        };
    }

    private static void AddMcmodDependency(Dictionary<string, string> depends, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var at = raw.IndexOf('@');
        var id = (at > 0 ? raw.Substring(0, at) : raw).Trim();
        if (string.IsNullOrEmpty(id)) return;
        depends[id] = NormalizeForgeRange(at > 0 ? raw.Substring(at + 1).Trim() : "");
    }

    /// <summary>
    /// Forge 的 mods.toml 常把最低版本直接写成 "2.11.20"。按精确版本判断会把
    /// 2.12.x 这类新版本误报成不兼容,所以单个纯版本按 >= 处理;版本列表保持任一匹配。
    /// </summary>
    private static string NormalizeForgeRange(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0 || text == "*") return text;
        if (text.IndexOfAny(new[] { '<', '>', '=', '~', '^', '[', '(', '*', ' ', ',' }) >= 0)
            return text;
        return ">=" + text;
    }

    private sealed record NestedInfo(string Id, string Version, string Name);

    /// <summary>Forge/NeoForge 的 jar-in-jar:子模组也算“已提供”,不能报成缺前置。</summary>
    private static void CollectNestedIds(ZipArchive zip, List<string> provides, List<NestedInfo>? infos = null)
    {
        foreach (var nested in zip.Entries.Where(e =>
                     e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && e.Length > 0))
        {
            try
            {
                using var stream = nested.Open();
                using var nestedZip = new ZipArchive(stream, ZipArchiveMode.Read);
                var fabric = FindEntry(nestedZip, "fabric.mod.json");
                if (fabric != null)
                {
                    var json = ReadJson(fabric);
                    var id = json.Value<string>("id");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        provides.Add(id!);
                        infos?.Add(new NestedInfo(id!, json.Value<string>("version") ?? "",
                            json.Value<string>("name") ?? id!));
                    }
                    if (json["provides"] is JArray arr)
                        provides.AddRange(arr.Values<string>().Where(v => !string.IsNullOrWhiteSpace(v)));
                    continue;
                }
                var toml = FindEntry(nestedZip, "META-INF/neoforge.mods.toml")
                           ?? FindEntry(nestedZip, "META-INF/mods.toml");
                if (toml != null)
                {
                    foreach (var block in ParseToml(ReadText(toml))
                                 .Where(b => b.Header.Equals("mods", StringComparison.OrdinalIgnoreCase)))
                    {
                        var id = Value(block.Values, "modId");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        provides.Add(id);
                        infos?.Add(new NestedInfo(id, Value(block.Values, "version"),
                            Value(block.Values, "displayName")));
                    }
                    continue;
                }
                var info = FindEntry(nestedZip, "mcmod.info");
                if (info != null)
                {
                    var root = ReadJson(info);
                    var arr = root as JArray ?? root["modList"] as JArray ?? new JArray();
                    foreach (var mod in arr.OfType<JObject>())
                    {
                        var id = mod.Value<string>("modid") ?? mod.Value<string>("modId");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        provides.Add(id!);
                        infos?.Add(new NestedInfo(id!, mod.Value<string>("version") ?? "",
                            mod.Value<string>("name") ?? id!));
                    }
                }
            }
            catch (Exception)
            {
                // 嵌套 jar 解析失败不影响其余模组
            }
        }
    }

    /// <summary>
    /// 只有 META-INF/jarjar 的 bundle jar(KotlinForForge 这类):
    /// 顶层没有 mods.toml,真正的模组 ID 在子 jar 里。
    /// </summary>
    private static InstalledMod? TryFromBundle(ZipArchive zip, string path, string fileName, string sha1)
    {
        var provides = new List<string>();
        var infos = new List<NestedInfo>();
        CollectNestedIds(zip, provides, infos);
        if (infos.Count == 0) return null;

        var primary = infos[0];
        var version = ResolveJarVersion(zip, "${file.jarVersion}");
        return new InstalledMod
        {
            FilePath = path,
            Sha1 = sha1,
            FileName = fileName,
            Id = primary.Id,
            Version = string.IsNullOrEmpty(version) ? primary.Version : version,
            Name = string.IsNullOrEmpty(primary.Name) ? primary.Id : primary.Name,
            Environment = "",
            Depends = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Provides = CleanProvides(provides, primary.Id),
        };
    }

    private static List<string> CleanProvides(List<string> provides, string selfId) =>
        provides.Where(x => !string.IsNullOrWhiteSpace(x) &&
                            !x.Equals(selfId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name) =>
        zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static JToken ReadJson(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return JToken.Parse(reader.ReadToEnd());
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>mods.toml 的 version 常写 ${file.jarVersion},从 MANIFEST 取实现版本。</summary>
    private static string ResolveJarVersion(ZipArchive zip, string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf("${file.jarVersion}", StringComparison.OrdinalIgnoreCase) < 0)
            return raw;
        var manifest = FindEntry(zip, "META-INF/MANIFEST.MF");
        if (manifest == null) return "";
        string? impl = null, spec = null;
        using var reader = new StreamReader(manifest.Open());
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (key.Equals("Implementation-Version", StringComparison.OrdinalIgnoreCase)) impl = value;
            else if (key.Equals("Specification-Version", StringComparison.OrdinalIgnoreCase)) spec = value;
        }
        return impl ?? spec ?? "";
    }

    private sealed record TomlBlock(string Header, Dictionary<string, string> Values);

    private static string Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : "";

    /// <summary>只解析 mods.toml 需要的子集:[[表]] 和 key=value(字符串/布尔)。</summary>
    private static List<TomlBlock> ParseToml(string text)
    {
        var blocks = new List<TomlBlock>();
        Dictionary<string, string>? values = null;
        var inArray = false;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = StripTomlComment(raw).Trim();
            if (line.Length == 0) continue;
            if (inArray)
            {
                if (line.IndexOf(']') >= 0) inArray = false;
                continue;
            }
            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                var header = line.Substring(2, line.Length - 4).Trim();
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                blocks.Add(new TomlBlock(header, values));
                continue;
            }
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                values = null; // 普通表不需要
                continue;
            }
            if (values == null) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();
            if (value.StartsWith("[", StringComparison.Ordinal) && value.IndexOf(']') < 0)
            {
                inArray = true; // authors 之类的多行数组,跳过
                continue;
            }
            values[key] = UnquoteToml(value);
        }
        return blocks;
    }

    private static string StripTomlComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'')
            {
                quote = c;
                continue;
            }
            if (c == '#') return line.Substring(0, i);
        }
        return line;
    }

    private static string UnquoteToml(string raw)
    {
        raw = raw.Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
            return raw.Substring(1, raw.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (raw.Length >= 2 && raw[0] == '\'' && raw[raw.Length - 1] == '\'')
            return raw.Substring(1, raw.Length - 2);
        return raw;
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
