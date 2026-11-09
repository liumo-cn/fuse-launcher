using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.Download;

/// <summary>
/// 读响应体的统一封装。.NET Framework 下 CancellationToken 掐不断"已经开始读"的 body
/// (只有 HttpClient 的总超时会真正 Abort),所以超时和取消都改成主动 Dispose 响应,
/// 由它关掉底层连接,挂起的读取才会立刻失败。
/// </summary>
internal static class HttpBody
{
    public const int DefaultBufferSize = 64 * 1024;

    /// <summary>
    /// 把响应体拷进目标流,返回字节数。每读到一块就重置停滞计时;超过 maxBytes 就提前停(0 = 不限)。
    /// </summary>
    public static async Task<long> CopyToAsync(
        HttpResponseMessage response,
        Stream destination,
        TimeSpan stallTimeout,
        CancellationToken cancellationToken,
        int bufferSize = DefaultBufferSize,
        long maxBytes = 0,
        CancellationToken attemptToken = default,
        Action<long>? onCopied = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptToken);
        // 计时一到就 Dispose 响应:这是 net48 里唯一能打断挂起读取的办法
        using var abort = deadline.Token.Register(() => SafeDispose(response));
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        var buffer = new byte[Math.Max(1, bufferSize)];
        long total = 0;
        while (true)
        {
            var want = buffer.Length;
            if (maxBytes > 0) want = (int)Math.Min(want, maxBytes - total);
            if (want <= 0) return total;

            deadline.CancelAfter(stallTimeout);
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, 0, want, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 用户暂停/取消优先于"停滞超时"
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new IOException("下载停滞超时");
                throw;
            }

            if (read <= 0) return total;
            total += read;
            await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            onCopied?.Invoke(total);
        }
    }

    public static async Task<string> ReadAsStringAsync(
        HttpResponseMessage response, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await CopyToAsync(response, buffer, stallTimeout, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static async Task<byte[]> ReadAsBytesAsync(
        HttpResponseMessage response, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await CopyToAsync(response, buffer, stallTimeout, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>从别的线程 Dispose 响应以打断挂起的读取;Dispose 内部可能等锁,放线程池上做。</summary>
    private static void SafeDispose(HttpResponseMessage response)
    {
        _ = Task.Run(() =>
        {
            try { response.Dispose(); }
            catch (Exception)
            {
            }
        });
    }
}
