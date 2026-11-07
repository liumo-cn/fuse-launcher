using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Fuse.App;

/// <summary>
/// 写文本剪贴板:绕开 WPF 的 OLE 路径(SetDataObject 在游戏开着时实测持续抛
/// CLIPBRD_E_CANT_OPEN,外部进程却开得了剪贴板),直接走 user32,带重试。
/// 失败由调用方给提示,不再抛未处理异常。
/// </summary>
internal static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>尝试写入,全部重试失败返回 false。最多 blocking 约 0.5 秒,不长时间卡 UI。</summary>
    public static bool TrySetText(string text, int attempts = 8)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (Win32SetText(text)) return true;
            Thread.Sleep(60);
        }
        return false;
    }

    private static bool Win32SetText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            var chars = text.ToCharArray();
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((chars.Length + 1) * 2));
            if (h == IntPtr.Zero) return false;
            var ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero) { GlobalFree(h); return false; }
            Marshal.Copy(chars, 0, ptr, chars.Length);
            Marshal.WriteInt16(ptr, chars.Length * 2, 0);
            GlobalUnlock(h);
            // 成功后内存归系统所有,不再 GlobalFree
            return SetClipboardData(CF_UNICODETEXT, h) != IntPtr.Zero;
        }
        finally
        {
            CloseClipboard();
        }
    }
}
