using System;
using System.Collections.Generic;
using System.Linq;

namespace Fuse.Core.ModConflict;

/// <summary>Fabric 依赖里的版本约束判断(覆盖常见写法;无法解析的按"满足"处理,避免误报)。</summary>
public static class VersionRange
{
    /// <summary>约束里是否是启动器能识别的版本格式(未知版本要单独提示,不能混在"不满足"里)。</summary>
    public static bool IsKnownVersion(string? version) => MavenVersion.TryCreate(version, out _);

    public static bool Matches(string? version, string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint) || constraint!.Trim() is "*" or "") return true;
        if (!MavenVersion.TryCreate(version, out var current)) return true;

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
    private static bool? MatchMavenRanges(MavenVersion current, string constraint)
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
        return MavenVersion.TryCreate(exact.Groups[1].Value.Trim(), out var target)
            ? current.CompareTo(target) == 0
            : (bool?)null;
    }

    private static bool InInterval(MavenVersion current, System.Text.RegularExpressions.Match m)
    {
        if (!MavenVersion.TryCreate(m.Groups[2].Value.Trim(), out var lower) && m.Groups[2].Value.Trim().Length > 0)
            return true;
        if (!MavenVersion.TryCreate(m.Groups[3].Value.Trim(), out var upper) && m.Groups[3].Value.Trim().Length > 0)
            return true;
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

    private static bool MatchesClause(MavenVersion current, string clause)
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
            return current.Raw.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) ||
                   current.Raw.Equals(prefix, StringComparison.OrdinalIgnoreCase);
        }

        if (!MavenVersion.TryCreate(clause, out var target)) return true;

        var cmp = current.CompareTo(target);
        return op switch
        {
            ">=" => cmp >= 0,
            ">" => cmp > 0,
            "<=" => cmp <= 0,
            "<" => cmp < 0,
            "=" => cmp == 0,
            // ~:同主次版本;^:同主版本(Fabric 语义)
            "~" => cmp >= 0 && current.Major == target.Major && current.Minor == target.Minor,
            "^" => cmp >= 0 && current.Major == target.Major,
            _ => cmp == 0,
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
    /// 先按 Maven 规则判断,失败再从字符串里提取所有版本片段逐个判断。
    /// </summary>
    public static bool MatchesAny(string? raw, string? constraint)
    {
        // 直判(mc 开头的版本串会整体解析)失败后,仍要试数字片段提取——
        // 版本号可能带 mc1.20.1 这类前缀,真正有效的版本藏在后半段
        if (MavenVersion.TryCreate(raw ?? "", out _) && Matches(raw ?? "", constraint)) return true;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(raw ?? "", @"\d+(?:\.\d+){1,3}"))
        {
            if (Matches(m.Value, constraint)) return true;
        }
        return false;
    }

    public static string Describe(string? constraint) => string.IsNullOrWhiteSpace(constraint) ? "任意版本" : constraint!;
}
