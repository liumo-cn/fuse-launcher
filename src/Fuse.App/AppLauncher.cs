using System;
using System.Collections.Generic;
using System.Text;
using System;
using System.Threading.Tasks;
using Fuse.Core.Launch;
using Fuse.Core.Pipeline;

namespace Fuse.App;

/// <summary>启动结果:成功拉起 / 同版本已占用被拦下 / 启动失败。</summary>
public enum LaunchOutcome
{
    Started,
    AlreadyBusy,
    Failed,
}

/// <summary>启动入口(悬浮组件与版本面板共用):封装管线调用与状态回传。</summary>
public static class AppLauncher
{
    /// <summary>启动入口统一打点:所有入口(大圆/版本面板 ▶)都写 boot.log,便于确认点击是否生效。</summary>
    private static void Log(string message)
    {
        Fuse.Core.Logging.LogSink.Append(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FuseLauncher", "boot.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] [launch] {message}" + Environment.NewLine);
    }

    public static async Task<LaunchOutcome> LaunchAsync(string versionId, Action<string> status, string? serverAddress = null)
    {
        // 同一版本只允许一个实例:启动中/运行中都会在这里被挡下,不管从哪个入口点击
        var blocked = LaunchState.TryBegin(versionId);
        if (blocked != null)
        {
            Log("blocked " + versionId + ": " + blocked);
            status(blocked);
            return LaunchOutcome.AlreadyBusy;
        }

        Log("begin " + versionId);

        // 启动器重启后内存态丢了:有带标记的同一局还在跑就直接认领,不再拉起第二个游戏
        if (AdoptRunningGame(versionId))
        {
            status("检测到游戏已在运行，直接使用");
            return LaunchOutcome.Started;
        }

        status("正在启动…");
        var widget = AppServices.Widget;
        widget?.NotifyLaunchStarted();
        try
        {
            // 版本可能在任何一个游戏目录里(主目录 / 导入的官方或第三方目录),按所在目录启动
            var root = AppServices.FindVersionRoot(versionId) ?? AppServices.ActiveGameDirectory;
            var versionsRoot = System.IO.Path.Combine(root, "versions");
            var gameDirectory = AppServices.InstanceDirectoryIn(root, versionId);
            Log("root " + versionId + ": " + root);

            // 版本列表可能是旧的(文件夹切换/外部删除),启动前先确认 version.json 真的在;
            // 否则管线会抛 "找不到版本"，用户只看到一堆堆栈看不懂发生了什么
            var versionJson = System.IO.Path.Combine(versionsRoot, versionId, versionId + ".json");
            if (!System.IO.File.Exists(versionJson))
            {
                Log("missing version json: " + versionJson);
                status("版本文件不完整：缺少 " + versionId + ".json");
                ErrorReporter.Report("启动失败 " + versionId,
                    new System.IO.FileNotFoundException(
                        "找不到版本文件：" + versionJson + "\n\n" +
                        "可能原因：下载/安装中断、文件被移动或删除，或者当前游戏目录里没有这个版本。\n" +
                        "可以在该版本详情页点「校验并修复文件」，或重新下载这个版本。"),
                    null);
                LaunchState.Abandon(versionId);
                widget?.NotifyLaunchFailed();
                return LaunchOutcome.Failed;
            }

            // 启动前模组冲突预检:只拦"能自动修"的问题,纯建议不打扰
            if (AppServices.Config.CheckModConflictsBeforeLaunch)
            {
                try
                {
                    var gameVersion = Fuse.Core.Versioning.VersionJsonLoader.ResolveGameVersion(versionsRoot, versionId);
                    var modsDir = System.IO.Path.Combine(gameDirectory, "mods");
                    if (System.IO.Directory.Exists(modsDir))
                    {
                        var loader = DetectLoader(versionsRoot, versionId);
                        var report = await Task.Run(() =>
                            Fuse.Core.ModConflict.ModConflictAnalyzer.Analyze(modsDir, gameVersion, loader));
                        if (report.Actions.Any(a => a.CanAutoFix))
                        {
                            var dialog = new ModConflictWindow(report)
                            {
                                Owner = widget,
                            };
                            if (dialog.ShowDialog() != true || dialog.Selected.Count == 0)
                            {
                                Log("cancelled by conflict dialog: " + versionId);
                                status("已取消启动：请先修复模组冲突");
                                LaunchState.Abandon(versionId);
                                widget?.NotifyLaunchFailed();
                                return LaunchOutcome.Failed;
                            }

                            status("正在修复模组冲突…");
                            var outcome = await Fuse.Core.ModConflict.ModConflictFixer.ApplyAsync(
                                modsDir, gameVersion, "", dialog.Selected, AppServices.Http,
                                new Progress<string>(msg => status(msg)), System.Threading.CancellationToken.None);
                            if (outcome.Failed.Count > 0)
                            {
                                new InfoWindow("模组冲突修复",
                                    "以下项目没有修好：" + Environment.NewLine + "· " +
                                    string.Join(Environment.NewLine + "· ", outcome.Failed)) { Owner = widget }.ShowDialog();
                                LaunchState.Abandon(versionId);
                                widget?.NotifyLaunchFailed();
                                return LaunchOutcome.Failed;
                            }
                            Log("mod conflicts fixed before launch: " + outcome.Applied.Count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 预检失败不能阻止正常启动
                    Log("conflict precheck skipped: " + ex.Message);
                }
            }

            // 外置登录账号:先静默换新 token 并确保 authlib-injector 就位(失败会抛,走下面的 catch + 弹窗)
            var agentArg = await AppServices.PrepareExternalLaunchAsync().ConfigureAwait(false);

            // 皮肤来源跟账号类型走:外置/正版停用 CSL(官方/站点供皮),离线铺本地皮肤
            await AppServices.PrepareSkinAsync(versionId, root).ConfigureAwait(false);

            var pipeline = new LaunchPipeline(AppServices.Http)
            {
                AuthProvider = AppServices.CreateAuthProvider(),
            };
            // 每版本配置优先,无则回退全局设置(FR-12 详情页「修改」)
            var profile = AppServices.VersionProfiles.Get(versionId);
            // 内存:每版本固定值 > 自动(物理内存 + 模组数) > 手动全局值
            var heapMb = AppServices.ResolveHeapMb(versionId, out var heapAuto, out var modCount);
            Log("heap " + versionId + ": " + heapMb + "MB (" + (heapAuto ? "auto" : "manual")
                + ", mods=" + modCount + ")");
            // 显式传入的地址(服务器列表「加入」)优先;否则用版本档案里的"自动加入"
            var effectiveServer = string.IsNullOrWhiteSpace(serverAddress)
                ? profile.AutoJoinServer
                : serverAddress;
            var options = new LaunchOptions
            {
                VersionsRoot = versionsRoot,
                VersionId = versionId,
                GameDirectory = gameDirectory,
                LibrariesDirectory = System.IO.Path.Combine(root, "libraries"),
                AssetsRoot = System.IO.Path.Combine(root, "assets"),
                JavaRuntimesRoot = System.IO.Path.Combine(AppServices.InstallDirectory, "runtimes"),
                JavaHomeOverride = profile.JavaPolicy == "manual" && !string.IsNullOrEmpty(profile.JavaHomeOverride)
                    ? profile.JavaHomeOverride
                    : AppServices.Config.JavaPolicy == "manual" ? AppServices.Config.JavaHomeOverride : null,
                MaxHeapMb = heapMb,
                ExtraJvmArgs = BuildExtraJvmArgs(agentArg, profile.JvmArgs, versionId),
                // 服务器列表「加入」或档案里的"自动加入":传 host 或 host:port,启动管线走 quickPlay/--server
                ServerAddress = string.IsNullOrWhiteSpace(effectiveServer) ? null : effectiveServer!.Trim(),
            };
            var result = await pipeline.LaunchAsync(options, new Progress<LoaderProgress>(p =>
            {
                status($"{p.Stage} {p.Progress:P0}");
                widget?.NotifyLaunchProgress(LaunchFx.MapStageProgress(p.Stage, p.Progress));
            }));
            AppServices.Config.LastLaunchedVersion = versionId;
            AppServices.SaveConfig();
            Log("ok " + versionId);
            // 游戏进程接管"运行中"占位,退出后自动释放,期间同版本再点会被拦下
            LaunchState.MarkRunning(versionId, result.GameProcess);
            // 游玩统计:从窗口出现开始计时,进程退出时结算
            PlayTracker.Start(versionId, result.GameProcess);
            // 进程优先级(设置 → 进程优先级):调整 MC 对 CPU 资源的占用档位
            try
            {
                result.GameProcess.PriorityClass = AppServices.Config.GamePriority switch
                {
                    "high" => System.Diagnostics.ProcessPriorityClass.High,
                    "low" => System.Diagnostics.ProcessPriorityClass.BelowNormal,
                    _ => System.Diagnostics.ProcessPriorityClass.Normal,
                };
                Log("priority " + versionId + ": " + AppServices.Config.GamePriority);
            }
            catch (Exception)
            {
                // 优先级设置失败不影响游戏本身
            }
            // 用户反馈:游戏窗口一出现就炸,不再等主菜单日志
            widget?.NotifyLaunchSucceeded();
            status("游戏已启动 ✓");
            return LaunchOutcome.Started;
        }
        catch (Exception ex)
        {
            LaunchState.Abandon(versionId);
            widget?.NotifyLaunchFailed();
            Log("failed " + versionId + ": " + ex.Message);
            Fuse.Core.Logging.LogSink.Append(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FuseLauncher", "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 启动失败\n{ex}\n\n");
            status("启动失败：" + ex.Message);
            var logPath = System.IO.Path.Combine(AppServices.InstanceDirectory(versionId), "logs", "latest-launch.log");
            ErrorReporter.Report("启动失败 " + versionId, ex, logPath);
            return LaunchOutcome.Failed;
        }
    }

    /// <summary>外置登录的 -javaagent 在前;认领标记随后;用户自定义 JVM 参数最后(详情页「修改」里填)。</summary>
    private static IReadOnlyList<string> BuildExtraJvmArgs(string? agentArg, string? userArgs, string versionId)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(agentArg)) list.Add(agentArg!);
        // 认领标记:启动器重启后 RunningGameFinder 靠它把还在跑的游戏认回来
        list.Add("-Dfuse.version=" + versionId);
        list.AddRange(SplitCommandLine(userArgs));
        return list;
    }

    /// <summary>扫还在跑的 javaw,带本版本标记就接管进 LaunchState。成功返回 true。</summary>
    internal static bool AdoptRunningGame(string versionId)
    {
        try
        {
            var proc = RunningGameFinder.FindByMarker(versionId);
            if (proc == null) return false;
            LaunchState.MarkRunning(versionId, proc);
            Log("adopted " + versionId + " pid=" + proc.Id);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>按空格拆分参数,支持双引号(路径带空格时用 "C:\x y\a.jar")。</summary>
    private static IEnumerable<string> SplitCommandLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        var current = new StringBuilder();
        var quoted = false;
        foreach (var ch in raw!)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static string DetectLoader(string versionsRoot, string versionId)
    {
        try
        {
            var merged = Fuse.Core.Versioning.VersionJsonLoader.Load(versionsRoot, versionId);
            foreach (var lib in merged.Libraries)
            {
                var name = lib.Name;
                if (name.StartsWith("net.fabricmc:fabric-loader", StringComparison.OrdinalIgnoreCase)) return "fabric";
                if (name.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase)) return "neoforge";
                if (name.StartsWith("net.minecraftforge:", StringComparison.OrdinalIgnoreCase)) return "forge";
            }
        }
        catch (Exception)
        {
        }
        return "";
    }
}
