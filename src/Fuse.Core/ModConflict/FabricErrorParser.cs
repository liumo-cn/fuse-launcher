using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Fuse.Core.ModConflict;

/// <summary>从 Fabric 的 Incompatible mods 报错里提取修复线索(中英双语格式)。</summary>
public static class FabricErrorParser
{
    public enum HintKind { MissingDependency, ReplaceVersion, MinecraftMismatch }

    public sealed record Hint(
        HintKind Kind, string ModId, string ModName, string Constraint, string RequiredMinecraft);

    public static List<Hint> Parse(string? text)
    {
        var hints = new List<Hint>();
        if (string.IsNullOrWhiteSpace(text)) return hints;

        // 中文:需要 searchables 的 1.0.4 及以上版本,但没有安装它!
        foreach (Match m in Regex.Matches(text!,
                     @"需要\s*([A-Za-z0-9_\-]+)\s*的\s*([0-9][^\s，。;；]*)\s*及以上版本"))
            hints.Add(new Hint(HintKind.MissingDependency, m.Groups[1].Value, m.Groups[1].Value,
                ">=" + m.Groups[2].Value, ""));

        // 中文(建议行):安装 searchables，1.0.4 及以上版本
        foreach (Match m in Regex.Matches(text!,
                     @"安装\s*([A-Za-z0-9_\-]+)\s*[，,]\s*([0-9][^\s，。;；]*)\s*及以上版本"))
            hints.Add(new Hint(HintKind.MissingDependency, m.Groups[1].Value, m.Groups[1].Value,
                ">=" + m.Groups[2].Value, ""));

        // 英文:requires searchables 1.0.4 or above
        foreach (Match m in Regex.Matches(text!,
                     @"requires\s+([A-Za-z0-9_\-]+)\s+([0-9][0-9A-Za-z.\-+]*)\s+or\s+above",
                     RegexOptions.IgnoreCase))
            hints.Add(new Hint(HintKind.MissingDependency, m.Groups[1].Value, m.Groups[1].Value,
                ">=" + m.Groups[2].Value, ""));

        // 中文:将 模组 'Sodium' (sodium) 0.8.9... 替换为 任意 0.9.x 版本
        foreach (Match m in Regex.Matches(text!,
                     @"将\s*模组\s*'([^']+)'\s*\(([^)]+)\)[^\r\n]*?替换为\s*任意\s*([0-9][^\s，。;；]*)\s*版本"))
            hints.Add(new Hint(HintKind.ReplaceVersion, m.Groups[2].Value, m.Groups[1].Value,
                m.Groups[3].Value, ""));

        // 英文:Replace mod 'Sodium' (sodium) ... with any version 0.9.x
        foreach (Match m in Regex.Matches(text!,
                     @"Replace\s+mod\s+'([^']+)'\s*\(([^)]+)\)[^\r\n]*?with\s+any\s+version\s+([0-9][0-9A-Za-z.\-+xX*]*)",
                     RegexOptions.IgnoreCase))
            hints.Add(new Hint(HintKind.ReplaceVersion, m.Groups[2].Value, m.Groups[1].Value,
                m.Groups[3].Value, ""));

        // 中文:模组 'MezzConfig' (mezz_config) ... 需要 'Minecraft' (minecraft) 的 26.1.2 版本
        foreach (Match m in Regex.Matches(text!,
                     @"模组\s*'([^']+)'\s*\(([^)]+)\)[^\r\n]*?需要\s*'Minecraft'\s*\(minecraft\)\s*的\s*([0-9][0-9.]*)\s*版本"))
            hints.Add(new Hint(HintKind.MinecraftMismatch, m.Groups[2].Value, m.Groups[1].Value,
                "", m.Groups[3].Value));

        // 英文:mod 'X' (id) ... requires 'minecraft' version 26.1.2
        foreach (Match m in Regex.Matches(text!,
                     @"mod\s+'([^']+)'\s*\(([^)]+)\)[^\r\n]*?requires\s+'minecraft'\s+version\s+([0-9][0-9.]*)",
                     RegexOptions.IgnoreCase))
            hints.Add(new Hint(HintKind.MinecraftMismatch, m.Groups[2].Value, m.Groups[1].Value,
                "", m.Groups[3].Value));

        // 英文旧格式:Mod 'X' (id) requires minecraft 26.1.2, but ...
        foreach (Match m in Regex.Matches(text!,
                     @"Mod\s+'([^']+)'\s*\(([^)]+)\)\s+requires\s+minecraft\s+([0-9][0-9.]*)",
                     RegexOptions.IgnoreCase))
            hints.Add(new Hint(HintKind.MinecraftMismatch, m.Groups[2].Value, m.Groups[1].Value,
                "", m.Groups[3].Value));

        return hints;
    }
}
