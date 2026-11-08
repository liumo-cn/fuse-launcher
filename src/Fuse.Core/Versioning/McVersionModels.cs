using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Versioning;

/// <summary>版本规则：按 OS/架构/特性决定参数与库是否生效（官方 version.json 的 rules 数组）。</summary>
public sealed class McRule
{
    /// <summary>"allow" 或 "deny"。</summary>
    public string Action { get; set; } = "allow";

    public McRuleOs? Os { get; set; }

    /// <summary>特性开关，如 has_custom_resolution；null 表示不限。</summary>
    public Dictionary<string, bool>? Features { get; set; }

    public static McRule FromJToken(JToken t)
    {
        var o = (JObject)t;
        var rule = new McRule
        {
            Action = o.Value<string>("action") ?? "allow",
        };
        if (o["os"] is JObject os)
        {
            rule.Os = new McRuleOs
            {
                Name = os.Value<string>("name"),
                Arch = os.Value<string>("arch"),
            };
        }
        if (o["features"] is JObject f)
        {
            rule.Features = new Dictionary<string, bool>();
            foreach (var p in f.Properties())
                rule.Features[p.Name] = p.Value?.Type == JTokenType.Boolean && p.Value.Value<bool>();
        }
        return rule;
    }
}

public sealed class McRuleOs
{
    /// <summary>"windows" / "linux" / "osx"；null 表示不限。</summary>
    public string? Name { get; set; }

    /// <summary>"x86" 表示 32 位进程；null 表示不限。</summary>
    public string? Arch { get; set; }
}

/// <summary>arguments.jvm/game 中的一个条目：值可能是一个字符串或字符串数组，可带 rules。</summary>
public sealed class McValueArg
{
    public List<string> Values { get; set; } = new();

    public List<McRule>? Rules { get; set; }

    public static McValueArg FromJToken(JToken t)
    {
        if (t.Type == JTokenType.String)
            return new McValueArg { Values = { t.Value<string>()! } };
        var o = (JObject)t;
        var arg = new McValueArg { Rules = new List<McRule>() };
        if (o["value"] is JToken v)
        {
            if (v.Type == JTokenType.Array)
                foreach (var s in v) arg.Values.Add(s.Value<string>() ?? "");
            else
                arg.Values.Add(v.Value<string>() ?? "");
        }
        if (o["rules"] is JArray rules)
            foreach (var r in rules) arg.Rules.Add(McRule.FromJToken(r));
        else
            arg.Rules = null;
        return arg;
    }
}

public sealed class McArguments
{
    public List<McValueArg> Jvm { get; init; } = new();
    public List<McValueArg> Game { get; init; } = new();
}

public sealed class McArtifact
{
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
}

/// <summary>一个 Minecraft 库。新式 natives（1.19+）是名称带 :natives-windows 的独立库；旧式在 classifiers 里。</summary>
public sealed class McLibrary
{
    public string Name { get; set; } = "";

    public McArtifact? Artifact { get; set; }

    /// <summary>旧式 natives classifier（≤1.18 常见）："natives-windows" → 对应 jar。</summary>
    public Dictionary<string, McArtifact>? Classifiers { get; set; }

    public List<McRule>? Rules { get; set; }

    /// <summary>旧式 natives jar 中同时携带的类（部分库 extract 排除项），仅记录备用。</summary>
    /// <summary>maven 坐标 → libraries 下的相对路径;坐标非法返回 null。</summary>
    private static string? MavenRelativePath(string coord)
    {
        var at = coord.IndexOf('@');
        var ext = "jar";
        if (at >= 0)
        {
            ext = coord.Substring(at + 1);
            coord = coord.Substring(0, at);
        }
        var parts = coord.Split(':');
        if (parts.Length < 3) return null;
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 && parts[3].Length > 0 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
    }

    public static McLibrary FromJToken(JToken t)
    {
        var o = (JObject)t;
        var lib = new McLibrary
        {
            Name = o.Value<string>("name") ?? "",
        };
        if (o["rules"] is JArray rules)
        {
            lib.Rules = new List<McRule>();
            foreach (var r in rules) lib.Rules.Add(McRule.FromJToken(r));
        }
        if (o["downloads"] is JObject dl)
        {
            if (dl["artifact"] is JObject art)
                lib.Artifact = art.ToObject<McArtifact>();
            if (dl["classifiers"] is JObject cls)
            {
                lib.Classifiers = new Dictionary<string, McArtifact>();
                foreach (var p in cls.Properties())
                    lib.Classifiers[p.Name] = (p.Value as JObject)?.ToObject<McArtifact>() ?? new McArtifact();
            }
        }

        // 只有 maven 坐标、没有 downloads 节点的库(Fabric meta 的 launcherMeta 就是这样):
        // 按坐标合成 artifact 路径,下载地址取 url 字段(缺省按 Fabric maven)。
        // 不补的话 BuildClasspath 会把它整个跳过,启动直接 ClassNotFoundException(KnotClient)。
        // 但 natives-only 的库(如 jinput-platform)只有 classifiers,没有主 jar;
        // 按坐标合成会造出一个不存在的 jinput-platform-2.0.5.jar,体检永远报缺 1 个库。
        if (lib.Artifact == null && lib.Classifiers == null && o["downloads"] == null
            && !string.IsNullOrEmpty(lib.Name) && lib.Name.Contains(":"))
        {
            var rel = MavenRelativePath(lib.Name);
            if (rel != null)
            {
                var baseUrl = o.Value<string>("url") ?? "https://maven.fabricmc.net/";
                lib.Artifact = new McArtifact
                {
                    Path = rel,
                    Url = baseUrl.EndsWith("/") ? baseUrl + rel : baseUrl + "/" + rel,
                };
            }
        }
        return lib;
    }
}

public sealed class McJavaVersion
{
    public int MajorVersion { get; set; }
    public string Component { get; set; } = "";
}

public sealed class McAssetIndex
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha1 { get; set; }
    public long TotalSize { get; set; }
}

/// <summary>log4j 日志配置文件描述（version.json 的 logging.client）。</summary>
public sealed class McLoggingClient
{
    /// <summary>如 "-Dlog4j.configurationFile=${path}"。</summary>
    public string Argument { get; set; } = "";

    public McArtifact File { get; set; } = new();
}

/// <summary>合并继承链后的完整版本描述。</summary>
public sealed class McVersion
{
    public string Id { get; set; } = "";
    public string? InheritsFrom { get; set; }
    public string MainClass { get; set; } = "";
    /// <summary>游戏主 jar 的版本 id（缺省用 Id）。</summary>
    public string? Jar { get; set; }
    /// <summary>版本 json 声明的真实 MC 版本（第三方启动器 写的 26.1-Fabric 0.19.5 这类自包含版本靠它）。</summary>
    public string? ClientVersion { get; set; }
    public string? Assets { get; set; }
    public McAssetIndex? AssetIndex { get; set; }
    public McJavaVersion? JavaVersion { get; set; }
    public McArguments? Arguments { get; set; }
    /// <summary>旧版本（&lt;1.13）的整条游戏参数串。</summary>
    public string? MinecraftArguments { get; set; }
    public List<McLibrary> Libraries { get; set; } = new();
    public McLoggingClient? LoggingClient { get; set; }
    /// <summary>发布时间（ISO 8601），用于 QuickPlay 判定。</summary>
    public string? ReleaseTime { get; set; }
    public string? Type { get; set; }

    /// <summary>官方 downloads.client（自动修复用）。</summary>
    public McArtifact? ClientDownload { get; set; }

    public static McVersion FromJObject(JObject o)
    {
        var v = new McVersion
        {
            Id = o.Value<string>("id") ?? "",
            InheritsFrom = o.Value<string>("inheritsFrom"),
            MainClass = o.Value<string>("mainClass") ?? "",
            Jar = o.Value<string>("jar"),
            ClientVersion = o.Value<string>("clientVersion"),
            Assets = o.Value<string>("assets"),
            MinecraftArguments = o.Value<string>("minecraftArguments"),
            ReleaseTime = o.Value<string>("releaseTime"),
            Type = o.Value<string>("type"),
        };
        if (o["javaVersion"] is JObject jv)
            v.JavaVersion = new McJavaVersion
            {
                MajorVersion = jv.Value<int?>("majorVersion") ?? 8,
                Component = jv.Value<string>("component") ?? "",
            };
        if (o["assetIndex"] is JObject ai)
            v.AssetIndex = new McAssetIndex
            {
                Id = ai.Value<string>("id") ?? v.Assets ?? "",
                Url = ai.Value<string>("url") ?? "",
                Sha1 = ai.Value<string>("sha1"),
                TotalSize = ai.Value<long?>("totalSize") ?? 0,
            };
        if (o["arguments"] is JObject args)
        {
            var a = new McArguments();
            if (args["jvm"] is JArray jvm)
                foreach (var t in jvm) a.Jvm.Add(McValueArg.FromJToken(t));
            if (args["game"] is JArray game)
                foreach (var t in game) a.Game.Add(McValueArg.FromJToken(t));
            v.Arguments = a;
        }
        if (o["libraries"] is JArray libs)
            foreach (var t in libs) v.Libraries.Add(McLibrary.FromJToken(t));
        if (o["logging"]?["client"] is JObject lc)
            v.LoggingClient = new McLoggingClient
            {
                Argument = lc.Value<string>("argument") ?? "",
                File = lc["file"]?.ToObject<McArtifact>() ?? new McArtifact(),
            };
        if (o["downloads"]?["client"] is JObject cd)
            v.ClientDownload = cd.ToObject<McArtifact>();
        return v;
    }
}
