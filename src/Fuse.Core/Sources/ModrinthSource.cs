using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Sources;

/// <summary>Modrinth 搜索结果条目。</summary>
public sealed record ModrinthHit(
    string ProjectId, string Slug, string Title, string Description, long Downloads,
    string ProjectType, string IconUrl, string DateModified);

/// <summary>按文件哈希反查到的一条 Modrinth 版本(用于本地模组更新检查)。</summary>
public sealed record HashedModrinthVersion(
    string Hash, string ProjectId, string VersionNumber, string VersionName,
    ModrinthVersionFile File);

/// <summary>Modrinth 上某个模组文件的下载信息。</summary>
public sealed record ModrinthFile(string Url, string FileName, string Sha1, string VersionId, List<string> GameVersions);

/// <summary>模组版本文件 + 前置依赖(required)项目 id 列表。</summary>
/// <summary>整合包/模组的必需依赖(project_id + 可选的精确 version_id)。</summary>
public sealed record ModrinthDependency(string ProjectId, string VersionId);

public sealed record ModrinthVersionFile(
    string Url, string FileName, string Sha1, string VersionId,
    List<string> GameVersions, List<ModrinthDependency> RequiredDeps);

/// <summary>
/// Modrinth 源（PRD 13.2 确认的模组主源，开放 API 免 Key）。
/// 仅做元数据聚合 + 官方文件直链下载，遵循各作者许可（PRD §2.3）。
/// </summary>
public static class ModrinthSource
{

    /// <summary>Modrinth 项目网页地址(网页跳转按钮用)。</summary>
    public static string ProjectPageUrl(string slug, string projectType)
    {
        var section = projectType switch
        {
            "modpack" => "modpack",
            "shader" => "shader",
            "resourcepack" => "resourcepack",
            _ => "mod",
        };
        return $"https://modrinth.com/{section}/{slug}";
    }

    public const string ApiBase = "https://api.modrinth.com/v2";

    // tag/game_version 只拉一次:版本组展开(见 ExpandGameVersionsAsync)每次搜索都要用
    private static List<string>? _gameVersionTags;

    /// <summary>
    /// Modrinth 的 versions facet 是精确匹配:查 "1.20" 不会命中标了 "1.20.1" 的模组,
    /// 而绝大多数模组只标补丁版。所以把 1.20 展开为 1.20 / 1.20.1 / 1.20.2 …(同一次要版本)。
    /// </summary>
    private static async Task<List<string>> ExpandGameVersionsAsync(
        HttpClient http, string gameVersion, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(gameVersion)) return new List<string>();
        if (_gameVersionTags == null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/tag/game_version");
                request.Headers.UserAgent.ParseAdd("FuseLauncher/1.0.0 Java/17.0.15 (Windows 10; x64; minecraft launcher)");
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var arr = JArray.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                _gameVersionTags = arr.Cast<JObject>()
                    .Select(t => t.Value<string>("version") ?? "")
                    .Where(v => v.Length > 0)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string> { gameVersion }; // 拿不到标签就退回精确匹配
            }
        }

        var prefix = gameVersion + ".";
        var group = _gameVersionTags!.Where(v => v == gameVersion || v.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (!group.Contains(gameVersion)) group.Add(gameVersion);
        return group;
    }

    /// <summary>
    /// 搜索模组(支持分页/版本/加载器/分类筛选)。
    /// 返回 (hits, 总数):总数用于无限滚动判断「没有更多了」。
    /// </summary>
    public static async Task<(List<ModrinthHit> Hits, int Total)> SearchAsync(
        HttpClient http, string query, string gameVersion, string loader,
        string category, int offset, int limit, CancellationToken ct,
        string projectType = "mod")
    {

        var facets = new JArray();
        var versions = await ExpandGameVersionsAsync(http, gameVersion, ct).ConfigureAwait(false);
        facets.Add(new JArray(versions.Select(v => (object)("versions:" + v)).ToArray()));
        if (!string.IsNullOrEmpty(loader) && loader != "全部")
            facets.Add(new JArray($"categories:{loader}"));
        if (!string.IsNullOrEmpty(category) && category != "全部")
            facets.Add(new JArray($"categories:{category}"));
        // mod(默认)/ shader(光影)/ resourcepack(材质包)
        facets.Add(new JArray("project_type:" + projectType));

        // 有搜索词时按相关性排(名称匹配的靠前),纯浏览仍按下载量降序
        var sort = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/search?query={Uri.EscapeDataString(query ?? "")}" +
            $"&limit={limit}&offset={offset}&index={sort}&facets={Uri.EscapeDataString(facets.ToString(Newtonsoft.Json.Formatting.None))}");
        request.Headers.UserAgent.ParseAdd("FuseLauncher/1.0.0 Java/17.0.15 (Windows 10; x64; minecraft launcher)");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var total = json.Value<int?>("total_hits") ?? 0;
        var hits = json["hits"]!.Cast<JObject>()
            .Select(h => new ModrinthHit(
                h.Value<string>("project_id") ?? "",
                h.Value<string>("slug") ?? "",
                h.Value<string>("title") ?? "",
                h.Value<string>("description") ?? "",
                h.Value<long?>("downloads") ?? 0,
                "mod",
                h.Value<string>("icon_url") ?? "",
                IsoDate(h["date_modified"])))
            .Where(h => !string.IsNullOrEmpty(h.ProjectId))
            .ToList();
        return (hits, total);
    }

    /// <summary>
    /// 取 yyyy-MM-dd。Newtonsoft 会把 ISO 时间串自动解析成 DateTime,
    /// 直接 Value&lt;string&gt;() 会按系统区域渲染成 10/28/2026,所以这里显式格式化。
    /// </summary>
    private static string IsoDate(JToken? token)
    {
        if (token is JValue value)
        {
            if (value.Value is DateTime dt)
                return dt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (value.Value is string s)
                return s.Length >= 10 ? s.Substring(0, 10) : s;
        }
        return "";
    }

    /// <summary>取某模组适配指定 MC 版本（与加载器）的最新文件 + 前置依赖。</summary>
    public static async Task<ModrinthVersionFile> GetLatestVersionFileAsync(
        HttpClient http, string projectId, string gameVersion, string loader, CancellationToken ct)
    {
        var candidates = await QueryVersionObjectsAsync(http, projectId, gameVersion, loader, ct)
            .ConfigureAwait(false);
        if (candidates.Count == 0) throw NoVersionError(gameVersion, loader);
        // 版本组展开(26.1 → 26.1/26.1.1/26.1.2…)后,列表按发布时间倒序。
        // 规则:优先当前版本精确匹配;没有则只接受"不高于当前版本"的构建,
        // 绝不把 26.1.2 的文件装进 26.1 实例(Iris/Sodium 不兼容的根因)。
        var exact = candidates.FirstOrDefault(v => MatchesExactGameVersion(v, gameVersion));
        if (exact != null) return ParseVersionFile(exact);

        var older = candidates
            .Where(v => HasVersionAtOrBelow(v, gameVersion))
            .OrderByDescending(v => MaxVersionAtOrBelow(v, gameVersion))
            .FirstOrDefault();
        if (older != null) return ParseVersionFile(older);

        throw new InvalidOperationException(
            $"该模组只提供更新的 Minecraft {gameVersion} 构建（例如 26.1.2），当前实例是 {gameVersion}；" +
            "请升级游戏版本，或换一个支持当前版本的模组版本。");
    }

    /// <summary>
    /// 取某模组适配指定 MC 版本的**全部**候选文件(精确版本优先,其余按发布时间倒序)。
    /// 光影前置需要逐条检查"它要求的配套版本是否也支持当前 MC 版本",不能只看最新一条。
    /// </summary>
    public static async Task<List<ModrinthVersionFile>> GetVersionFilesAsync(
        HttpClient http, string projectId, string gameVersion, string loader, CancellationToken ct)
    {
        var candidates = await QueryVersionObjectsAsync(http, projectId, gameVersion, loader, ct)
            .ConfigureAwait(false);
        if (candidates.Count == 0) throw NoVersionError(gameVersion, loader);
        return candidates
            .Where(v => MatchesExactGameVersion(v, gameVersion) || HasVersionAtOrBelow(v, gameVersion))
            .OrderByDescending(v => MatchesExactGameVersion(v, gameVersion))
            .ThenByDescending(v => MaxVersionAtOrBelow(v, gameVersion))
            .Select(ParseVersionFile)
            .ToList();
    }

    /// <summary>该版本是否有 game_versions 落在当前版本或更早(不含更新的补丁)。</summary>
    private static bool HasVersionAtOrBelow(JObject version, string current)
    {
        var currentParsed = Fuse.Core.ModConflict.VersionRange.Parse(current);
        if (currentParsed == null) return false;
        foreach (var gv in (version["game_versions"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>())
        {
            var parsed = Fuse.Core.ModConflict.VersionRange.Parse(gv ?? "");
            if (parsed != null && parsed.CompareTo(currentParsed) <= 0) return true;
        }
        return false;
    }

    private static int MaxVersionAtOrBelow(JObject version, string current)
    {
        var currentParsed = Fuse.Core.ModConflict.VersionRange.Parse(current);
        var best = -1;
        if (currentParsed == null) return best;
        foreach (var gv in (version["game_versions"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>())
        {
            var parsed = Fuse.Core.ModConflict.VersionRange.Parse(gv ?? "");
            if (parsed == null) continue;
            if (parsed.Major != currentParsed.Major) continue;
            var key = parsed.Minor * 1000 + parsed.Patch;
            if (parsed.CompareTo(currentParsed) <= 0 && key > best) best = key;
        }
        return best;
    }

    private static InvalidOperationException NoVersionError(string gameVersion, string loader)
        => new(string.IsNullOrEmpty(loader)
            ? $"没有适配 {gameVersion} 的版本"
            : $"该模组没有适配 {gameVersion}（{loader}）的版本");

    /// <summary>查询 /project/{id}/version,返回原始 JSON 条目(版本组展开 + 加载器过滤)。</summary>
    private static async Task<List<JObject>> QueryVersionObjectsAsync(
        HttpClient http, string projectId, string gameVersion, string loader, CancellationToken ct)
    {
        // 版本按组展开(1.20 → 1.20/1.20.1/…),否则只标补丁版的模组会被判成「无适配版本」
        var versions = await ExpandGameVersionsAsync(http, gameVersion, ct).ConfigureAwait(false);
        // 注意:facets 只对 /search 有效;/project/{id}/version 用的是 game_versions / loaders
        // 两个 JSON 数组参数。用错参数会被静默忽略,直接返回最新版本(可能属于别的 MC 版本)。
        var gv = Uri.EscapeDataString(
            "[" + string.Join(",", versions.Select(v => $"\"{v}\"")) + "]");
        // loader 为空(光影/材质包不按加载器过滤)时省略 loaders 参数
        var ld = string.IsNullOrEmpty(loader) || loader == "全部"
            ? ""
            : "&loaders=" + Uri.EscapeDataString($"[\"{loader}\"]");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/project/{projectId}/version?game_versions={gv}{ld}");
        request.Headers.UserAgent.ParseAdd("FuseLauncher/1.0.0 Java/17.0.15 (Windows 10; x64; minecraft launcher)");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var arr = JArray.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return arr.Cast<JObject>().ToList();
    }

    private static bool MatchesExactGameVersion(JObject version, string gameVersion)
        => (version["game_versions"] as JArray)?.Any(t =>
               string.Equals(t.Value<string>(), gameVersion, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>把 Modrinth 的一条 version 记录解析成下载文件 + 依赖(含精确版本号)。</summary>
    private static ModrinthVersionFile ParseVersionFile(JObject version)
    {
        var files = version["files"] as JArray ?? new JArray();
        var file = files.Cast<JObject>().FirstOrDefault(f => f.Value<bool?>("primary") == true)
                   ?? files.Cast<JObject>().FirstOrDefault()
                   ?? throw new InvalidOperationException("版本条目缺少文件");
        var deps = (version["dependencies"] as JArray ?? new JArray()).Cast<JObject>()
            .Where(d => d.Value<string>("dependency_type") == "required")
            .Select(d => new ModrinthDependency(
                d.Value<string>("project_id") ?? "",
                d.Value<string>("version_id") ?? ""))
            .Where(d => d.ProjectId.Length > 0)
            .ToList();
        return new ModrinthVersionFile(
            file.Value<string>("url") ?? throw new InvalidOperationException("文件缺少下载链接"),
            file.Value<string>("filename") ?? "mod.jar",
            file["hashes"]?.Value<string>("sha1") ?? "",
            version.Value<string>("id") ?? "",
            version["game_versions"]?.Select(t => t.Value<string>() ?? "").ToList() ?? new List<string>(),
            deps);
    }

    /// <summary>按精确 version_id 取文件(依赖里指定的"配套版本"用它下载)。</summary>
    private static readonly Dictionary<string, bool> JarCompatibilityCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 以 jar 内 fabric.mod.json 的 depends.minecraft 为准判断兼容性。
    /// Modrinth 的 game_versions 标签可能保守(实测 Sodium 0.9.1 标 26.1.2,但 jar 里根本没有
    /// minecraft 依赖,在 26.1 上能正常加载);只看标签会把可用组合误判成不兼容。
    /// </summary>
    public static async Task<bool> IsJarCompatibleWithMcAsync(
        HttpClient http, ModrinthVersionFile file, string gameVersion, CancellationToken ct)
    {
        if (file.GameVersions.Any(v => v.Equals(gameVersion, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (JarCompatibilityCache.TryGetValue(file.Url, out var cached)) return cached;
        try
        {
            using var response = await http.GetAsync(file.Url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            using var memory = new MemoryStream(bytes);
            using var zip = new System.IO.Compression.ZipArchive(memory);
            var entry = zip.GetEntry("fabric.mod.json");
            if (entry == null) return false;
            using var reader = new StreamReader(entry.Open());
            var mod = JObject.Parse(reader.ReadToEnd());
            var range = mod["depends"]?["minecraft"]?.ToString();
            var ok = string.IsNullOrWhiteSpace(range) ||
                     Fuse.Core.ModConflict.VersionRange.Matches(gameVersion, range);
            JarCompatibilityCache[file.Url] = ok;
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<ModrinthVersionFile> GetVersionFileByIdAsync(
        HttpClient http, string versionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/version/{versionId}");
        request.Headers.UserAgent.ParseAdd("FuseLauncher/1.0.0 Java/17.0.15 (Windows 10; x64; minecraft launcher)");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var version = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return ParseVersionFile(version);
    }

    /// <summary>按文件哈希批量反查 Modrinth 版本(本地模组更新检查用)。</summary>
    public static Task<Dictionary<string, HashedModrinthVersion>> GetVersionsByHashesAsync(
        HttpClient http, IReadOnlyCollection<string> hashes, string algorithm, CancellationToken ct)
        => PostHashesAsync(http, "/version_files", hashes, algorithm, null, null, ct);

    /// <summary>
    /// 按文件哈希批量取"当前 MC 版本 + 加载器下最新的" Modrinth 版本。
    /// 注意 game_versions 传**精确版本**:版本组展开会让 26.1 实例拿到 26.1.2 的构建。
    /// </summary>
    public static Task<Dictionary<string, HashedModrinthVersion>> GetLatestVersionsByHashesAsync(
        HttpClient http, IReadOnlyCollection<string> hashes, string algorithm,
        string gameVersion, string loader, CancellationToken ct)
        => PostHashesAsync(http, "/version_files/update", hashes, algorithm,
            string.IsNullOrEmpty(gameVersion) ? null : new[] { gameVersion },
            string.IsNullOrEmpty(loader) ? null : loader, ct);

    private static async Task<Dictionary<string, HashedModrinthVersion>> PostHashesAsync(
        HttpClient http, string path, IReadOnlyCollection<string> hashes, string algorithm,
        IReadOnlyCollection<string>? gameVersions, string? loader, CancellationToken ct)
    {
        var result = new Dictionary<string, HashedModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        if (hashes.Count == 0) return result;

        var body = new JObject
        {
            ["hashes"] = new JArray(hashes),
            ["algorithm"] = algorithm,
        };
        if (gameVersions is { Count: > 0 }) body["game_versions"] = new JArray(gameVersions);
        if (!string.IsNullOrEmpty(loader)) body["loaders"] = new JArray(loader!);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + path)
        {
            Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.UserAgent.ParseAdd("FuseLauncher/1.0.0 Java/17.0.15 (Windows 10; x64; minecraft launcher)");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var obj = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        foreach (var prop in obj.Properties())
        {
            if (prop.Value is not JObject v) continue;
            var file = ParseVersionFile(v);
            result[prop.Name] = new HashedModrinthVersion(
                prop.Name,
                v.Value<string>("project_id") ?? "",
                v.Value<string>("version_number") ?? "",
                v.Value<string>("name") ?? v.Value<string>("version_number") ?? "",
                file);
        }
        return result;
    }

    /// <summary>
    /// 整合包:取某项目最新的版本文件(不按 MC 版本/加载器过滤——
    /// 整合包条目的游戏版本随包而定),并带回版本号与版本名。
    /// </summary>
    public static async Task<(ModrinthVersionFile File, string VersionNumber, string VersionName)>
        GetLatestPackFileAsync(HttpClient http, string projectId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/project/{projectId}/version");
        request.Headers.UserAgent.ParseAdd("FuseLauncher/1.0.0 Java/17.0.15 (Windows 10; x64; minecraft launcher)");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var arr = JArray.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var first = arr.FirstOrDefault() as JObject
            ?? throw new InvalidOperationException("该整合包没有任何版本");

        var record = ParseVersionFile(first);
        return (record, first.Value<string>("version_number") ?? "",
            first.Value<string>("name") ?? first.Value<string>("version_number") ?? "");
    }

    /// <summary>冲突修复用:按项目 id/slug 找满足版本约束的兼容文件。</summary>
    public sealed record ResolvedModFile(
        string ProjectId, string VersionId, string VersionNumber, string FileName, string Url, string Sha1);

    public static async Task<ResolvedModFile?> ResolveCompatibleAsync(
        HttpClient http, string idOrSlug, string gameVersion, string loader,
        string? constraint, CancellationToken ct, string? sha1 = null)
    {
        try
        {
            var candidates = await QueryVersionObjectsAsync(http, idOrSlug, gameVersion, loader, ct)
                .ConfigureAwait(false);
            // 冲突修复必须用"明确支持当前 MC 版本"的文件:
            // 只标 26.1.2 的 Sodium 不能装到 26.1 实例上(会被 Fabric 判定不兼容)。
            foreach (var obj in candidates.Where(v => MatchesExactGameVersion(v, gameVersion)))
            {
                var number = obj.Value<string>("version_number") ?? "";
                if (!Fuse.Core.ModConflict.VersionRange.MatchesAny(number, constraint)) continue;
                var file = ParseVersionFile(obj);
                var versionId = obj.Value<string>("id") ?? "";
                return new ResolvedModFile(idOrSlug, versionId, number, file.FileName, file.Url, file.Sha1);
            }
            return null;
        }
        catch (Exception)
        {
            // 不是项目 id/slug:用搜索按 mod id 找项目再试一次
        }

        // 按 jar 的 SHA-1 反查项目(Modrinth 上 mod id 与项目 slug 不一致时最可靠)
        if (!string.IsNullOrEmpty(sha1))
        {
            try
            {
                using var fileReq = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/version_file/" + sha1);
                fileReq.Headers.UserAgent.ParseAdd("FuseLauncher/1.1.1");
                using var fileResp = await http.SendAsync(fileReq, ct).ConfigureAwait(false);
                if (fileResp.IsSuccessStatusCode)
                {
                    var fileJson = JObject.Parse(await fileResp.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var projectId = fileJson.Value<string>("project_id");
                    if (!string.IsNullOrEmpty(projectId))
                    {
                        var candidates = await QueryVersionObjectsAsync(http, projectId!, gameVersion, loader, ct)
                            .ConfigureAwait(false);
                        foreach (var obj in candidates.Where(v => MatchesExactGameVersion(v, gameVersion)))
                        {
                            var number = obj.Value<string>("version_number") ?? "";
                            if (!Fuse.Core.ModConflict.VersionRange.MatchesAny(number, constraint)) continue;
                            var file = ParseVersionFile(obj);
                            return new ResolvedModFile(projectId!, obj.Value<string>("id") ?? "", number,
                                file.FileName, file.Url, file.Sha1);
                        }
                        return null;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        try
        {
            var facets = new JArray
            {
                new JArray("project_type:mod"),
                new JArray("versions:" + gameVersion),
                new JArray("categories:" + loader),
            };
            var url = $"{ApiBase}/search?query={Uri.EscapeDataString(idOrSlug)}&limit=5" +
                      $"&facets={Uri.EscapeDataString(facets.ToString(Newtonsoft.Json.Formatting.None))}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("FuseLauncher/1.1.1");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var projectId = (json["hits"] as JArray)?.First?["project_id"]?.ToString();
            if (string.IsNullOrEmpty(projectId)) return null;
            var candidates = await QueryVersionObjectsAsync(http, projectId!, gameVersion, loader, ct)
                .ConfigureAwait(false);
            foreach (var obj in candidates.OrderByDescending(v => MatchesExactGameVersion(v, gameVersion)))
            {
                var number = obj.Value<string>("version_number") ?? "";
                if (!Fuse.Core.ModConflict.VersionRange.MatchesAny(number, constraint)) continue;
                var file = ParseVersionFile(obj);
                return new ResolvedModFile(projectId!, obj.Value<string>("id") ?? "", number,
                    file.FileName, file.Url, file.Sha1);
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    /// <summary>解析前置依赖项目 → 其最新文件(同版本/加载器)。</summary>
    public static Task<ModrinthVersionFile> GetProjectLatestFileAsync(
        HttpClient http, string projectId, string gameVersion, string loader, CancellationToken ct)
        => GetLatestVersionFileAsync(http, projectId, gameVersion, loader, ct);
}
