using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuse.Core.Installer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fuse.Core.Download;

/// <summary>下载任务状态机（PRD 9.1）。</summary>
public enum DownloadTaskStatus
{
    Queued,
    Downloading,
    Paused,
    Failed,
    Completed,
    /// <summary>任务被用户删除:只用于通知界面刷新,不算失败(不能弹"下载失败"提示)。</summary>
    Removed,
}

/// <summary>任务记录（PRD 10.1 tasks 表）：持久化于 tasks.json，重启后恢复队列。</summary>
public sealed class DownloadTaskRecord
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>"file"（单文件分块下载）/"fileset"（小文件集合）/"user-file"（用户自选保存位置的高速下载）等。</summary>
    public string Kind { get; set; } = "file";
    public string SourceUrl { get; set; } = "";
    public string DestPath { get; set; } = "";
    public string? ExpectedHash { get; set; }
    public HashAlgo Algo { get; set; } = HashAlgo.Sha1;
    /// <summary>fileset 任务的小文件清单 json 路径（入队前由调用方写出）。</summary>
    public string? ManifestPath { get; set; }
    /// <summary>扩展信息（如 vanilla-install 的版本与安装布局），JSON 字符串。</summary>
    public string? Meta { get; set; }

    public DownloadTaskStatus Status { get; set; } = DownloadTaskStatus.Queued;
    /// <summary>已发出暂停请求,等执行线程真正退出。UI 用这个先给"暂停中"反馈。</summary>
    public bool Pausing { get; set; }
    public double Progress { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>整合包装完后仍需手动下载的模组(作者禁止第三方 API),已带项目/文件 ID</summary>
    public List<ManualModDownload>? ManualMods { get; set; }
    /// <summary>这些模组要放进哪个 mods 目录(一键打开用)</summary>
    public string? ManualModsDir { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
}

/// <summary>tasks.json 存取：整表原子写（先写 .tmp 再替换），跨线程单锁。</summary>
public sealed class DownloadTaskStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<DownloadTaskRecord> _records;

    public DownloadTaskStore(string path)
    {
        _path = path;
        _records = Load(path);
    }

    private static List<DownloadTaskRecord> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<List<DownloadTaskRecord>>(File.ReadAllText(path))
                       ?? new List<DownloadTaskRecord>();
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
            // 存储损坏即重建，任务可重新添加
        }
        return new List<DownloadTaskRecord>();
    }

    public IReadOnlyList<DownloadTaskRecord> GetAll()
    {
        lock (_lock) return _records.Select(Clone).ToList();
    }

    public DownloadTaskRecord? Get(string taskId)
    {
        lock (_lock)
        {
            var record = _records.FirstOrDefault(r => r.TaskId == taskId);
            return record == null ? null : Clone(record);
        }
    }

    public void Upsert(DownloadTaskRecord record)
    {
        lock (_lock)
        {
            var index = _records.FindIndex(r => r.TaskId == record.TaskId);
            if (index >= 0) _records[index] = Clone(record);
            else _records.Add(Clone(record));
            Save();
        }
    }

    public bool Remove(string taskId)
    {
        lock (_lock)
        {
            var removed = _records.RemoveAll(r => r.TaskId == taskId) > 0;
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>重启恢复：Downloading 转回 Queued（断点状态在 .chunks.json 里，可续传）。</summary>
    public IReadOnlyList<DownloadTaskRecord> RestoreOnStartup()
    {
        lock (_lock)
        {
            var restored = 0;
            foreach (var r in _records.Where(r => r.Status == DownloadTaskStatus.Downloading))
            {
                r.Status = DownloadTaskStatus.Queued;
                restored++;
            }
            if (restored > 0) Save();
            return _records.Select(Clone).ToList();
        }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(tmp, JsonConvert.SerializeObject(_records, Formatting.Indented));
        if (File.Exists(_path)) File.Delete(_path);
        File.Move(tmp, _path);
    }

    private static DownloadTaskRecord Clone(DownloadTaskRecord r)
        => JsonConvert.DeserializeObject<DownloadTaskRecord>(JsonConvert.SerializeObject(r))!;
}

/// <summary>任务执行回调注入点：真实队列注入分块/小文件下载器，测试注入假执行器。</summary>
public interface IDownloadTaskExecutor
{
    Task ExecuteAsync(DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation);
}

/// <summary>
/// 下载队列（FR-02）：并发上限、暂停/继续/重试/删除、状态事件。
/// 事件可能来自任意线程，UI 侧自行调度到 UI 线程。
/// </summary>
public sealed class DownloadQueue
{
    private readonly DownloadTaskStore _store;
    private readonly IDownloadTaskExecutor _executor;
    private readonly int _maxConcurrent;
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    /// <summary>已删除但执行线程尚未退出的任务:退出时不要再写回 store(否则删除会"复活")。</summary>
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private Task? _pumpTask;

    public event Action<DownloadTaskRecord>? TaskChanged;

    /// <summary>诊断日志回调(外壳接到 boot.log);不设置时静默。失败时能看出卡在哪个阶段。</summary>
    public static Action<string>? Log;

    private static void Trace(string message) => Log?.Invoke(message);

    public DownloadQueue(DownloadTaskStore store, IDownloadTaskExecutor executor, int maxConcurrent = 3)
    {
        _store = store;
        _executor = executor;
        _maxConcurrent = Math.Max(1, maxConcurrent);
    }

    public static DownloadQueue CreateDefault(DownloadTaskStore store, HttpClient http, int maxConcurrent, int smallFileParallelism)
        => new(store, new DefaultExecutor(http, smallFileParallelism), maxConcurrent);

    public DownloadTaskRecord Enqueue(DownloadTaskRecord record)
    {
        record.Status = DownloadTaskStatus.Queued;
        record.Progress = 0;
        record.ErrorMessage = null;
        record.FinishedAt = null;
        _store.Upsert(record);
        OnChanged(record);
        Pump();
        return record;
    }

    public void Pause(string taskId)
    {
        var record = _store.Get(taskId);
        if (record == null || record.Status != DownloadTaskStatus.Downloading) return;
        lock (_lock)
        {
            if (_running.TryGetValue(taskId, out var cts))
            {
                Trace("pause " + taskId + " -> cancel");
                // 先给界面"暂停中"反馈:执行线程可能要等当前请求超时才退出
                record.Pausing = true;
                _store.Upsert(record);
                OnChanged(record);
                try { cts.Cancel(); }
                catch (ObjectDisposedException)
                {
                    // 执行线程刚好收尾并 Dispose 了 CTS:任务已经结束,不用再取消
                    Trace("pause " + taskId + " cts disposed");
                }
            }
            else
            {
                Trace("pause " + taskId + " ignored: not in running");
            }
        }
        // 状态由执行循环在取消后落为 Paused
    }

    public void Resume(string taskId) => Requeue(taskId, onlyFrom: new[] { DownloadTaskStatus.Paused });

    public void Retry(string taskId) => Requeue(taskId, onlyFrom: new[] { DownloadTaskStatus.Paused, DownloadTaskStatus.Failed });

    public bool Remove(string taskId)
    {
        Pause(taskId);
        bool removed;
        lock (_lock)
        {
            // 执行线程还活着时打标记,避免它退出时把已删除的记录写回 store
            if (_running.ContainsKey(taskId)) _removed.Add(taskId);
            removed = _store.Remove(taskId);
        }
        // 注意:删除不是失败。以前这里发 Failed 事件,外壳的失败监听会弹"坏菜了"
        if (removed) OnChanged(new DownloadTaskRecord { TaskId = taskId, Status = DownloadTaskStatus.Removed });
        return removed;
    }

    public IReadOnlyList<DownloadTaskRecord> RestoreOnStartup()
    {
        var all = _store.RestoreOnStartup();
        Pump();
        return all;
    }

    public IReadOnlyList<DownloadTaskRecord> GetTasks() => _store.GetAll();

    // ---------- 内部 ----------

    private void Requeue(string taskId, DownloadTaskStatus[] onlyFrom)
    {
        var record = _store.Get(taskId);
        if (record == null || !onlyFrom.Contains(record.Status)) return;
        record.Status = DownloadTaskStatus.Queued;
        record.Pausing = false;
        // ErrorMessage 留着:重试时整合包执行器要从上次错误里回捞手动模组清单
        _store.Upsert(record);
        OnChanged(record);
        Pump();
    }

    private void Pump()
    {
        lock (_lock)
        {
            if (_pumpTask == null || _pumpTask.IsCompleted)
                _pumpTask = Task.Run(PumpLoop);
        }
        _signal.Release();
    }

    private async Task PumpLoop()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            while (true)
            {
                DownloadTaskRecord? next;
                CancellationTokenSource cts;
                lock (_lock)
                {
                    if (_running.Count >= _maxConcurrent) break;
                    next = _store.GetAll()
                        .Where(r => r.Status == DownloadTaskStatus.Queued && !_running.ContainsKey(r.TaskId))
                        .OrderBy(r => r.CreatedAt)
                        .FirstOrDefault();
                    if (next == null) break;
                    cts = new CancellationTokenSource();
                    _running[next.TaskId] = cts;
                }
                _ = RunOneAsync(next, cts);
            }
        }
    }

    private async Task RunOneAsync(DownloadTaskRecord record, CancellationTokenSource cts)
    {
        // 注意:不能用 Progress<T> —— 它把回调异步投递到线程池,几千个小文件每秒报告一次时
        // 会严重积压(界面看到的进度一直停在旧值,结束时才一次跳到 1.0)。
        // 这里同步写 record.Progress,持久化/事件按 250ms 节流。
        var lastNotify = DateTime.MinValue;
        var progress = new CallbackProgress(p =>
        {
            // 同一任务内进度只增不减:分块失败重试/子阶段重跑会让进度回跳,视觉上很难看
            var clamped = double.IsNaN(p) || double.IsInfinity(p) ? record.Progress : Math.Max(0, Math.Min(1, p));
            if (clamped > record.Progress) record.Progress = clamped;
            var now = DateTime.Now;
            if ((now - lastNotify).TotalMilliseconds < 250) return;
            lastNotify = now;
            _store.Upsert(record);   // 不落盘的话 GetTasks() 读到的永远是旧进度(圆环不动)
            OnChanged(record);
        });
        record.Status = DownloadTaskStatus.Downloading;
        record.Pausing = false;
        _store.Upsert(record);
        OnChanged(record);
        Trace("run " + record.TaskId + " (" + record.Kind + ")");

        try
        {
            await _executor.ExecuteAsync(record, progress, cts.Token).ConfigureAwait(false);
            record.Pausing = false;
            record.Status = DownloadTaskStatus.Completed;
            record.Progress = 1;
            record.ErrorMessage = null;
            record.FinishedAt = DateTime.Now;
            Trace("completed " + record.TaskId);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 只有"用户主动暂停/取消"才是 Paused;HttpClient 超时抛的 TaskCanceledException
            // 必须走 Failed,否则任务静默停在 3% —— 用户看到的就是"检测到了但从来不补全"。
            record.Pausing = false;
            record.Status = DownloadTaskStatus.Paused;
            Trace("paused " + record.TaskId);
        }
        catch (Exception ex)
        {
            record.Pausing = false;
            record.Status = DownloadTaskStatus.Failed;
            record.ErrorMessage = ex.Message;
            record.FinishedAt = DateTime.Now;
            // 完整异常(含内部异常/堆栈)进日志:任务面板只显示 Message,排查时不够用
            Trace("task " + record.TaskId + " (" + record.Kind + " / " + record.Name + ") failed: " + ex);
        }
        finally
        {
            bool wasRemoved;
            lock (_lock)
            {
                _running.Remove(record.TaskId);
                wasRemoved = _removed.Remove(record.TaskId);
            }
            cts.Dispose();
            if (!wasRemoved)
            {
                _store.Upsert(record);
                OnChanged(record);
            }
            _signal.Release(); // 空出槽位 → 重新泵
        }
    }

    private void OnChanged(DownloadTaskRecord record) => TaskChanged?.Invoke(record);

    /// <summary>真实执行器：file → 分块断点下载；fileset → 小文件并发队列。</summary>
    public static string SanitizePackVersionId(string slug, string versionNumber)
    {
        static string Clean(string raw)
        {
            var chars = raw.ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                .ToArray();
            return new string(chars).Trim('-');
        }
        var id = Clean(slug) + "-" + Clean(versionNumber);
        return id.Length == 0 ? "modpack-" + Guid.NewGuid().ToString("N")[..8] : id;
    }

    private sealed class DefaultExecutor : IDownloadTaskExecutor
    {
        private readonly HttpClient _http;
        private readonly int _smallFileParallelism;

        public DefaultExecutor(HttpClient http, int smallFileParallelism)
        {
            _http = http;
            _smallFileParallelism = smallFileParallelism;
        }

        public async Task ExecuteAsync(DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation)
        {
            if (record.Kind == "vanilla-install")
            {
                await ExecuteVanillaInstallAsync(record, progress, cancellation).ConfigureAwait(false);
                return;
            }

            if (record.Kind == "fabric-install")
            {
                await ExecuteFabricInstallAsync(record, progress, cancellation).ConfigureAwait(false);
                return;
            }

            if (record.Kind == "optifine-assemble")
            {
                await ExecuteOptiFineAssembleAsync(record, progress, cancellation).ConfigureAwait(false);
                return;
            }

            if (record.Kind == "modpack-install")
            {
                await ExecuteModpackInstallAsync(record, progress, cancellation).ConfigureAwait(false);
                return;
            }

            if (record.Kind == "optifine-install")
            {
                await ExecuteOptiFineInstallAsync(record, progress, cancellation).ConfigureAwait(false);
                return;
            }

            if (record.Kind == "mod-update")
            {
                await ExecuteModUpdateAsync(record, progress, cancellation).ConfigureAwait(false);
                return;
            }

            if (record.Kind == "forge-install" || record.Kind == "neoforge-install")
            {
                await ExecuteModLoaderInstallAsync(record, progress, cancellation).ConfigureAwait(false);
                return;
            }

            if (record.Kind == "fileset")
            {
                if (string.IsNullOrEmpty(record.ManifestPath) || !File.Exists(record.ManifestPath))
                    throw new InvalidOperationException("小文件清单缺失，无法执行集合下载");
                var items = JsonConvert.DeserializeObject<FileDownloadItem[]>(File.ReadAllText(record.ManifestPath))
                    ?? Array.Empty<FileDownloadItem>();
                var downloader = new SmallFileDownloader(_http);
                var result = await downloader.DownloadSetAsync(items,
                    new SmallFileOptions { Parallelism = _smallFileParallelism }, progress, cancellation)
                    .ConfigureAwait(false);
                if (result.Failed.Count > 0)
                    throw new IOException($"{result.Failed.Count} 个文件下载失败：{result.Failed[0]}");
                return;
            }

            // "file" 与 "user-file" 都是单文件分块下载;user-file 的 DestPath 由用户指定,
            // 执行前做一次安全校验(拒绝系统/程序目录、拒绝空文件名)。
            if (record.Kind == "user-file")
                UserDownloadGuard.Validate(record.SourceUrl, record.DestPath);

            var chunked = new ChunkedDownloader(_http);
            // 校验算法跟任务走:MC 官方文件是 SHA-1,用户下载是 SHA-256
            var algo = record.Algo == HashAlgo.Sha256 && record.Kind == "user-file"
                ? new ChunkedDownloadOptions { Algo = HashAlgo.Sha256 }
                : null;
            await chunked.DownloadAsync(record.SourceUrl, record.DestPath, record.ExpectedHash,
                algo, progress, cancellation).ConfigureAwait(false);
        }

        /// <summary>

        /// <summary>同一版本 id 的安装串行化:多个任务同时装同一版本会争抢 &lt;id&gt;.json。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> VersionLocks
            = new(StringComparer.Ordinal);

        private static SemaphoreSlim LockFor(string? versionId)
            => VersionLocks.GetOrAdd(versionId ?? "", _ => new SemaphoreSlim(1, 1));

        private async Task ExecuteVanillaInstallAsync(
            DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation)
        {
            var meta = JsonConvert.DeserializeObject<VanillaInstallMeta>(record.Meta ?? "{}")
                       ?? new VanillaInstallMeta();
            var versionsRoot = meta.VersionsRoot ?? throw new InvalidOperationException("安装任务缺少版本目录");

            var gate = LockFor(meta.VersionId);
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                await VanillaInstallCoreAsync(record, meta, versionsRoot, progress, cancellation).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task VanillaInstallCoreAsync(
            DownloadTaskRecord record, VanillaInstallMeta meta, string versionsRoot,
            IProgress<double> progress, CancellationToken cancellation)
        {

            // 1. 官方 version.json（SHA-1 强校验，占 5% 进度）
            var entry = new Sources.MojangVersionEntry(meta.VersionId ?? record.Name, "release", "", record.SourceUrl, record.ExpectedHash ?? "");
            var version = await Sources.MojangSource.DownloadVersionJsonAsync(_http, entry, versionsRoot, cancellation)
                .ConfigureAwait(false);
            progress.Report(0.05);

            // 2. 完整安装（客户端 jar / libraries / assets / log 配置，占 95%）
            var installer = new VanillaInstaller(_http) { SmallFileParallelism = _smallFileParallelism };
            await installer.InstallAsync(
                version,
                versionsRoot,
                meta.LibrariesRoot ?? Path.Combine(versionsRoot, "..", "libraries"),
                meta.AssetsRoot ?? Path.Combine(versionsRoot, "..", "assets"),
                meta.GameDirectory ?? Path.Combine(versionsRoot, "..", "instances", version.Id),
                ToScaled(progress, 0.05, 0.95),
                cancellation).ConfigureAwait(false);
        }

        private static IProgress<double> ToScaled(IProgress<double>? progress, double offset, double weight)
            => new CallbackProgress(p => progress?.Report(offset + weight * p));

        private sealed class FabricInstallMeta
        {
            public string? GameVersion { get; set; }
            /// <summary>真正的 MC 版本(底座是 OptiFine 等派生版本时与之不同)。</summary>
            public string? McVersion { get; set; }
            public string? LoaderVersion { get; set; }
            public string? VersionsRoot { get; set; }
            public string? LibrariesRoot { get; set; }
            public string? AssetsRoot { get; set; }
            public string? GameDirectory { get; set; }
            /// <summary>OptiFine 安装器需要的 Java 主目录。</summary>
            public string? JavaHome { get; set; }
            /// <summary>instances 根目录(OptiFine 组装任务用)。</summary>
            public string? InstancesRoot { get; set; }
        }

        /// <summary>OptiFine:下载官方 installer、在临时 APPDATA 下静默安装,再把产物复制进 versions。</summary>
        private sealed class ModpackInstallMeta
        {
            public string? MrpackUrl { get; set; }
            public string? MrpackSha1 { get; set; }
            /// <summary>本地 .mrpack 路径(文件夹列表「导入整合包」);设置后跳过下载。</summary>
            public string? MrpackLocalPath { get; set; }
            public string? PackSlug { get; set; }
            public string? VersionNumber { get; set; }
            public string? Title { get; set; }
            public string? VersionsRoot { get; set; }
            public string? LibrariesRoot { get; set; }
            public string? AssetsRoot { get; set; }
            public string? InstancesRoot { get; set; }
            /// <summary>实例目录(可选):外部目录布局 = 根目录本身,不设时按 instancesRoot/id。</summary>
            public string? InstanceDir { get; set; }
            /// <summary>CurseForge 整合包:队列内下载 pack zip 并合成 mrpack,才有进度和暂停。</summary>
            [JsonProperty("cfProjectId")] public int CurseForgeProjectId { get; set; }
            [JsonProperty("cfFileId")] public int CurseForgeFileId { get; set; }
            [JsonProperty("cfDownloadUrl")] public string? CurseForgeDownloadUrl { get; set; }
            [JsonProperty("cfSha1")] public string? CurseForgeSha1 { get; set; }
        }

        /// <summary>
        /// Modrinth 整合包(.mrpack)全自动安装:
        /// 下载 mrpack → 解析 index → 原版底座(缺则装) → 加载器(缺则装,Forge 缺 Java 自动补)
        /// → 写整合包版本 json(inheritsFrom 加载器版) → 文件清单 + overrides 落进实例目录。
        /// </summary>
        private async Task ExecuteModpackInstallAsync(
            DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation)
        {
            // 上次失败时列出的手动模组,用户可能已经下好放进 mods 了。
            // 全部就位就直接算完成,不再把整包重下一遍。
            var manual = record.ManualMods is { Count: > 0 }
                ? record.ManualMods!
                : ManualModDownloads.ParseFromMessage(record.ErrorMessage);
            var manualDir = record.ManualModsDir
                            ?? ManualModDownloads.ModsDirectoryFromMessage(record.ErrorMessage);
            if (manual.Count > 0 && !string.IsNullOrEmpty(manualDir) &&
                manual.All(m => ManualModDownloads.IsPresent(manualDir, m)))
            {
                Trace("modpack retry: manual mods already present in " + manualDir);
                record.ManualMods = null;
                record.ManualModsDir = null;
                progress.Report(1.0);
                return;
            }
            record.ManualMods = null;
            record.ManualModsDir = null;

            var meta = JsonConvert.DeserializeObject<ModpackInstallMeta>(record.Meta ?? "{}")
                       ?? new ModpackInstallMeta();
            var hasLocalPack = !string.IsNullOrEmpty(meta.MrpackLocalPath) && File.Exists(meta.MrpackLocalPath);
            var hasCfPack = meta.CurseForgeProjectId > 0 && !string.IsNullOrEmpty(meta.CurseForgeDownloadUrl);
            if ((!hasLocalPack && !hasCfPack && string.IsNullOrEmpty(meta.MrpackUrl)) ||
                string.IsNullOrEmpty(meta.PackSlug) ||
                string.IsNullOrEmpty(meta.VersionsRoot) || string.IsNullOrEmpty(meta.InstancesRoot))
                throw new InvalidOperationException("整合包安装任务缺少必要参数");

            var versionNumber = (meta.VersionNumber ?? "1.0").Trim();
            var packVersionId = SanitizePackVersionId(meta.PackSlug!, versionNumber);
            var gate = LockFor(packVersionId);
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                // 1. 下载 .mrpack(占 0~5%)
                var tempRoot = Path.Combine(Path.GetTempPath(), "mcdl-modpack-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);
                Trace("modpack " + packVersionId + ": source=" + (hasLocalPack ? "local file" : meta.MrpackUrl));
                try
                {
                    IReadOnlyList<Installer.ModpackInstaller.MissingModFile> cfMissingFiles =
                        Array.Empty<Installer.ModpackInstaller.MissingModFile>();
                    var mrpackPath = Path.Combine(tempRoot, "pack.mrpack");
                    if (hasLocalPack)
                    {
                        // 本地导入:直接拷进临时目录,跳过下载与 sha1(用户选的就是这个文件)
                        File.Copy(meta.MrpackLocalPath!, mrpackPath, overwrite: true);
                        progress.Report(0.05);
                    }
                    else if (hasCfPack)
                    {
                        // CurseForge:先在队列里下 pack zip,再按 manifest 合成 mrpack。
                        // 以前这段在 UI 线程里串行跑,没有任务/进度/暂停,看起来就是"点了没反应还特别慢"。
                        var cfZip = Path.Combine(tempRoot, "cf-pack.zip");
                        // 整合包本体是几十到几百 MB,走分块下载:有进度,多连接也快
                        var cfDownloader = new ChunkedDownloader(_http);
                        await cfDownloader.DownloadAsync(
                            meta.CurseForgeDownloadUrl!,
                            cfZip,
                            string.IsNullOrEmpty(meta.CurseForgeSha1) ? null : meta.CurseForgeSha1,
                            new ChunkedDownloadOptions { Parallelism = 4 },
                            ToScaled(progress, 0.00, 0.02), cancellation).ConfigureAwait(false);

                        var missingFiles = await Installer.ModpackInstaller.BuildCurseForgeMrpackAsync(
                            _http, meta.CurseForgeProjectId, cfZip, mrpackPath,
                            ToScaled(progress, 0.02, 0.03), cancellation, Trace).ConfigureAwait(false);
                        if (missingFiles.Count > 0)
                            Trace("modpack " + packVersionId + ": " + missingFiles.Count + " files not downloadable via API");
                        cfMissingFiles = missingFiles;
                        Trace("modpack " + packVersionId + ": curseforge manifest converted");
                    }
                    else
                    {
                        // 整合包本体是几十到几百 MB,走分块下载:有进度,多连接也快
                        var mrDownloader = new ChunkedDownloader(_http);
                        await mrDownloader.DownloadAsync(
                            meta.MrpackUrl!,
                            mrpackPath,
                            string.IsNullOrEmpty(meta.MrpackSha1) ? null : meta.MrpackSha1,
                            new ChunkedDownloadOptions { Parallelism = 4 },
                            ToScaled(progress, 0.00, 0.05), cancellation).ConfigureAwait(false);
                    }

                    // 2. 解包 + 解析 index(5%)
                    Installer.ModpackInstaller.ExtractAll(mrpackPath, tempRoot, cancellation);
                    var index = Installer.ModpackInstaller.ParseIndex(tempRoot);
                    var gv = index.GameVersion;
                    Trace("modpack " + packVersionId + ": mc=" + gv + " loader=" + (index.LoaderType ?? "vanilla") +
                          " files=" + index.Files.Count);
                    var gameDir = string.IsNullOrEmpty(meta.InstanceDir)
                        ? Path.Combine(meta.InstancesRoot!, packVersionId)
                        : meta.InstanceDir!;
                    Directory.CreateDirectory(gameDir);

                    // 3. 原版底座(缺则装;占 5~38%)。版本 json 的 url/sha1 必须先从官方清单解析,
                    //    否则 VanillaInstallCoreAsync 拿到空地址直接失败。
                    var vanillaJson = Path.Combine(meta.VersionsRoot!, gv, gv + ".json");
                    if (!File.Exists(vanillaJson))
                    {
                        var manifest = await Sources.MojangSource.FetchManifestAsync(_http, cancellation)
                            .ConfigureAwait(false);
                        var entry = manifest.FirstOrDefault(v =>
                            string.Equals(v.Id, gv, StringComparison.Ordinal))
                            ?? throw new InvalidOperationException($"官方清单里没有版本 {gv}");
                        await VanillaInstallCoreAsync(
                            new DownloadTaskRecord { Name = gv, SourceUrl = entry.Url, ExpectedHash = entry.Sha1 },
                            new VanillaInstallMeta
                            {
                                VersionId = gv,
                                VersionsRoot = meta.VersionsRoot,
                                LibrariesRoot = meta.LibrariesRoot,
                                AssetsRoot = meta.AssetsRoot,
                                GameDirectory = Path.Combine(meta.InstancesRoot!, gv),
                            },
                            meta.VersionsRoot!, ToScaled(progress, 0.05, 0.33), cancellation).ConfigureAwait(false);
                    }

                    // 4. 加载器(38~58%);quilt 暂不支持
                    var inheritsFrom = gv;
                    Trace("modpack " + packVersionId + ": vanilla ready, installing loader");
                    if (index.LoaderType == "fabric")
                    {
                        await Installer.FabricInstaller.InstallAsync(_http, gv, index.LoaderVersion!,
                            meta.VersionsRoot!, meta.LibrariesRoot!, meta.AssetsRoot!,
                            Path.Combine(meta.InstancesRoot!, gv),
                            ToScaled(progress, 0.38, 0.20), cancellation).ConfigureAwait(false);
                        inheritsFrom = $"{gv}-fabric-{index.LoaderVersion}";
                    }
                    else if (index.LoaderType is "forge" or "neoforge")
                    {
                        var flavor = index.LoaderType == "forge"
                            ? Installer.ModLoaderInstaller.Forge
                            : Installer.ModLoaderInstaller.NeoForge;
                        var javaExe = await EnsureJavaForAsync(
                            meta.VersionsRoot!, gv, ToScaled(progress, 0.38, 0.08), cancellation).ConfigureAwait(false);
                        await Installer.ModLoaderInstaller.InstallAsync(_http, flavor, gv, index.LoaderVersion!,
                            meta.VersionsRoot!, meta.LibrariesRoot!, javaExe,
                            Path.Combine(tempRoot, "loader"), ToScaled(progress, 0.46, 0.12), cancellation)
                            .ConfigureAwait(false);
                        inheritsFrom = $"{gv}-{flavor.Key}-{index.LoaderVersion}";
                    }
                    else if (index.LoaderType == "quilt")
                    {
                        throw new InvalidOperationException("该整合包使用 Quilt 加载器,暂不支持;请选 Fabric / Forge / NeoForge 整合包");
                    }

                    // 5. 写整合包版本 json(inheritsFrom 沿加载器版,递归合并到原版)
                    var versionDir = Path.Combine(meta.VersionsRoot!, packVersionId);
                    Directory.CreateDirectory(versionDir);
                    File.WriteAllText(Path.Combine(versionDir, packVersionId + ".json"),
                        new JObject
                        {
                            ["id"] = packVersionId,
                            ["inheritsFrom"] = inheritsFrom,
                            ["type"] = "release",
                            ["releaseTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            // 标记这是整合包实例(模组页会先提醒"不建议自己加模组"),也便于后续检查更新
                            ["fuseModpack"] = true,
                            ["fusePackSlug"] = meta.PackSlug,
                            ["fusePackVersion"] = meta.VersionNumber,
                            ["fusePackTitle"] = meta.Title,
                        }.ToString(Newtonsoft.Json.Formatting.Indented));

                    // 6. 文件清单(60~92%)
                    var items = index.Files.Select(f => new FileDownloadItem
                    {
                        Url = f.Url,
                        DestPath = Path.Combine(gameDir, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                        ExpectedHash = string.IsNullOrEmpty(f.Sha1) ? null : f.Sha1,
                        Algo = HashAlgo.Sha1,
                    }).ToList();
                    if (items.Count > 0)
                    {
                        var downloader = new SmallFileDownloader(_http);
                        var result = await downloader.DownloadSetAsync(items,
                            new SmallFileOptions
                            {
                                Parallelism = _smallFileParallelism,
                                InitialParallelism = _smallFileParallelism,
                            },
                            ToScaled(progress, 0.60, 0.32), cancellation).ConfigureAwait(false);
                        if (result.Failed.Count > 0)
                            throw new IOException($"整合包文件有 {result.Failed.Count} 个下载失败:{result.Failed[0]}");
                    }
                    Trace("modpack " + packVersionId + ": loader=" + inheritsFrom + ", files done (" + items.Count + ")");

                    // 7. overrides(92~96%;index 清单路径不被覆盖)
                    Installer.ModpackInstaller.ExtractOverrides(
                        tempRoot, gameDir, Installer.ModpackInstaller.ReservedPaths(index.Files), cancellation);
                    if (cfMissingFiles.Count > 0)
                    {
                        var modsDir = Path.Combine(gameDir, "mods");
                        var missingPath = Path.Combine(gameDir, "需手动下载的模组.txt");
                        // 用户可能上次失败后已经手动把文件放进 mods:这次能对上就直接算完成
                        var stillMissing = cfMissingFiles
                            .Where(x => !ManualModDownloads.IsPresent(modsDir, new ManualModDownload
                            {
                                Name = x.FileName,
                                FileName = x.FileName,
                                Sha1 = x.Sha1,
                                ProjectId = x.ProjectId,
                                FileId = x.FileId,
                            }))
                            .ToList();

                        if (stillMissing.Count == 0)
                        {
                            Trace("modpack " + packVersionId + ": manual mods found in " + modsDir);
                            try { if (File.Exists(missingPath)) File.Delete(missingPath); }
                            catch (IOException) { }
                        }
                        else
                        {
                            Func<Installer.ModpackInstaller.MissingModFile, string> label = x =>
                                string.IsNullOrEmpty(x.FileName)
                                    ? x.ProjectId + "/" + x.FileId
                                    : x.FileName + " (" + x.ProjectId + "/" + x.FileId + ")";
                            var list = string.Join(Environment.NewLine, stillMissing.Select(x => "· " + label(x)));
                            try
                            {
                                File.WriteAllText(missingPath,
                                    "以下模组作者禁止第三方 API 下载，请从 CurseForge 网页下载后放进本目录的 mods 文件夹："
                                    + Environment.NewLine + list + Environment.NewLine);
                            }
                            catch (IOException)
                            {
                            }
                            // 带上结构化清单:原因弹窗可以直接一键开网页,不用再解析文字
                            record.ManualModsDir = modsDir;
                            record.ManualMods = stillMissing.Select(x => new ManualModDownload
                            {
                                Name = x.FileName,
                                FileName = x.FileName,
                                Sha1 = x.Sha1,
                                ProjectId = x.ProjectId,
                                FileId = x.FileId,
                            }).ToList();
                            throw new IOException(
                                "整合包已安装，但需手动下载 " + stillMissing.Count + " 个模组（作者禁止第三方 API 下载）。"
                                + "清单已写到 " + missingPath + Environment.NewLine + list);
                        }
                    }
                    progress.Report(1.0);
                    Trace("modpack " + packVersionId + ": done");
                }
                finally
                {
                    try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
                    catch (IOException) { }
                }
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>模组更新:下载新文件,成功落盘后再删除旧文件(中断时至少保留一个可用的)。</summary>
        private async Task ExecuteModUpdateAsync(
            DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation)
        {
            var meta = JsonConvert.DeserializeObject<ModUpdateMeta>(record.Meta ?? "{}")
                       ?? new ModUpdateMeta();
            if (string.IsNullOrEmpty(meta.Url) || string.IsNullOrEmpty(meta.DestPath))
                throw new InvalidOperationException("模组更新任务缺少必要参数");

            var downloader = new SmallFileDownloader(_http);
            var result = await downloader.DownloadSetAsync(new[]
            {
                new FileDownloadItem
                {
                    Url = meta.Url!,
                    DestPath = meta.DestPath!,
                    ExpectedHash = string.IsNullOrEmpty(meta.Sha1) ? null : meta.Sha1,
                    Algo = HashAlgo.Sha1,
                },
            }, new SmallFileOptions { Parallelism = 1 }, progress, cancellation).ConfigureAwait(false);
            if (result.Failed.Count > 0)
                throw new IOException("模组下载失败:" + result.Failed[0]);

            try
            {
                if (!string.IsNullOrEmpty(meta.OldPath) && File.Exists(meta.OldPath) &&
                    !string.Equals(Path.GetFullPath(meta.OldPath!), Path.GetFullPath(meta.DestPath!),
                        StringComparison.OrdinalIgnoreCase))
                    File.Delete(meta.OldPath!);
            }
            catch (IOException)
            {
                // 旧文件删不掉不影响新文件生效;下次更新还会覆盖
            }
        }

        private sealed class ModUpdateMeta
        {
            public string? Url { get; set; }
            public string? Sha1 { get; set; }
            public string? DestPath { get; set; }
            public string? OldPath { get; set; }
        }

        /// <summary>Forge/NeoForge processors 需要 Java:优先本机,缺失时自动从 Mojang 运行时清单补齐。</summary>
        private async Task<string?> EnsureJavaForAsync(
            string versionsRoot, string gameVersion, IProgress<double>? progress, CancellationToken cancellation)
        {
            var merged = Versioning.VersionJsonLoader.Load(versionsRoot, gameVersion);
            var major = merged.JavaVersion?.MajorVersion ?? 17;
            var runtimesRoot = Path.GetFullPath(Path.Combine(versionsRoot, "..", "runtimes"));
            var all = Java.JavaLocator.FindInstalled()
                .Concat(Java.JavaLocator.ScanRuntimesRoot(runtimesRoot));
            var pick = Java.JavaLocator.Pick(all.ToList(), major);
            if (pick != null) return pick.JavaExecutable;

            var entries = await Java.MojangJavaRuntime.FetchCatalogAsync(_http, cancellation).ConfigureAwait(false);
            var entry = Java.MojangJavaRuntime.Pick(entries, merged.JavaVersion?.Component, major)
                ?? throw new InvalidOperationException($"没有可用的 Java {major} 运行时");
            var runtime = await Java.MojangJavaRuntime.DownloadAsync(_http, entry,
                Path.Combine(runtimesRoot, entry.Component + "-" + major), progress, cancellation)
                .ConfigureAwait(false);
            return runtime.JavaExecutable;
        }

        private async Task ExecuteOptiFineInstallAsync(
            DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation)
        {
            var meta = JsonConvert.DeserializeObject<FabricInstallMeta>(record.Meta ?? "{}")
                       ?? new FabricInstallMeta();
            if (string.IsNullOrEmpty(meta.GameVersion) || string.IsNullOrEmpty(meta.LoaderVersion) ||
                string.IsNullOrEmpty(meta.VersionsRoot))
                throw new InvalidOperationException("OptiFine 安装任务缺少必要参数");

            // Meta.LoaderVersion 存的是 "OptiFine 1.20.1 HD_U I6" 这种显示名,这里拆回 type/patch
            var parts = meta.LoaderVersion!.Split(' ');
            if (parts.Length < 4)
                throw new InvalidOperationException("OptiFine 版本信息不完整:" + meta.LoaderVersion);
            var version = new Sources.OptiFineVersion(parts[1], parts[2], parts[3], "", null);

            var javaExe = meta.JavaHome != null
                ? Path.Combine(meta.JavaHome, "bin", "java.exe")
                : throw new InvalidOperationException("缺少 Java 路径");

            var tempRoot = Path.Combine(Path.GetTempPath(), "mcdl-optifine-" + Guid.NewGuid().ToString("N"));
            try
            {
                await Installer.OptiFineInstaller.InstallAsync(_http, javaExe,
                    meta.GameVersion!, version, meta.VersionsRoot!, meta.LibrariesRoot!, tempRoot,
                    progress, cancellation).ConfigureAwait(false);
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (IOException) { }
            }
        }

        /// <summary>OptiFine 组装:Fabric 底座 + OptiFabric + OptiFine 作为 mod(方案 A)。</summary>
        private async Task ExecuteOptiFineAssembleAsync(
            DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation)
        {
            var meta = JsonConvert.DeserializeObject<FabricInstallMeta>(record.Meta ?? "{}")
                       ?? new FabricInstallMeta();
            if (string.IsNullOrEmpty(meta.McVersion) || string.IsNullOrEmpty(meta.LoaderVersion) ||
                string.IsNullOrEmpty(meta.VersionsRoot) || string.IsNullOrEmpty(meta.LibrariesRoot) ||
                string.IsNullOrEmpty(meta.InstancesRoot))
                throw new InvalidOperationException("OptiFine 组装任务缺少必要参数");

            await Installer.OptiFineAssembler.AssembleAsync(_http,
                meta.McVersion!, meta.LoaderVersion!,
                meta.VersionsRoot!, meta.LibrariesRoot!, meta.AssetsRoot!, meta.InstancesRoot!,
                progress, cancellation).ConfigureAwait(false);
        }

        /// <summary>Forge / NeoForge:下载 installer 取 version.json,再拉库(官方优先,超时换镜像)。</summary>
        private async Task ExecuteModLoaderInstallAsync(
            DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation)
        {
            var meta = JsonConvert.DeserializeObject<FabricInstallMeta>(record.Meta ?? "{}")
                       ?? new FabricInstallMeta();
            if (string.IsNullOrEmpty(meta.GameVersion) || string.IsNullOrEmpty(meta.LoaderVersion) ||
                string.IsNullOrEmpty(meta.VersionsRoot) || string.IsNullOrEmpty(meta.LibrariesRoot))
                throw new InvalidOperationException("加载器安装任务缺少必要参数");

            var flavor = record.Kind == "forge-install"
                ? Installer.ModLoaderInstaller.Forge
                : Installer.ModLoaderInstaller.NeoForge;

            var javaExe = string.IsNullOrEmpty(meta.JavaHome)
                ? null
                : Path.Combine(meta.JavaHome!, "bin", "java.exe");
            var tempRoot = Path.Combine(Path.GetTempPath(), "FuseLauncher", "loader-install");

            // 同版本原版安装未完成前不要动(会撞 versions/<mc>/<mc>.json 的文件锁)
            var gate = LockFor(meta.GameVersion!);
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                await Installer.ModLoaderInstaller.InstallAsync(_http, flavor,
                    meta.GameVersion!, meta.LoaderVersion!,
                    meta.VersionsRoot!, meta.LibrariesRoot!,
                    javaExe, tempRoot,
                    progress, cancellation).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task ExecuteFabricInstallAsync(
            DownloadTaskRecord record, IProgress<double> progress, CancellationToken cancellation)
        {
            var meta = JsonConvert.DeserializeObject<FabricInstallMeta>(record.Meta ?? "{}")
                       ?? new FabricInstallMeta();
            if (string.IsNullOrEmpty(meta.GameVersion) || string.IsNullOrEmpty(meta.LoaderVersion))
                throw new InvalidOperationException("Fabric 安装任务缺少版本信息");

            // 加载器任务依赖同版本原版底座:必须等原版安装任务跑完再动,
            // 否则两边同时读写 versions/<mc>/<mc>.json 会报"正由另一进程使用"(用户实测卡死)。
            var gate = LockFor(meta.GameVersion);
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                await Installer.FabricInstaller.InstallAsync(_http, meta.GameVersion!, meta.LoaderVersion!,
                    meta.VersionsRoot!, meta.LibrariesRoot!, meta.AssetsRoot!, meta.GameDirectory!,
                    progress, cancellation, meta.McVersion).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private sealed class VanillaInstallMeta
        {
            public string? VersionId { get; set; }
            public string? VersionsRoot { get; set; }
            public string? LibrariesRoot { get; set; }
            public string? AssetsRoot { get; set; }
            public string? GameDirectory { get; set; }
            /// <summary>OptiFine 安装器需要的 Java 主目录。</summary>
            public string? JavaHome { get; set; }
            /// <summary>instances 根目录(OptiFine 组装任务用)。</summary>
            public string? InstancesRoot { get; set; }
        }
    }
}

/// <summary>同步进度回调:Progress&lt;T&gt; 会把回调异步投递到线程池,海量小文件时会积压。</summary>
internal sealed class CallbackProgress : IProgress<double>
{
    private readonly Action<double> _report;
    public CallbackProgress(Action<double> report) => _report = report;
    public void Report(double value) => _report(value);
}

/// <summary>
/// 用户自选下载的安全闸:拒绝系统/程序目录与无效文件名。
/// </summary>
public static class UserDownloadGuard
{
    private static readonly string[] BlockedRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        AppDomain.CurrentDomain.BaseDirectory,
    };

    public static void Validate(string url, string destPath)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("下载地址必须是 http/https 链接");
        if (string.IsNullOrWhiteSpace(destPath))
            throw new InvalidOperationException("请先选择保存位置");

        string full;
        try { full = Path.GetFullPath(destPath); }
        catch (Exception ex) { throw new InvalidOperationException("保存位置无效:" + ex.Message); }
        var fileName = Path.GetFileName(full);
        if (string.IsNullOrWhiteSpace(fileName) || fileName == "." || fileName == "..")
            throw new InvalidOperationException("保存位置必须是一个文件名");

        foreach (var root in BlockedRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var rootFull = Path.GetFullPath(root).TrimEnd('\\') + Path.DirectorySeparatorChar;
            if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能保存到系统或程序目录:" + rootFull);
        }
    }
}
