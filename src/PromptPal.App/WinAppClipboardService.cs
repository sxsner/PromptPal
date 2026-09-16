using System.Runtime.InteropServices;

namespace PromptPal_App;

/// <summary>
/// WinUI 3 剪贴板服务 — 使用 P/Invoke 访问 user32.dll
/// 关键点：SetClipboardData 后系统接管 hMem，成功路径不能 GlobalFree
/// </summary>
public sealed class WinAppClipboardService : PromptPal.Core.Services.IClipboardService
{
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, IntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_FIXED = 0x0000;

    // OpenClipboard 常被其他程序短暂独占（失败返回 0），按 Windows 通行做法退避重试
    private const int OpenMaxAttempts = 5;
    private const int OpenRetryDelayMs = 50;

    public Task<bool> CopyTextAsync(string text)
    {
        return Task.Run(() =>
        {
            // 重试打开剪贴板；全部失败则明确返回 false（不再静默当成功）
            bool opened = false;
            for (int attempt = 1; attempt <= OpenMaxAttempts; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero)) { opened = true; break; }
                if (attempt < OpenMaxAttempts) Thread.Sleep(OpenRetryDelayMs);
            }
            if (!opened) return false;

            try
            {
                if (!EmptyClipboard()) return false;

                var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                var hMem = GlobalAlloc(GMEM_FIXED, (IntPtr)bytes.Length);
                if (hMem == IntPtr.Zero) return false;

                var ptr = GlobalLock(hMem);
                if (ptr == IntPtr.Zero)
                {
                    // 失败路径：手动释放，避免泄漏
                    GlobalFree(hMem);
                    return false;
                }

                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                GlobalUnlock(hMem);

                // 成功路径：SetClipboardData 后系统接管 hMem，绝不 GlobalFree（否则剪贴板数据失效）
                // 失败路径：SetClipboardData 返回 NULL 时 hMem 仍归我们，必须释放，否则内存泄漏
                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                {
                    GlobalFree(hMem);
                    return false;
                }
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        });
    }
}
