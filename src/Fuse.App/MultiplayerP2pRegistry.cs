using System;
using System.Collections.Generic;
using Fuse.Core.Multiplayer.FuseLink;

namespace Fuse.App;

/// <summary>
/// 好友侧 P2P 隧道生命周期:邀请窗关掉后隧道还得继续跑(游戏正在连 127.0.0.1:端口)。
/// 交给这里持有,启动器退出时统一释放。
/// </summary>
internal static class MultiplayerP2pRegistry
{
    private static readonly object Gate = new();
    private static readonly List<FuseLinkGuest> Sessions = new();

    /// <summary>接管一个已就绪的 guest 会话(不再由邀请窗释放)。</summary>
    public static void Hold(FuseLinkGuest guest)
    {
        lock (Gate) Sessions.Add(guest);
    }

    public static void Release(FuseLinkGuest guest)
    {
        lock (Gate) Sessions.Remove(guest);
        try { guest.Dispose(); } catch (Exception) { }
    }

    public static void DisposeAll()
    {
        lock (Gate)
        {
            foreach (var guest in Sessions)
            {
                try { guest.Dispose(); } catch (Exception) { }
            }
            Sessions.Clear();
        }
    }
}
