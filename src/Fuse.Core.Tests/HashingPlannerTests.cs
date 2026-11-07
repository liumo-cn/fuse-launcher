using System;
using System.IO;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Xunit;

namespace Fuse.Core.Tests;

public class HashingTests
{
    [Fact]
    public async Task KnownVectors_Abc()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcdl-hash-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "abc");
        try
        {
            Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d",
                await Hashing.ComputeFileAsync(path, HashAlgo.Sha1));
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                await Hashing.ComputeFileAsync(path, HashAlgo.Sha256));
            Assert.Equal("ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
                await Hashing.ComputeFileAsync(path, HashAlgo.Sha512));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Verify_NullExpected_Passes()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcdl-hash-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "x");
        try
        {
            Assert.True(await Hashing.VerifyFileAsync(path, null, HashAlgo.Sha1));
            Assert.False(await Hashing.VerifyFileAsync(path, "deadbeef", HashAlgo.Sha1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("sha1", HashAlgo.Sha1)]
    [InlineData("SHA-256", HashAlgo.Sha256)]
    [InlineData("sha512", HashAlgo.Sha512)]
    [InlineData(null, HashAlgo.Sha1)]
    public void Parse_AcceptsCommonSpellings(string? input, HashAlgo expected)
    {
        Assert.Equal(expected, Hashing.Parse(input));
    }
}

public class ChunkPlannerTests
{
    [Fact]
    public void Plan_CoversWholeFileWithoutOverlap()
    {
        var chunks = ChunkPlanner.Plan(20 * 1024 * 1024, 8 * 1024 * 1024);
        Assert.Equal(3, chunks.Count);
        Assert.Equal((0, 8L * 1024 * 1024), (chunks[0].Offset, chunks[0].Length));
        Assert.Equal((8L * 1024 * 1024, 8L * 1024 * 1024), (chunks[1].Offset, chunks[1].Length));
        Assert.Equal((16L * 1024 * 1024, 4L * 1024 * 1024), (chunks[2].Offset, chunks[2].Length));
    }

    [Fact]
    public void Plan_SmallerThanChunk_SingleChunk()
    {
        var chunks = ChunkPlanner.Plan(1024, 8 * 1024 * 1024);
        Assert.Single(chunks);
        Assert.Equal(1024, chunks[0].Length);
    }

    [Fact]
    public void Plan_Zero_Empty()
    {
        Assert.Empty(ChunkPlanner.Plan(0));
    }

    [Fact]
    public void Remaining_ExcludesCompleted()
    {
        var chunks = ChunkPlanner.Plan(24, 10);
        Assert.Equal(3, chunks.Count);
        var remain = ChunkPlanner.Remaining(chunks, new HashSet<int> { 0, 2 });
        Assert.Single(remain);
        Assert.Equal(10, remain[0].Offset);
    }

    [Theory]
    [InlineData(1L, 512L * 1024)]
    [InlineData(16L * 1024 * 1024, 512L * 1024)]
    [InlineData(16L * 1024 * 1024 + 1, 1024L * 1024)]
    [InlineData(64L * 1024 * 1024, 1024L * 1024)]
    [InlineData(64L * 1024 * 1024 + 1, 2L * 1024 * 1024)]
    public void ChooseChunkBytes_BySize(long total, long expected)
    {
        Assert.Equal(expected, ChunkPlanner.ChooseChunkBytes(total));
    }
}

public class DownloadPlannerTests
{
    [Fact]
    public void MissingFile_NeedsDownload() =>
        Assert.Equal(DownloadPlanner.Decision.NeedsDownload, DownloadPlanner.Decide(false, null, "abc"));

    [Fact]
    public void HashMismatch_NeedsDownload() =>
        Assert.Equal(DownloadPlanner.Decision.NeedsDownload, DownloadPlanner.Decide(true, "aaa", "abc"));

    [Fact]
    public void HashMatch_Skips() =>
        Assert.Equal(DownloadPlanner.Decision.SkipUpToDate, DownloadPlanner.Decide(true, "abc", "ABC"));

    [Fact]
    public void NoExpectedHash_ExistingFileTrusted() =>
        Assert.Equal(DownloadPlanner.Decision.SkipUpToDate, DownloadPlanner.Decide(true, null, null));
}
