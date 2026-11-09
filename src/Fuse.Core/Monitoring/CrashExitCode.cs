using System;
using System.Collections.Generic;

namespace Fuse.Core.Monitoring;

/// <summary>常见崩溃退出码的人类可读分类(Windows NTSTATUS,负数是 uint 溢出)。</summary>
public static class CrashExitCode
{
    private static readonly Dictionary<int, string> Known = new()
    {
        [unchecked((int)0xC000013A)] = "被用户/系统终止(CTRL+C、关机或注销)",
        [unchecked((int)0xC0000005)] = "内存访问违例(0xC0000005,常见于 natives/显卡驱动)",
        [unchecked((int)0xC00000FD)] = "栈溢出(0xC00000FD)",
        [unchecked((int)0xC0000409)] = "栈缓冲区溢出保护触发(0xC0000409)",
        [unchecked((int)0xC000041D)] = "未处理异常导致进程终止(0xC000041D)",
        [-1] = "进程被强制杀死(-1)",
    };

    /// <summary>给统计/崩溃行的后缀;未知退出码返回空串(调用方按 !=0 显示「崩溃」)。</summary>
    public static string Describe(int exitCode)
        => Known.TryGetValue(exitCode, out var why) ? "(" + why + ")" : "";
}
