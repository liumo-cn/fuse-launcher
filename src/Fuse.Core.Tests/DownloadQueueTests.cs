using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Xunit;

namespace Fuse.Core.Tests;

public class DownloadTaskStoreTests : IDisposable
{
    private readonly string _dir;

    public DownloadTaskStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mcdl-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void RoundTrip_PreservesFields()
    {
        var path = Path.Combine(_dir, "tasks.json");
        var store = new DownloadTaskStore(path);
        var record = new DownloadTaskRecord
        {
            Name = "原版 1.20.1",
            Kind = "file",
            SourceUrl = "https://example.com/client.jar",
            DestPath = @"C:\mc\client.jar",
            ExpectedHash = "abc",
            Status = DownloadTaskStatus.Failed,
            ErrorMessage = "网络中断",
        };
        store.Upsert(record);

        var reopened = new DownloadTaskStore(path);
        var loaded = reopened.Get(record.TaskId);
        Assert.NotNull(loaded);
        Assert.Equal("原版 1.20.1", loaded!.Name);
        Assert.Equal("https://example.com/client.jar", loaded.SourceUrl);
        Assert.Equal(DownloadTaskStatus.Failed, loaded.Status);
        Assert.Equal("网络中断", loaded.ErrorMessage);
    }

    [Fact]
    public void RestoreOnStartup_DownloadingBecomesQueued()
    {
        var path = Path.Combine(_dir, "tasks.json");
        var store = new DownloadTaskStore(path);
        store.Upsert(new DownloadTaskRecord { Name = "a", Status = DownloadTaskStatus.Downloading });
        store.Upsert(new DownloadTaskRecord { Name = "b", Status = DownloadTaskStatus.Paused });

        var reopened = new DownloadTaskStore(path);
        var all = reopened.RestoreOnStartup();
        Assert.Equal(DownloadTaskStatus.Queued, all.First(r => r.Name == "a").Status);
        Assert.Equal(DownloadTaskStatus.Paused, all.First(r => r.Name == "b").Status);
    }

    [Fact]
    public void CorruptStore_RebuildsEmpty()
    {
        var path = Path.Combine(_dir, "tasks.json");
        File.WriteAllText(path, "{ not valid json !!");
        var store = new DownloadTaskStore(path);
        Assert.Empty(store.GetAll());
    }
}

public class DownloadQueueTests
{
    /// <summary>假执行器：记录并发峰值，可控制完成时机。</summary>
    private sealed class FakeExecutor : IDownloadTaskExecutor
    {
        private readonly TaskCompletionSource<bool> _gate;
        private readonly bool _fail;
        private readonly ConcurrentDictionary<string, byte> _active = new();
        private int _peak;
        public int PeakConcurrency => Volatile.Read(ref _peak);
        public List<string> Completed { get; } = new();

        public FakeExecutor(TaskCompletionSource<bool>? gate = null, bool fail = false)
        {
            _gate = gate ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _fail = fail;
        }

        public void Release() => _gate.TrySetResult(true);

        public async Task ExecuteAsync(DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation)
        {
            _active.TryAdd(record.TaskId, 0);
            var observed = _active.Count;
            var peak = Volatile.Read(ref _peak);
            while (observed > peak &&
                   Interlocked.CompareExchange(ref _peak, observed, peak) != peak)
            {
                peak = Volatile.Read(ref _peak);
            }

            await _gate.Task.WaitAsync(cancellation);
            _active.TryRemove(record.TaskId, out _);
            if (_fail) throw new IOException("模拟网络失败");
            Completed.Add(record.TaskId);
        }
    }

    [Fact]
    public async Task ConcurrencyLimit_Respected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcdl-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DownloadTaskStore(Path.Combine(dir, "tasks.json"));
            var executor = new FakeExecutor();
            var queue = new DownloadQueue(store, executor, maxConcurrent: 2);

            for (var i = 0; i < 5; i++)
                queue.Enqueue(new DownloadTaskRecord { Name = "t" + i });

            // 等待调度稳定
            await Task.Delay(400);
            Assert.True(executor.PeakConcurrency <= 2, $"峰值并发 {executor.PeakConcurrency} 超过上限 2");

            // 放行全部 → 5 个都完成（每次 Release 允许下一个等待中的执行器通过）
            for (var i = 0; i < 6; i++) executor.Release();
            await Task.Delay(600);
            Assert.Equal(5, store.GetAll().Count(r => r.Status == DownloadTaskStatus.Completed));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PauseAndResume_Transitions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcdl-q2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DownloadTaskStore(Path.Combine(dir, "tasks.json"));
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var executor = new FakeExecutor(gate);
            var queue = new DownloadQueue(store, executor, maxConcurrent: 1);

            var task = queue.Enqueue(new DownloadTaskRecord { Name = "job" });
            await WaitForStatusAsync(store, task.TaskId, DownloadTaskStatus.Downloading);

            queue.Pause(task.TaskId);
            await WaitForStatusAsync(store, task.TaskId, DownloadTaskStatus.Paused);

            gate.TrySetResult(true);
            queue.Resume(task.TaskId);
            await WaitForStatusAsync(store, task.TaskId, DownloadTaskStatus.Completed);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Failure_MarksAndRetryWorks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcdl-q3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DownloadTaskStore(Path.Combine(dir, "tasks.json"));
            var failFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var executor = new FakeExecutor(failFirst, fail: true);
            var queue = new DownloadQueue(store, executor, maxConcurrent: 1);

            var task = queue.Enqueue(new DownloadTaskRecord { Name = "will-fail" });
            await WaitForStatusAsync(store, task.TaskId, DownloadTaskStatus.Downloading);
            executor.Release(); // 触发假执行器抛出 失败
            await WaitForStatusAsync(store, task.TaskId, DownloadTaskStatus.Failed);

            // 换成功执行器重试
            var okGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var okExecutor = new FakeExecutor(okGate, fail: false);
            var queue2 = new DownloadQueue(store, okExecutor, maxConcurrent: 1);
            okGate.TrySetResult(true);
            queue2.Retry(task.TaskId);
            await WaitForStatusAsync(store, task.TaskId, DownloadTaskStatus.Completed);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    static async Task WaitForStatusAsync(DownloadTaskStore store, string id, DownloadTaskStatus expected, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (store.Get(id)?.Status == expected) return;
            await Task.Delay(25);
        }
        Assert.Equal(expected, store.Get(id)!.Status);
    }
}
