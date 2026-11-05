using System;
using System.Collections.Generic;

namespace Fuse.App;

/// <summary>
/// 记录"这批下载是从哪个页面发起的":圆环只显示在第一个发起下载的页面上,
/// 避免同时打开多个面板时每页都跳一个进度环。
/// </summary>
public static class DownloadFocus
{
    private static readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal);

    /// <summary>当前独占进度显示的页面 kind(download / version-detail …)。</summary>
    public static string? Current { get; private set; }

    public static void Register(string taskId, string panelKind)
    {
        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(panelKind)) return;
        if (!Owners.ContainsKey(taskId)) Owners[taskId] = panelKind;
        if (Current == null) Current = panelKind;
    }

    public static string? OwnerOf(string taskId)
        => Owners.TryGetValue(taskId, out var kind) ? kind : null;

    /// <summary>按当前活跃任务刷新:全部结束后释放独占。</summary>
    public static void Sync(IEnumerable<string> activeTaskIds)
    {
        var alive = new HashSet<string>(activeTaskIds, StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var id in Owners.Keys)
            if (!alive.Contains(id)) stale.Add(id);
        foreach (var id in stale) Owners.Remove(id);

        if (Owners.Count == 0) Current = null;
        else if (Current == null)
        {
            foreach (var kind in Owners.Values) { Current = kind; break; }
        }
    }
}
