using System;
using System.Runtime.InteropServices;

namespace Fuse.App;

/// <summary>窗口移动交给系统(DWM):比手改 Left/Top 少无数次整窗重新呈现。</summary>
internal partial class NativeMethods
{
    internal const int WmNcLButtonDown = 0x00A1;
    internal const int HtCaption = 0x0002;

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
