using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Fuse.Core.Versioning;

/// <summary>servers.dat 里的一条服务器记录。</summary>
public sealed record ServerEntry(string Name, string Ip, bool Hidden, string? Icon = null);

/// <summary>
/// 读取/写回 Minecraft 的 servers.dat(可能是 gzip 压缩的 NBT,也可能是未压缩 NBT)。
/// 写入时保留 icon(hidden 也保留),先写临时文件再替换,并留一份 .bak。
/// </summary>
public static class ServersDat
{
    public static List<ServerEntry> Read(string path)
    {
        var result = new List<ServerEntry>();
        if (!File.Exists(path)) return result;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var magic = new byte[2];
            var n = fs.Read(magic, 0, 2);
            fs.Position = 0;
            Stream input = fs;
            if (n == 2 && magic[0] == 0x1F && magic[1] == 0x8B)
                input = new GZipStream(fs, CompressionMode.Decompress);

            if (Nbt.Read(input) is not Dictionary<string, object?> root) return result;
            if (!root.TryGetValue("servers", out var raw) || raw is not List<object?> servers) return result;
            foreach (var item in servers)
            {
                if (item is not Dictionary<string, object?> s) continue;
                var name = s.TryGetValue("name", out var nm) ? nm as string ?? "" : "";
                var ip = s.TryGetValue("ip", out var ipv) ? ipv as string ?? "" : "";
                if (ip.Length == 0) continue;
                var hidden = s.TryGetValue("hidden", out var h) && h is sbyte b && b != 0;
                var icon = s.TryGetValue("icon", out var ic) ? ic as string : null;
                result.Add(new ServerEntry(name, ip, hidden, icon));
            }
        }
        catch (Exception)
        {
            // 文件损坏/游戏正在写:当作没有服务器
        }
        return result;
    }

    /// <summary>
    /// 写回 servers.dat。**必须写未压缩 NBT**:原版用 NbtIo.read 读,遇到 gzip 头(0x1f)
    /// 会报 "Invalid tag id: 31"(QuickPlay 连接时踩过)。读取仍兼容两种格式。
    /// </summary>
    public static void Write(string path, IReadOnlyList<ServerEntry> servers)
    {
        using var ms = new MemoryStream();
        Nbt.WriteServers(ms, servers);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            ms.WriteTo(fs);
        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", overwrite: true); } catch (IOException) { }
            File.Delete(path);
        }
        File.Move(tmp, path);
    }
}

/// <summary>最小 NBT 读取器(只读,支持 servers.dat 用到的全部基础类型)。</summary>
internal static class Nbt
{
    public static object? Read(Stream stream)
    {
        var reader = new Reader(stream);
        var type = reader.ReadByte();
        if (type == 0) return null;
        reader.ReadString(); // 根标签名(通常为空)
        return reader.ReadPayload(type);
    }

    /// <summary>按 Minecraft 的结构写 servers 列表(根 compound + servers 列表)。</summary>
    public static void WriteServers(Stream stream, IReadOnlyList<ServerEntry> servers)
    {
        WriteTag(stream, 10, "");
        WriteTag(stream, 9, "servers");
        stream.WriteByte(10);   // 元素类型 = compound
        WriteInt(stream, servers.Count);
        foreach (var server in servers)
        {
            WriteTag(stream, 8, "name");
            WriteString(stream, server.Name ?? "");
            WriteTag(stream, 8, "ip");
            WriteString(stream, server.Ip ?? "");
            if (!string.IsNullOrEmpty(server.Icon))
            {
                WriteTag(stream, 8, "icon");
                WriteString(stream, server.Icon!);
            }
            WriteTag(stream, 1, "hidden");
            stream.WriteByte((byte)(server.Hidden ? 1 : 0));
            stream.WriteByte(0); // end compound
        }
        stream.WriteByte(0);     // end root compound
    }

    private static void WriteTag(Stream stream, byte type, string name)
    {
        stream.WriteByte(type);
        WriteString(stream, name);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        stream.WriteByte((byte)(bytes.Length >> 8));
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteInt(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private sealed class Reader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[8];

        public Reader(Stream stream) => _stream = stream;

        public byte ReadByte()
        {
            var value = _stream.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            return (byte)value;
        }

        private void Fill(int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = _stream.Read(_buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }

        public short ReadShort()
        {
            Fill(2);
            return (short)((_buffer[0] << 8) | _buffer[1]);
        }

        public int ReadInt()
        {
            Fill(4);
            return (_buffer[0] << 24) | (_buffer[1] << 16) | (_buffer[2] << 8) | _buffer[3];
        }

        public long ReadLong()
        {
            Fill(8);
            long value = 0;
            for (var i = 0; i < 8; i++) value = (value << 8) | _buffer[i];
            return value;
        }

        public float ReadFloat()
        {
            Fill(4);
            if (BitConverter.IsLittleEndian) Array.Reverse(_buffer, 0, 4);
            return BitConverter.ToSingle(_buffer, 0);
        }

        public double ReadDouble()
        {
            Fill(8);
            if (BitConverter.IsLittleEndian) Array.Reverse(_buffer, 0, 8);
            return BitConverter.ToDouble(_buffer, 0);
        }

        public string ReadString()
        {
            var length = (ushort)ReadShort();
            if (length == 0) return "";
            var bytes = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = _stream.Read(bytes, read, length - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            return Encoding.UTF8.GetString(bytes);
        }

        public object? ReadPayload(byte type)
        {
            switch (type)
            {
                case 1: return (sbyte)ReadByte();
                case 2: return ReadShort();
                case 3: return ReadInt();
                case 4: return ReadLong();
                case 5: return ReadFloat();
                case 6: return ReadDouble();
                case 7:
                    {
                        var length = ReadInt();
                        var bytes = new byte[Math.Max(0, length)];
                        var read = 0;
                        while (read < bytes.Length)
                        {
                            var n = _stream.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) throw new EndOfStreamException();
                            read += n;
                        }
                        return bytes;
                    }
                case 8: return ReadString();
                case 9:
                    {
                        var elementType = ReadByte();
                        var length = ReadInt();
                        var list = new List<object?>(Math.Max(0, Math.Min(length, 4096)));
                        for (var i = 0; i < length; i++)
                            list.Add(elementType == 0 ? null : ReadPayload(elementType));
                        return list;
                    }
                case 10:
                    {
                        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                        while (true)
                        {
                            var childType = ReadByte();
                            if (childType == 0) break;
                            var name = ReadString();
                            dict[name] = ReadPayload(childType);
                        }
                        return dict;
                    }
                case 11:
                    {
                        var length = ReadInt();
                        var array = new int[Math.Max(0, length)];
                        for (var i = 0; i < array.Length; i++) array[i] = ReadInt();
                        return array;
                    }
                case 12:
                    {
                        var length = ReadInt();
                        var array = new long[Math.Max(0, length)];
                        for (var i = 0; i < array.Length; i++) array[i] = ReadLong();
                        return array;
                    }
                default:
                    throw new InvalidDataException("未知 NBT 类型:" + type);
            }
        }
    }
}
