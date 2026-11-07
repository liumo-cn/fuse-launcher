using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Fuse.App;

/// <summary>UPnP 映射结果。</summary>
public sealed record UpnpResult(bool Success, int ExternalPort, string? Error);

/// <summary>
/// UPnP 端口映射(NATUPnP 1.0 Type Library,系统自带 COM,零外部依赖)。
/// 世界开放期间加映射,游戏退出必删(Stop 由调用方保证)。
/// </summary>
internal sealed class UpnpSupport : IDisposable
{
    private const string Description = "Fuse Launcher (Minecraft 联机)";
    private int _mappedExternalPort;

    /// <summary>尝试加映射:外部随机高端口 → 本机内网IP:内部端口(TCP)。成功返回外部端口。</summary>
    public UpnpResult AddMapping(int internalPort)
    {
        RemoveMapping();
        try
        {
            var nat = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.NATUPnP")!);
            var mappings = nat?.GetType().InvokeMember("StaticPortMappingCollection",
                System.Reflection.BindingFlags.GetProperty, null, nat, null);
            if (mappings == null)
                return new UpnpResult(false, 0, "路由器不支持 UPnP 或已关闭(双层 NAT 也会这样)");

            var localIp = Fuse.Core.Multiplayer.Ipv6Probe.GetLocalIPv4();
            if (string.IsNullOrEmpty(localIp))
                return new UpnpResult(false, 0, "取不到本机内网 IPv4 地址");

            var rnd = new Random();
            var external = rnd.Next(49152, 65535);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var port = attempt == 0 ? external : rnd.Next(49152, 65535);
                try
                {
                    mappings.GetType().InvokeMember("Add",
                        System.Reflection.BindingFlags.InvokeMethod, null, mappings, new object?[]
                        {
                            port,                 // lExternalPort
                            "TCP",                // bstrProtocol
                            internalPort,         // lInternalPort
                            localIp,              // bstrInternalClient
                            true,                 // bEnabled
                            Description,          // bstrDescription
                        });
                    _mappedExternalPort = port;
                    return new UpnpResult(true, port, null);
                }
                catch (Exception ex)
                {
                    // 端口被占/失败:换一个再试
                    if (attempt == 9)
                        return new UpnpResult(false, 0, "UPnP 添加映射失败: " + Short(ex.Message));
                }
            }
            return new UpnpResult(false, 0, "UPnP 添加映射失败");
        }
        catch (Exception ex)
        {
            return new UpnpResult(false, 0, "UPnP 不可用: " + Short(ex.Message));
        }
    }

    /// <summary>删除本类加过的映射(只删自己加的,按端口+描述匹配)。</summary>
    public void RemoveMapping()
    {
        if (_mappedExternalPort == 0) return;
        try
        {
            var nat = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.NATUPnP")!);
            var mappings = nat?.GetType().InvokeMember("StaticPortMappingCollection",
                System.Reflection.BindingFlags.GetProperty, null, nat, null);
            mappings?.GetType().InvokeMember("Remove",
                System.Reflection.BindingFlags.InvokeMethod, null, mappings, new object?[]
                {
                    _mappedExternalPort, "TCP",
                });
        }
        catch (Exception)
        {
            // 映射可能已被路由器清掉;尽力而为
        }
        finally
        {
            _mappedExternalPort = 0;
        }
    }

    public void Dispose() => RemoveMapping();

    private static string Short(string message)
    {
        message = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return message.Length > 120 ? message.Substring(0, 120) : message;
    }
}

/// <summary>Windows 防火墙:确认 javaw 有 TCP 入站规则(公用网络配置尤其会拦朋友)。</summary>
internal static class FirewallSupport
{
    private const string RuleName = "Fuse Launcher (Minecraft)";

    public static bool RuleExists()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=\"" + RuleName + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output.Contains(RuleName) && !output.Contains("没有与指定标准匹配的规则");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>为指定 javaw 路径加 TCP 入站规则(需要管理员,弹 UAC;失败不抛)。</summary>
    public static Task<bool> EnsureRuleAsync(string javawPath)
    {
        if (RuleExists()) return Task.FromResult(true);
        try
        {
            var args = "/C netsh advfirewall firewall add rule name=\"" + RuleName +
                "\" dir=in action=allow protocol=TCP program=\"" + javawPath + "\" enable=yes profile=any";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas", // 触发 UAC
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var p = Process.Start(psi);
            if (p == null) return Task.FromResult(false);
            return Task.Run(() =>
            {
                try
                {
                    p.WaitForExit(30000);
                    return RuleExists();
                }
                catch (Exception)
                {
                    return false;
                }
            });
        }
        catch (Exception)
        {
            // 用户点了 UAC 取消等
            return Task.FromResult(false);
        }
    }
}
