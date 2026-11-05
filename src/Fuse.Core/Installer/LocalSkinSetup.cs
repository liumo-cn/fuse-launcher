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
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Installer;

/// <summary>
/// 离线/正版账号的"本地皮肤"落地(M5):
///   ① 确认实例加载器(fabric/forge/neoforge)与 MC 版本
///   ② 缺 CustomSkinLoader 就从 Modrinth 下载进 mods
///   ③ 生成 config/CustomSkinLoader.json,皮肤源指向本地 skins\local
///   ④ 把 skin.png / cape.png 复制成 CSL 的 LocalSkin 目录布局
/// 任何一步失败都返回 false(调用方只提示,不拦截启动 —— 用户要求"跳过并提示")。
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
    /// 在线账号(正版/皮肤站):本地皮肤一律失效 —— 把 CustomSkinLoader 移出 mods。
    /// 正版有 Mojang 官方源、外置有 authlib-injector 站点源,都不需要 CSL;
    /// CSL 留在 mods 里只会用本地文件把官方皮肤盖掉。
    /// </summary>
    public static void SuspendForExternal(string instanceDir)
    {
        try
        {
            var modsDir = Path.Combine(instanceDir, "mods");
            if (!Directory.Exists(modsDir)) return;

            var stash = Path.Combine(instanceDir, SuspendedDirName);
            foreach (var jar in Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(jar).IndexOf("CustomSkinLoader", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    Directory.CreateDirectory(stash);
                    var target = Path.Combine(stash, Path.GetFileName(jar));
                    File.Copy(jar, target, overwrite: true);
                    File.Delete(jar);
                }
                catch (Exception)
                {
                    // 游戏正在跑锁住了 jar:这次保持原样,下次启动会再尝试
                }
            }

            // CSL 关掉了,缓存也别留着,免得切回本地账号时读到外置期间的结果
            ClearCaches(instanceDir);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>本地/正版账号:把之前挪走的 CSL 放回 mods(换回本地皮肤前先恢复)。</summary>
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
    /// 查不到(例如 NeoForge 不在该项目的 loader 列表里)就退回按游戏版本查,
    /// CSL 是 universal jar,任何加载器都能跑。
    /// </summary>
    private static async Task EnsureCustomSkinLoaderAsync(
        HttpClient http, string mc, string loader, string modsDir, CancellationToken ct)
    {
        Directory.CreateDirectory(modsDir);
        var hasCsl = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
            .Any(f => Path.GetFileName(f).IndexOf("CustomSkinLoader", StringComparison.OrdinalIgnoreCase) >= 0);
        if (hasCsl) return;

        ModrinthVersionFile? file = null;
        try
        {
            file = await ModrinthSource.GetLatestVersionFileAsync(http, "customskinloader", mc, loader, ct)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            file = await FindCslByGameVersionAsync(http, mc, ct).ConfigureAwait(false);
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
