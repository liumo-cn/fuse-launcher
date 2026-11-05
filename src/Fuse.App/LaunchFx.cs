using System;

namespace Fuse.App;

/// <summary>
/// 启动大圆特效的共享支撑:管线阶段名 → 总进度换算
/// (权重与 Core 的 .Then 调用一一对应)。
/// 成功判定:游戏窗口一出现(LaunchAsync 返回)即触发成功脉冲
/// (用户反馈等主菜单日志延迟太久,方案 B 已废弃)。
/// </summary>
public static class LaunchFx
{
    private static readonly (string Name, double CumBefore, double Weight)[] Stages =
    {
        ("预检查", 0, 1), ("选择 Java", 1, 1), ("登录", 2, 1), ("自动修复", 3, 2),
        ("提取 Natives", 5, 1), ("组装参数", 6, 1), ("启动游戏", 7, 1), ("等待游戏窗口", 8, 3),
    };
    private const double TotalWeight = 11;

    /// <summary>把"阶段名 + 阶段内进度"换算成 0..1 的总进度;未知阶段给中间值。</summary>
    public static double MapStageProgress(string stage, double progress)
    {
        var p = Math.Max(0, Math.Min(1, progress));
        foreach (var s in Stages)
            if (string.Equals(s.Name, stage, StringComparison.Ordinal))
                return Math.Min(1.0, (s.CumBefore + p * s.Weight) / TotalWeight);
        return 0.5;
    }
}
