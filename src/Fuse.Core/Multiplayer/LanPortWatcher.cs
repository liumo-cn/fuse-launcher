using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Multiplayer;

/// <summary>一次日志扫描的结果:公网地址(mod 打的)与局域网端口。</summary>
public sealed record LanScanResult((string Host, int Port)? Port, string? ModAddress);

/// <summary>
/// 从实例的 latest.log 里抓"对局域网开放"的端口。
/// 集成服务器开放时会打印监听行;各版本措辞不同,按模式逐个匹配。
/// 也负责抓联机 mod 打到聊天/日志里的公网地址(e4mc 的 xxx.e4mc.link 等)。
/// </summary>
public sealed class LanPortWatcher : IDisposable
{
    private static readonly Regex[] PortPatterns =
    {
        // 1.20.2+/26.x 集成服务器开放局域网的新措辞
        new("Published LAN server on port (\\d+)", RegexOptions.Compiled),
        new("Listening on /0\\.0\\.0\\.0:(\\d+)", RegexOptions.Compiled),
        new("Started serving on /(\\d+\\.\\d+\\.\\d+\\.\\d+):(\\d+)", RegexOptions.Compiled),
        new("Starting to serve .* on port (\\d+)", RegexOptions.Compiled),
        new("\\[CHAT\\] .*?(?:端口|port)[:：]\\s*(\\d{2,5})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    /// <summary>联机 mod 会往聊天/日志里打公网地址,按域名/地址特征抓。子域允许多级(e4mc 中转: xxx.jp.e4mc.link)。</summary>
    private static readonly Regex[] ModAddressPatterns =
    {
        new("\\b([a-z0-9\\-]+(?:\\.[a-z0-9\\-]+)*\\.e4mc\\.link)\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new("\\b([a-z0-9\\-]+(?:\\.[a-z0-9\\-]+)*\\.playit\\.gg)\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new("\\b((?:\\d{1,3}\\.){3}\\d{1,3}:\\d{2,5})\\b", RegexOptions.Compiled),
        new("\\b(\\[[0-9a-fA-F:]+\\]:\\d{2,5})\\b", RegexOptions.Compiled),
    };

    private readonly string _logPath;
    private readonly int _pollMs;
    private long _lastLength;
    private bool _seeded;
    private string _pendingTail = "";

    public LanPortWatcher(string logPath, int pollMs = 800)
    {
        _logPath = logPath;
        _pollMs = pollMs;
    }

    /// <summary>
    /// 轮询等待端口/公网地址出现;超时返回最后一次扫描结果(null = 没抓到)。
    /// alivePort:判断端口是否真的在监听——日志尾部的旧记录(上一局开的)必须跳过。
    /// </summary>
    public async Task<LanScanResult> WaitForAsync(int timeoutMs, CancellationToken ct = default, Func<int, bool>? alivePort = null)
    {
        var deadline = UtcNowMs() + timeoutMs;
        var last = new LanScanResult(null, null);
        while (UtcNowMs() < deadline && !ct.IsCancellationRequested)
        {
            last = Scan(ReadTail());
            if (IsUsable(last, alivePort)) return last;
            await Task.Delay(_pollMs, ct).ConfigureAwait(false);
        }
        return Scan(ReadTail());
    }

    /// <summary>匹配到端口但端口已不监听 = 上一局的旧日志,当作没匹配继续等。</summary>
    private static bool IsUsable(LanScanResult r, Func<int, bool>? alivePort)
    {
        if (r.Port == null && r.ModAddress == null) return false;
        if (r.Port != null && alivePort != null && !alivePort(r.Port.Value.Port)) return false;
        return true;
    }

    /// <summary>不等待,立刻扫一次当前日志。</summary>
    public LanScanResult TryScanOnce() => Scan(ReadTail());

    private LanScanResult Scan(string content)
    {
        // 都取"最后一个"匹配:日志里可能同时留着上一局(旧)和本局(新)的开放记录,
        // 最近一次开放才是当前房间;配合 WaitForAsync 的存活过滤器把旧端口淘汰掉
        (string Host, int Port)? port = null;
        foreach (var line in content.Split('\n'))
        {
            foreach (var regex in PortPatterns)
            {
                var m = regex.Match(line);
                if (m.Success && int.TryParse(m.Groups[m.Groups.Count - 1].Value, out var p)
                    && p >= 1024 && p <= 65535)
                {
                    var host = m.Groups.Count > 2 ? m.Groups[1].Value : "127.0.0.1";
                    port = (host, p);
                }
            }
        }

        string? modAddress = null;
        foreach (var line in content.Split('\n'))
        {
            // e4mc 的握手/调度行里也有 *.e4mc.link 域名,但那是 broker 不是房间地址
            // (房间地址在 "Domain assigned:" 与聊天行的"托管在域名[xxx]上")
            if (line.IndexOf("broker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("relaymap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("natives.e4mc", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            foreach (var regex in ModAddressPatterns)
            {
                var m = regex.Match(line);
                if (!m.Success) continue;
                var candidate = m.Groups[1].Value;
                // 局域网监听行也会被"IP:端口"模式命中(0.0.0.0:51234),这不是公网地址,排除内网/回环
                var colon = candidate.LastIndexOf(':');
                var hostPart = candidate.StartsWith("[")
                    ? candidate.Substring(1, Math.Max(colon, 1) - 2)
                    : candidate.Substring(0, colon > 0 ? colon : candidate.Length);
                if (System.Net.IPAddress.TryParse(hostPart.Trim('[', ']'), out var ip) &&
                    (System.Net.IPAddress.IsLoopback(ip) || IsPrivateIp(ip)))
                    continue;
                modAddress = candidate;
                break;
            }
        }
        return new LanScanResult(port, modAddress);
    }

    private static bool IsPrivateIp(System.Net.IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 169 && bytes[1] == 254);
        if (bytes.Length == 16)
        {
            // 链路本地 fe80::/10、ULA fc00::/7、未指定 ::
            return bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80
                || (bytes[0] & 0xFE) == 0xFC
                || bytes.All(b => b == 0);
        }
        return false;
    }

    /// <summary>增量读日志尾部(FileShare.ReadWrite,游戏运行时也能读),拼接已见内容防断行。</summary>
    private string ReadTail()
    {
        try
        {
            if (!File.Exists(_logPath)) return _pendingTail;
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!_seeded)
            {
                // 首次直接从尾部 256KB 开始:游戏往往先开了局域网、后点的创建房间,
                // 从头爬既慢又会错过"已经发生"的开放事件
                _lastLength = Math.Max(0, fs.Length - 256 * 1024);
                _seeded = true;
            }
            if (fs.Length < _lastLength)
            {
                // 日志被轮转(新一局):重置
                _lastLength = 0;
                _pendingTail = "";
            }
            if (fs.Length == _lastLength) return _pendingTail;
            fs.Position = _lastLength;
            var size = (int)Math.Min(fs.Length - _lastLength, 256 * 1024);
            var buffer = new byte[size];
            var read = 0;
            while (read < size)
            {
                var n = fs.Read(buffer, read, size - read);
                if (n <= 0) break;
                read += n;
            }
            _lastLength += read;
            _pendingTail += System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            if (_pendingTail.Length > 64 * 1024)
                _pendingTail = _pendingTail.Substring(_pendingTail.Length - 64 * 1024);
            return _pendingTail;
        }
        catch (IOException)
        {
            return _pendingTail;
        }
        catch (UnauthorizedAccessException)
        {
            return _pendingTail;
        }
    }

    /// <summary>UTC 毫秒(netstandard2.0 没有 TickCount64)。</summary>
    private static long UtcNowMs() => (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;

    public void Dispose()
    {
        _pendingTail = "";
    }
}

/// <summary>日志尾部读取小工具(崩溃分析/启动检测共用语义)。</summary>
public static class LogTail
{
    public static string Read(string path, int maxBytes = 256 * 1024)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var size = (int)Math.Min(fs.Length, maxBytes);
            fs.Position = fs.Length - size;
            var buffer = new byte[size];
            var read = 0;
            while (read < size)
            {
                var n = fs.Read(buffer, read, size - read);
                if (n <= 0) break;
                read += n;
            }
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception)
        {
            return "";
        }
    }
}
