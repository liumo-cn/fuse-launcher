using System;
using System.Collections.Generic;
using Fuse.Core.Versioning;
using Xunit;

namespace Fuse.Core.Tests;

public class RuleEvaluatorTests
{
    private static McRule Rule(string action, string? os = null, string? arch = null, Dictionary<string, bool>? features = null)
        => new() { Action = action, Os = os == null && arch == null ? null : new McRuleOs { Name = os, Arch = arch }, Features = features };

    private readonly RuleEnvironment _win64 = new() { OsName = "windows", Arch = "x64" };
    private readonly RuleEnvironment _win86 = new() { OsName = "windows", Arch = "x86" };

    [Fact]
    public void NullRules_AlwaysAllowed()
    {
        Assert.True(RuleEvaluator.Evaluate(null, _win64));
        Assert.True(RuleEvaluator.Evaluate(new List<McRule>(), _win64));
    }

    [Fact]
    public void AllowWindows_MatchesOnlyWindows()
    {
        var rules = new List<McRule> { Rule("allow", os: "windows") };
        Assert.True(RuleEvaluator.Evaluate(rules, _win64));
        Assert.False(RuleEvaluator.Evaluate(rules, new RuleEnvironment { OsName = "linux", Arch = "x64" }));
    }

    [Fact]
    public void DenyOsx_Style_NonMatchingOsNotAllowed()
    {
        // 1.20.1 的 -XstartOnFirstThread 规则：allow osx。在 windows 上不启用
        var rules = new List<McRule> { Rule("allow", os: "osx") };
        Assert.False(RuleEvaluator.Evaluate(rules, _win64));
        Assert.True(RuleEvaluator.Evaluate(rules, new RuleEnvironment { OsName = "osx", Arch = "x64" }));
    }

    [Fact]
    public void ArchRule_OnlyMatchesSpecifiedBitness()
    {
        var rules = new List<McRule> { Rule("allow", arch: "x86") };
        Assert.True(RuleEvaluator.Evaluate(rules, _win86));
        Assert.False(RuleEvaluator.Evaluate(rules, _win64));
    }

    [Fact]
    public void FeatureRule_RequiresEnabledFeature()
    {
        var rules = new List<McRule> { Rule("allow", features: new Dictionary<string, bool> { ["has_custom_resolution"] = true }) };
        Assert.False(RuleEvaluator.Evaluate(rules, _win64));
        var env = new RuleEnvironment { OsName = "windows", Arch = "x64", Features = { "has_custom_resolution" } };
        Assert.True(RuleEvaluator.Evaluate(rules, env));
    }

    [Fact]
    public void FeatureDeclaredFalse_MatchesWhenFeatureAbsent()
    {
        var rules = new List<McRule> { Rule("allow", features: new Dictionary<string, bool> { ["is_demo_user"] = false }) };
        Assert.True(RuleEvaluator.Evaluate(rules, _win64));
        var demo = new RuleEnvironment { OsName = "windows", Arch = "x64", Features = { "is_demo_user" } };
        Assert.False(RuleEvaluator.Evaluate(rules, demo));
    }

    [Fact]
    public void MultipleRules_LastMatchingWins()
    {
        var rules = new List<McRule>
        {
            Rule("allow"),
            Rule("deny", os: "windows"),
        };
        Assert.False(RuleEvaluator.Evaluate(rules, _win64));
        Assert.True(RuleEvaluator.Evaluate(rules, new RuleEnvironment { OsName = "linux", Arch = "x64" }));
    }

    [Fact]
    public void NoRuleMatches_Disabled()
    {
        var rules = new List<McRule> { Rule("allow", os: "linux") };
        Assert.False(RuleEvaluator.Evaluate(rules, _win64));
    }
}
