using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Fuse.Core.Config;

/// <summary>
/// 内存建议:按物理内存总量 + 当前版本模组数量给出 -Xmx 推荐值。
/// 规则参考主流启动器(基础值按内存分段,模组多则加码,再用物理内存的 55% 封顶),
/// 目的是既不饿死游戏,也不把系统挤进交换区、不让过大的堆拖慢 GC。
/// </summary>
public static class MemoryAdvisor
{
    /// <summary>物理内存总量(MB);取不到时按 8 GB 估。</summary>
    public static long TotalPhysicalMemoryMb()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (GlobalMemoryStatusEx(status))
            {
                var mb = (long)(status.TotalPhys / (1024UL * 1024UL));
                if (mb >= 1024) return mb;
            }
        }
        catch (Exception)
        {
        }
        return 8192;
    }

    /// <summary>统计实例里启用的模组数量(只算 .jar,已禁用的 .jar.disabled 不占运行内存)。</summary>
    public static int CountMods(string modsDir)
    {
        try
        {
            if (!Directory.Exists(modsDir)) return 0;
            return Directory.GetFiles(modsDir, "*.jar").Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>推荐堆大小(MB):基础值按物理内存分段,模组数量加码,上限 = min(55% 物理内存, 12288)。</summary>
    public static long Suggest(int modCount, long totalRamMb)
    {
        long baseMb;
        if (totalRamMb <= 4608) baseMb = 1024;        // ≤4.5 GB:只够原版
        else if (totalRamMb <= 8704) baseMb = 2048;   // 8 GB
        else if (totalRamMb <= 17408) baseMb = 4096;  // 16 GB
        else if (totalRamMb <= 33792) baseMb = 6144;  // 32 GB
        else baseMb = 8192;                           // 64 GB+

        var bonus = modCount >= 300 ? 4096
                  : modCount >= 150 ? 2048
                  : modCount >= 50 ? 1024
                  : 0;

        var cap = Math.Min(totalRamMb * 55 / 100, 12288);
        if (cap < 1024) cap = 1024;
        var value = Math.Min(baseMb + bonus, cap);
        // 取整到 256MB,滑块/显示都不出现零头
        return value / 256 * 256;
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}
