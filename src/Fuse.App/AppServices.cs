using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Fuse.Core.Accounts;
using Fuse.Core.Auth;
using Fuse.Core.Config;
using Fuse.Core.Download;

namespace Fuse.App;

/// <summary>全局服务定位：配置、目录布局、下载队列、账号（轻量 code-behind 风格，无 DI 容器）。</summary>
public static class AppServices
{
    /// <summary>产品名(启动器本体)。</summary>
    public const string ProductName = "Fuse Launcher";

    /// <summary>短名:界面上放不下全名时用(如托盘提示、日志)。</summary>
    public const string ShortName = "Fuse";

    /// <summary>作者。</summary>
    public const string Author = "liumo";

    /// <summary>版本号(与 csproj 的 &lt;Version&gt; 保持一致,直接读程序集,发布时只需改一处)。</summary>
    public static string AppVersion { get; } =
        (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
         ?? new Version(1, 0, 0)).ToString(3);

    public static AppConfig Config { get; private set; } = new();

    /// <summary>
    /// 便携模式:exe 同目录存在名为 <c>portable</c> 的空文件时,
    /// 配置/账号/日志放 &lt;exe目录&gt;\data,默认游戏目录放 &lt;exe目录&gt;\games。
    /// 安装器勾选"便携模式"就是写入这个标记文件。
    /// </summary>
    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable"));

    /// <summary>应用数据根目录(%APPDATA%\FuseLauncher;便携模式 = exe\data)。</summary>
    public static string AppDataDir { get; } = ResolveAppDataDir();

    private static string ResolveAppDataDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "portable")))
            return Path.Combine(baseDir, "data");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuseLauncher");
    }

    /// <summary>旧版数据目录(V1.0.0 之前叫 MCDownloader),启动时一次性迁移。</summary>
    public static string LegacyAppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCDownloader");

    public static string ConfigPath { get; } = Path.Combine(AppDataDir, "config.json");

    public static string AccountStorePath { get; } = Path.Combine(AppDataDir, "accounts.json");

    public static HttpClient Http { get; } = new HttpClient();

    public static DownloadQueue? Queue { get; private set; }

    public static AccountStore Accounts { get; private set; } = new(AccountStorePath);

    /// <summary>游玩时长/启动/崩溃统计(随便携模式落到 data/play-stats.json)。</summary>
    public static PlayStatsStore PlayStats { get; } = new(Path.Combine(AppDataDir, "play-stats.json"));

    /// <summary>悬浮组件窗体（设置面板可见性切换用）。</summary>
    public static WidgetWindow? Widget { get; set; }

    /// <summary>安装目录默认在用户文档下（避免系统级提权）。</summary>
    public static string DefaultInstallDirectory { get; } = IsPortable
        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "games")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FuseLauncher");

    public static string InstallDirectory => NormalizeDir(string.IsNullOrEmpty(Config.InstallDirectory)
        ? DefaultInstallDirectory
        : Config.InstallDirectory);

    /// <summary>
    /// 全部可用游戏目录:主目录在前,其后是导入目录(官方或第三方启动器)。
    /// 去重、保序;主目录永远存在。
    /// </summary>
    public static List<string> GameDirectories
    {
        get
        {
            var list = new List<string>();
            void Add(string? d)
            {
                if (string.IsNullOrWhiteSpace(d)) return;
                var full = NormalizeDir(d!);
                if (full.Length == 0) return;
                if (!list.Any(x => x.Equals(full, StringComparison.OrdinalIgnoreCase))) list.Add(full);
            }
            Add(InstallDirectory);
            foreach (var d in Config.ExtraGameDirectories) Add(d);
            Add(ActiveGameDirectory);   // 保险:激活目录一定在列表里
            return list;
        }
    }

    /// <summary>
    /// 当前选中的游戏目录:版本列表 / 下载 / 自动补全都用它。
    /// 无效或未设置时回退主目录(游戏目录被删等场景不会炸)。
    /// </summary>
    public static string ActiveGameDirectory
    {
        get
        {
            var dir = Config.ActiveGameDirectory;
            if (string.IsNullOrWhiteSpace(dir)) return InstallDirectory;
            var full = NormalizeDir(dir!);
            return Directory.Exists(full) ? full : InstallDirectory;
        }
    }

    public static void SetActiveGameDirectory(string dir)
    {
        Config.ActiveGameDirectory = NormalizeDir(dir);
        SaveConfig();
        WidgetWindow.Boot("[game dir] active: " + Config.ActiveGameDirectory);
    }

    public static void AddGameDirectory(string dir)
    {
        var full = NormalizeDir(dir);
        if (!Directory.Exists(full)) return;
        if (!GameDirectories.Any(x => x.Equals(full, StringComparison.OrdinalIgnoreCase)))
            Config.ExtraGameDirectories.Add(full);
        SetActiveGameDirectory(full);
    }

    // ---------- 文件夹列表(折叠列表风格:显示名 / 重命名 / 移出 / 删除 / 新建) ----------

    /// <summary>主目录 = 启动器自己的游戏目录(不可移出、不可删除)。</summary>
    public static bool IsMainGameDirectory(string dir) =>
        NormalizeDir(dir).Equals(InstallDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>列表显示名:自定义名优先,否则按路径推导。</summary>
    public static string GameFolderName(string dir)
    {
        var full = NormalizeDir(dir);
        foreach (var kv in Config.GameFolderNames)
        {
            if (!string.IsNullOrWhiteSpace(kv.Value) &&
                NormalizeDir(kv.Key).Equals(full, StringComparison.OrdinalIgnoreCase))
                return kv.Value.Trim();
        }
        return DefaultGameFolderName(full);
    }

    /// <summary>默认名:主目录固定「Fuse 启动器文件夹」;*.minecraft 取父目录名;其他取目录名。</summary>
    public static string DefaultGameFolderName(string dir)
    {
        var full = NormalizeDir(dir);
        if (IsMainGameDirectory(full)) return "Fuse 启动器文件夹";
        var leaf = Path.GetFileName(full);
        if (leaf.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(full);
            var parentName = string.IsNullOrEmpty(parent) ? "" : Path.GetFileName(parent);
            if (!string.IsNullOrEmpty(parentName)) return parentName;
        }
        return string.IsNullOrEmpty(leaf) ? full : leaf;
    }

    /// <summary>列表 (路径, 名称);重名自动追加序号,保证显示可区分。</summary>
    public static List<(string Path, string Name)> GameFolderEntries()
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        foreach (var dir in GameDirectories)
        {
            var name = GameFolderName(dir);
            if (used.TryGetValue(name, out var n))
            {
                used[name] = n + 1;
                name = name + " (" + (n + 1) + ")";
            }
            else used[name] = 1;
            list.Add((dir, name));
        }
        return list;
    }

    /// <summary>重命名:只改显示名(不动磁盘);传空 = 恢复默认名。</summary>
    public static void RenameGameFolder(string dir, string? name)
    {
        var full = NormalizeDir(dir);
        var key = Config.GameFolderNames.Keys
            .FirstOrDefault(k => NormalizeDir(k).Equals(full, StringComparison.OrdinalIgnoreCase));
        if (key != null) Config.GameFolderNames.Remove(key);
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length > 0 && !trimmed.Equals(DefaultGameFolderName(full), StringComparison.Ordinal))
            Config.GameFolderNames[full] = trimmed;
        SaveConfig();
        WidgetWindow.Boot("[game dir] renamed: " + full + " -> " + GameFolderName(full));
    }

    /// <summary>移出列表(不删磁盘);主目录不允许移出。移出当前目录时回退主目录。</summary>
    public static bool RemoveGameDirectory(string dir)
    {
        var full = NormalizeDir(dir);
        if (IsMainGameDirectory(full)) return false;
        Config.ExtraGameDirectories.RemoveAll(x =>
            NormalizeDir(x).Equals(full, StringComparison.OrdinalIgnoreCase));
        var key = Config.GameFolderNames.Keys
            .FirstOrDefault(k => NormalizeDir(k).Equals(full, StringComparison.OrdinalIgnoreCase));
        if (key != null) Config.GameFolderNames.Remove(key);
        if (!string.IsNullOrEmpty(Config.ActiveGameDirectory) &&
            NormalizeDir(Config.ActiveGameDirectory!).Equals(full, StringComparison.OrdinalIgnoreCase))
            Config.ActiveGameDirectory = null;   // 回退主目录
        SaveConfig();
        WidgetWindow.Boot("[game dir] removed: " + full);
        return true;
    }

    /// <summary>删除磁盘目录并从列表移除;调用方必须先完成二次确认(输入 yes)。</summary>
    public static bool DeleteGameDirectory(string dir, out string error)
    {
        error = "";
        var full = NormalizeDir(dir);
        if (IsMainGameDirectory(full)) { error = "启动器自己的游戏目录不能删除"; return false; }
        if (!Config.ExtraGameDirectories.Any(x =>
                NormalizeDir(x).Equals(full, StringComparison.OrdinalIgnoreCase)))
        { error = "该目录不在文件夹列表里"; return false; }

        // 危险路径保护:盘符根目录 / 包含启动器自身目录的路径
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || parent.Equals(full, StringComparison.OrdinalIgnoreCase))
        { error = "不能删除磁盘根目录"; return false; }
        if (InstallDirectory.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { error = "该目录包含启动器本体,不能删除"; return false; }

        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        RemoveGameDirectory(full);
        return true;
    }

    /// <summary>新建 &lt;位置&gt;\&lt;名称&gt;\.minecraft,建好标准子目录后加入列表并选中。</summary>
    public static string? CreateGameFolder(string parentDir, string name, out string error)
    {
        error = "";
        name = (name ?? "").Trim();
        if (name.Length == 0) { error = "请输入文件夹名称"; return null; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("\\") || name.Contains("/"))
        { error = "名称不能包含 \\ / : * ? \" < > |"; return null; }
        if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir))
        { error = "位置不存在"; return null; }

        var game = Path.Combine(Path.GetFullPath(parentDir), name, ".minecraft");
        if (Directory.Exists(game)) { error = "该位置已有 .minecraft 文件夹"; return null; }
        try
        {
            foreach (var sub in new[] { "versions", "libraries", "assets", "saves", "mods", "logs" })
                Directory.CreateDirectory(Path.Combine(game, sub));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        AddGameDirectory(game);
        WidgetWindow.Boot("[game dir] created: " + game);
        return game;
    }

    private static string NormalizeDir(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir.Trim());
            var root = Path.GetPathRoot(full) ?? "";
            // 盘符根目录("C:\")保持不变,其余去掉尾部分隔符,保证比较一致
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception)
        {
            return (dir ?? "").Trim();
        }
    }

    // ---------- 解压式整合包(博主自制的 zip,没有 Modrinth/CurseForge 清单) ----------

    /// <summary>zip 里看起来像"直接解压就能用"的整合包(mods / config / .minecraft 等)。</summary>
    public static bool LooksLikePlainPack(System.IO.Compression.ZipArchive zip)
    {
        return zip.Entries.Any(e =>
        {
            var n = e.FullName.Replace('\\', '/').ToLowerInvariant();
            return n.StartsWith("mods/") || n.StartsWith("config/") || n.StartsWith(".minecraft/") ||
                   n.Contains("/mods/") || n.Contains("/config/") || n.Contains("/.minecraft/");
        });
    }

    /// <summary>
    /// 导入解压式整合包:解压到独立实例目录,并写一个 inheritsFrom 基础版本的版本 json。
    /// 仅支持启动器自己的布局(主目录/带 instances 的目录);外部官方目录不覆盖,避免误伤。
    /// </summary>
    public static string? ImportPlainPack(string zipPath, string name, string baseVersionId, out string error)
    {
        error = "";
        name = (name ?? "").Trim();
        if (name.Length == 0) { error = "请输入整合包名称"; return null; }
        if (string.IsNullOrEmpty(baseVersionId)) { error = "请选择基础版本"; return null; }

        var root = ActiveGameDirectory;
        if (!IsManagedLayout(root))
        {
            error = "当前选中的是外部游戏目录(共享根布局),解压式整合包会直接覆盖该目录里的 mods/config。\n\n" +
                    "请先切换到启动器自己的「Fuse 启动器文件夹」再导入。";
            return null;
        }
        var baseRoot = FindVersionRoot(baseVersionId);
        if (baseRoot == null)
        {
            error = "基础版本 " + baseVersionId + " 不在当前游戏目录里,请重新选择。";
            return null;
        }

        var id = MakePackVersionId(root, name);
        var versionsRoot = Path.Combine(root, "versions");
        var versionDir = Path.Combine(versionsRoot, id);
        var instanceDir = InstanceDirectoryIn(root, id);
        try
        {
            Directory.CreateDirectory(versionDir);
            Directory.CreateDirectory(instanceDir);
            ExtractPack(zipPath, instanceDir);

            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var json = new Newtonsoft.Json.Linq.JObject
            {
                ["id"] = id,
                ["inheritsFrom"] = baseVersionId,
                ["type"] = "release",
                ["releaseTime"] = now,
                ["time"] = now,
                ["fuseModpack"] = true,
                ["fusePackTitle"] = name,
            };
            File.WriteAllText(Path.Combine(versionDir, id + ".json"), json.ToString());

            var profile = VersionProfiles.Get(id);
            profile.DisplayName = name;
            VersionProfiles.Set(id, profile);
            WidgetWindow.Boot($"[pack] imported '{name}' -> {id} (base {baseVersionId})");
            return id;
        }
        catch (Exception ex)
        {
            // 失败回滚:别留下半个实例/版本
            try { if (Directory.Exists(versionDir)) Directory.Delete(versionDir, true); } catch (Exception) { }
            try { if (!instanceDir.Equals(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(instanceDir)) Directory.Delete(instanceDir, true); } catch (Exception) { }
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 导入启动器自己导出的完整版本 zip(versions/&lt;id&gt; + instances/&lt;id&gt; + libraries)。
    /// 版本 id 冲突时自动加 -importN,版本 json 里的 id 同步改写。
    /// </summary>
    public static string? ImportExportedVersion(string zipPath, out string error)
    {
        error = "";
        var root = ActiveGameDirectory;
        if (!IsManagedLayout(root))
        {
            error = "当前选中的是外部游戏目录(共享根布局),导出包需要实例目录结构。\n\n" +
                    "请先切换到启动器自己的「Fuse 启动器文件夹」再导入。";
            return null;
        }
        var versionsRoot = Path.Combine(root, "versions");
        var instancesRoot = Path.Combine(root, "instances");
        var librariesRoot = Path.Combine(root, "libraries");
        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + Path.DirectorySeparatorChar;

        string? srcId = null;
        using (var probe = ZipFile.OpenRead(zipPath))
        {
            foreach (var e in probe.Entries)
            {
                var parts = e.FullName.Replace('\\', '/').Split('/');
                if (parts.Length == 3 && parts[0].Equals("versions", StringComparison.OrdinalIgnoreCase) &&
                    parts[2].Equals(parts[1] + ".json", StringComparison.OrdinalIgnoreCase))
                {
                    srcId = parts[1];
                    break;
                }
            }
        }
        if (srcId == null)
        {
            error = "不是启动器导出的版本包:缺少 versions/<id>/<id>.json。";
            return null;
        }

        var newId = MakePackVersionId(root, srcId + "-import");
        var versionDir = Path.Combine(versionsRoot, newId);
        var instanceDir = Path.Combine(instancesRoot, newId);
        try
        {
            Directory.CreateDirectory(versionDir);
            Directory.CreateDirectory(instanceDir);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var rel = entry.FullName.Replace('\\', '/');
                string? target = null;
                var isLibrary = false;
                if (rel.StartsWith("versions/" + srcId + "/", StringComparison.OrdinalIgnoreCase))
                    target = Path.Combine(versionDir, rel.Substring(("versions/" + srcId + "/").Length));
                else if (rel.StartsWith("instances/" + srcId + "/", StringComparison.OrdinalIgnoreCase))
                    target = Path.Combine(instanceDir, rel.Substring(("instances/" + srcId + "/").Length));
                else if (rel.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase))
                {
                    target = Path.Combine(librariesRoot, rel.Substring("libraries/".Length));
                    isLibrary = true;
                }
                if (target == null) continue;

                target = Path.GetFullPath(target);
                if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("压缩包含有非法路径:" + entry.FullName);

                // 版本 json 要改名成 <新id>.json 并改写 id
                if (Path.GetFileName(target).Equals(srcId + ".json", StringComparison.OrdinalIgnoreCase) &&
                    target.StartsWith(Path.GetFullPath(versionDir), StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(entry.Open());
                    var json = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());
                    json["id"] = newId;
                    File.WriteAllText(Path.Combine(versionDir, newId + ".json"), json.ToString());
                    continue;
                }

                // 库文件按路径共享:已存在就跳过,避免覆盖和重复写
                if (isLibrary && File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            if (!File.Exists(Path.Combine(versionDir, newId + ".json")))
                throw new InvalidOperationException("导出包里缺少版本 json");
            WidgetWindow.Boot("[import] exported version " + srcId + " -> " + newId);
            return newId;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { if (Directory.Exists(versionDir)) Directory.Delete(versionDir, true); } catch (Exception) { }
            try { if (Directory.Exists(instanceDir)) Directory.Delete(instanceDir, true); } catch (Exception) { }
            return null;
        }
    }

    /// <summary>版本 id:ASCII 短名,冲突自动加序号(中文名不适合做目录名)。</summary>
    private static string MakePackVersionId(string root, string name)
    {
        var chars = name.ToLowerInvariant()
            .Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
            .ToArray();
        var slug = new string(chars).Trim('-', '_');
        if (slug.Length == 0) slug = "pack-" + DateTime.Now.ToString("MMdd-HHmm");
        var id = slug;
        var n = 2;
        while (File.Exists(Path.Combine(root, "versions", id, id + ".json")) ||
               Directory.Exists(Path.Combine(InstanceDirectoryIn(root, id))))
            id = slug + "-" + n++;
        return id;
    }

    /// <summary>解压整合包:自动剥掉 ".minecraft/"(或唯一的包装文件夹),并拒绝 ../ 越界。</summary>
    private static void ExtractPack(string zipPath, string instanceDir)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (files.Count == 0) throw new InvalidOperationException("压缩包是空的");

        var prefix = DetectPackWrapper(files.Select(e => e.FullName.Replace('\\', '/')).ToList());
        var rootFull = Path.GetFullPath(instanceDir).TrimEnd('\\') + Path.DirectorySeparatorChar;
        foreach (var entry in files)
        {
            var rel = entry.FullName.Replace('\\', '/');
            if (prefix.Length > 0)
            {
                if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                rel = rel.Substring(prefix.Length);
            }
            if (rel.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(instanceDir,
                rel.Replace('/', Path.DirectorySeparatorChar)));
            // 防 zip-slip:任何条目都必须落在实例目录内
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("整合包含有非法路径:" + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>识别压缩包是否套了一层 ".minecraft/" 或一个包装文件夹;返回要剥掉的前缀。</summary>
    private static string DetectPackWrapper(List<string> names)
    {
        // 已知的游戏目录名:即使整个包里只有 mods/,也不能把 mods/ 当包装层剥掉
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mods", "config", "defaultconfigs", "resourcepacks", "resourcepack", "shaderpacks",
            "saves", "logs", "crash-reports", "screenshots", "options.txt", "servers.dat",
            "libraries", "versions", "assets", "kubejs", "scripts", "schematics", "journeymap",
        };

        if (names.All(n => n.StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase)))
            return ".minecraft/";

        if (names.Any(n => !n.Contains('/'))) return "";
        var first = names.Select(n => n.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (first.Count != 1) return "";
        var seg = first[0];
        if (known.Contains(seg)) return "";
        // 包装层里应该能看到 mods/ config/ 或 .minecraft/
        var hasPayload = names.Any(n =>
            n.StartsWith(seg + "/mods/", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith(seg + "/config/", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith(seg + "/.minecraft/", StringComparison.OrdinalIgnoreCase));
        return hasPayload ? seg + "/" : "";
    }

    /// <summary>启动时清掉配置里已不存在的导入目录(磁盘被删/移动),避免列表出现死行。</summary>
    private static void PruneMissingGameDirectories()
    {
        var before = Config.ExtraGameDirectories.Count;
        var missing = Config.ExtraGameDirectories.Where(d => !Directory.Exists(d)).ToList();
        if (missing.Count == 0) return;
        Config.ExtraGameDirectories.RemoveAll(d => !Directory.Exists(d));
        if (!string.IsNullOrEmpty(Config.ActiveGameDirectory) &&
            !Directory.Exists(Config.ActiveGameDirectory))
            Config.ActiveGameDirectory = null;
        SaveConfig();
        foreach (var d in missing) WidgetWindow.Boot("[game dir] pruned missing: " + d);
        WidgetWindow.Boot($"[game dir] pruned {before - Config.ExtraGameDirectories.Count} missing folder(s)");
    }

    /// <summary>
    /// 布局判别:主目录(或带 instances 子目录的目录)是我们的"实例隔离"布局;
    /// 导入的官方或第三方目录是共享根布局(游戏目录 = 根本身,mods/saves 都在根下)。
    /// </summary>
    public static bool IsManagedLayout(string root) =>
        root.Equals(InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
        Directory.Exists(Path.Combine(root, "instances"));

    /// <summary>
    /// 某目录下某版本的"实例目录":
    ///   我们的布局 → instances/&lt;id&gt;;
    ///   外部共享布局(未开版本隔离) → 根目录本身;
    ///   外部隔离布局(开了版本隔离) → versions/&lt;id&gt;。
    /// </summary>
    public static string InstanceDirectoryIn(string root, string versionId) =>
        IsManagedLayout(root)
            ? Path.Combine(root, "instances", versionId)
            : Fuse.Core.Versioning.InstanceLayout.ResolveExternal(root, versionId);

    /// <summary>在所有游戏目录里找版本所在目录(激活目录优先);找不到返回 null。</summary>
    public static string? FindVersionRoot(string versionId)
    {
        foreach (var dir in GameDirectories.OrderByDescending(
                     d => d.Equals(ActiveGameDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            if (File.Exists(Path.Combine(dir, "versions", versionId, versionId + ".json")))
                return dir;
        }
        return null;
    }

    // 以下路径都跟随当前选中的游戏目录:下载/补全/版本列表自动落到所选目录
    public static string VersionsRoot => Path.Combine(ActiveGameDirectory, "versions");
    public static string LibrariesRoot => Path.Combine(ActiveGameDirectory, "libraries");
    public static string AssetsRoot => Path.Combine(ActiveGameDirectory, "assets");
    public static string InstancesRoot => Path.Combine(ActiveGameDirectory, "instances");

    /// <summary>FR-08 版本隔离：每实例独立游戏目录(外部布局共享根目录)，assets/libraries 全局共享。</summary>
    public static string InstanceDirectory(string versionId) => InstanceDirectoryIn(ActiveGameDirectory, versionId);

    public static string ModsDirectory(string versionId) => Path.Combine(InstanceDirectory(versionId), "mods");

    /// <summary>
    /// 该版本启动时实际使用的堆大小(MB)。优先级:每版本固定值 &gt; 自动(物理内存+模组数) &gt; 手动全局值。
    /// 设置面板与版本详情页都用它显示,保证"看到的值"和"启动用的值"一致。
    /// </summary>
    public static long ResolveHeapMb(string versionId, out bool auto, out int modCount)
    {
        var profile = VersionProfiles.Get(versionId);
        modCount = Fuse.Core.Config.MemoryAdvisor.CountMods(ModsDirectory(versionId));
        if (profile.MaxHeapMb > 0)
        {
            auto = false;
            return profile.MaxHeapMb;
        }
        auto = Config.AutoHeap;
        return auto
            ? Fuse.Core.Config.MemoryAdvisor.Suggest(modCount, Fuse.Core.Config.MemoryAdvisor.TotalPhysicalMemoryMb())
            : Config.MaxHeapMb;
    }

    public static string TasksStorePath => Path.Combine(InstallDirectory, "data", "tasks.json");
    public static string ProfilesStorePath => Path.Combine(InstallDirectory, "data", "profiles.json");

    public static VersionProfiles VersionProfiles { get; private set; } =
        new VersionProfiles(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FuseLauncher", "profiles.json"));

    /// <summary>记录模组安装来源(导出下载清单用)。</summary>
    public static void RecordModInstall(string instanceDir, string url, string fileName, string? sha1)
    {
        try
        {
            var path = System.IO.Path.Combine(instanceDir, "mods.mcdl.json");
            var list = new System.Collections.Generic.List<object>();
            if (System.IO.File.Exists(path))
                list = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<object>>(
                    System.IO.File.ReadAllText(path)) ?? new System.Collections.Generic.List<object>();
            list.Add(new { url, fileName, sha1 = sha1 ?? "" });
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(list, Newtonsoft.Json.Formatting.Indented));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// V1.0.0 改名的一次性数据迁移:MCDownloader → FuseLauncher(应用数据 + 文档目录)。
    /// 同卷 Directory.Move 是瞬时重命名;任何一步失败都保留旧目录继续可用。
    /// </summary>
    private static void MigrateLegacyData()
    {
        // 便携模式不碰用户漫游目录/文档目录,避免"带着 U 盘启动却把本机数据搬进来"
        if (IsPortable) return;
        // 逐文件搬移而不是整目录 Move:安装器可能已预置 config.json,
        // 新位置已有的文件以新数据为准,缺的逐个补 —— 两种安装场景都成立。
        MoveLegacyContents(LegacyAppDataDir, AppDataDir);
        MoveLegacyContents(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MCDownloader"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FuseLauncher"));
    }

    /// <summary>便携模式首次启动:把本机已有的配置/账号复制过来(只补缺,不覆盖)。</summary>
    private static void MigrateRoamingToPortable()
    {
        if (!IsPortable) return;
        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuseLauncher");
        if (!Directory.Exists(roaming)) return;
        try { Directory.CreateDirectory(AppDataDir); } catch (IOException) { return; }
        foreach (var name in new[] { "config.json", "accounts.json" })
        {
            var src = Path.Combine(roaming, name);
            var dst = Path.Combine(AppDataDir, name);
            if (!File.Exists(src) || File.Exists(dst)) continue;
            try { File.Copy(src, dst); } catch (IOException) { }
        }
    }

    private static void MoveLegacyContents(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var entry in Directory.GetFileSystemEntries(from))
            {
                var target = Path.Combine(to, Path.GetFileName(entry));
                if (File.Exists(target) || Directory.Exists(target)) continue;   // 新数据优先
                try
                {
                    Directory.Move(entry, target);   // 对文件与目录都生效(同卷瞬时)
                }
                catch (Exception)
                {
                    // 单个条目被占用:跳过它,不影响其余迁移
                }
            }
            try
            {
                if (Directory.GetFileSystemEntries(from).Length == 0) Directory.Delete(from);
            }
            catch (Exception)
            {
            }
        }
        catch (Exception)
        {
            // 迁移失败不阻塞启动,旧目录保留仍可读
        }
    }

    public static void Initialize()
    {
        MigrateLegacyData();
        MigrateRoamingToPortable();

        // .NET Framework 默认每域名 2 个并发连接,会把 32 路的小文件下载压成 2 路——
        // 几千个资源文件按 2 个/秒的速度要下几十分钟(用户实测 6 秒只进 1 个文件)。
        System.Net.ServicePointManager.DefaultConnectionLimit = 256;
        System.Net.ServicePointManager.Expect100Continue = false;
        System.Net.ServicePointManager.SecurityProtocol =
            System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls11;

        Config = AppConfig.Load(ConfigPath);
        if (string.IsNullOrEmpty(Config.InstallDirectory))
            Config.InstallDirectory = DefaultInstallDirectory;
        // JSON 里可能显式写 null(旧配置/手改),统一补回空集合
        if (Config.ExtraGameDirectories == null) Config.ExtraGameDirectories = new List<string>();
        if (Config.GameFolderNames == null) Config.GameFolderNames = new Dictionary<string, string>();
        // 旧配置里 UpdateManifestUrl 显式为 null(没有更新源):补上默认 OSS 清单;
        // 空字符串表示用户主动留空,不覆盖。
        if (Config.UpdateManifestUrl == null)
        {
            Config.UpdateManifestUrl = AppConfig.DefaultUpdateManifestUrl;
            SaveConfig();
        }
        PruneMissingGameDirectories();
        // 2026-11-04: 原版批量下载改走官方源(不限流),并发从 8 提到 16;
        // 旧配置里这个字段没暴露在 UI,统一升级,避免重装后仍是"8 路 + 429 退避"的慢速组合
        if (Config.SmallFileParallelism <= 8)
        {
            Config.SmallFileParallelism = 16;
            SaveConfig();
        }
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "FuseLauncher/" + AppVersion + " Java/17.0.15 (Windows 10; x64; minecraft launcher)");
        // 跨游戏目录的只读对象缓存(按哈希命名;同盘硬链接,跨盘复制)
        Fuse.Core.Download.DownloadCache.Root = Path.Combine(Config.InstallDirectory, "cache", "objects");
        Accounts.Load();
        VersionProfiles = new VersionProfiles(ProfilesStorePath);
        ApplyTheme();
        RebuildQueue();
        Queue!.RestoreOnStartup();
        // 启动即并发测速:之后打开下载面板直接用最快的源,不必等官方超时
        Fuse.Core.Download.MirrorProbe.StartProbe(Http);
        // 上次启动器退出时游戏可能还在跑:能接上进程就继续计时,否则按心跳补记时长
        PlayTracker.ResumeOnStartup();
    }

    /// <summary>并发数等设置变更后重建队列（任务记录在 store 中保留）。</summary>
    public static void RebuildQueue()
    {
        var store = new DownloadTaskStore(TasksStorePath);
        Accounts.MigrateLegacyIfNeeded();   // 旧单账号数据迁移到账号列表
        // 下载队列的诊断日志并入 boot.log(失败时能看到卡在哪个阶段/完整异常)
        DownloadQueue.Log = msg => WidgetWindow.Boot("[download] " + msg);
        Queue = DownloadQueue.CreateDefault(store, Http, Config.MaxConcurrentTasks, Config.SmallFileParallelism);
        // 下载任务失败同样弹"坏菜了"(用户要求:任何差错都要提示并可导出日志)
        Queue.TaskChanged += record =>
        {
            if (record.Status != DownloadTaskStatus.Failed) return;
            ErrorReporter.Report("下载失败：" + record.Name, null,
                "任务 " + record.TaskId + " · " + record.ErrorMessage);
        };
    }

    /// <summary>管线登录节点：正版令牌可用时优先正版，否则离线档案。</summary>
    public static IAuthProvider CreateAuthProvider()
    {
        // 外置登录账号:令牌/UUID 由皮肤站签发(TokenAuthProvider)
        if (Accounts.Data.ActiveType == "external" && !string.IsNullOrEmpty(Accounts.Data.ExtPlayerName))
        {
            var uuid = Guid.TryParse(Accounts.Data.ExtUuid, out var parsed) ? parsed : Guid.NewGuid();
            return new TokenAuthProvider(Accounts.Data.ExtPlayerName!, uuid,
                Accounts.Data.ExtAccessToken ?? "0");
        }
        if (Accounts.Data.ActiveType == "msa" && Accounts.GetMicrosoftRefreshToken() != null)
            return new MicrosoftAuthProvider(Accounts, Config.MicrosoftClientId);
        return new OfflineAuthProvider(Accounts.Data.OfflinePlayerName);
    }

    /// <summary>
    /// 启动前的皮肤准备:来源跟账号类型走,互斥(方案 A 三分法)。
    /// 外置(皮肤站)账号 → 停用 CSL,皮肤/披风由 authlib-injector 从站点取;
    /// 正版(MSA)账号 → 停用 CSL,游戏用真会话令牌原生拉取 Mojang 官方皮肤/披风;
    /// 离线账号 → 恢复 CSL 并铺好本地皮肤(所有离线账号共用 skins\local)。
    /// 失败只记日志,不拦截启动。
    /// </summary>
    public static async Task PrepareSkinAsync(string versionId, string? rootDir = null)
    {
        try
        {
            var account = Accounts.Data;
            var root = rootDir ?? ActiveGameDirectory;
            var instanceDir = InstanceDirectoryIn(root, versionId);
            var versionsRoot = Path.Combine(root, "versions");

            if (account.ActiveType != "offline")
            {
                // 外置/正版都不需要 CSL:前者有站点源,后者有 Mojang 官方源;
                // CSL 留在 mods 里反而会用本地文件把官方皮肤盖掉
                Core.Installer.LocalSkinSetup.SuspendForExternal(instanceDir);
                return;
            }

            // 之前按外置/正版账号启动过就恢复 CSL,否则本地皮肤铺上了也不会生效
            Core.Installer.LocalSkinSetup.RestoreForLocal(instanceDir);

            await Core.Installer.LocalSkinSetup.EnsureAsync(
                Http, versionId, versionsRoot, instanceDir,
                System.IO.Path.Combine(InstallDirectory, "skins", "local"),
                account.OfflinePlayerName, System.Threading.CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 外置账号启动前准备:用存的密码静默换新 token,并确保 authlib-injector 就位。
    /// 返回要附加的 -javaagent 参数;离线/正版账号返回 null。失败直接抛(由调用方弹"坏菜了")。
    /// </summary>
    public static async Task<string?> PrepareExternalLaunchAsync()
    {
        if (Accounts.Data.ActiveType != "external") return null;
        var apiRoot = Accounts.Data.ExtApiRoot;
        if (string.IsNullOrEmpty(apiRoot))
            throw new InvalidOperationException("外置账号缺少站点，请重新登录");

        var oauthRefresh = Accounts.GetExternalOAuthRefresh();
        if (!string.IsNullOrEmpty(oauthRefresh))
        {
            // LittleSkin OAuth:刷新令牌 → 重新换取 Minecraft 令牌,不需要密码
            var token = await Core.Accounts.LittleSkinOAuth.RefreshAsync(
                Http, oauthRefresh!, System.Threading.CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token.RefreshToken)) Accounts.UpdateExternalOAuth(token.RefreshToken);
            var uuid = Accounts.Data.ExtUuid;
            if (string.IsNullOrEmpty(uuid))
                throw new InvalidOperationException("LittleSkin 账号缺少角色信息，请重新授权登录");
            var oauthSession = await Core.Accounts.LittleSkinOAuth.CreateMinecraftTokenAsync(
                Http, token.AccessToken, uuid!, System.Threading.CancellationToken.None).ConfigureAwait(false);
            Accounts.UpdateExternalSession(oauthSession);
        }
        else
        {
            var password = Accounts.GetExternalPassword();
            var userName = Accounts.Data.ExtUserName ?? Accounts.Data.ExtPlayerName;
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(userName))
                throw new InvalidOperationException("外置账号缺少站点或密码,请重新登录");

            var clientToken = Accounts.Data.ExtClientToken;
            if (string.IsNullOrEmpty(clientToken)) clientToken = Guid.NewGuid().ToString("N");
            var session = await Core.Accounts.ExternalAuth.AuthenticateAsync(
                Http, apiRoot!, userName!, password!, clientToken!, System.Threading.CancellationToken.None)
                .ConfigureAwait(false);
            Accounts.UpdateExternalSession(session);
        }

        var jar = await Core.Accounts.AuthlibInjector.EnsureAsync(
            Http, System.IO.Path.Combine(InstallDirectory, "injector"), System.Threading.CancellationToken.None)
            .ConfigureAwait(false);
        return $"-javaagent:{jar}={apiRoot}";
    }

    public static void SaveConfig() => Config.Save(ConfigPath);

    /// <summary>把配置里的自定义颜色应用到全局资源(组件高亮/面板底色)。</summary>
    public static void ApplyTheme()
    {
        var res = Application.Current.Resources;
        var accent = ParseHex(Config.AccentColorHex, Color.FromRgb(0xA4, 0x16, 0xE5));
        res["GreenBrush"] = new SolidColorBrush(accent);
        res["OrbGlow"] = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 120,
            Color = accent,
            ShadowDepth = 0,
            Opacity = 0.9,
        };
        res["PetalFillBrush"] = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        res["PetalBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x73, accent.R, accent.G, accent.B));
        res["AccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(0x59, accent.R, accent.G, accent.B));
        // 圆形填充的边缘羽化:外圈 93% 之后渐隐到透明,去掉"贴了张圆片"的硬边
        res["OrbFillBrush"] = SoftDisc(accent, 1.0, 0.86);
        res["PetalFillSoftBrush"] = SoftDisc(accent, 0.38, 0.55);
        // 花瓣垫底的光晕盘也必须羽化,否则它那个实心圆边会把"渐变透明"完全盖住
        res["PetalGlowDiscBrush"] = SoftDisc(accent, 0.55, 0.0);
        // 选中态底色 = 组件高亮色的实心填充(全站统一:"当前/选中"一律是实心强调色)
        // 更淡的提示底 / 状态与勾选文字沿用各自的不透明度(原先是写死的绿,主题换色后不跟随)
        res["AccentChipBrush"] = new SolidColorBrush(accent);
        res["AccentChipSoftBrush"] = new SolidColorBrush(Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
        res["AccentTextBrush"] = new SolidColorBrush(Lighten(accent, 0.45));
        res["PetalGlow"] = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 56,
            Color = accent,
            ShadowDepth = 0,
            Opacity = 0.6,
        };

        // 点击通明色:未单独设置时跟随组件高亮色,保证默认观感不变
        var hover = ParseHex(Config.HoverColorHex, accent);
        // 悬停不透明度 95%(用户两轮上调:30%→80%→95%,要求悬停色贴近色板原色);按下 97%
        res["HoverBrush"] = new SolidColorBrush(Color.FromArgb(0xF2, hover.R, hover.G, hover.B));
        res["HoverSoftBrush"] = new SolidColorBrush(Color.FromArgb(0x1A, hover.R, hover.G, hover.B));
        res["HoverPressedBrush"] = new SolidColorBrush(Color.FromArgb(0xF7, hover.R, hover.G, hover.B));
        res["HoverSolidBrush"] = new SolidColorBrush(Scale(hover, 0.72));
        res["HoverPetalBrush"] = new SolidColorBrush(Color.FromArgb(0x61, hover.R, hover.G, hover.B));
        // 花瓣悬浮态:同一套径向渐变、只提亮,不加任何描边
        res["PetalHoverFillBrush"] = SoftDisc(hover, 0.80, 0.30);
        res["HoverPetalBorderBrush"] = new SolidColorBrush(Color.FromArgb(0xB3, hover.R, hover.G, hover.B));
        res["HoverColor"] = hover; // Color 类型:给 DropShadowEffect.Color 用

        // 面板 95% 不透明(用户指定):留一点玻璃感,同时不出现整块灰底
        var bg = ParseHex(Config.PanelBgHex, Color.FromRgb(0x1E, 0x20, 0x26));
        res["PanelBgBrush"] = new SolidColorBrush(Color.FromArgb(0xF2, bg.R, bg.G, bg.B));
        // 大圆上的苦力怕脸用主题对比色,换色后需要重绘
        Widget?.RefreshOrbGlyph();
        ThemeLog($"accent=#{accent.R:X2}{accent.G:X2}{accent.B:X2} " +
                 $"hover=#{hover.R:X2}{hover.G:X2}{hover.B:X2} " +
                 $"bg=#{bg.R:X2}{bg.G:X2}{bg.B:X2}");

        // 文字/行底自适应:亮底深字,暗底白字
        var lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        var isLight = lum > 0.55;
        res["TextPrimaryBrush"] = new SolidColorBrush(
            isLight ? Color.FromRgb(0x17, 0x1A, 0x20) : Colors.White);
        res["TextSecondaryBrush"] = new SolidColorBrush(isLight
            ? Color.FromArgb(0xA6, 0x17, 0x1A, 0x20)
            : Color.FromArgb(0xA6, 255, 255, 255));
        res["TextTertiaryBrush"] = new SolidColorBrush(isLight
            ? Color.FromArgb(0x6B, 0x17, 0x1A, 0x20)
            : Color.FromArgb(0x6B, 255, 255, 255));
        res["RowBgBrush"] = new SolidColorBrush(isLight
            ? Color.FromArgb(0x14, 0, 0, 0)
            : Color.FromArgb(0x12, 255, 255, 255));
    }

    public static Color ParseHex(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex!.Trim());
        }
        catch (Exception)
        {
        }
        return fallback;
    }

    /// <summary>配色诊断:写入 boot.log,便于确认自定义色真的换上了。</summary>
    private static void ThemeLog(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FuseLauncher", "boot.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [theme] {message}" + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 径向渐变圆盘:从 featherStart 处开始向边缘渐隐到透明,让轮廓化开(代替 1px 描边)。
    /// featherStart=0 时整盘都是渐变(柔光球),越大越接近实心圆。
    /// </summary>
    private static RadialGradientBrush SoftDisc(Color c, double alpha, double featherStart)
    {
        var solid = Color.FromArgb((byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))), c.R, c.G, c.B);
        var clear = Color.FromArgb(0, c.R, c.G, c.B);
        var brush = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5) };
        brush.GradientStops.Add(new GradientStop(solid, 0.0));
        brush.GradientStops.Add(new GradientStop(solid, Math.Max(0.0, Math.Min(0.98, featherStart))));
        brush.GradientStops.Add(new GradientStop(clear, 1.0));
        brush.Freeze();
        return brush;
    }

    /// <summary>与白色混合,得到在主色上可读的亮化文字色。</summary>
    public static Color Lighten(Color c, double amount)
    {
        return Color.FromRgb(
            (byte)Math.Min(255, c.R + (255 - c.R) * amount),
            (byte)Math.Min(255, c.G + (255 - c.G) * amount),
            (byte)Math.Min(255, c.B + (255 - c.B) * amount));
    }

    /// <summary>按比例压暗(实心按钮的悬停/按下色)。</summary>
    public static Color Scale(Color c, double factor)
    {
        return Color.FromRgb(
            (byte)Math.Max(0, Math.Min(255, c.R * factor)),
            (byte)Math.Max(0, Math.Min(255, c.G * factor)),
            (byte)Math.Max(0, Math.Min(255, c.B * factor)));
    }

    public static void Dispatch(Action action, Dispatcher dispatcher)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
