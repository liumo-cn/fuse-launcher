using System;
using System.Globalization;
using Fuse.Core.Accounts;
using Xunit;

namespace Fuse.Core.Tests;

/// <summary>
/// 离线 UUID 黄金向量：由独立 Python 实现（MD5 → version 3 / IETF variant）预先计算，
/// 避免与 C# 实现同源循环验证。
/// </summary>
public class OfflineUuidTests
{
    [Theory]
    [InlineData("Notch", "b50ad385-829d-3141-a216-7e7d7539ba7f")]
    [InlineData("Steve", "5627dd98-e6be-3c21-b8a8-e92344183641")]
    [InlineData("小明", "e1fa999f-d9e9-3419-bc01-2f51157a00e5")]
    public void OfflineUuid_MatchesGoldenVectors(string name, string expected)
    {
        Assert.Equal(Guid.Parse(expected), OfflineAccount.CreateUuid(name));
    }

    [Fact]
    public void OfflineUuid_DeterministicAndVersion3()
    {
        var a = OfflineAccount.CreateUuid("player1");
        var b = OfflineAccount.CreateUuid("player1");
        Assert.Equal(a, b);
        var text = a.ToString("N", CultureInfo.InvariantCulture);
        Assert.Equal('3', text[12]); // RFC 版本号位（v3，MD5 名字空间）
        Assert.Equal('a', text[16]); // IETF 变体位（10xx）
    }

    [Fact]
    public void OfflineUuid_TrimsWhitespace()
    {
        Assert.Equal(OfflineAccount.CreateUuid("abc"), OfflineAccount.CreateUuid(" abc "));
    }

    [Fact]
    public void AuthResult_Offline_UsesLegacyType()
    {
        var auth = AuthResult.Offline("Steve");
        Assert.Equal("legacy", auth.UserType);
        Assert.Equal("0", auth.AccessToken);
        Assert.Equal(OfflineAccount.CreateUuid("Steve"), auth.PlayerUuid);
    }
}
