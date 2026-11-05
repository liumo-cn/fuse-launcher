using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Accounts;

/// <summary>登录节点抽象：离线档案与微软正版通道各自实现，管线其余部分无感。</summary>
public interface IAuthProvider
{
    Task<AuthResult> GetAuthAsync(CancellationToken cancellation);
}

/// <summary>外置登录账号:令牌与角色信息由 authlib-injector 站点签发,启动时原样交给客户端。</summary>
public sealed class TokenAuthProvider(string playerName, Guid playerUuid, string accessToken, string userType = "mojang")
    : IAuthProvider
{
    public Task<AuthResult> GetAuthAsync(CancellationToken cancellation)
        => Task.FromResult(new AuthResult(playerName, playerUuid, accessToken, userType));
}

/// <summary>离线登录：仅凭用户名，无任何凭证（FR-06 Must 通道）。</summary>
public sealed class OfflineAuthProvider(string playerName) : IAuthProvider
{
    public Task<AuthResult> GetAuthAsync(CancellationToken cancellation)
        => Task.FromResult(AuthResult.Offline(playerName));
}
