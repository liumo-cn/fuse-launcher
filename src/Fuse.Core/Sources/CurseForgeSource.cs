using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Sources;

/// <summary>CurseForge 搜索结果条目(最小字段集)。</summary>
public sealed record CfHit(
    int Id, string Slug, string Name, string Summary,
    string Author, long Downloads, string? IconUrl, string ProjectType);

/// <summary>CurseForge 项目详情。</summary>
public sealed record CfProject(
    int Id, string Slug, string Name, string Summary,
    string? DescriptionHtml, List<string> Categories);

/// <summary>CurseForge 文件条目。DownloadUrl 为空 = 作者未授权第三方分发。</summary>
public sealed record CfFile(
    int Id, int ModId, string DisplayName, string FileName, string? DownloadUrl,
    long FileLength, string[] GameVersions, string? ReleaseType, DateTime FileDate, string? Sha1);

/// <summary>CurseForge 依赖关系(relationType: 1=embedded, 2=optional, 3=required, ...)。</summary>
public sealed record CfDependency(int ModId, int RelationType);

/// <summary>
/// CurseForge 官方 API 接入骨架(需要 API Key 才能工作)。
///
/// 硬约束(来自 API 条款与申请承诺):
///  1) 下载只使用 API 返回的 downloadUrl,绝不自行拼接/镜像;为 null 时禁用下载。
///  2) Key 不写进源码:由宿主通过 <see cref="Configure"/> 注入(设置里 DPAPI 存储)。
///  3) 遵守限流:解析 Retry-After,429 退避;调用方负责缓存。
/// </summary>
public static class CurseForgeSource
{
    public const string ApiBase = "https://api.curseforge.com/v1";
    /// <summary>Minecraft 在 CurseForge 的 gameId。</summary>
    public const int MinecraftGameId = 432;

    // classId 对应关系(接入时再核对一次官方文档)
    public const int ClassMods = 6;
    public const int ClassModpacks = 4471;
    public const int ClassShaders = 6552;
    public const int ClassResourcePacks = 12;

    private static ICurseForgeTransport? _transport;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime At, string Body)> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    // 客户端令牌桶:容量 10,每秒补 1 个(约 60 次/分钟)。只用于防脚本刷,不防抓包。
    private static double _tokens = 10;
    private static DateTime _refillAt = DateTime.UtcNow;

    /// <summary>注入传输层(内嵌 Key 或代理)。传 null 表示未启用。</summary>
    public static void Configure(ICurseForgeTransport? transport) => _transport = transport;

    public static bool IsConfigured => _transport != null;

    // Modrinth 分类 slug → CurseForge categoryId(官方分类 slug 与 Modrinth 不同)
    private static readonly Dictionary<string, int> ModCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adventure"] = 422, ["decoration"] = 424, ["equipment"] = 434, ["food"] = 436,
        ["gameplay"] = 425, ["library"] = 421, ["magic"] = 419, ["management"] = 435,
        ["minigame"] = 425, ["optimization"] = 6814, ["social"] = 425, ["storage"] = 420,
        ["technology"] = 412, ["transportation"] = 415, ["utility"] = 5191, ["worldgen"] = 406,
    };

    private static readonly Dictionary<string, int> PackCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["technology"] = 4472, ["magic"] = 4473, ["adventure"] = 4475, ["skyblock"] = 4736,
        ["lightweight"] = 4481, ["multiplayer"] = 4484, ["quests"] = 4478, ["vanilla+"] = 5128,
    };

    private static readonly Dictionary<string, int> ShaderCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["realistic"] = 6553, ["fantasy"] = 6554, ["vanilla-like"] = 6555,
    };

    private static readonly Dictionary<string, int> ResourcePackCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["realistic"] = 400, ["simplistic"] = 403, ["vanilla-like"] = 403,
        ["32x"] = 394, ["16x"] = 393,
    };

    /// <summary>模组/整合包/光影/材质 → classId。</summary>
    public static int ClassIdFor(string projectType) => projectType switch
    {
        "modpack" => ClassModpacks,
        "shader" => ClassShaders,
        "resourcepack" => ClassResourcePacks,
        _ => ClassMods,
    };

    /// <summary>项目网页地址(详情页/网页按钮用,不消耗 API 配额)。</summary>
    public static string ProjectPageUrl(string slug, string projectType)
    {
        var section = projectType switch
        {
            "modpack" => "modpacks",
            "shader" => "shaders",
            "resourcepack" => "texture-packs",
            _ => "mc-mods",
        };
        return $"https://www.curseforge.com/minecraft/{section}/{slug}";
    }

    public static async Task<(List<CfHit> Hits, int Total)> SearchAsync(
        string query, string gameVersion, string loader, string category,
        int offset, int limit, CancellationToken ct, string projectType = "mod")
    {
        var url = ApiBase + "/mods/search" +
                  "?gameId=" + MinecraftGameId +
                  "&classId=" + ClassIdFor(projectType) +
                  "&index=" + Math.Max(0, offset) +
                  "&pageSize=" + (limit < 1 ? 1 : limit > 50 ? 50 : limit) +
                  "&searchFilter=" + Uri.EscapeDataString(query ?? "") +
                  "&sortField=6&sortOrder=desc";   // TotalDownloads 降序,和 Modrinth 的默认浏览一致
        if (!string.IsNullOrWhiteSpace(gameVersion))
            url += "&gameVersion=" + Uri.EscapeDataString(gameVersion);
        if (!string.IsNullOrWhiteSpace(loader) && loader != "全部")
            url += "&modLoaderType=" + LoaderTypeFor(loader);
        // 分类:把 Modrinth slug 映射成 CurseForge categoryId
        var categoryId = CategoryIdFor(ClassIdFor(projectType), category);
        if (categoryId > 0)
            url += "&categoryId=" + categoryId;

        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var hits = new List<CfHit>();
        foreach (var m in (json["data"] as JArray) ?? new JArray())
        {
            var links = m["links"] as JObject;
            var logo = m["logo"] as JObject;
            hits.Add(new CfHit(
                m.Value<int?>("id") ?? 0,
                links?.Value<string>("slug") ?? m.Value<string>("slug") ?? "",
                m.Value<string>("name") ?? "",
                m.Value<string>("summary") ?? "",
                (m["authors"] as JArray)?.First?["name"]?.Value<string>() ?? "",
                m.Value<long?>("downloadCount") ?? 0,
                logo?.Value<string>("thumbnailUrl") ?? logo?.Value<string>("url"),
                projectType));
        }
        var pagination = json["pagination"] as JObject;
        return (hits, pagination?.Value<int?>("totalCount") ?? hits.Count);
    }

    public static async Task<CfProject> GetProjectAsync(int projectId, CancellationToken ct)
    {
        var json = await GetJsonAsync(ApiBase + "/mods/" + projectId, ct).ConfigureAwait(false);
        var m = json["data"] ?? throw new InvalidOperationException("CurseForge 未返回项目数据");
        return new CfProject(
            m.Value<int?>("id") ?? projectId,
            (m["links"] as JObject)?.Value<string>("slug") ?? "",
            m.Value<string>("name") ?? "",
            m.Value<string>("summary") ?? "",
            m.Value<string>("description"),
            new List<string>());
    }

    public static async Task<List<CfFile>> GetFilesAsync(
        int projectId, string gameVersion, string loader, CancellationToken ct)
    {
        var url = ApiBase + "/mods/" + projectId + "/files?pageSize=50";
        if (!string.IsNullOrWhiteSpace(gameVersion))
            url += "&gameVersion=" + Uri.EscapeDataString(gameVersion);
        if (!string.IsNullOrWhiteSpace(loader) && loader != "全部")
            url += "&modLoaderType=" + LoaderTypeFor(loader);
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<CfFile>();
        foreach (var f in (json["data"] as JArray) ?? new JArray())
            list.Add(ToFile(f));
        return list;
    }

    /// <summary>单个文件元数据(整合包清单解析用)。</summary>
    public static async Task<CfFile> GetFileAsync(
        int projectId, int fileId, CancellationToken ct, bool skipRateLimit = false)
    {
        var json = await GetJsonAsync(
            ApiBase + "/mods/" + projectId + "/files/" + fileId, ct, skipRateLimit).ConfigureAwait(false);
        return ToFile(json["data"] ?? throw new InvalidOperationException("CurseForge 未返回文件数据"));
    }

    /// <summary>只返回 API 给的地址;作者禁用第三方分发时返回 null。</summary>
    public static async Task<string?> GetDownloadUrlAsync(int projectId, int fileId, CancellationToken ct)
    {
        var json = await GetJsonAsync(
            ApiBase + "/mods/" + projectId + "/files/" + fileId + "/download-url", ct)
            .ConfigureAwait(false);
        return json.Value<string>("data");
    }

    public static async Task<List<CfDependency>> GetDependenciesAsync(
        int projectId, int fileId, CancellationToken ct)
    {
        var json = await GetJsonAsync(
            ApiBase + "/mods/" + projectId + "/dependencies?fileId=" + fileId, ct).ConfigureAwait(false);
        var list = new List<CfDependency>();
        foreach (var d in (json["data"] as JArray) ?? new JArray())
            list.Add(new CfDependency(
                d.Value<int?>("modId") ?? 0,
                d.Value<int?>("relationType") ?? 0));
        return list;
    }

    private static int CategoryIdFor(int classId, string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || category == "全部") return 0;
        if (int.TryParse(category, out var numeric)) return numeric;
        var map = classId switch
        {
            ClassModpacks => PackCategories,
            ClassShaders => ShaderCategories,
            ClassResourcePacks => ResourcePackCategories,
            _ => ModCategories,
        };
        return map.TryGetValue(category!, out var id) ? id : 0;
    }

    public sealed record ResolvedCfFile(
        string ProjectId, string FileId, string FileName, string Url, string Sha1, string VersionNumber);

    /// <summary>冲突修复用:按 mod id/名称搜索 CurseForge,挑适配当前 MC/加载器的文件。</summary>
    public static async Task<ResolvedCfFile?> ResolveCompatibleAsync(
        string query, string gameVersion, string loader, string? constraint, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query)) return null;
        try
        {
            var search = await SearchAsync(query, gameVersion, loader, "全部", 0, 5, ct, "mod")
                .ConfigureAwait(false);
            var hit = search.Hits.FirstOrDefault(h =>
                          h.Slug.Equals(query, StringComparison.OrdinalIgnoreCase)) ?? search.Hits.FirstOrDefault();
            if (hit == null) return null;

            var files = await GetFilesAsync(hit.Id, gameVersion, loader, ct).ConfigureAwait(false);
            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file.DownloadUrl)) continue;
                if (!string.IsNullOrWhiteSpace(gameVersion) &&
                    file.GameVersions.Length > 0 &&
                    !file.GameVersions.Any(v => v.Equals(gameVersion, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!Fuse.Core.ModConflict.VersionRange.MatchesAny(file.DisplayName, constraint) &&
                    !Fuse.Core.ModConflict.VersionRange.MatchesAny(file.FileName, constraint))
                    continue;
                return new ResolvedCfFile(hit.Id.ToString(), file.Id.ToString(),
                    file.FileName, file.DownloadUrl!, file.Sha1 ?? "", file.DisplayName);
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static CfFile ToFile(JToken f) => new(
        f.Value<int?>("id") ?? 0,
        f.Value<int?>("modId") ?? 0,
        f.Value<string>("displayName") ?? "",
        f.Value<string>("fileName") ?? "",
        f.Value<string>("downloadUrl"),
        f.Value<long?>("fileLength") ?? 0,
        (f["gameVersions"] as JArray)?.ToObject<string[]>() ?? Array.Empty<string>(),
        f.Value<int?>("releaseType")?.ToString(),
        DateTime.TryParse(f.Value<string>("fileDate"), out var dt) ? dt : DateTime.MinValue,
        (f["hashes"] as JArray)?.FirstOrDefault(h => h.Value<string>("algo") == "1")?["value"]?.Value<string>());

    private static async Task<JObject> GetJsonAsync(string url, CancellationToken ct, bool skipRateLimit = false)
    {
        var transport = _transport ?? throw new InvalidOperationException("CurseForge API 未启用");
        var relative = url.StartsWith(ApiBase, StringComparison.OrdinalIgnoreCase)
            ? url.Substring(ApiBase.Length)
            : url;

        lock (Gate)
        {
            if (Cache.TryGetValue(relative, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
                return JObject.Parse(hit.Body);
        }

        if (!skipRateLimit) TakeToken();

        var text = await transport.GetAsync(relative, ct).ConfigureAwait(false);
        lock (Gate)
        {
            Cache[relative] = (DateTime.UtcNow, text);
            if (Cache.Count > 400) Cache.Clear(); // 粗暴上限,防止长期运行堆缓存
        }
        return JObject.Parse(text);
    }

    private static void TakeToken()
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            _tokens = Math.Min(10, _tokens + (now - _refillAt).TotalSeconds);
            _refillAt = now;
            if (_tokens < 1)
                throw new InvalidOperationException("请求过于频繁,请稍后重试");
            _tokens -= 1;
        }
    }

    /// <summary>CurseForge modLoaderType: 1=Forge, 4=Fabric, 5=Quilt, 6=NeoForge。</summary>
    private static int LoaderTypeFor(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0,
    };
}
