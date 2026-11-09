using System;
using System.Collections.Generic;
using System.Linq;

namespace Fuse.Core.ModConflict;

/// <summary>Fabric 依赖里的版本约束判断(覆盖常见写法;解析失败时按"满足"处理,避免误报)。</summary>
public static class VersionRange
{
    public static bool Matches(string? version, string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint) || constraint!.Trim() is "*" or "") return true;
        if (string.IsNullOrWhiteSpace(version)) return false;

        var current = Parse(version!);
        if (current == null) return true;

        // Forge / NeoForge 的 mods.toml 用 Maven 区间:[1.12.2,1.13)、[1.20,)、[1.20.1]
        var maven = MatchMavenRanges(current, constraint!);
        if (maven.HasValue) return maven.Value;

        var clauses = constraint!.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);

        // Forge 的 mods.toml 里常见 "1.18 1.18.1 1.20.1" 这种纯版本列表,表示任一都行
        if (clauses.Length > 1 &&
            clauses.All(c => c.IndexOfAny(new[] { '<', '>', '=', '~', '^', '[', '(' }) < 0))
            return clauses.Any(c => MatchesClause(current, c));

        foreach (var clause in clauses)
        {
            if (!MatchesClause(current, clause)) return false;
        }
        return true;
    }

    /// <summary>Maven 区间:返回 null 表示不是区间写法,继续走 Fabric 语义。</summary>
    private static bool? MatchMavenRanges(Version current, string constraint)
    {
        var text = constraint.Trim();
        if (text.IndexOf('[') < 0 && text.IndexOf('(') < 0) return null;

        // [1.12.2,1.13) / [1.20,) / (,1.12.2] / 逗号分隔的多个区间
        var intervals = System.Text.RegularExpressions.Regex.Matches(
            text, @"([\[\(])([^,\]\)]*),([^\]\)]*)([\]\)])");
        if (intervals.Count > 0)
        {
            foreach (System.Text.RegularExpressions.Match m in intervals)
                if (InInterval(current, m)) return true;
            return false;
        }

        // [1.12.2] 表示精确版本
        var exact = System.Text.RegularExpressions.Regex.Match(text, @"^\[([^\[\],]+)\]$");
        if (!exact.Success) return null;
        var target = Parse(exact.Groups[1].Value.Trim());
        return target == null ? null : current.CompareTo(target) == 0;
    }

    private static bool InInterval(Version current, System.Text.RegularExpressions.Match m)
    {
        var lower = Parse(m.Groups[2].Value.Trim());
        var upper = Parse(m.Groups[3].Value.Trim());
        if (lower == null && upper == null) return true;

        if (lower != null)
        {
            var cmp = current.CompareTo(lower);
            if (m.Groups[1].Value == "[" ? cmp < 0 : cmp <= 0) return false;
        }
        if (upper != null)
        {
            var cmp = current.CompareTo(upper);
            if (m.Groups[4].Value == "]" ? cmp > 0 : cmp >= 0) return false;
        }
        return true;
    }

    private static bool MatchesClause(Version current, string clause)
    {
        var op = "";
        foreach (var candidate in new[] { ">=", "<=", ">", "<", "=", "~", "^" })
        {
            if (!clause.StartsWith(candidate, StringComparison.Ordinal)) continue;
            op = candidate;
            clause = clause.Substring(candidate.Length);
            break;
        }
        clause = clause.Trim();
        if (clause.Length == 0) return true;

        // 通配:0.9.x / 1.0.*
        if (clause.IndexOf('x') >= 0 || clause.IndexOf('X') >= 0 || clause.IndexOf('*') >= 0)
        {
            var prefix = clause.Split('x', 'X', '*')[0].TrimEnd('.');
            if (prefix.Length == 0) return true;
            return current.Normalized.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) ||
                   current.Normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase);
        }

        var target = Parse(clause);
        if (target == null) return true;

        var cmp = current.CompareTo(target);
        return op switch
        {
            ">=" => cmp >= 0,
            ">" => cmp > 0,
            "<=" => cmp <= 0,
            "<" => cmp < 0,
            "=" => cmp == 0,
            "~" => cmp >= 0 && SameMajorMinor(current, target),
            "^" => cmp >= 0 && current.Major == target.Major,
            _ => cmp == 0,
        };
    }

    private static bool SameMajorMinor(Version a, Version b) =>
        a.Major == b.Major && a.Minor == b.Minor;

    public sealed class Version : IComparable<Version>
    {
        public int Major;
        public int Minor;
        public int Patch;
        public string PreRelease = "";
        public string Normalized = "";

        public int CompareTo(Version? other)
        {
            if (other == null) return 1;
            var cmp = Major.CompareTo(other.Major);
            if (cmp != 0) return cmp;
            cmp = Minor.CompareTo(other.Minor);
            if (cmp != 0) return cmp;
            cmp = Patch.CompareTo(other.Patch);
            if (cmp != 0) return cmp;
            // 预发布 < 正式版
            if (PreRelease.Length == 0 && other.PreRelease.Length == 0) return 0;
            if (PreRelease.Length == 0) return 1;
            if (other.PreRelease.Length == 0) return -1;
            return string.Compare(PreRelease, other.PreRelease, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static Version? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
        // 去掉 +build
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text.Substring(0, plus);
        var dash = text.IndexOf('-');
        var pre = dash >= 0 ? text.Substring(dash + 1) : "";
        if (dash >= 0) text = text.Substring(0, dash);

        var parts = text.Split('.');
        if (parts.Length == 0) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return new Version
        {
            Major = major,
            Minor = minor,
            Patch = patch,
            PreRelease = pre,
            Normalized = text,
        };
    }

    /// <summary>取两个约束里更严格的下限(用于给缺失依赖挑最低可用版本)。</summary>
    public static string? MinVersion(string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint)) return null;
        foreach (var clause in constraint!.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (clause.StartsWith(">=", StringComparison.Ordinal))
                return clause.Substring(2).Trim();
            if (clause.StartsWith(">", StringComparison.Ordinal))
                return clause.Substring(1).Trim();
        }
        return null;
    }

    /// <summary>
    /// Modrinth 的 version_number 不一定是纯 SemVer(Sodium 写成 mc26.1.1-0.8.9-fabric)。
    /// 先按纯版本判断,失败再从字符串里提取所有版本片段逐个判断。
    /// </summary>
    public static bool MatchesAny(string? raw, string? constraint)
    {
        if (Parse(raw ?? "") != null) return Matches(raw, constraint);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(raw ?? "", @"\d+(?:\.\d+){1,3}"))
        {
            if (Matches(m.Value, constraint)) return true;
        }
        return false;
    }

    public static string Describe(string? constraint) => string.IsNullOrWhiteSpace(constraint) ? "任意版本" : constraint!;
}
