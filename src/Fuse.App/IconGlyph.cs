using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fuse.App;

/// <summary>
/// 四个花瓣上的线条图标(书/齿轮/下载/人形)。
///
/// 素材是白底黑线的截图,这里变成「纯白线条 + 透明底」:
/// 亮度转 alpha(白→全透明、黑→不透明,灰阶自然是边缘过渡),裁到线条包围盒后
/// 等比缩放到统一画布,避免各图留白不同导致图标忽大忽小。
/// </summary>
public static class IconGlyph
{
    /// <summary>输出画布边长(超采样,显示时缩小到 30px 左右)。</summary>
    private const int Canvas = 256;

    private static readonly Dictionary<string, BitmapSource> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取图标(name: login / download / settings / versions)。失败返回 null。</summary>
    public static BitmapSource? Get(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri($"pack://application:,,,/Assets/{name}.png", UriKind.Absolute);
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();

            var bmp = Build(source);
            Cache[name] = bmp;
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapSource Build(BitmapImage source)
    {
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        var src = new byte[w * h * 4];
        source.CopyPixels(src, w * 4, 0);

        // 1. 亮度转 alpha + 求线条包围盒(严格阈值,排除浅灰边线)
        var alpha = new byte[w * h];
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var p = i * 4;
                var lum = (src[p + 2] + src[p + 1] + src[p]) / 3;
                if (lum >= 200) continue;          // 白底
                var a = (byte)(255 - lum);
                if (a < 40) continue;              // 极浅抗锯齿
                alpha[i] = a;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        var canvas = new byte[Canvas * Canvas * 4];
        if (maxX < minX || maxY < minY) return Create(canvas);

        // 2. 等比缩放到画布中央(长边铺满,留 6% 内边距)
        var cw = maxX - minX + 1;
        var ch = maxY - minY + 1;
        var usable = Canvas * 0.88;
        var scale = usable / Math.Max(cw, ch);
        var drawW = (int)(cw * scale);
        var drawH = (int)(ch * scale);
        var offX = (Canvas - drawW) / 2;
        var offY = (Canvas - drawH) / 2;

        // 双线性采样:最近邻会在缩放后留下明显锯齿(线条边缘一格一格的)
        for (var y = 0; y < drawH; y++)
        {
            for (var x = 0; x < drawW; x++)
            {
                var a = SampleBilinear(alpha, w, h,
                    minX + x / scale, minY + y / scale);
                if (a == 0) continue;
                var o = ((y + offY) * Canvas + (x + offX)) * 4;
                canvas[o] = 0xFF;     // B
                canvas[o + 1] = 0xFF; // G
                canvas[o + 2] = 0xFF; // R —— 纯白线条
                canvas[o + 3] = a;
            }
        }

        return Create(canvas);
    }

    /// <summary>双线性插值取 alpha(越界按 0 处理:包围盒外就是白底)。</summary>
    private static byte SampleBilinear(byte[] alpha, int w, int h, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var x1 = x0 + 1;
        var y1 = y0 + 1;

        double A(int px, int py) =>
            px < 0 || py < 0 || px >= w || py >= h ? 0 : alpha[py * w + px];

        var top = A(x0, y0) * (1 - fx) + A(x1, y0) * fx;
        var bottom = A(x0, y1) * (1 - fx) + A(x1, y1) * fx;
        return (byte)Math.Max(0, Math.Min(255, top * (1 - fy) + bottom * fy));
    }

    private static BitmapSource Create(byte[] pixels)
    {
        var bmp = BitmapSource.Create(Canvas, Canvas, 96, 96,
            PixelFormats.Bgra32, null, pixels, Canvas * 4);
        bmp.Freeze();
        return bmp;
    }
}
