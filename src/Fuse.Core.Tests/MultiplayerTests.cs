using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Fuse.Core.Multiplayer;
using Fuse.Core.Versioning;
using Xunit;

namespace Fuse.Core.Tests;

public class InviteCodecTests
{
    [Fact]
    public void BuildAndParseRoundTrip()
    {
        var link = InviteCodec.Build("203.0.113.7:51347", "小明的房间", "1.20.1", "tok123");
        Assert.StartsWith("fuse-launcher://join?v=1&addr=203.0.113.7%3A51347", link);
        var info = InviteCodec.Parse(link);
        Assert.NotNull(info);
        Assert.Equal("203.0.113.7", info!.Host);
        Assert.Equal(51347, info.Port);
        Assert.Equal("小明的房间", info.RoomName);
        Assert.Equal("1.20.1", info.GameVersion);
        Assert.Equal("tok123", info.Token);
    }

    [Fact]
    public void ParsesIpv6WithBrackets()
    {
        var info = InviteCodec.Parse("fuse-launcher://join?addr=%5B240e%3A56%3A4%3A%3A9%5D%3A25565");
        Assert.NotNull(info);
        Assert.Equal(25565, info!.Port);
        Assert.Equal("[240e:56:4::9]:25565", info.Address);
    }

    [Fact]
    public void ParseRejectsInjectionHosts()
    {
        foreach (var bad in new[]
                 {
                     "fuse-launcher://join?addr=evil.com%2F..%2F..%3A25565",
                     "fuse-launcher://join?addr=%22-x%20-del%20x%22%3A25565",
                     "fuse-launcher://join?addr=host%3A99999",
                     "fuse-launcher://join?addr=host%3A0",
                 })
        {
            Assert.Null(InviteCodec.Parse(bad));
        }
    }

    [Fact]
    public void AcceptsBareAddressWithNoise()
    {
        var info = InviteCodec.Parse("房间地址是 203.0.113.7:51347 记得用1.20.1进");
        Assert.NotNull(info);
        Assert.Equal("203.0.113.7", info!.Host);
        Assert.Equal(51347, info.Port);
    }

    [Fact]
    public void SplitAddressIpv6AndDefaultPort()
    {
        var (h1, p1) = InviteCodec.SplitAddress("[::1]:25575");
        Assert.Equal("::1", h1);
        Assert.Equal(25575, p1);
        var (h2, p2) = InviteCodec.SplitAddress("mc.example.com");
        Assert.Equal("mc.example.com", h2);
        Assert.Equal(25565, p2);
    }
}

public class ServerListPingTests
{
    [Theory]
    [InlineData(0, new byte[] { 0 })]
    [InlineData(1, new byte[] { 1 })]
    [InlineData(-1, new byte[] { 255, 255, 255, 255, 15 })]
    [InlineData(255, new byte[] { 255, 1 })]
    [InlineData(2147483647, new byte[] { 255, 255, 255, 255, 7 })]
    public void VarIntRoundTrip(int value, byte[] expected)
    {
        var encoded = ServerListPing.VarInt(value);
        Assert.Equal(expected, encoded);
        var pos = 0;
        Assert.Equal(value, ServerListPing.ReadVarInt(encoded, ref pos));
    }

    [Fact]
    public void ParsesVanillaStatusJson()
    {
        const string json = @"{
  ""version"": { ""name"": ""1.20.1"", ""protocol"": 763 },
  ""players"": { ""online"": 3, ""max"": 8 },
  ""description"": ""小明的房间\n欢迎来玩"",
  ""favicon"": ""data:image/png;base64,AAAA""
}";
        var status = ServerListPing.ParseStatus(json, 42);
        Assert.Equal(763, status.Protocol);
        Assert.Equal("1.20.1", status.VersionName);
        Assert.Equal(3, status.PlayersOnline);
        Assert.Equal(8, status.PlayersMax);
        Assert.Contains("欢迎来玩", status.Motd);
        Assert.Equal(42, status.PingMs);
    }

    [Fact]
    public void ParsesComponentMotd()
    {
        const string json = @"{
  ""description"": { ""extra"": [ { ""text"": ""欢迎"" }, { ""text"": ""加入"", ""color"": ""green"" } ], ""text"": ""前缀"" }
}";
        var status = ServerListPing.ParseStatus(json, 1);
        Assert.Equal("前缀欢迎加入", status.Motd);
    }

    /// <summary>起一个说 SLP 协议的假服,端到端验证握手/读包。</summary>
    [Fact]
    public async Task PingAsyncAgainstFakeServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var statusJson = @"{""version"":{""name"":""fake 26.1"",""protocol"":-1},""players"":{""online"":2,""max"":10},""description"":""测试房""}";

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            stream.Read(ReadFrame(stream), 0, 1); // 读掉握手帧(长度字节即可)
            var body = ReadFrame(stream);
            // status 响应: 包id=0 + 字符串
            var jsonBytes = Encoding.UTF8.GetBytes(statusJson);
            var payload = ServerListPing.VarInt(0)
                .Concat(ServerListPing.VarInt(jsonBytes.Length)).ToArray()
                .Concat(jsonBytes).ToArray();
            WriteFrame(stream, payload);
        });

        var status = await ServerListPing.PingAsync("127.0.0.1", port, 3000);
        await serverTask;
        Assert.Equal("fake 26.1", status.VersionName);
        Assert.Equal(2, status.PlayersOnline);
        Assert.Equal("测试房", status.Motd);
        listener.Stop();
    }

    private static byte[] ReadFrame(NetworkStream stream)
    {
        // 假服只关心帧边界:读一个变长长度 + 剩余
        var len = 0;
        var shift = 0;
        while (true)
        {
            var b = stream.ReadByte();
            len |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        var body = new byte[len];
        var offset = 0;
        while (offset < len)
        {
            var n = stream.Read(body, offset, len - offset);
            if (n <= 0) break;
            offset += n;
        }
        return body;
    }

    private static void WriteFrame(NetworkStream stream, byte[] payload)
    {
        var len = ServerListPing.VarInt(payload.Length);
        stream.Write(len, 0, len.Length);
        stream.Write(payload, 0, payload.Length);
    }
}

public class LanPortWatcherTests
{
    [Fact]
    public async Task CatchesVanillaLanPortFromLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-mp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "latest.log");
        File.WriteAllText(log, "[Server thread/INFO]: Starting minecraft server version 1.20.1\n");

        var watcher = new LanPortWatcher(log, pollMs: 40);
        var pending = watcher.WaitForAsync(2000);

        await Task.Delay(150);
        File.AppendAllText(log, "[Server thread/INFO]: Listening on /0.0.0.0:51234\n");

        var result = await pending;
        Assert.NotNull(result.Port);
        Assert.Equal(51234, result.Port!.Value.Port);
        Assert.Null(result.ModAddress);
    }

    [Fact]
    public async Task CatchesE4mcDomainFromChat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-mp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "latest.log");
        var watcher = new LanPortWatcher(Path.Combine(dir, "latest.log"), pollMs: 40);
        var pending = watcher.WaitForAsync(2000);

        await Task.Delay(120);
        File.WriteAllText(log,
            "[Render thread/INFO]: [CHAT] LAN world opened at calm-otter-42.e4mc.link — 把这个地址发给朋友\n");

        var result = await pending;
        Assert.Equal("calm-otter-42.e4mc.link", result.ModAddress);
        watcher.Dispose();
    }

    [Fact]
    public void IgnoresRandomNumbers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-mp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "latest.log");
        File.WriteAllText(log, "[Render thread/INFO]: Loaded 1024 recipes in 45ms\n");
        var watcher = new LanPortWatcher(log);
        Assert.Null(watcher.TryScanOnce().Port);
    }
}

public class TunnelRunnerTests
{
    [Fact]
    public void WriteConfigReplacesLocalPort()
    {
        var runner = new TunnelRunner(TunnelPreset.OpenFrp, "C:/不存在的frpc.exe");
        var config = @"serverAddr = ""frp.example.com""
serverPort = 7000

[[proxies]]
name = ""mc""
type = ""tcp""
localIp = ""127.0.0.1""
localPort = 25565
remotePort = 54321
";
        var path = runner.WriteConfig(config, 51234);
        try
        {
            var text = File.ReadAllText(path);
            Assert.Contains("localPort = 51234", text);
            Assert.Contains("remotePort = 54321", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadRemoteAddressFromTomlAndIni()
    {
        var toml = "serverAddr = \"hk.openfrp.top\"\nremotePort = 54321\n";
        Assert.Equal("hk.openfrp.top:54321", TunnelRunner.ReadRemoteAddress(toml));

        var ini = "server_addr = frp.example.com\nremote_port = 23333\n";
        Assert.Equal("frp.example.com:23333", TunnelRunner.ReadRemoteAddress(ini));
    }
}

public class ServersDatFormatTests
{
    [Fact]
    public void WritesUncompressedNbtForVanilla()
    {
        // 原版 NbtIo.read 不认 gzip:首字节必须是根 TAG_Compound(0x0A)
        var path = Path.Combine(Path.GetTempPath(), "fuse-servers-" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            ServersDat.Write(path, new[]
            {
                new ServerEntry("好友房间", "203.0.113.7:25565", false),
            });
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0x0A, bytes[0]);
            Assert.False(bytes.Length > 1 && bytes[0] == 0x1F && bytes[1] == 0x8B, "不能是 gzip");
            var read = ServersDat.Read(path);
            Assert.Single(read);
            Assert.Equal("好友房间", read[0].Name);
            Assert.Equal("203.0.113.7:25565", read[0].Ip);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void ReadsLegacyGzipFiles()
    {
        // 旧版启动器写过 gzip:读取要兼容,避免升级后服务器列表消失
        var path = Path.Combine(Path.GetTempPath(), "fuse-servers-gz-" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            var plain = Path.Combine(Path.GetTempPath(), "fuse-servers-plain-" + Guid.NewGuid().ToString("N") + ".dat");
            ServersDat.Write(plain, new[] { new ServerEntry("旧服", "1.2.3.4:25565", false) });
            using (var input = File.OpenRead(plain))
            using (var output = File.Create(path))
            using (var gz = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress))
                input.CopyTo(gz);
            var read = ServersDat.Read(path);
            Assert.Single(read);
            Assert.Equal("旧服", read[0].Name);
            File.Delete(plain);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
