using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace PromptPal_App;

/// <summary>
/// 顶层窗口的 Win32 小工具：owner 归属（不进任务栏、标题栏保持原生）、DPI 缩放、图标。
/// 设置窗/编辑窗以主窗口为 owner 且不显示在任务栏，符合"一个应用一个任务栏项"的原生形态。
/// </summary>
internal static class WindowInterop
{
    private const int GWL_HWNDPARENT = -8;
    private const uint WS_EX_APPWINDOW = 0x00040000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>窗口当前 DPI 缩放（物理像素 / DIP），拿不到时按 100% 处理。</summary>
    public static double GetScale(IntPtr hwnd)
    {
        try { uint dpi = GetDpiForWindow(hwnd); return dpi > 0 ? dpi / 96.0 : 1.0; }
        catch { return 1.0; }
    }

    /// <summary>
    /// 把窗口归属到主窗口（owner）：随主窗最小化/还原、始终在主窗之上。
    /// 被拥有、且没有 WS_EX_APPWINDOW 的顶层窗口默认不出现在任务栏，因此“一个应用一个任务栏项”。
    /// 注意：不要加 WS_EX_TOOLWINDOW —— Win10 会给工具窗口换成矮一截的标题栏和小号方块
    /// 标题按钮（关闭钮悬停是一个小红方块），看起来像非原生自绘控件。
    /// </summary>
    public static void SetWindowOwner(IntPtr hwnd, IntPtr owner)
    {
        try
        {
            if (owner != IntPtr.Zero)
                SetWindowLongPtr(hwnd, GWL_HWNDPARENT, owner);

            // 双保险：个别宿主路径可能已带 APPWINDOW，显式摘掉以免冒出第二个任务栏按钮
            const int GWL_EXSTYLE = -20;
            uint ex = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_APPWINDOW) != 0)
            {
                ex &= ~WS_EX_APPWINDOW;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((long)ex));
            }
        }
        catch (Exception ex) { Logger.Error("设置窗口 owner 失败", ex); }
    }

    /// <summary>给 AppWindow 设置标题栏/任务栏图标（失败不影响运行，回退系统默认图标）。</summary>
    public static void TrySetIcon(AppWindow appWindow, string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath) || !System.IO.File.Exists(iconPath)) return;
        try { appWindow.SetIcon(iconPath); }
        catch (Exception ex) { Logger.Error("窗口图标设置失败", ex); }
    }

    /// <summary>在各输出目录中定位 AppIcon.ico（调试目录与发布目录层级不同）。</summary>
    public static string? ResolveIconPath()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            System.IO.Path.Combine(baseDir, "Assets", "AppIcon.ico"),
            System.IO.Path.Combine(baseDir, "AppX", "Assets", "AppIcon.ico"),
            System.IO.Path.Combine(baseDir, "..", "AppX", "Assets", "AppIcon.ico"),
        ];
        foreach (var c in candidates)
            if (System.IO.File.Exists(c)) return c;
        return null;
    }
}
