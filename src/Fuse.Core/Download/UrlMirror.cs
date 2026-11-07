using System;
using System.Collections.Generic;

namespace Fuse.Core.Download;

/// <summary>
/// 官方下载地址 → 镜像/官方的选源策略。
///
/// 2026-11-04 实测(这台机器 + 用户反馈"下载变慢"):
///   ① BMCLAPI 对**所有**请求做了频率限制,短窗口只有 **10 次/分钟**
///      (`X-RateLimit-Limit-short: 10`),批量下几千个资源文件必然 429;
///   ② 曾经用来接盘 429 的 HKMC(bsmirror.hkmc.online)**证书已过期**
///      (`SEC_E_CERT_EXPIRED`),于是 429 → 熔断 → HKMC → 每个文件再退避 4s×3 次,
///      整体从"几分钟"拖成"长期卡住";
///   ③ 官方 resources/libraries 直连实测 0.5~2 MB/s 且无频率限制。
///
/// 现在的策略:
///   - **原版批量文件(assets / libraries)= 官方优先**,失败才转镜像;
///   - **库 / 加载器 maven = USTC / CERNET 镜像优先**,BMCL → QLU 兜底;
///   - **元数据与大文件(清单 / 版本 json / piston-data)= BMCL 优先**(请求少、快),失败回官方;
///   - 任何一次 429 都会触发 5 分钟"官方模式",避免继续撞限流墙。
///   - 2026-11-08 起 Meta / Objects / Maven 会先按类别测真实带宽再排序(3 分钟缓存),
///     Assets 仍保持官方优先,避免 BMCL 10 次/分钟的限流被几千个小文件打爆。
/// 其它域(Modrinth CDN 等)保持原样——它们各自已有镜像策略。
/// </summary>
public static class UrlMirror
{
    private const string Bmcl = "https://bmclapi2.bangbang93.com";
    /// <summary>QLU 的 BMCLAPI 同构镜像(路径 = /bmclapi/ + maven 路径;不是全量,作最后备用)。</summary>
    private const string QluMaven = "https://mirrors4.qlu.edu.cn/bmclapi/";
    /// <summary>
    /// USTC 的 BMCLAPI 同构 maven 镜像。实测 1.7MB/s 且**没有** BMCLAPI 的 10 次/分钟限流,
    /// 用来接管库/Forge 依赖下载(路径 = /bmclapi/ + maven 路径,assets/元数据它没有)。
    /// </summary>
    private const string UstcMaven = "https://mirrors.ustc.edu.cn/bmclapi/";
    /// <summary>
    /// CERNET/JCUT 的 BMCLAPI maven 镜像。只有 maven 目录(库/Forge/NeoForge/Fabric),
    /// 没有 meta/objects/assets;路径同样是 /bmclapi/ + maven 路径。
    /// </summary>
    private const string CernetMaven = "https://mirrors.cernet.edu.cn/bmclapi/";
    /// <summary>NeoForged 官方 CDN(与 maven.neoforged.net 同构,国内实测快一个数量级)。</summary>
    private const string NeoCdn = "https://neoforged.forgecdn.net";

    /// <summary>下载源策略(对应 official / balanced / mirror 三档)。</summary>
    public enum SourceMode
    {
        /// <summary>只用官方源。最稳,但国内慢。</summary>
        Official,
        /// <summary>按资源类型自动选官方/镜像。默认值。</summary>
        Balanced,
        /// <summary>只用镜像。BMCLAPI 限流(10 次/分钟),批量下载会被 429。</summary>
        Mirror,
    }

    /// <summary>按资源类型分组测速,避免拿元数据的延迟去代表大文件带宽。</summary>
    public enum SourceCategory
    {
        Other,
        Meta,
        Objects,
        Maven,
        Assets,
    }

    /// <summary>当前下载源策略。</summary>
    public static SourceMode Mode { get; set; } = SourceMode.Balanced;

    /// <summary>是否启用镜像改写(兼容旧调用;等价于 Mode != Official)。</summary>
    public static bool Enabled
    {
        get => Mode != SourceMode.Official;
        set => Mode = value ? SourceMode.Balanced : SourceMode.Official;
    }

    /// <summary>
    /// 第 attempt 次尝试使用的地址。Balanced 会按类别和主机查询实测排序,
    /// 没有测速结果时回退到原来的固定顺序。
    /// </summary>
    public static string ForAttempt(string url, int attempt)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (Mode == SourceMode.Official) return url;
        if (Mode == SourceMode.Mirror) return Rewrite(url, attempt);
        if (attempt < 0) attempt = 0;

        // 熔断期(刚被 429)内一律官方,避免继续撞限流墙
        if (_degradedUntil > DateTime.UtcNow) return url;

        var candidates = Candidates(url);
        if (candidates.Count == 1) return candidates[0];

        var category = CategoryOf(url);

        // 资源文件是几千个小文件,BMCL 只有 10 次/分钟,不参与测速重排
        if (category == SourceCategory.Assets)
            return candidates[Math.Min(attempt, candidates.Count - 1)];

        var key = category + "|" + HostOf(url);
        var measured = MirrorProbe.TryOrderForUrl(key, candidates);
        var order = measured ?? candidates;
        return order[Math.Min(attempt, order.Count - 1)];
    }

    /// <summary>资源类型:决定候选源列表和测速分组。</summary>
    public static SourceCategory CategoryOf(string url)
    {
        if (string.IsNullOrEmpty(url)) return SourceCategory.Other;
        if (url.StartsWith("https://resources.download.minecraft.net/", StringComparison.OrdinalIgnoreCase))
            return SourceCategory.Assets;
        if (url.StartsWith("https://libraries.minecraft.net/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://maven.minecraftforge.net/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://maven.neoforged.net/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://maven.fabricmc.net/", StringComparison.OrdinalIgnoreCase))
            return SourceCategory.Maven;
        if (url.StartsWith("https://piston-data.mojang.com/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://launcher.mojang.com/", StringComparison.OrdinalIgnoreCase))
            return SourceCategory.Objects;
        if (url.StartsWith("https://piston-meta.mojang.com/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://launchermeta.mojang.com/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://meta.fabricmc.net/", StringComparison.OrdinalIgnoreCase))
            return SourceCategory.Meta;
        return SourceCategory.Other;
    }

    /// <summary>某个官方地址的候选源,按原来的固定优先级排列。</summary>
    public static IReadOnlyList<string> Candidates(string url)
    {
        var list = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (var existing in list)
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(value!);
        }

        var category = CategoryOf(url);
        switch (category)
        {
            case SourceCategory.Assets:
                Add(url);
                Add(BmclAssets(url));
                break;
            case SourceCategory.Meta:
                Add(BmclMeta(url));
                Add(url);
                break;
            case SourceCategory.Objects:
                Add(BmclObject(url));
                Add(url);
                break;
            case SourceCategory.Maven:
                var host = HostOf(url);
                var path = PathAfterHost(url);
                if (host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
                {
                    Add(url);
                    Add(UstcMaven + path);
                    Add(CernetMaven + path);
                    Add(Bmcl + "/maven/" + path);
                    Add(QluMaven + path);
                }
                else if (host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
                {
                    // NeoForge 官方路径带 releases/,BMCL/USTC/QLU 的 maven 根下没有这一段
                    if (path.StartsWith("releases/", StringComparison.OrdinalIgnoreCase))
                        path = path.Substring("releases/".Length);
                    Add(NeoCdnUrl(url));
                    Add(CernetMaven + path);
                    Add(Bmcl + "/maven/" + path);
                    Add(QluMaven + path);
                    Add(url);
                    Add(UstcMaven + path);
                }
                else
                {
                    Add(UstcMaven + path);
                    Add(CernetMaven + path);
                    Add(Bmcl + "/maven/" + path);
                    Add(QluMaven + path);
                    Add(url);
                }
                break;
            default:
                Add(url);
                break;
        }

        if (list.Count == 0) list.Add(url);
        return list;
    }

    private static string HostOf(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return "";
        var start = scheme + 3;
        var end = url.IndexOf('/', start);
        return end < 0 ? url.Substring(start) : url.Substring(start, end - start);
    }

    private static string PathAfterHost(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return "";
        var slash = url.IndexOf('/', scheme + 3);
        return slash < 0 ? "" : url.Substring(slash + 1);
    }

    private static string? BmclMeta(string url)
    {
        var path = Prefix(url, "https://piston-meta.mojang.com/")
                   ?? Prefix(url, "https://launchermeta.mojang.com/");
        if (path != null) return Bmcl + "/" + path;
        path = Prefix(url, "https://meta.fabricmc.net/");
        return path == null ? null : Bmcl + "/fabric-meta/" + path;
    }

    private static string? BmclObject(string url)
    {
        var path = Prefix(url, "https://piston-data.mojang.com/")
                   ?? Prefix(url, "https://launcher.mojang.com/");
        return path == null ? null : Bmcl + "/" + path;
    }

    private static string? BmclAssets(string url)
    {
        var path = Prefix(url, "https://resources.download.minecraft.net/");
        return path == null ? null : Bmcl + "/assets/" + path;
    }

    private static string? NeoCdnUrl(string url)
    {
        var path = Prefix(url, "https://maven.neoforged.net/releases/");
        if (path != null) return NeoCdn + "/releases/" + path;
        path = Prefix(url, "https://maven.neoforged.net/");
        return path == null ? null : NeoCdn + "/" + path;
    }

    /// <summary>限流熔断截止时间:一旦收到 429,这段时间内所有请求直接走官方源。</summary>
    private static DateTime _degradedUntil = DateTime.MinValue;

    /// <summary>最近一次熔断的原因(调试用)。</summary>
    public static DateTime? DegradedUntil => _degradedUntil > DateTime.UtcNow ? _degradedUntil : null;

    /// <summary>镜像返回 429 时调用:整体降级到官方源,避免继续撞限流墙。</summary>
    public static void NoteThrottled(TimeSpan? window = null)
        => _degradedUntil = DateTime.UtcNow.Add(window ?? TimeSpan.FromMinutes(5));

    public static string Rewrite(string url) => Rewrite(url, 0);

    /// <summary>把官方地址改写到镜像(Mirror 模式 / Balanced 的重试步)。</summary>
    public static string Rewrite(string url, int attempt)
    {
        if (!Enabled || string.IsNullOrEmpty(url)) return url;
        var mavenBase = MavenBaseFor(attempt);

        var maven = Prefix(url, "https://libraries.minecraft.net/");
        if (maven != null) return mavenBase + maven;

        var assets = Prefix(url, "https://resources.download.minecraft.net/");
        if (assets != null) return Bmcl + "/assets/" + assets;

        var piston = Prefix(url, "https://piston-data.mojang.com/");
        if (piston != null) return Bmcl + "/" + piston;

        var launcher = Prefix(url, "https://launcher.mojang.com/");
        if (launcher != null) return Bmcl + "/" + launcher;

        // 版本清单与 Java 运行时清单也支持同名路径镜像(实测 302)
        var launcherMeta = Prefix(url, "https://launchermeta.mojang.com/");
        if (launcherMeta != null) return Bmcl + "/" + launcherMeta;

        var pistonMeta = Prefix(url, "https://piston-meta.mojang.com/");
        if (pistonMeta != null) return Bmcl + "/" + pistonMeta;

        // Forge / NeoForge 的版本 JSON 里库地址指向他们自己的 maven,
        // 不改写的话"列表能出来但库下不动"——这是加载器安装卡住的真正原因。
        var forgeMaven = Prefix(url, "https://maven.minecraftforge.net/");
        if (forgeMaven != null) return mavenBase + forgeMaven;

        // NeoForge 库:NeoForged CDN 与官方 maven 同构且快得多(实测 0.35s vs 20s+)
        var neoMaven = Prefix(url, "https://maven.neoforged.net/releases/");
        if (neoMaven != null)
            return attempt % 2 == 0 ? NeoCdn + "/releases/" + neoMaven : mavenBase + neoMaven;

        var neoMaven2 = Prefix(url, "https://maven.neoforged.net/");
        if (neoMaven2 != null)
            return attempt % 2 == 0 ? NeoCdn + "/" + neoMaven2 : mavenBase + neoMaven2;

        // Fabric 的 maven 官方源实测 25s/600KB,镜像 1s 内可下
        var fabricMaven = Prefix(url, "https://maven.fabricmc.net/");
        if (fabricMaven != null) return mavenBase + fabricMaven;

        // Fabric meta(BMCLAPI 有 /fabric-meta/ 同构路径,实测 0.8s)
        var fabricMeta = Prefix(url, "https://meta.fabricmc.net/");
        if (fabricMeta != null) return Bmcl + "/fabric-meta/" + fabricMeta;

        return url;
    }

    /// <summary>maven 镜像轮换:USTC(不限流、实测 1.7MB/s) → BMCL → QLU(最慢,兜底)。</summary>
    private static string MavenBaseFor(int attempt)
    {
        if (attempt <= 0) return UstcMaven;
        if (attempt == 1) return Bmcl + "/maven/";
        return QluMaven;
    }

    private static string? Prefix(string url, string prefix)
    {
        return url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? url.Substring(prefix.Length)
            : null;
    }
}
