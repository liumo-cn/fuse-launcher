using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fuse.Core.Download;
using Fuse.Core.Versioning;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 导入→隔离→删除安全 全链路(2026-11-18 香草纪元事故回归):
/// mrpack overrides 落到外部共享根 → staging 通用补链让 kubejs 等可被隔离游戏看见
/// → 删除版本仍然只删版本目录。
/// </summary>
public class ModpackImportIsolationTests : IDisposable
{
    private readonly string _root;      // 外部共享根
    private readonly string _install;   // 主安装目录

    public ModpackImportIsolationTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "mcdl-imp-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "game");
        _install = Path.Combine(baseDir, "install");
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        try { SafeDirectory.DeleteTree(_root, Path.Combine(_root, "..")); } catch (IOException) { }
        try { Directory.Delete(_install, true); } catch (IOException) { }
    }

    /// <summary>模拟香草纪元那种包:文件全在 overrides 里,index 文件清单为空。</summary>
    private static string BuildPack(string path)
    {
        const string index = """
            {"formatVersion":1,"gameVersion":"1.20.1","versionId":"1.0","name":"demo pack",
             "dependencies":{"minecraft":"1.20.1"},"files":[]}
            """;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var idx = zip.CreateEntry("modrinth.index.json");
        using (var w = new StreamWriter(idx.Open())) w.Write(index);
        void Add(string rel, string content)
        {
            var e = zip.CreateEntry(rel);
            using var w = new StreamWriter(e.Open()); w.Write(content);
        }
        Add("overrides/mods/demo-mod.jar", "jar-bytes");
        Add("overrides/kubejs/startup_scripts/main.js", "StartupEvents.registry('item', e => {})");
        Add("overrides/config/demo.toml", "key = 1");
        Add("overrides/schematics/house.schema", "schem");
        return path;
    }

    private static async Task WaitForStatusAsync(DownloadTaskStore store, string taskId,
        DownloadTaskStatus status, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var rec = store.Get(taskId);
            if (rec?.Status == status) return;
            await Task.Delay(100);
        }
        var last = store.Get(taskId);
        throw new TimeoutException("任务未到 " + status + ": " + last?.Status + " " + last?.ErrorMessage);
    }

    /// <summary>合成 CurseForge zip:manifest.json 的 files 为空(离线安全),内容全在 overrides。</summary>
    private static string BuildCfZip(string path)
    {
        // 不带 modLoaders:离线沙盒跳过 Forge 安装;真包会带,队列内正常走加载器安装
        const string manifest = """
            {"minecraft":{"version":"1.20.1"},
             "manifestType":"minecraftModpack","manifestVersion":1,
             "name":"CF Demo Pack","version":"2.0","author":"tester","files":[]}
            """;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var e0 = zip.CreateEntry("manifest.json");
        using (var w = new StreamWriter(e0.Open())) w.Write(manifest);
        void Add(string rel, string content)
        {
            var e = zip.CreateEntry(rel);
            using var w = new StreamWriter(e.Open()); w.Write(content);
        }
        Add("overrides/mods/cf-mod.jar", "jar");
        Add("overrides/kubejs/startup_scripts/cf.js", "StartupEvents.listen(e => {})");
        Add("client-overrides/config/option.toml", "a = 1");
        return path;
    }

    [Fact]
    public async Task ImportCurseForgeZip_OverridesLand_AndStagingLinksKubejs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcdl-cfz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var gvDir = Path.Combine(_root, "versions", "1.20.1");
            Directory.CreateDirectory(gvDir);
            File.WriteAllText(Path.Combine(gvDir, "1.20.1.json"), """{"id":"1.20.1","type":"release"}""");
            var zipPath = BuildCfZip(Path.Combine(dir, "cf-demo.zip"));
            var store = new DownloadTaskStore(Path.Combine(dir, "tasks.json"));
            using var http = new HttpClient();
            var queue = DownloadQueue.CreateDefault(store, http, maxConcurrent: 2, smallFileParallelism: 2);

            var meta = new JObject
            {
                ["cfZipLocalPath"] = zipPath,
                ["packSlug"] = "CF Demo Pack",
                ["versionNumber"] = "2.0",
                ["title"] = "CF Demo Pack",
                ["versionsRoot"] = Path.Combine(_root, "versions"),
                ["librariesRoot"] = Path.Combine(_root, "libraries"),
                ["assetsRoot"] = Path.Combine(_root, "assets"),
                ["instancesRoot"] = Path.Combine(_root, "instances"),
                ["instanceDir"] = _root,
            }.ToString();
            var task = queue.Enqueue(new DownloadTaskRecord { Name = "CF Demo Pack", Kind = "modpack-install", Meta = meta });
            await WaitForStatusAsync(store, task.TaskId, DownloadTaskStatus.Completed);

            // overrides + client-overrides 全部落地共享根
            Assert.True(File.Exists(Path.Combine(_root, "mods", "cf-mod.jar")));
            Assert.True(File.Exists(Path.Combine(_root, "kubejs", "startup_scripts", "cf.js")));
            Assert.True(File.Exists(Path.Combine(_root, "config", "option.toml")));
            // 版本 json 带 fuseModpack 标记并继承原版底座
            var json = File.ReadAllText(Path.Combine(_root, "versions", "cfdemopack-2.0", "cfdemopack-2.0.json"));
            Assert.Contains("fuseModpack", json);
            Assert.Contains("\"inheritsFrom\": \"1.20.1\"", json);

            var staged = IsolationStager.EnsureStaged(_root, "cfdemopack-2.0", _install);
            Assert.Contains("kubejs", staged.Junctions);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ImportToSharedRoot_ThenStaged_ContentVisible_AndDeleteSafe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcdl-imp2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 预置原版底座 json,跳过 Mojang 清单网络步骤(沙盒测试只关心导入/隔离/删除)
            var gvDir = Path.Combine(_root, "versions", "1.20.1");
            Directory.CreateDirectory(gvDir);
            File.WriteAllText(Path.Combine(gvDir, "1.20.1.json"), """{"id":"1.20.1","type":"release"}""");

            var mrpack = BuildPack(Path.Combine(dir, "demo-pack.mrpack"));
            var store = new DownloadTaskStore(Path.Combine(dir, "tasks.json"));
            using var http = new HttpClient();
            var queue = DownloadQueue.CreateDefault(store, http, maxConcurrent: 2, smallFileParallelism: 2);

            var meta = new JObject
            {
                ["mrpackLocalPath"] = mrpack,
                ["packSlug"] = "demo-pack",
                ["versionNumber"] = "1.0",
                ["title"] = "Demo Pack",
                ["versionsRoot"] = Path.Combine(_root, "versions"),
                ["librariesRoot"] = Path.Combine(_root, "libraries"),
                ["assetsRoot"] = Path.Combine(_root, "assets"),
                ["instancesRoot"] = Path.Combine(_root, "instances"),
                ["instanceDir"] = _root,   // 外部共享布局:实例目录就是根
            }.ToString();
            var task = queue.Enqueue(new DownloadTaskRecord { Name = "Demo Pack", Kind = "modpack-install", Meta = meta });
            await WaitForStatusAsync(store, task.TaskId, DownloadTaskStatus.Completed);

            // overrides 全量落地共享根(香草纪元事故第一环:不是导入丢文件)
            Assert.True(File.Exists(Path.Combine(_root, "mods", "demo-mod.jar")));
            Assert.True(File.Exists(Path.Combine(_root, "kubejs", "startup_scripts", "main.js")));
            Assert.True(File.Exists(Path.Combine(_root, "config", "demo.toml")));
            Assert.True(File.Exists(Path.Combine(_root, "schematics", "house.schema")));

            // 版本 json 带 fuseModpack 标记
            var jsonPath = Path.Combine(_root, "versions", "demo-pack-1.0", "demo-pack-1.0.json");
            Assert.True(File.Exists(jsonPath));
            Assert.Contains("fuseModpack", File.ReadAllText(jsonPath));

            // staging:generic 目录(kubejs/schematics)必须被链进版本目录
            var staged = IsolationStager.EnsureStaged(_root, "demo-pack-1.0", _install);
            Assert.Contains("kubejs", staged.Junctions);
            Assert.Contains("schematics", staged.Junctions);
            Assert.True(File.Exists(Path.Combine(_root, "versions", "demo-pack-1.0", "kubejs", "startup_scripts", "main.js")));

            // 删除版本:只删版本目录,共享内容(含 kubejs)完好
            var instDir = InstanceLayout.ResolveExternal(_root, "demo-pack-1.0");
            Assert.Equal(Path.Combine(_root, "versions", "demo-pack-1.0"), instDir);
            foreach (var t in VersionDeletion.PlanDirectories(_root, "demo-pack-1.0", instDir))
                SafeDirectory.DeleteTree(t, _root);
            Assert.True(File.Exists(Path.Combine(_root, "kubejs", "startup_scripts", "main.js")));
            Assert.True(File.Exists(Path.Combine(_root, "mods", "demo-mod.jar")));
            Assert.False(Directory.Exists(Path.Combine(_root, "versions", "demo-pack-1.0")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
