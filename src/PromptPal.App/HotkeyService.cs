using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace PromptPal_App;

/// <summary>
/// WinUI 3 全局热键服务 — 通过 P/Invoke RegisterHotKey 实现
/// </summary>
public sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HotkeyId = 1;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x2000; // 按住热键不自动重复触发，避免窗口反复唤起
    private const uint VK_P = 0x50;

    public event Action? HotkeyPressed;

    private readonly IntPtr _hwnd;
    private bool _registered;

    public HotkeyService(Window window)
    {
        _hwnd = WindowNative.GetWindowHandle(window);
    }

    /// <summary>注册热键，返回 true 表示成功</summary>
    public bool Register()
    {
        if (_registered) return true;

        // 优先 NOREPEAT：按住不连发热键
        if (RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_P))
        {
            _registered = true;
            return true;
        }

        // 部分安全软件/键盘钩子会拒绝带 NOREPEAT 的注册（实测返回 ACCESS_DENIED，任意键均失败）：
        // 退化为普通注册。唤起操作（Show+Activate）幂等，自动重复无副作用。
        _registered = RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_SHIFT, VK_P);
        return _registered;
    }

    public void HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
        }
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }
}
