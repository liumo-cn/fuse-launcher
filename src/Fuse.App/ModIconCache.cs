using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

namespace Fuse.App;

/// <summary>
/// 本地已安装文件(模组/光影包/材质包)的图标缓存。
/// 首次:后台按文件 SHA-1 去 Modrinth 反查项目并下载图标,落盘到 %APPDATA%\FuseLauncher\iconcache;
/// 之后:路径+大小+修改时间 → SHA-1 的索引直接从磁盘命中,打开本地页同步铺图标,不等网络。
/// </summary>
internal static class ModIconCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, BitmapSource> IconsByHash = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> HashByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Border>> Waiting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim DownloadGate = new(4);
    private static bool _indexLoaded;
    private static bool _batchRunning;
    private static string? _cacheDir;

    private static string CacheDir
    {
        get
        {
            if (_cacheDir != null) return _cacheDir;
            var dir = Path.Combine(AppServices.AppDataDir, "iconcache");
            try { Directory.CreateDirectory(dir); } catch (Exception) { }
            _cacheDir = dir;
            return dir;
        }
    }

    /// <summary>给一行列表挂图标:缓存命中时同步生效,未命中先放首字母块随后台补齐。</summary>
    public static void Attach(Border host, string filePath, Dispatcher dispatcher)
    {
        var title = Path.GetFileNameWithoutExtension(filePath);
        host.Child = Letter(title);
        LoadIndex();

        FileInfo info;
        try { info = new FileInfo(filePath); } catch (Exception) { return; }
        if (!info.Exists) return;

        var key = filePath + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        string? hash;
        lock (Gate) HashByPath.TryGetValue(key, out hash);

        if (hash != null)
        {
            var cached = LoadCachedIcon(hash);
            if (cached != null)
            {
                host.Child = MakeImage(cached);
                return;
            }
            if (HasMiss(hash)) return;
            Queue(hash, host, dispatcher);
            return;
        }

        // 没算过哈希:后台算,算完能命中磁盘缓存就直接上图标(不再打网络)
        _ = HashThenResolveAsync(key, filePath, host, dispatcher);
    }

    private static async Task HashThenResolveAsync(string key, string filePath, Border host, Dispatcher dispatcher)
    {
        string? hash = null;
        try { hash = await Task.Run(() => ComputeSha1(filePath)).ConfigureAwait(false); }
        catch (Exception) { }
        if (hash == null) return;

        lock (Gate) HashByPath[key] = hash;
        PersistHash(hash, key);

        var icon = LoadCachedIcon(hash);
        if (icon != null)
        {
            await dispatcher.InvokeAsync(new Action(() =>
            {
                if (host.Parent != null) host.Child = MakeImage(icon);
            }));
            return;
        }
        if (HasMiss(hash)) return;
        Queue(hash, host, dispatcher);
    }

    private static void Queue(string hash, Border host, Dispatcher dispatcher)
    {
        lock (Gate)
        {
            if (!Waiting.TryGetValue(hash, out var hosts))
                Waiting[hash] = hosts = new List<Border>();
            hosts.Add(host);
            if (_batchRunning) return;
            _batchRunning = true;
        }
        _ = Task.Run(() => BatchLoopAsync(dispatcher));
    }

    private static async Task BatchLoopAsync(Dispatcher dispatcher)
    {
        // 稍等一会儿,让整页行都排进来,合并成一次批量反查
        await Task.Delay(200).ConfigureAwait(false);
        while (true)
        {
            List<KeyValuePair<string, List<Border>>> batch;
            lock (Gate)
            {
                if (Waiting.Count == 0)
                {
                    _batchRunning = false;
                    return;
                }
                batch = Waiting.Take(80).ToList();
                foreach (var pair in batch) Waiting.Remove(pair.Key);
            }

            try { await ResolveBatchAsync(batch, dispatcher).ConfigureAwait(false); }
            catch (Exception) { }
            // 请求失败的行不标 miss,下次打开会再试
        }
    }

    private static async Task ResolveBatchAsync(
        List<KeyValuePair<string, List<Border>>> batch, Dispatcher dispatcher)
    {
        var hashes = batch.Select(p => p.Key).ToList();
        var body = new JObject
        {
            ["algorithm"] = "sha1",
            ["hashes"] = new JArray(hashes),
        };
        using var content = new StringContent(
            body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        using var response = await AppServices.Http
            .PostAsync("https://api.modrinth.com/v2/version_files", content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = JObject.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        var projectByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in payload.Properties())
        {
            var projectId = (string?)prop.Value["project_id"];
            if (!string.IsNullOrEmpty(projectId)) projectByHash[prop.Name] = projectId!;
        }
        foreach (var hash in hashes)
            if (!projectByHash.ContainsKey(hash)) MarkMiss(hash);

        var iconsByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = projectByHash.Values.Distinct().ToList();
        if (ids.Count > 0)
        {
            var idsJson = "[" + string.Join(",", ids.Select(id => "\"" + id + "\"")) + "]";
            var url = "https://api.modrinth.com/v2/projects?ids=" + Uri.EscapeDataString(idsJson);
            var text = await AppServices.Http.GetStringAsync(url).ConfigureAwait(false);
            foreach (var project in JArray.Parse(text))
            {
                var id = (string?)project["id"];
                var iconUrl = (string?)project["icon_url"];
                if (id != null && !string.IsNullOrEmpty(iconUrl)) iconsByProject[id] = iconUrl!;
            }
        }

        var running = new List<Task>();
        foreach (var pair in batch)
        {
            if (!projectByHash.TryGetValue(pair.Key, out var projectId) ||
                !iconsByProject.TryGetValue(projectId, out var iconUrl))
            {
                MarkMiss(pair.Key);
                continue;
            }
            running.Add(DownloadIconAsync(pair.Key, iconUrl, pair.Value, dispatcher));
            if (running.Count >= 4)
            {
                await Task.WhenAll(running).ConfigureAwait(false);
                running.Clear();
            }
        }
        if (running.Count > 0) await Task.WhenAll(running).ConfigureAwait(false);
    }

    private static async Task DownloadIconAsync(
        string hash, string iconUrl, List<Border> hosts, Dispatcher dispatcher)
    {
        await DownloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await AppServices.Http.GetByteArrayAsync(iconUrl).ConfigureAwait(false);
            var icon = Decode(bytes);
            if (icon == null)
            {
                MarkMiss(hash);
                return;
            }
            try { File.WriteAllBytes(Path.Combine(CacheDir, hash + ".png"), bytes); } catch (Exception) { }
            lock (Gate) IconsByHash[hash] = icon;

            await dispatcher.InvokeAsync(new Action(() =>
            {
                foreach (var host in hosts)
                {
                    if (host.Parent != null) host.Child = MakeImage(icon);
                }
            }));
        }
        catch (Exception)
        {
            // 网络失败不标记 miss,下次打开重试
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    // ---------- 磁盘缓存 ----------

    private static void LoadIndex()
    {
        if (_indexLoaded) return;
        _indexLoaded = true;
        try
        {
            var index = Path.Combine(CacheDir, "hashes.txt");
            if (!File.Exists(index)) return;
            foreach (var line in File.ReadAllLines(index))
            {
                // 格式: <sha1>\t<路径|长度|修改时间ticks>
                var tab = line.IndexOf('\t');
                if (tab <= 0 || tab >= line.Length - 1) continue;
                var hash = line.Substring(0, tab);
                var key = line.Substring(tab + 1);
                lock (Gate) HashByPath[key] = hash;
            }
        }
        catch (Exception)
        {
        }
    }

    private static void PersistHash(string hash, string key)
    {
        try
        {
            var index = Path.Combine(CacheDir, "hashes.txt");
            File.AppendAllText(index, hash + "\t" + key + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }

    private static BitmapSource? LoadCachedIcon(string hash)
    {
        lock (Gate)
            if (IconsByHash.TryGetValue(hash, out var cached)) return cached;
        try
        {
            var file = Path.Combine(CacheDir, hash + ".png");
            if (!File.Exists(file)) return null;
            var icon = Decode(File.ReadAllBytes(file));
            if (icon != null) lock (Gate) IconsByHash[hash] = icon;
            return icon;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasMiss(string hash)
    {
        try { return File.Exists(Path.Combine(CacheDir, hash + ".none")); }
        catch (Exception) { return false; }
    }

    private static void MarkMiss(string hash)
    {
        try { File.WriteAllBytes(Path.Combine(CacheDir, hash + ".none"), Array.Empty<byte>()); }
        catch (Exception) { }
    }

    // ---------- 工具 ----------

    private static string? ComputeSha1(string path)
    {
        using var sha = SHA1.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        var bytes = sha.ComputeHash(stream);
        var text = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) text.Append(b.ToString("x2"));
        return text.ToString();
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 72;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Image MakeImage(BitmapSource source) => new()
    {
        Source = source,
        Width = 32,
        Height = 32,
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Letter(string title) => new()
    {
        Text = string.IsNullOrEmpty(title) ? "?" : title.Substring(0, 1).ToUpperInvariant(),
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
