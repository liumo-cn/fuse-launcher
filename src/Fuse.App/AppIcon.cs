using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Fuse.App;

internal partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>
/// 启动器图标:Assets/fuse.ico —— 组件高亮绿的大圆 + 方块闪电(与悬浮组件大圆同款)。
/// 同一个文件既是 exe 图标(探索器/任务栏),也是窗口图标和托盘图标。
/// </summary>
public static class AppIcon
{
    private static BitmapSource? _image;
    private static bool _loaded;

    /// <summary>WPF 窗口图标(取 .ico 里最大的一帧)。</summary>
    public static BitmapSource? Image
    {
        get
        {
            if (_loaded) return _image;
            _loaded = true;
            try
            {
                using var stream = OpenStream();
                if (stream == null) return null;
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                foreach (var frame in decoder.Frames)
                    if (_image == null || frame.PixelWidth > _image.PixelWidth) _image = frame;
            }
            catch (Exception)
            {
            }
            return _image;
        }
    }

    /// <summary>WinForms 托盘图标(32px 帧;取不到就退回一个绿圆)。</summary>
    public static System.Drawing.Icon BuildTrayIcon()
    {
        try
        {
            using var stream = OpenStream();
            if (stream != null)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                buffer.Position = 0;
                return new System.Drawing.Icon(buffer, 32, 32);
            }
        }
        catch (Exception)
        {
        }

        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new System.Drawing.SolidBrush(System.Drawing.ColorTranslator.FromHtml("#A416E5"));
            g.FillEllipse(brush, 1, 1, 30, 30);
        }
        // Icon.FromHandle 的句柄不随 Icon 释放,克隆后立即销毁原生句柄,避免 GDI 泄漏
        var handle = bmp.GetHicon();
        try
        {
            using var fromHandle = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)fromHandle.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static Stream? OpenStream()
    {
        var res = Application.GetResourceStream(new Uri("Assets/fuse.ico", UriKind.Relative));
        return res?.Stream;
    }
}
