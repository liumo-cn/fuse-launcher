using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Sources;

/// <summary>
/// CurseForge 请求传输层。当前实现是"Key 内嵌 + 内存解密"，
/// 以后可替换成自有代理（Key 不出服务端），业务代码无需改动。
/// </summary>
public interface ICurseForgeTransport
{
    /// <summary>GET 一个相对路径（例如 "/mods/search?..."），由实现补上 ApiBase 与鉴权头。</summary>
    Task<string> GetAsync(string pathAndQuery, CancellationToken ct);
}
