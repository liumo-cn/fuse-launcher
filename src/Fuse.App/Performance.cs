using System;
using System.Windows.Media;

namespace Fuse.App;

/// <summary>
/// 渲染能力探测与低特效开关。只在启动时判定一次:
/// 自动开启过就记 LowEffectsAutoApplied,之后用户手动关掉就不会被再次覆盖。
/// </summary>
internal static class Performance
{
    /// <summary>WPF 渲染档位:2=完整硬件加速,1=部分,0=纯软件。</summary>
    public static int Tier { get; private set; } = 2;

    /// <summary>远程桌面/终端服务会话(WPF 在这些会话里会掉到软件渲染)。</summary>
    public static bool RemoteSession { get; private set; }

    public static bool LowEffects => AppServices.Config.LowEffects;

    public static void Initialize()
    {
        try { Tier = RenderCapability.Tier >> 16; }
        catch (Exception) { Tier = 2; }
        try { RemoteSession = System.Windows.Forms.SystemInformation.TerminalServerSession; }
        catch (Exception) { RemoteSession = false; }

        try
        {
            if (!AppServices.Config.LowEffectsAutoApplied)
            {
                AppServices.Config.LowEffectsAutoApplied = true;
                if (Core.Config.PerfPolicy.ShouldAutoEnableLowEffects(Tier, RemoteSession))
                    AppServices.Config.LowEffects = true;
                AppServices.SaveConfig();
            }
        }
        catch (Exception)
        {
            // 判定失败不影响启动:保持配置原值
        }

        WidgetWindow.Boot($"[perf] tier={Tier} remote={RemoteSession} low={LowEffects}");
    }
}
