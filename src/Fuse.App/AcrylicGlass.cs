using System;
using System.Runtime.InteropServices;

namespace Fuse.App;

/// <summary>
/// Win10/11 系统亚克力毛玻璃（SetWindowCompositionAttribute，非官方文档 API，
/// 现代桌面软件通用做法）。失败时由调用方退回半透明底色。
/// </summary>
public static class AcrylicGlass
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute { WCA_ACCENT_POLICY = 19 }

    private enum AccentState { Disabled = 0, BlurBehind = 3, AcrylicBlurBehind = 4 }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>启用亚克力。tint 为 ABGR（0xAARRGGBB 需换序），alpha 控制整体浓度。</summary>
    public static bool Enable(IntPtr hwnd, uint abgrTint)
    {
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = (int)AccentState.AcrylicBlurBehind,
                GradientColor = abgrTint,
            };
            var accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = Marshal.SizeOf(accent),
            };
            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(accentPtr);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
