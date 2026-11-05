using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fuse.App;

/// <summary>
/// 启动大圆上的浮雕图形(当前:方块闪电,曾为苦力怕脸)。
///
/// 处理成「纯白实心轮廓 + 中间清晰外围淡出」,素材极性自动判别:
/// ① 黑底白形(方块闪电):亮像素直接作为实心 mask,图形内部的暗缝属于设计,保留;
///    白底黑线(旧苦力怕):线条二值化后洪泛填充,线条围成的内部一并填满;
/// ② 按「最远墨迹点的半径」缩放,让整个轮廓落在圆内;
/// ③ mask 做多遍 3×3 均值柔化边缘,再乘一条径向 smoothstep 淡出曲线——
///    圆心附近保持纯白,越往外越透明,看起来像一团柔和的白光。
///
/// 形状只算一次并缓存。
/// </summary>
public static class OrbGlyph
{
    /// <summary>mask 边长(超采样后),显示时缩放到 80px 宽的大圆。</summary>
    private const int MaskSize = 256;

    /// <summary>
    /// 四周留白(按最大半径算)。mask 256 时,留白 P 让图形占圆直径的 (256-2P)/256:
    /// P=56 → 约 56%(苦力怕脸时期);P=44 → 约 66%(方块闪电更细长,加大一档才够醒目)。
    /// </summary>
    private const int MaskPadding = 44;

    private static byte[]? _solid; // 实心脸形(0/1),已缩放到 MaskSize
    private static byte[]? _soft;  // 同上但边缘已柔化(0..255)

    /// <summary>
    /// 纯白苦力怕脸:不再是凹雕,而是白色实心轮廓,中间清晰、向外逐渐淡出模糊。
    /// 淡出用半径 50% 之后开始的 smoothstep,配合 mask 自身的边缘柔化,形成柔光效果。
    /// </summary>
    public static BitmapSource RenderWhiteFace()
    {
        var soft = GetSoftMask();
        var size = MaskSize;
        var half = size / 2.0;
        var maxR = Math.Max(1.0, half - MaskPadding);

        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                var a = soft[i];
                if (a == 0) continue;

                // 径向淡出:内圈实白,越往外越透明(视觉上"中间清晰外围模糊")。
                // 淡出从 55% 半径开始、到 130% 半径才归零:细长图形(闪电)的尖端
                // 落在 r≈1.0,若按旧曲线(1.0 处归零)会把整个尖端吃掉,只剩中央一小块
                var dx = x - half;
                var dy = y - half;
                var r = Math.Sqrt(dx * dx + dy * dy) / maxR;
                var t = (r - 0.55) / 0.75;
                double fade;
                if (t <= 0) fade = 1.0;
                else if (t >= 1) fade = 0.0;
                else fade = 1.0 - t * t * (3 - 2 * t); // smoothstep

                var alpha = (byte)Math.Max(0, Math.Min(255, a * fade));
                if (alpha == 0) continue;

                var idx = i * 4;
                pixels[idx] = 0xFF;
                pixels[idx + 1] = 0xFF;
                pixels[idx + 2] = 0xFF;
                pixels[idx + 3] = alpha;
            }
        }

        var bmp = BitmapSource.Create(size, size, 96, 96,
            PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>形状边缘柔化(多遍 3×3 均值),让白色轮廓的边界不至于一刀切。</summary>
    private static byte[] GetSoftMask()
    {
        if (_soft != null) return _soft;
        var hard = GetSolid();
        var size = MaskSize;
        // GetSolid 输出 0/1,这里放大到 0..255 才能作为 alpha 使用
        var cur = new byte[size * size];
        for (var i = 0; i < hard.Length; i++) cur[i] = (byte)(hard[i] * 255);

        for (var pass = 0; pass < 3; pass++)
        {
            var next = new byte[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var sum = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var ny = y + dy;
                        if (ny < 0 || ny >= size) continue;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = x + dx;
                            if (nx < 0 || nx >= size) continue;
                            sum += cur[ny * size + nx];
                        }
                    }
                    next[y * size + x] = (byte)(sum / 9);
                }
            }
            cur = next;
        }
        _soft = cur;
        return _soft;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FuseLauncher", "boot.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [glyph] {message}" + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>实心脸形(惰性计算并缓存)。失败时返回全 0,不影响大圆本身。</summary>
    private static byte[] GetSolid()
    {
        if (_solid != null) return _solid;
        try
        {
            _solid = BuildSolid();
        }
        catch (Exception ex)
        {
            // 形状生成失败会让脸整个消失,把原因落到 boot.log 便于定位
            try
            {
                File.AppendAllText(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "FuseLauncher", "boot.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] [glyph] build failed: {ex.GetType().Name} {ex.Message}" + Environment.NewLine);
            }
            catch (Exception)
            {
            }
            _solid = new byte[MaskSize * MaskSize];
        }
        return _solid;
    }

    private static byte[] BuildSolid()
    {
        var source = LoadSource();
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        var src = new byte[w * h * 4];
        source.CopyPixels(src, w * 4, 0);

        // 0. 按边界平均亮度自动判别素材极性:
        //    边界亮 = 白底黑线(墨=暗像素,洪泛填内部);边界暗 = 黑底白形(墨=亮像素,实心直取)
        double borderSum = 0;
        var borderCount = 0;
        void Acc(int x, int y)
        {
            var p = (y * w + x) * 4;
            borderSum += (src[p + 2] + src[p + 1] + src[p]) / 3;
            borderCount++;
        }
        for (var x = 0; x < w; x++) { Acc(x, 0); Acc(x, h - 1); }
        for (var y = 0; y < h; y++) { Acc(0, y); Acc(w - 1, y); }
        var outlineAsset = borderSum / Math.Max(1, borderCount) >= 128;

        // 1. 严格阈值二值化(黑底白形:亮于 100 即为墨,含淡紫色侧面;白底黑线:暗于 160)
        var ink = new bool[w * h];
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = (y * w + x) * 4;
                var lum = (src[p + 2] + src[p + 1] + src[p]) / 3;
                var isInk = outlineAsset ? lum < 160 : lum >= 100;
                if (!isInk) continue;
                ink[y * w + x] = true;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < minX || maxY < minY) return new byte[MaskSize * MaskSize];

        if (!outlineAsset)
        {
            // 黑底白形素材本身就是实心图形:直接作 mask(图形内的暗缝保留,不做洪泛)
            var solidDirect = new byte[w * h];
            for (var i = 0; i < solidDirect.Length; i++) solidDirect[i] = ink[i] ? (byte)1 : (byte)0;
            return ScaleRadial(solidDirect, w, minX, minY, maxX - minX + 1, maxY - minY + 1, MaskSize);
        }

        // 2. 从图像边界洪泛:填不到的空白区 = 线条围成的内部,和线条一起构成实心脸
        var outside = new bool[w * h];
        var queue = new Queue<int>();
        void Seed(int x, int y)
        {
            var i = y * w + x;
            if (ink[i] || outside[i]) return;
            outside[i] = true;
            queue.Enqueue(i);
        }
        for (var x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (var y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var x = i % w;
            var y = i / w;
            if (x > 0 && !ink[i - 1] && !outside[i - 1]) { outside[i - 1] = true; queue.Enqueue(i - 1); }
            if (x < w - 1 && !ink[i + 1] && !outside[i + 1]) { outside[i + 1] = true; queue.Enqueue(i + 1); }
            if (y > 0 && !ink[i - w] && !outside[i - w]) { outside[i - w] = true; queue.Enqueue(i - w); }
            if (y < h - 1 && !ink[i + w] && !outside[i + w]) { outside[i + w] = true; queue.Enqueue(i + w); }
        }

        var solid = new byte[w * h];
        for (var i = 0; i < solid.Length; i++)
            solid[i] = (byte)(ink[i] || !outside[i] ? 1 : 0);

        // 3. 按最大半径缩放,保证四角也在圆内
        return ScaleRadial(solid, w, minX, minY, maxX - minX + 1, maxY - minY + 1, MaskSize);
    }

    private static BitmapImage LoadSource()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/bolt.png", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>按最远点半径归一化缩放并居中。</summary>
    private static byte[] ScaleRadial(
        byte[] src, int w, int ox, int oy, int cw, int ch, int size)
    {
        var cx = ox + cw / 2.0;
        var cy = oy + ch / 2.0;

        double maxDist = 1.0;
        for (var y = oy; y < oy + ch; y++)
        {
            for (var x = ox; x < ox + cw; x++)
            {
                if (src[y * w + x] == 0) continue;
                var dx = x - cx;
                var dy = y - cy;
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d > maxDist) maxDist = d;
            }
        }

        var targetRadius = Math.Max(1.0, size / 2.0 - MaskPadding);
        var k = maxDist / targetRadius;
        var half = size / 2.0;

        var dst = new byte[size * size];
        for (var my = 0; my < size; my++)
        {
            var sy = (int)Math.Round(cy + (my - half) * k);
            if (sy < oy || sy >= oy + ch) continue;
            for (var mx = 0; mx < size; mx++)
            {
                var sx = (int)Math.Round(cx + (mx - half) * k);
                if (sx < ox || sx >= ox + cw) continue;
                dst[my * size + mx] = src[sy * w + sx];
            }
        }
        return dst;
    }
}
