using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Multiplayer;

/// <summary>frp 隧道平台的预设。启动器不提供任何服务器,这里只是托管用户自己账号的第三方客户端。</summary>
public sealed record TunnelPreset(string Id, string Title, string GuideUrl, string ArgNote)
{
    /// <summary>樱花FRP:一条命令 frpc -f 访问密钥:隧道ID(密钥/ID 在其面板复制)。</summary>
    public static readonly TunnelPreset SakuraFrp = new("sakura", "樱花内网穿透（SakuraFrp）",
        "https://www.natfrp.com/tunnel/", "-f 访问密钥:隧道ID");

    /// <summary>OpenFrp/自建 frps:面板生成配置文本,启动器写文件后 frpc -c 配置。</summary>
    public static readonly TunnelPreset OpenFrp = new("openfrp", "OpenFrp / 自建 frps",
        "https://www.openfrp.net/console/launch", "-c 配置文件（面板复制配置文本，localPort 会自动改成本次端口）");
}

/// <summary>隧道运行状态。</summary>
public enum TunnelStatus { Idle, Starting, Connected, Exited, Failed }

/// <summary>
/// 托管第三方 frpc 客户端进程:生成/写配置、拉起进程、按输出判定"隧道已通"。
/// 凭据只保存在用户本机内存/文件,启动器不做平台登录、不存云端。
/// </summary>
public sealed class TunnelRunner : IDisposable
{
    /// <summary>frpc 输出里出现这些才算"穿透成功"。</summary>
    private static readonly Regex[] SuccessPatterns =
    {
        new("start proxy success", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new("proxy .* (?:added|started)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new("(?:登录|启动|连接)成功", RegexOptions.Compiled),
    };

    private readonly TunnelPreset _preset;
    private readonly string _exePath;
    private Process? _process;
    private readonly StringBuilder _output = new();
    
    public TunnelStatus Status { get; private set; } = TunnelStatus.Idle;
    public string? LastError { get; private set; }
    public string ConfigPath { get; private set; } = "";
    public event Action<TunnelStatus>? StatusChanged;

    public TunnelRunner(TunnelPreset preset, string exePath)
    {
        _preset = preset;
        _exePath = exePath;
    }

    /// <summary>
    /// 启动隧道。樱花: token:id;OpenFrp/自建: 面板复制的配置文本(会替换 localPort 为本次 LAN 端口)。
    /// </summary>
    public bool Start(string argsOrConfig, int localPort)
    {
        if (!File.Exists(_exePath))
        {
            LastError = "frpc 程序不存在: " + _exePath;
            return false;
        }
        Stop();

        var arguments = _preset.Id == "sakura"
            ? "-f " + argsOrConfig.Trim()
            : "-c \"" + WriteConfig(argsOrConfig, localPort) + "\"";

        var start = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_exePath) ?? "",
        };
        try
        {
            _process = Process.Start(start);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = TunnelStatus.Failed;
            StatusChanged?.Invoke(Status);
            return false;
        }
        if (_process == null)
        {
            LastError = "frpc 无法启动";
            Status = TunnelStatus.Failed;
            StatusChanged?.Invoke(Status);
            return false;
        }

        Status = TunnelStatus.Starting;
        StatusChanged?.Invoke(Status);
        

        ListenOutput(_process.StandardOutput);
        ListenOutput(_process.StandardError);
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            Status = Status == TunnelStatus.Connected ? TunnelStatus.Exited : TunnelStatus.Failed;
            StatusChanged?.Invoke(Status);
        };

        // 8 秒兜底:还没成功也没退出 → 看输出;退出了 → 失败
        _ = Task.Run(() =>
        {
            Thread.Sleep(8000);
            var proc = _process;
            if (Status != TunnelStatus.Starting || proc == null) return;
            var text = _output.ToString();
            if (SuccessPatterns.Any(r => r.IsMatch(text)))
            {
                Status = TunnelStatus.Connected;
                StatusChanged?.Invoke(Status);
                return;
            }
            try
            {
                if (proc.HasExited)
                {
                    Status = TunnelStatus.Failed;
                    LastError = "frpc 已退出（" + LastLine(text) + "）";
                    StatusChanged?.Invoke(Status);
                }
            }
            catch (Exception)
            {
            }
        });
        return true;
    }

    /// <summary>写配置文件(替换 localPort),返回路径。占位符 {{LOCAL_PORT}} 也会被替换。</summary>
    public string WriteConfig(string configText, int localPort)
    {
        var text = Regex.Replace(configText, @"(?m)^(\s*localPort\s*=\s*)\d+", "${1}" + localPort);
        text = text.Replace("{{LOCAL_PORT}}", localPort.ToString());
        // localIp 常见默认值是 127.0.0.1,保持不动
        var dir = Path.Combine(Path.GetTempPath(), "FuseLauncher", "tunnel");
        Directory.CreateDirectory(dir);
        ConfigPath = Path.Combine(dir, "frpc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".ini");
        File.WriteAllText(ConfigPath, text, new System.Text.UTF8Encoding(false));
        return ConfigPath;
    }

    /// <summary>从配置文本里读出远端地址(host:port),生成邀请码用。</summary>
    public static string? ReadRemoteAddress(string configText)
    {
        var server = Regex.Match(configText, @"(?m)^\s*serverAddr\s*=\s*""?([^""\r\n]+)""?");
        if (!server.Success)
            server = Regex.Match(configText, @"(?m)^\s*server_addr\s*=\s*([^ \t\r\n]+)");
        var port = Regex.Match(configText, @"(?m)^\s*remotePort\s*=\s*(\d+)");
        if (!port.Success)
            port = Regex.Match(configText, @"(?m)^\s*remote_port\s*=\s*(\d+)");
        if (!server.Success || !port.Success) return null;
        return server.Groups[1].Value.Trim() + ":" + port.Groups[1].Value;
    }

    private void ListenOutput(StreamReader reader)
    {
        _ = Task.Run(() =>
        {
            try
            {
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;
                    lock (_output)
                    {
                        _output.AppendLine(line);
                        if (_output.Length > 32 * 1024) _output.Remove(0, _output.Length - 32 * 1024);
                    }
                    if (SuccessPatterns.Any(r => r.IsMatch(line)))
                    {
                        Status = TunnelStatus.Connected;
                        StatusChanged?.Invoke(Status);
                    }
                }
            }
            catch (Exception)
            {
            }
        });
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n');
        return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            if (Status != TunnelStatus.Idle)
            {
                Status = TunnelStatus.Idle;
                StatusChanged?.Invoke(Status);
            }
            try
            {
                if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            }
            catch (Exception)
            {
            }
        }
    }

    public string RecentOutput()
    {
        lock (_output) return _output.ToString();
    }

    public void Dispose() => Stop();
}
