using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Fuse.Core.Config;

/// <summary>单个版本的独立配置(显示名/Java/内存),只对该版本生效。</summary>
public sealed class VersionProfile
{
    /// <summary>显示名(别名),底层 id 不动。</summary>
    public string? DisplayName { get; set; }
    /// <summary>"auto" 或 "manual"。</summary>
    public string JavaPolicy { get; set; } = "auto";
    public string? JavaHomeOverride { get; set; }
    /// <summary>该版本专属内存上限(MB);0 = 跟随全局设置(否则全局滑块永远被这里的默认值覆盖)。</summary>
    public long MaxHeapMb { get; set; }
    /// <summary>该版本专属的额外 JVM 参数(空格分隔,支持双引号);null/空 = 不加。</summary>
    public string? JvmArgs { get; set; }
    /// <summary>启动该版本时自动加入的服务器地址(host 或 host:port);null/空 = 正常打开游戏。</summary>
    public string? AutoJoinServer { get; set; }
}

/// <summary>
/// 每版本配置存取(data/profiles.json):键 = 版本 id。
/// 启动管线优先级:每版本配置 > 全局设置(无则回退)。
/// </summary>
public sealed class VersionProfiles
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, VersionProfile> _profiles = new(StringComparer.Ordinal);

    public VersionProfiles(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    _profiles = JsonConvert.DeserializeObject<Dictionary<string, VersionProfile>>(
                                        File.ReadAllText(_path))
                                    ?? new Dictionary<string, VersionProfile>(StringComparer.Ordinal);
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(_profiles, Formatting.Indented));
        if (File.Exists(_path)) File.Delete(_path);
        File.Move(tmp, _path);
    }

    public VersionProfile Get(string versionId)
    {
        lock (_lock)
        {
            if (!_profiles.TryGetValue(versionId, out var p))
            {
                p = new VersionProfile();
                _profiles[versionId] = p;
            }
            return p;
        }
    }

    public void Set(string versionId, VersionProfile profile)
    {
        lock (_lock)
        {
            _profiles[versionId] = profile;
            Save();
        }
    }

    /// <summary>删除版本时一并清掉它的专属配置。</summary>
    public void Remove(string versionId)
    {
        lock (_lock)
        {
            if (_profiles.Remove(versionId)) Save();
        }
    }
}
