using System;
using System.Security.Cryptography;
using System.Text;

namespace Fuse.App;

/// <summary>
/// 内嵌的 CurseForge API Key 解包器。
///
/// 生成文件 <c>Generated\CfKey.g.cs</c> 只在构建机上存在（.gitignore 排除），
/// 里面是"密文 int[] + 随机 salt/pepper + 版本号"，没有明文 Key；
/// 解密所需的一切都在二进制里，所以这只能提高逆向成本，不能绝对保密。
/// </summary>
internal static class CfKey
{
    /// <summary>解出 Key 字节；未注入 Key 的构建返回 null。调用方用完必须清零。</summary>
    internal static byte[]? Unwrap()
    {
#if FUSE_CF_KEY
        var data = CfKeyGenerated.Data;
        if (data.Length == 0) return null;

        var material = new byte[CfKeyGenerated.PepperA.Length + CfKeyGenerated.Salt.Length
                                + CfKeyGenerated.PepperB.Length + Encoding.UTF8.GetByteCount(CfKeyGenerated.Version)];
        var offset = 0;
        Buffer.BlockCopy(CfKeyGenerated.PepperA, 0, material, offset, CfKeyGenerated.PepperA.Length);
        offset += CfKeyGenerated.PepperA.Length;
        Buffer.BlockCopy(CfKeyGenerated.Salt, 0, material, offset, CfKeyGenerated.Salt.Length);
        offset += CfKeyGenerated.Salt.Length;
        Buffer.BlockCopy(CfKeyGenerated.PepperB, 0, material, offset, CfKeyGenerated.PepperB.Length);
        offset += CfKeyGenerated.PepperB.Length;
        var versionBytes = Encoding.UTF8.GetBytes(CfKeyGenerated.Version);
        Buffer.BlockCopy(versionBytes, 0, material, offset, versionBytes.Length);

        var key = new byte[data.Length * 4];
        var block = new byte[32];
        var counter = new byte[4];
        var buffer = new byte[material.Length + 4];
        Buffer.BlockCopy(material, 0, buffer, 0, material.Length);

        var produced = 0;
        var index = 0;
        using (var sha = SHA256.Create())
        {
            while (produced < key.Length)
            {
                counter[0] = (byte)(index & 0xFF);
                counter[1] = (byte)((index >> 8) & 0xFF);
                counter[2] = (byte)((index >> 16) & 0xFF);
                counter[3] = (byte)((index >> 24) & 0xFF);
                Buffer.BlockCopy(counter, 0, buffer, material.Length, 4);
                block = sha.ComputeHash(buffer);
                for (var i = 0; i < block.Length && produced < key.Length; i++, produced++)
                {
                    var packed = data[produced / 4];
                    var cipher = (byte)((packed >> (8 * (produced % 4))) & 0xFF);
                    key[produced] = (byte)(cipher ^ block[i]);
                }
                index++;
            }
        }
        Array.Clear(buffer, 0, buffer.Length);
        return key;
#else
        return null;
#endif
    }
}
