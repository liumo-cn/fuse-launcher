using System;
using System.Collections.Generic;

namespace Fuse.Core.Versioning;

/// <summary>当前运行环境描述，供规则求值。</summary>
public sealed class RuleEnvironment
{
    public static RuleEnvironment WindowsX64 { get; } = new() { OsName = "windows", Arch = "x64" };

    public static RuleEnvironment WindowsX86 { get; } = new() { OsName = "windows", Arch = "x86" };

    public string OsName { get; init; } = "windows";

    /// <summary>"x64" 或 "x86"（指 JVM 进程位数）。</summary>
    public string Arch { get; init; } = "x64";

    /// <summary>启用特性，如 has_custom_resolution、is_demo_user。</summary>
    public HashSet<string> Features { get; init; } = new();

    public static RuleEnvironment FromFeatureSet(IDictionary<string, bool>? features)
    {
        var env = new RuleEnvironment();
        if (features != null)
            foreach (var kv in features)
                if (kv.Value) env.Features.Add(kv.Key);
        return env;
    }
}

/// <summary>
/// 官方规则求值语义：遍历规则，条件命中的规则其 action 覆盖结果（后者覆盖前者）；
/// 无任何规则命中时不启用该参数/库。空条件的规则恒命中。
/// </summary>
public static class RuleEvaluator
{
    public static bool Evaluate(IReadOnlyList<McRule>? rules, RuleEnvironment env)
    {
        if (rules == null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (Matches(rule, env))
                allowed = rule.Action == "allow";
        }
        return allowed;
    }

    public static bool Matches(McRule rule, RuleEnvironment env)
    {
        if (rule.Os != null)
        {
            if (rule.Os.Name != null && !string.Equals(rule.Os.Name, env.OsName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (rule.Os.Arch != null && !string.Equals(rule.Os.Arch, env.Arch, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (rule.Features != null)
        {
            foreach (var kv in rule.Features)
            {
                // 特性匹配要求声明的值与启用状态一致（声明 false 的特性在未启用时才命中）
                var enabled = env.Features.Contains(kv.Key);
                if (enabled != kv.Value) return false;
            }
        }
        return true;
    }
}
