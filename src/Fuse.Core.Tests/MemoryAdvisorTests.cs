using System;
using System.IO;
using Fuse.Core.Config;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>自动内存建议:基础值分段、模组加码、物理内存 55% 封顶(最多 12 GB)。</summary>
public class MemoryAdvisorTests
{
    [Theory]
    [InlineData(4096, 0, 1024)]     // 4 GB:只够原版
    [InlineData(8192, 0, 2048)]     // 8 GB
    [InlineData(16384, 0, 4096)]    // 16 GB
    [InlineData(32768, 0, 6144)]    // 32 GB
    [InlineData(65536, 0, 8192)]    // 64 GB
    public void Base_tiers_follow_physical_memory(long ramMb, int mods, long expected)
    {
        Assert.Equal(expected, MemoryAdvisor.Suggest(mods, ramMb));
    }

    [Theory]
    [InlineData(32768, 49, 6144)]    // 未到 50 个:不加码
    [InlineData(32768, 50, 7168)]    // +1 GB
    [InlineData(32768, 150, 8192)]   // +2 GB
    [InlineData(32768, 300, 10240)]  // +4 GB
    public void Mod_counts_add_headroom(long ramMb, int mods, long expected)
    {
        Assert.Equal(expected, MemoryAdvisor.Suggest(mods, ramMb));
    }

    [Theory]
    [InlineData(8192, 400, 4352)]    // 2048+4096=6144,但 8 GB 的 55% ≈ 4505,取整到 4352
    [InlineData(16384, 400, 8192)]   // 4096+4096=8192,未超 16 GB 的 55%
    [InlineData(32768, 400, 10240)]
    [InlineData(65536, 400, 12288)]  // 封顶 12 GB
    public void Physical_memory_cap_wins(long ramMb, int mods, long expected)
    {
        Assert.Equal(expected, MemoryAdvisor.Suggest(mods, ramMb));
    }

    [Fact]
    public void Tiny_machines_still_get_a_usable_floor()
    {
        Assert.Equal(1024, MemoryAdvisor.Suggest(0, 2048));
        Assert.Equal(1024, MemoryAdvisor.Suggest(400, 2048));
    }

    [Fact]
    public void Count_mods_reads_enabled_jars_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-mem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.jar"), "x");
            File.WriteAllText(Path.Combine(dir, "b.jar"), "x");
            File.WriteAllText(Path.Combine(dir, "c.jar.disabled"), "x");
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "x");
            Assert.Equal(2, MemoryAdvisor.CountMods(dir));
            Assert.Equal(0, MemoryAdvisor.CountMods(Path.Combine(dir, "missing")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    [Fact]
    public void Reports_plausible_physical_memory()
    {
        // 兼容 CI:只校验落在合理区间(取不到时实现会退回 8 GB)
        var mb = MemoryAdvisor.TotalPhysicalMemoryMb();
        Assert.InRange(mb, 1024, 1024L * 1024);
    }
}
