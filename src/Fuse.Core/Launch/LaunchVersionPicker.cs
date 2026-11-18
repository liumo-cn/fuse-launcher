namespace Fuse.Core.Launch;

/// <summary>
/// 大圆"一键启动上次版本"的版本选择。
/// LastLaunchedVersion 可能是被改名/乱码/删除的旧 id,必须对照已安装列表校验,
/// 否则点大圆会直接弹"缺少 version json"。
/// </summary>
public static class LaunchVersionPicker
{
    /// <summary>
    /// 返回要启动的版本 id。last 不在 installed 里时退回第一个已安装版本,
    /// 并通过 repaired 告知调用方需要把配置里的旧值修掉。
    /// </summary>
    public static string? Pick(string? last, IReadOnlyList<string> installed, out bool repaired)
    {
        repaired = false;
        if (installed == null || installed.Count == 0) return null;

        if (!string.IsNullOrEmpty(last))
        {
            for (var i = 0; i < installed.Count; i++)
                if (string.Equals(installed[i], last, StringComparison.Ordinal)) return installed[i];
            repaired = true;
        }
        return installed[0];
    }
}
