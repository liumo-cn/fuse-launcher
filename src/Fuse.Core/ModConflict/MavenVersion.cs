using System;
using System.Collections.Generic;
using System.Numerics;

namespace Fuse.Core.ModConflict;

/// <summary>
/// Forge / NeoForge 依赖区间使用的版本比较,等价移植 Apache Maven 的 ComparableVersion
/// (Apache-2.0;本实现为等价重写,非逐行拷贝)。
/// 关键结构:'.' 是同级分隔,'-' 开一个子列表,"1.20.1-1.2.8" 解析成 [1,20,1,[1,2,8]],
/// 所以 1.20.1-1.2.8 &gt; 1.20-1.2(MC 段 1.20.1 &gt; 1.20)——早前把 '-' 拉平成同级 token
/// 会让两段错位比较,误判 Forge 整合包"依赖版本不满足"(2026-11-18 香草纪元 FarmersDelight)。
/// 限定符权重:alpha &lt; beta &lt; milestone &lt; rc &lt; snapshot &lt; release/ga/final &lt; sp &lt; 未知;
/// 越界一侧按 release 参与(1.0 == 1.0.0、1.0 &lt; 1.0.1、1.0 &gt; 1.0-alpha、1.0 &lt; 1.0-sp)。
/// </summary>
public sealed class MavenVersion : IComparable<MavenVersion?>
{
    private readonly ListItem root;
    private readonly string raw;

    private MavenVersion(string raw, ListItem root)
    {
        this.raw = raw;
        this.root = root;
    }

    public string Raw => raw;

    /// <summary>解析失败只发生在空串;Maven 切分规则下几乎所有字符串都可解析。</summary>
    public static bool TryCreate(string? raw, out MavenVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw!.Trim();
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1]))
            text = text.Substring(1);
        try
        {
            version = new MavenVersion(text, Parse(text.ToLowerInvariant()));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public int CompareTo(MavenVersion? other) => other == null ? 1 : root.CompareTo(other.root);

    /// <summary>首个数字段(用于 ~ / ^ 语义的 Major/Minor 判断)。</summary>
    public long Major => NumericAt(0);
    public long Minor => NumericAt(1);

    private long NumericAt(int index)
    {
        if (index < 0 || index >= root.Items.Count) return 0;
        return root.Items[index] is IntItem item ? item.AsLong : 0;
    }

    // ---------- Maven ComparableVersion 的三类 item ----------

    private abstract class Item
    {
        public abstract int CompareTo(Item? other);
    }

    /// <summary>数字段。与缺失一侧比:0 视为等价,其余更大(1.0 == 1,1.1 &gt; 1)。</summary>
    private sealed class IntItem : Item
    {
        private readonly BigInteger value;
        public IntItem(BigInteger value) => this.value = value;
        public long AsLong => value > long.MaxValue ? long.MaxValue : (long)value;

        public override int CompareTo(Item? other)
        {
            if (other == null) return value.IsZero ? 0 : 1;
            switch (other)
            {
                case IntItem i: return value.CompareTo(i.value);
                case StringItem _: return 1;   // 1.1 > 1-sp
                case ListItem _: return 1;     // 1.1 > 1-1
                default: return 1;
            }
        }
    }

    /// <summary>限定符段,权重按 Maven 的 QUALIFIERS 顺序;未知限定符排在 sp 之后并按其名字排序。</summary>
    private sealed class StringItem : Item
    {
        private static readonly string[] Qualifiers =
            { "alpha", "beta", "milestone", "rc", "snapshot", "", "sp" };
        private const string ReleaseIndex = "5";   // QUALIFIERS 里 "" 的下标

        private readonly string value;

        private StringItem(string value) => this.value = value;

        public static StringItem Create(string value, bool followedByDigit)
        {
            // "1.0a1" 这种单字母前缀是 alpha/beta/milestone 的简写
            if (followedByDigit && value.Length == 1)
            {
                switch (value[0])
                {
                    case 'a': value = "alpha"; break;
                    case 'b': value = "beta"; break;
                    case 'm': value = "milestone"; break;
                }
            }
            switch (value)
            {
                case "ga":
                case "final":
                case "release":
                    value = "";
                    break;
                case "cr":
                    value = "rc";
                    break;
            }
            return new StringItem(value);
        }

        private static string ComparableQualifier(string qualifier)
        {
            var index = Array.IndexOf(Qualifiers, qualifier);
            return index < 0 ? Qualifiers.Length + "-" + qualifier : index.ToString();
        }

        public override int CompareTo(Item? other)
        {
            if (other == null)
                return string.CompareOrdinal(ComparableQualifier(value), ReleaseIndex);
            switch (other)
            {
                case IntItem _: return -1;   // 1.any < 1.1
                case StringItem s: return string.CompareOrdinal(ComparableQualifier(value), ComparableQualifier(s.value));
                case ListItem _: return -1;  // 1.any < 1-1
                default: return -1;
            }
        }
    }

    /// <summary>'-' 之后的一段(根也是列表)。比数字/限定符都小,比另一列表按元素逐个比。</summary>
    private sealed class ListItem : Item
    {
        public List<Item> Items { get; } = new();

        public override int CompareTo(Item? other)
        {
            if (other == null)
                return Items.Count == 0 ? 0 : Items[0].CompareTo(null);
            switch (other)
            {
                case IntItem _: return -1;   // 1-1 < 1.1
                case StringItem _: return 1; // 1-1 > 1-sp
                case ListItem list:
                    var max = Math.Max(Items.Count, list.Items.Count);
                    for (var i = 0; i < max; i++)
                    {
                        var left = i < Items.Count ? Items[i] : null;
                        var right = i < list.Items.Count ? list.Items[i] : null;
                        var result = left == null
                            ? (right == null ? 0 : -right.CompareTo(null))
                            : left.CompareTo(right);
                        if (result != 0) return result;
                    }
                    return 0;
                default: return 0;
            }
        }
    }

    // ---------- 解析(照 Maven parseVersion 的结构) ----------

    private static ListItem Parse(string version)
    {
        var root = new ListItem();
        var list = root;
        var stack = new Stack<ListItem>();
        stack.Push(root);
        var isDigit = false;
        var startIndex = 0;

        for (var i = 0; i < version.Length; i++)
        {
            var c = version[i];
            if (c == '.')
            {
                list.Items.Add(i == startIndex
                    ? new IntItem(BigInteger.Zero)
                    : NewItem(isDigit, version.Substring(startIndex, i - startIndex)));
                startIndex = i + 1;
            }
            else if (c == '-')
            {
                list.Items.Add(i == startIndex
                    ? new IntItem(BigInteger.Zero)
                    : NewItem(isDigit, version.Substring(startIndex, i - startIndex)));
                startIndex = i + 1;
                list = Push(list, stack);   // '-' 开子列表:1.2.3-4 里的 4 不跟 1.2.3 同级比
            }
            else if (char.IsDigit(c))
            {
                if (!isDigit && i > startIndex)
                {
                    // 限定符后接数字(1.0-alpha1):限定符成一段,数字起新子列表
                    list.Items.Add(StringItem.Create(version.Substring(startIndex, i - startIndex), followedByDigit: true));
                    startIndex = i;
                    list = Push(list, stack);
                }
                isDigit = true;
            }
            else
            {
                if (isDigit && i > startIndex)
                {
                    // 数字后接限定符(1.0.1rc1):数字先成一段,限定符起新子列表
                    list.Items.Add(NewItem(true, version.Substring(startIndex, i - startIndex)));
                    startIndex = i;
                    list = Push(list, stack);
                }
                isDigit = false;
            }
        }

        if (version.Length > startIndex)
            list.Items.Add(NewItem(isDigit, version.Substring(startIndex)));

        // Maven 的 normalize:去掉末尾连续的空子列表(与 "1.0" 等价的写法)
        while (stack.Count > 0) Normalize(stack.Pop());
        return root;
    }

    private static ListItem Push(ListItem parent, Stack<ListItem> stack)
    {
        var sub = new ListItem();
        parent.Items.Add(sub);
        stack.Push(sub);
        return sub;
    }

    private static void Normalize(ListItem list)
    {
        for (var i = list.Items.Count - 1; i >= 0; i--)
        {
            if (list.Items[i] is ListItem empty && empty.Items.Count == 0) list.Items.RemoveAt(i);
            else if (list.Items[i] is not ListItem) break;
        }
    }

    private static Item NewItem(bool isDigit, string text)
    {
        if (!isDigit) return StringItem.Create(text, followedByDigit: false);
        // 前导零不影响数值:Maven 同样会 strip leading zeroes
        var trimmed = text.TrimStart('0');
        return new IntItem(trimmed.Length == 0 ? BigInteger.Zero : BigInteger.Parse(trimmed));
    }
}
