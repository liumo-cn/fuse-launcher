using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Fuse.Core.Sources;
using Fuse.Core.Versioning;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// CustomSkinLoader(CSL)的启动前落地,按账号类型分三条:
///   离线 → 下载 CSL、铺 LocalSkin 本地文件;
///   正版(MSA) → 保留 CSL,Mojang 源供官方皮肤;
///   皮肤站外置 → 保留 CSL,站点源排到 Mojang 之前。
/// 任何一步失败都只返回 false(调用方只提示,不拦截启动 —— 用户要求"跳过并提示")。
/// </summary>
public static class LocalSkinSetup
{
    public static async Task<bool> EnsureAsync(
        HttpClient http, string versionId, string versionsRoot, string instanceDir,
        string skinsDir, string playerName, CancellationToken ct)
    {
        try
        {
            var skinFile = Path.Combine(skinsDir, "skin.png");
            var capeFile = Path.Combine(skinsDir, "cape.png");
            if (!File.Exists(skinFile) && !File.Exists(capeFile)) return false;

            var version = VersionJsonLoader.Load(versionsRoot, versionId);
            var mc = string.IsNullOrEmpty(version.Jar) ? versionId : version.Jar!;
            var loader = DetectLoader(version);
            if (loader == null) return false;   // 原版没有模组加载能力

            var modsDir = Path.Combine(instanceDir, "mods");
            await EnsureCustomSkinLoaderAsync(http, mc, loader, modsDir, ct).ConfigureAwait(false);

            // 2. LocalSkin 目录布局(skin/<name>.png、cape/<name>.png,另留一份 <name>.png 兼容旧版)
            var skinDir = Path.Combine(skinsDir, "skin");
            var capeDir = Path.Combine(skinsDir, "cape");
            Directory.CreateDirectory(skinDir);
            Directory.CreateDirectory(capeDir);
            if (File.Exists(skinFile))
            {
                File.Copy(skinFile, Path.Combine(skinDir, playerName + ".png"), overwrite: true);
                File.Copy(skinFile, Path.Combine(skinsDir, playerName + ".png"), overwrite: true);
            }
            if (File.Exists(capeFile))
            {
                File.Copy(capeFile, Path.Combine(capeDir, playerName + ".png"), overwrite: true);
                File.Copy(capeFile, Path.Combine(skinsDir, playerName + ".cape.png"), overwrite: true);
            }

            // 2.5 直接写进 CSL 自带的本地目录约定:
            //     <实例>/LocalSkin/skins/<玩家名>.png 与 .../capes/<玩家名>.png
            //     默认配置里的 Legacy 源就是读这里,不依赖我们改配置是否成功。
            // 路径相对 <实例>/CustomSkinLoader 解析(CSL 自己建的就是这个目录)
            var cslSkins = Path.Combine(instanceDir, "CustomSkinLoader", "LocalSkin", "skins");
            var cslCapes = Path.Combine(instanceDir, "CustomSkinLoader", "LocalSkin", "capes");
            Directory.CreateDirectory(cslSkins);
            Directory.CreateDirectory(cslCapes);
            if (File.Exists(skinFile))
                File.Copy(skinFile, Path.Combine(cslSkins, playerName + ".png"), overwrite: true);
            if (File.Exists(capeFile))
                File.Copy(capeFile, Path.Combine(cslCapes, playerName + ".png"), overwrite: true);

            // CSL 会缓存"这个玩家没有皮肤"的结果,换皮肤后必须清掉缓存才会重新读本地文件
            ClearCaches(instanceDir);

            // 3. 配置文件:不再自造加载器类型(CSL 只认内置类型,LocalSkin 不是其一)。
            //    CSL 默认配置里本来就有一条 Legacy 本地源:
            //      skin:  LocalSkin/skins/{USERNAME}.png
            //      cape:  LocalSkin/capes/{USERNAME}.png
            //    它相对 <实例>/CustomSkinLoader 解析,上面已经把文件放好了。
            //    若现有配置是我们早期写坏的(含 "type":"LocalSkin" 或没有 Legacy 本地源),
            //    直接删掉让它下次启动重新生成完整默认配置。
            var cfgPath = Path.Combine(instanceDir, "CustomSkinLoader", "CustomSkinLoader.json");
            if (File.Exists(cfgPath))
            {
                try
                {
                    var cfg = JObject.Parse(File.ReadAllText(cfgPath));
                    var list = cfg["loadlist"] as JArray ?? new JArray();
                    var hasInvalid = list.OfType<JObject>().Any(o =>
                        string.Equals(o.Value<string>("type"), "LocalSkin", StringComparison.OrdinalIgnoreCase));
                    var hasLegacyLocal = list.OfType<JObject>().Any(o =>
                        string.Equals(o.Value<string>("type"), "Legacy", StringComparison.OrdinalIgnoreCase)
                        && (o.Value<string>("skin") ?? "").IndexOf("LocalSkin/skins", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hasInvalid || !hasLegacyLocal) File.Delete(cfgPath);
                }
                catch (Exception)
                {
                    try { File.Delete(cfgPath); } catch (Exception) { }
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;   // 失败只提示,不拦截启动
        }
    }

    /// <summary>停用目录:挪走的 CSL jar 放这里(Fabric/Forge 只扫 mods 顶层,不会被加载)。</summary>
    private const string SuspendedDirName = "disabled-mods";

    /// <summary>
    /// 皮肤站外置账号:恢复 CSL 并让站点源优先。
    ///
    /// 旧实现把 CSL 挪进 disabled-mods,只留 authlib-injector —— 它只能改客户端自己的
    /// profile;离线服的服务端会按名字重新下发不带 textures 的 profile,客户端 profile
    /// 被覆盖后就只剩默认皮肤(单人正常、进服没皮肤的根因)。
    /// 现在和正版一样保留 CSL;站点源排到 Mojang 之前,避免同名玩家被解析成官方皮肤。
    /// </summary>
    public static async Task<bool> PrepareForSkinSiteAsync(
        HttpClient http, string versionId, string versionsRoot, string instanceDir,
        string? apiRoot, CancellationToken ct)
    {
        try
        {
            RestoreForLocal(instanceDir);   // 旧版本停用过 CSL 的先放回 mods

            var version = VersionJsonLoader.Load(versionsRoot, versionId);
            var mc = string.IsNullOrEmpty(version.Jar) ? versionId : version.Jar!;
            var loader = DetectLoader(version);
            if (loader == null) return false;   // 原版装不了模组,只能靠站点直供

            await EnsureCustomSkinLoaderAsync(http, mc, loader, Path.Combine(instanceDir, "mods"), ct)
                .ConfigureAwait(false);

            if (CleanInvalidSources(instanceDir)) ClearCaches(instanceDir);
            EnsureSiteSourceFirst(instanceDir, apiRoot);
            return true;
        }
        catch (Exception)
        {
            return false;   // 失败只记日志,不拦截启动
        }
    }

    /// <summary>
    /// 把皮肤站源排到 CSL 加载列表最前(只排在 GameProfile 之后)。
    /// 配置存在就就地改;不存在或解析失败时退回 ExtraList —— CSL 14.4+ 会把它
    /// 前置到列表顶端并自行删除,不依赖我们猜它的完整配置结构。
    /// </summary>
    public static bool EnsureSiteSourceFirst(string instanceDir, string? apiRoot)
    {
        var source = BuildSiteSource(apiRoot);
        if (source == null) return false;
        try
        {
            var cfgPath = Path.Combine(instanceDir, "CustomSkinLoader", "CustomSkinLoader.json");
            // null = 没有配置或解析失败,交给 ExtraList;false = 已经在正确位置
            var applied = File.Exists(cfgPath) ? ApplySourceFirst(cfgPath, source) : (bool?)null;
            if (applied == null) WriteExtraList(instanceDir, source);
            if (applied != false) ClearCaches(instanceDir);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 正版(MSA)账号:保留 CSL(2026-11-14 对齐市面启动器)。
    /// 离线服服务端不转发正版玩家 profile,服务器里其他玩家的皮肤全靠每个客户端的
    /// CSL 按名字解析;正版玩家自己的皮肤若在皮肤站上也一样需要 CSL。
    /// HMCL/PCL2 都不会动用户的皮肤模组,这里只把停用过的 jar 放回来并清理失效条目。
    /// </summary>
    public static void RestoreForExternal(string instanceDir)
    {
        RestoreForLocal(instanceDir);   // 停用区里只有 CSL jar,放回 mods
        if (CleanInvalidSources(instanceDir)) ClearCaches(instanceDir);
    }

    /// <summary>清掉早期版本写坏的自定义源(CSL 只认内置类型,LocalSkin 不是其一)。</summary>
    private static bool CleanInvalidSources(string instanceDir)
    {
        try
        {
            var cfgPath = Path.Combine(instanceDir, "CustomSkinLoader", "CustomSkinLoader.json");
            if (!File.Exists(cfgPath)) return false;
            var cfg = JObject.Parse(File.ReadAllText(cfgPath));
            if (cfg["loadlist"] is not JArray list) return false;
            var invalid = list.OfType<JObject>()
                .Where(o => string.Equals(o.Value<string>("type"), "LocalSkin", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (invalid.Count == 0) return false;
            foreach (var o in invalid) list.Remove(o);
            File.WriteAllText(cfgPath, cfg.ToString(Formatting.Indented));
            return true;
        }
        catch (Exception)
        {
            // 配置损坏不影响启动:CSL 下次启动会自行重建默认配置
            return false;
        }
    }

    /// <summary>
    /// 改 loadlist:站点源插到 GameProfile 之后、Mojang 之前。
    /// 返回 true=有改动,false=已经在正确位置,null=读不出 loadlist(交给 ExtraList)。
    /// </summary>
    private static bool? ApplySourceFirst(string cfgPath, JObject source)
    {
        JObject cfg;
        try { cfg = JObject.Parse(File.ReadAllText(cfgPath)); }
        catch (Exception) { return null; }
        if (cfg["loadlist"] is not JArray list) return null;

        var type = source.Value<string>("type") ?? "";
        var root = NormalizeRoot(source.Value<string>("root"));

        // CSL 15 的 GameProfile 源用服务端下发的 textures,必须留在最前
        var insertAt = 0;
        while (insertAt < list.Count
               && string.Equals((list[insertAt] as JObject)?.Value<string>("type"), "GameProfile",
                                StringComparison.OrdinalIgnoreCase))
            insertAt++;

        var matches = new List<int>();
        for (var i = 0; i < list.Count; i++)
            if (SameSource(list[i] as JObject, type, root)) matches.Add(i);

        if (matches.Count == 1 && matches[0] == insertAt) return false;   // 已就位

        // 默认配置里 LittleSkin 在 Mojang 之后:先删掉旧位置再前插,避免重复源
        for (var i = matches.Count - 1; i >= 0; i--) list.RemoveAt(matches[i]);
        list.Insert(Math.Min(insertAt, list.Count), source.DeepClone());

        File.WriteAllText(cfgPath, cfg.ToString(Formatting.Indented));
        return true;
    }

    private static bool SameSource(JObject? entry, string type, string? root)
    {
        if (entry == null) return false;
        if (!string.Equals(entry.Value<string>("type"), type, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(NormalizeRoot(entry.Value<string>("root")), root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeRoot(string? root)
        => string.IsNullOrWhiteSpace(root) ? null : root!.Trim().TrimEnd('/') + "/";

    /// <summary>
    /// 从 authlib-injector 的 apiRoot 推出 CSL 源。
    /// LittleSkin 用内置源;其它站点按 Blessing Skin 约定取 <站点根>/csl/。
    /// </summary>
    private static JObject? BuildSiteSource(string? apiRoot)
    {
        if (string.IsNullOrWhiteSpace(apiRoot)) return null;
        var raw = apiRoot!.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;

        if (uri.Host.IndexOf("littleskin", StringComparison.OrdinalIgnoreCase) >= 0)
            return new JObject
            {
                ["name"] = "LittleSkin",
                ["type"] = "CustomSkinAPI",
                ["root"] = "https://littleskin.cn/csl/",
            };

        var site = raw.TrimEnd('/');
        const string yggdrasilSuffix = "/api/yggdrasil";
        if (site.EndsWith(yggdrasilSuffix, StringComparison.OrdinalIgnoreCase))
            site = site.Substring(0, site.Length - yggdrasilSuffix.Length);
        return new JObject
        {
            ["name"] = uri.Host,
            ["type"] = "CustomSkinAPI",
            ["root"] = site.TrimEnd('/') + "/csl/",
        };
    }

    /// <summary>一次性导入文件:CSL 14.4+ 启动时会把它插到加载列表顶端并删除。</summary>
    private static void WriteExtraList(string instanceDir, JObject source)
    {
        var dir = Path.Combine(instanceDir, "CustomSkinLoader", "ExtraList");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "FuseSkinSite.json"), source.ToString(Formatting.Indented));
    }

    /// <summary>本地/离线账号:把之前挪走的 CSL 放回 mods(换回本地皮肤前先恢复)。</summary>
    public static void RestoreForLocal(string instanceDir)
    {
        try
        {
            var stash = Path.Combine(instanceDir, SuspendedDirName);
            if (!Directory.Exists(stash)) return;

            var modsDir = Path.Combine(instanceDir, "mods");
            Directory.CreateDirectory(modsDir);
            foreach (var jar in Directory.GetFiles(stash, "*.jar", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(modsDir, Path.GetFileName(jar));
                try
                {
                    if (File.Exists(target)) File.Delete(jar);
                    else File.Move(jar, target);
                }
                catch (Exception)
                {
                }
            }
            try
            {
                if (Directory.GetFileSystemEntries(stash).Length == 0) Directory.Delete(stash);
            }
            catch (Exception)
            {
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>清 CSL 的玩家皮肤缓存(改皮肤/换来源后必须清,否则会沿用旧结果)。</summary>
    private static void ClearCaches(string instanceDir)
    {
        foreach (var cacheDir in new[]
                 {
                     Path.Combine(instanceDir, "CustomSkinLoader", "ProfileCache"),
                     Path.Combine(instanceDir, "CustomSkinLoader", "caches"),
                 })
        {
            try
            {
                if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// 确保 mods 里有 CustomSkinLoader。先在 Modrinth 按"MC 版本 + 加载器"查;
    /// 查不到(例如 NeoForge 不在该项目的 loader 列表里)就退回按游戏版本查。
    ///
    /// Forge 特殊处理:CSL 15.x 的 universal jar 在 Forge + Sinytra Connector 下会
    /// 触发 JPMS 冲突(Modules minecraft and customskinloader export package …),
    /// 游戏直接起不来。Forge 一律用 14.x 的 ForgeV2 专用包;已经装着的 universal 15.x
    /// 就地改名成 .disabled,不再被 Forge 扫描。
    /// </summary>
    private static async Task EnsureCustomSkinLoaderAsync(
        HttpClient http, string mc, string loader, string modsDir, CancellationToken ct)
    {
        Directory.CreateDirectory(modsDir);
        var cslJars = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f).IndexOf("CustomSkinLoader", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        if (string.Equals(loader, "forge", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var jar in cslJars.Where(f => IsIncompatibleForgeCsl(Path.GetFileName(f))))
            {
                try
                {
                    var disabled = jar + ".disabled";
                    if (File.Exists(disabled)) File.Delete(disabled);
                    File.Move(jar, disabled);
                }
                catch (Exception)
                {
                    // 游戏正在跑锁住了 jar:这次保持原样,下次启动会再尝试
                }
            }
            cslJars = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f).IndexOf("CustomSkinLoader", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }
        if (cslJars.Count > 0) return;

        ModrinthVersionFile? file = null;
        if (string.Equals(loader, "forge", StringComparison.OrdinalIgnoreCase))
            file = await FindForgeCslAsync(http, mc, ct).ConfigureAwait(false);

        if (file == null)
        {
            try
            {
                file = await ModrinthSource.GetLatestVersionFileAsync(http, "customskinloader", mc, loader, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                file = await FindCslByGameVersionAsync(http, mc, ct).ConfigureAwait(false);
            }
        }

        if (file == null) return;   // 该 MC 版本确实没有 CSL:调用方只提示,不拦截启动

        try
        {
            var downloader = new SmallFileDownloader(http);
            await downloader.DownloadSetAsync(new[]
            {
                new FileDownloadItem
                {
                    Url = file.Url,
                    DestPath = Path.Combine(modsDir, file.FileName),
                    ExpectedHash = string.IsNullOrEmpty(file.Sha1) ? null : file.Sha1,
                    Algo = HashAlgo.Sha1,
                },
            }, new SmallFileOptions { Parallelism = 1 }, null, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // 下载失败不影响本地文件铺设,下次启动会重试
        }
    }

    /// <summary>
    /// CSL 15+ 的 universal jar 在 Forge 上会和 Connector 加载的 Fabric 模组抢
    /// net.minecraft.client.renderer 的模块导出,导致 ResolutionException。
    /// ForgeV2 专用包(14.x)没有这个问题。
    /// </summary>
    public static bool IsIncompatibleForgeCsl(string fileName)
    {
        var i = fileName.IndexOf("Universal-", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var rest = fileName.Substring(i + "Universal-".Length);
        var dot = rest.IndexOf('.');
        var majorText = dot > 0 ? rest.Substring(0, dot) : rest;
        return int.TryParse(majorText, out var major) && major >= 15;
    }

    /// <summary>取该 MC 版本下最新的 14.x ForgeV2 包;没有就返回 null 走通用逻辑。</summary>
    private static async Task<ModrinthVersionFile?> FindForgeCslAsync(
        HttpClient http, string mc, CancellationToken ct)
    {
        var url = "https://api.modrinth.com/v2/project/customskinloader/version?game_versions=%5B%22"
                  + Uri.EscapeDataString(mc) + "%22%5D&loaders=%5B%22forge%22%5D";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        using var resp = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var arr = JArray.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        foreach (var version in arr.OfType<JObject>())
        {
            // 15.x 是 universal,会和 Connector 冲突;14.x 才有 ForgeV2 专用构建
            var number = version.Value<string>("version_number") ?? "";
            if (!number.StartsWith("14.", StringComparison.Ordinal)) continue;
            var f = (version["files"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(x => ((x.Value<string>("filename")) ?? "").EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
            if (f == null) continue;
            return new ModrinthVersionFile(
                f.Value<string>("url") ?? "",
                f.Value<string>("filename") ?? "",
                (f["hashes"]?.Value<string>("sha1")) ?? "",
                version.Value<string>("id") ?? "",
                new List<string>(),
                new List<ModrinthDependency>());
        }
        return null;
    }

    /// <summary>不按加载器过滤,只按游戏版本找 CustomSkinLoader 的 jar。</summary>
    private static async Task<ModrinthVersionFile?> FindCslByGameVersionAsync(
        HttpClient http, string mc, CancellationToken ct)
    {
        var url = "https://api.modrinth.com/v2/project/customskinloader/version?game_versions=%5B%22"
                  + Uri.EscapeDataString(mc) + "%22%5D";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        using var resp = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var arr = JArray.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        foreach (var version in arr.OfType<JObject>())
        {
            var f = (version["files"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(x => ((x.Value<string>("filename")) ?? "").EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
            if (f == null) continue;
            return new ModrinthVersionFile(
                f.Value<string>("url") ?? "",
                f.Value<string>("filename") ?? "",
                (f["hashes"]?.Value<string>("sha1")) ?? "",
                version.Value<string>("id") ?? "",
                new List<string>(),
                new List<ModrinthDependency>());
        }
        return null;
    }

    /// <summary>按 version.json 里的库判断加载器(与详情页同规则)。</summary>
    private static string? DetectLoader(McVersion version)
    {
        var names = version.Libraries.Select(l => l.Name).ToList();
        if (names.Any(n => n.StartsWith("net.fabricmc:fabric-loader", StringComparison.OrdinalIgnoreCase)))
            return "fabric";
        if (names.Any(n => n.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase)))
            return "neoforge";
        if (names.Any(n => n.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase)))
            return "forge";
        return null;
    }
}
