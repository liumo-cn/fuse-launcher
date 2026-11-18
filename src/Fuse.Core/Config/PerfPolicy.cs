namespace Fuse.Core.Config;

/// <summary>
/// 启动器的动画开销策略(纯逻辑,便于单元测试)。
/// </summary>
public static class PerfPolicy
{
    /// <summary>
    /// 是否自动切到低特效:
    /// Tier 0/1 表示 WPF 没有完整硬件加速(远程桌面、缺驱动、被优化工具关掉),
    /// 这种机器上分层窗口的每帧开销会放大很多。
    /// </summary>
    public static bool ShouldAutoEnableLowEffects(int tier, bool remoteSession)
        => tier < 2 || remoteSession;
}
