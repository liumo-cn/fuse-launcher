using System;
using System.IO;
using Newtonsoft.Json;

namespace Fuse.Core.Config;

/// <summary>应用配置（PRD 10.1 config.json）：可配置项集中于此，外壳启动时加载。</summary>
public sealed class AppConfig
{
    public string InstallDirectory { get; set; } = "";
    public int Threads { get; set; } = 8;
    public int MaxConcurrentTasks { get; set; } = 3;
    /// <summary>
    /// 小文件下载并发。原版批量文件现在优先走官方源(无频率限制),16 路实测约 2.4MB/s;
    /// 旧默认 8 是为 BMCLAPI 的 10 次/分钟限流留的余量,现在只有回退镜像时才会打到 BMCL。
    /// </summary>
    public int SmallFileParallelism { get; set; } = 16;

    /// <summary>"auto"（按官方 javaVersion 自动管理）或 "manual"。</summary>
    public string JavaPolicy { get; set; } = "auto";
    public string? JavaHomeOverride { get; set; }

    /// <summary>离线档案用户名（FR-06 离线通道）。</summary>
    public string PlayerName { get; set; } = "Player";
    /// <summary>微软 OAuth 客户端 ID（null 用官方启动器公共 ID）。</summary>
    public string? MicrosoftClientId { get; set; }
    public string? LastLaunchedVersion { get; set; }
    /// <summary>手动内存分配 -Xmx（FR-12 设置面板滑块）；AutoHeap 开启时不使用。</summary>
    public long MaxHeapMb { get; set; } = 4096;
    /// <summary>按物理内存 + 模组数量自动决定堆大小（默认开；关闭后用手动滑块的 MaxHeapMb）。</summary>
    public bool AutoHeap { get; set; } = true;
    /// <summary>悬浮组件可见性（关闭后最小化到托盘，FR-12 设置面板）。</summary>
    public bool ShowWidget { get; set; } = true;
    /// <summary>启动游戏后是否保持显示：true = 一直显示；false = 爆炸播完就收进托盘。</summary>
    public bool KeepVisibleOnLaunch { get; set; } = true;
    /// <summary>自动检查更新(启动后后台检查;关闭后只走设置页的「检查更新」)。</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>默认更新清单地址(OSS 广州,fuse/ 前缀);null 表示未配置。</summary>
    public const string DefaultUpdateManifestUrl =
        "https://fuse-launcher-update.oss-cn-guangzhou.aliyuncs.com/fuse/manifest.json";
    /// <summary>更新清单地址(JSON: version/url/sha1/notes);空 = 未配置更新源。</summary>
    public string? UpdateManifestUrl { get; set; } = DefaultUpdateManifestUrl;
    /// <summary>开机自启动(默认关):写/删 HKCU Run 注册表项。</summary>
    public bool LaunchOnStartup { get; set; } = false;
    /// <summary>数据浮窗(实时显示 FPS/GPU/CPU/内存/网速/延迟)。</summary>
    public bool ShowStatsOverlay { get; set; } = false;
    /// <summary>数据浮窗位置(可拖动记忆);-1 = 未设置。</summary>
    public double StatsOverlayLeft { get; set; } = -1;
    public double StatsOverlayTop { get; set; } = -1;
    /// <summary>MC 进程优先级:high / normal / low(默认 normal),启动后应用到游戏进程。</summary>
    public string GamePriority { get; set; } = "normal";
    /// <summary>导入的额外游戏目录(官方或第三方启动器);主目录(InstallDirectory)不在其中。</summary>
    public List<string> ExtraGameDirectories { get; set; } = new();
    /// <summary>当前选中的游戏目录(版本列表 / 下载 / 自动补全都用它);空 = 主目录。</summary>
    public string? ActiveGameDirectory { get; set; }
    /// <summary>文件夹列表的显示名覆盖(完整路径 → 自定义名);只改显示,不动磁盘。</summary>
    public Dictionary<string, string> GameFolderNames { get; set; } = new();
    /// <summary>组件高亮颜色(FR-12 自定义,色环选取,#RRGGBB)。</summary>
    public string? AccentColorHex { get; set; }
    /// <summary>面板背景颜色(FR-12 自定义,色环选取,#RRGGBB)。</summary>
    public string? PanelBgHex { get; set; }
    /// <summary>点击(悬停/按下)高亮颜色(FR-12 自定义);为空时跟随 AccentColorHex。</summary>
    public string? HoverColorHex { get; set; }
    /// <summary>悬浮组件位置（可拖动记忆）。</summary>
    public double WidgetLeft { get; set; } = -1;
    public double WidgetTop { get; set; } = -1;

    // 个性化 / 外观（FR-09）
    public string? BackgroundImage { get; set; }
    public int BackgroundOpacity { get; set; } = 70;
    public int WindowOpacity { get; set; } = 100;

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        return new AppConfig();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
