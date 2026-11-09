using System;
using System.IO;
using System.Text;

namespace Fuse.Core.Launch;

/// <summary>@author liumo | Original Algorithm — Windows 长命令行 @argfile 方案。
/// CreateProcess 上限 32767 字符,Forge 大包的 classpath 逼近悬崖;Java 9+ 支持
/// <c>java @file</c>,把 -cp 段挪进参数文件。Java 8 不支持,原串返回并让调用方告警。
///
/// 字符集是实测结论(2026-11-10,JDK 25 / 中文 Windows):java 启动器按平台原生
/// 字符集读 argfile,\uXXXX 转义与 UTF-8 文件都会ClassNotFoundException;
/// JVM 的 sun.jnu.encoding 与 .NET 的 Encoding.Default 同源于系统 ANSI 代码页,
/// 所以按 Encoding.Default 原样写,两边永远自洽(含"Beta UTF-8"全局面板)。</summary>
public static class LiumoArgfile
{
    /// <summary>超过这个长度才值得换 argfile(CreateProcess 悬崖留余量)。</summary>
    public const int LiumoArgfileThresholdChars = 24000;

    /// <summary>
    /// 命令行超长且 Java 9+ 时,把 `-cp "…"` 段挪进参数文件并替换为 @路径;
    /// 其余情况原样返回(ArgfilePath=null)。文件不存在或写失败时原样返回,不阻塞启动。
    /// </summary>
    public static (string Args, string? ArgfilePath) TryBuild(
        string fullArgs, int javaMajor, string binDir, string versionId)
    {
        if (string.IsNullOrEmpty(fullArgs) || fullArgs.Length <= LiumoArgfileThresholdChars)
            return (fullArgs, null);
        if (javaMajor < 9)
            return (fullArgs, null); // Java 8 没有 @argfile,只能靠单串(32k 内实测可用)

        var marker = "-cp \"";
        var start = fullArgs.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return (fullArgs, null);
        var contentStart = start + marker.Length;
        var end = fullArgs.IndexOf('"', contentStart);
        if (end < 0) return (fullArgs, null);
        var classpath = fullArgs.Substring(contentStart, end - contentStart);

        try
        {
            Directory.CreateDirectory(binDir);
            var argfilePath = Path.Combine(binDir, "liumo-classpath-" + Sanitize(versionId) + ".args");
            // argfile 规则:每行一个参数,含空格的参数整体加引号,反斜杠与引号成对转义
            var quoted = "\"" + classpath.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            File.WriteAllText(argfilePath, "-cp " + quoted + Environment.NewLine, Encoding.Default);
            var newArgs = fullArgs.Substring(0, start)
                          + "-cp @" + argfilePath
                          + fullArgs.Substring(end + 1);
            return (newArgs, argfilePath);
        }
        catch (IOException)
        {
            return (fullArgs, null); // argfile 写不出来就退回单串,启动优先于长度优化
        }
        catch (UnauthorizedAccessException)
        {
            return (fullArgs, null);
        }
    }

    private static string Sanitize(string id)
    {
        var chars = id.ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                chars[i] = '_';
        return new string(chars);
    }
}
