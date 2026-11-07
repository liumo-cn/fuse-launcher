using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Fuse.App;

/// <summary>
/// 剪贴板被别的进程短暂占用时 OpenClipboard 会抛 CLIPBRD_E_CANT_OPEN(0x800401D0),
/// WPF 的 Clipboard.SetText 没有重试,游戏开着的时候很容易撞上,这里带退避重试。
/// </summary>
internal static class ClipboardHelper
{
    /// <summary>尝试写入文本剪贴板,失败返回 false(调用方给提示,不再炸未处理异常)。</summary>
    public static bool TrySetText(string text, int attempts = 10)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x800401D0u)
            {
                Thread.Sleep(80);
            }
            catch (ExternalException)
            {
                Thread.Sleep(80);
            }
        }
        return false;
    }
}
