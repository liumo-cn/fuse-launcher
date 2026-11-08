using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Fuse.App;

/// <summary>
/// 五个花瓣的 24 网格描边图标。路径直接从设计稿 SVG 转成 WPF 几何,
/// 运行时用 Path 描边,不依赖位图,任何 DPI 下都清晰。
/// </summary>
internal static class PetalIcons
{
    private static readonly Dictionary<string, Geometry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Data = new(StringComparer.OrdinalIgnoreCase)
    {
        ["login"] = "M 9.7 5.3 A 2.3 2.3 0 1 1 14.3 5.3 A 2.3 2.3 0 1 1 9.7 5.3 Z M 12 9.6 C 9.6 9.6, 7.8 10, 7.6 12 C 7.4 14.6, 7.6 18, 9.4 19.8 C 10.8 20.8, 13.2 20.8, 14.6 19.8 C 16.4 18, 16.6 14.6, 16.4 12 C 16.2 10, 14.4 9.6, 12 9.6 Z M 9.9 13.4 C 9.6 15, 9.7 16.4, 10.1 17.4 M 14.1 13.4 C 14.4 15, 14.3 16.4, 13.9 17.4",
        ["download"] = "M 6 7 V 5 A 2 2 0 0 1 8 3 H 16 A 2 2 0 0 1 18 5 V 7 M 6 21 L 18 21 M 12 9 L 12 18.6 M 8.8 15.2 L 12 18.6 L 15.2 15.2",
        ["settings"] = "M 10.44 3.14 L 13.56 3.14 L 13.93 5.27 L 15.39 5.88 L 17.16 4.63 L 19.37 6.84 L 18.12 8.61 L 18.73 10.07 L 20.86 10.44 L 20.86 13.56 L 18.73 13.93 L 18.12 15.39 L 19.37 17.16 L 17.16 19.37 L 15.39 18.12 L 13.93 18.73 L 13.56 20.86 L 10.44 20.86 L 10.07 18.73 L 8.61 18.12 L 6.84 19.37 L 4.63 17.16 L 5.88 15.39 L 5.27 13.93 L 3.14 13.56 L 3.14 10.44 L 5.27 10.07 L 5.88 8.61 L 4.63 6.84 L 6.84 4.63 L 8.61 5.88 L 10.07 5.27 Z M 7.9 12 A 4.1 4.1 0 1 1 16.1 12 A 4.1 4.1 0 1 1 7.9 12 Z",
        ["versions"] = "M 3.2 10.5 L 12 4.9 L 20.8 10.5 L 12 16.1 Z M 3.2 10.5 V 14.1 L 12 19.7 L 21 14.1 C 19.6 13.2, 20.4 11.8, 20.8 10.5 M 12 16.1 C 10.2 16.8, 10.2 19, 12 19.7 M 12.9 17.8 L 20.3 12.9",
        ["more"] = "M 12 3 C 10.79 3 9.8 3.94 9.8 5.1 C 9.8 8.8 10.4 12 10.94 13.9 C 10.94 14.45 11.35 14.9 12 14.9 C 12.65 14.9 13.06 14.45 13.06 13.9 C 13.6 12 14.2 8.8 14.2 5.1 C 14.2 3.94 13.21 3 12 3 Z M 10.1 19.1 A 1.9 1.9 0 1 1 13.9 19.1 A 1.9 1.9 0 1 1 10.1 19.1 Z",
    };

    public static Geometry? Get(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        if (!Data.TryGetValue(name, out var data)) return null;
        try
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            Cache[name] = geometry;
            return geometry;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
