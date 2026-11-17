using System;
using System.Collections.Generic;
using System.Linq;

namespace Fuse.Core.ModConflict;

/// <summary>
/// Forge / NeoForge 依赖区间使用的版本比较,语义对齐 Apache Maven 的
/// ComparableVersion(Apache-2.0;本实现为等价移植,非逐行拷贝):
/// 按 '.' 与 '-' 切 token,数字↔限定符转换处也切("9+mc1" 会切成 9 和 +mc1);
/// 数字 token 按数值比,限定符 token 按权重比
/// (alpha &lt; beta &lt; milestone &lt; rc/cr &lt; snapshot &lt; release/ga/final &lt; sp &lt; 未知限定符);
/// 越界一侧按 0/release 参与(1.0 == 1.0.0、1.0 &lt; 1.0.1、1.0 &gt; 1.0-alpha、1.0 &lt; 1.0-sp)。
/// </summary>
public sealed class MavenVersion : IComparable<MavenVersion?>
{
    private readonly List<object> items = new();   // long(数字) 或 string(限定符,已小写)
    private readonly string raw;

    private MavenVersion(string raw, List<object> items)
    {
        this.raw = raw;
        this.items = items;
    }

    public string Raw => raw;

    /// <summary>解析失败只发生在空串;Maven 切分规则下几乎所有字符串都可解析。</summary>
    public static bool TryCreate(string? raw, out MavenVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase) && text.Length > 1
            && char.IsDigit(text[1]))
            text = text.Substring(1);
        try
        {
            var v = new MavenVersion(text);
            if (v.items.Count == 0) return false;
            version = v;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private MavenVersion(string text)
    {
        raw = text;
        var isDigit = char.IsDigit(text[0]);
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var boundary = i == text.Length;
            if (!boundary)
            {
                var c = text[i];
                if (c == '.' || c == '-') boundary = true;
                else if (char.IsDigit(c) != isDigit) boundary = true;   // 数字↔限定符转换也切
            }
            if (!boundary) continue;

            var token = text.Substring(start, i - start);
            if (token.Length == 0)
                items.Add(isDigit ? (object)0L : (object)"");   // 连续分隔符:数字 0 / release
            else if (isDigit && long.TryParse(token, out var n))
                items.Add(n);
            else
                items.Add(token.ToLowerInvariant());
            isDigit = i + 1 < text.Length && char.IsDigit(text[i + 1]);
            start = i + 1;
        }
    }

    /// <summary>限定符权重:release = 0,alpha 及以前为负,sp/未知为正(未知再按字典序)。</summary>
    private static int QualifierWeight(string q)
    {
        if (string.IsNullOrEmpty(q)) return 0;
        if (q.StartsWith("alpha")) return -5;
        if (q.StartsWith("beta")) return -4;
        if (q.StartsWith("milestone")) return -3;
        if (q.StartsWith("rc") || q.StartsWith("cr")) return -2;
        if (q.StartsWith("snapshot")) return -1;
        if (q == "ga" || q == "final" || q == "release") return 0;
        if (q.StartsWith("sp")) return 1;
        if (q == "a") return -5;
        if (q == "b") return -4;
        if (q == "m") return -3;
        return 2;   // 未知限定符:release 之后,同权重按字典序
    }

    public int CompareTo(MavenVersion? other)
    {
        if (other == null) return 1;
        var max = Math.Max(items.Count, other.items.Count);
        for (var i = 0; i < max; i++)
        {
            object? a = i < items.Count ? items[i] : null;
            object? b = i < other.items.Count ? other.items[i] : null;
            if (a == null && b == null) continue;

            if (a is long && b is long)
            {
                var cmp = ((long)a!).CompareTo((long)b!);
                if (cmp != 0) return cmp;
                continue;
            }
            if (a is string && b is string)
            {
                var qa = (string)a!;
                var qb = (string)b!;
                var wa = QualifierWeight(qa);
                var wb = QualifierWeight(qb);
                if (wa != wb) return wa.CompareTo(wb);
                // 未知限定符同权重时按字典序;已知限定符(ga/final/release)等价,不算先后
                if (wa >= 2)
                {
                    var sc = string.CompareOrdinal(qa, qb);
                    if (sc != 0) return sc;
                }
                continue;
            }

            // 一侧数字一侧限定符:都在同一条权重轴上比(数字按数值,限定符按权重)
            var wl = Weight(a!);
            var wr = Weight(b!);
            if (wl != wr) return wl.CompareTo(wr);
        }
        return 0;
    }

    private static long Weight(object? item)
    {
        if (item == null) return 0;   // 越界一侧按 0/release 参与(1.0 == 1.0.0)
        if (item is long l) return l;
        return QualifierWeight((string)item);
    }

    /// <summary>首个数字 token(用于 ~ / ^ 语义的 Major/Minor 判断)。</summary>
    public long Major => NumericAt(0);
    public long Minor => NumericAt(1);

    private long NumericAt(int index) => items.Count > index && items[index] is long v ? v : 0;
}
