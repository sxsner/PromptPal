using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace PromptPal_App;

/// <summary>
/// 独立顶层弹窗宿主：尺寸/位置不受主窗口限制，跟随主题与系统强调色。
/// 通过 Complete(bool) 由内容里的按钮回传结果，ShowDialogAsync 等待关闭。
/// </summary>
public sealed class DialogWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _hwnd;

    public DialogWindow(string title, int widthDip, int heightDip)
    {
        Title = title;

        // 背景不能在构造时直接抓 Application.Resources["AppBg"]：那是按应用级主题（跟随系统）解析的画刷，
        // 本窗随后切到浅色时该画刷不会变，会出现深色底+深色字。改在 ApplyTheme 中按 ThemeDictionaries 取色
        var host = new Grid();
        Content = host;
        _host = host;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (App.MainWindowInstance is { } owner)
            WindowInterop.SetWindowOwner(hwnd, owner.Hwnd);
        WindowInterop.TrySetIcon(AppWindow, WindowInterop.ResolveIconPath());

        // 入参是 DIP，AppWindow.Resize 要物理像素
        try
        {
            double scale = WindowInterop.GetScale(hwnd);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)(widthDip * scale), (int)(heightDip * scale)));
        }
        catch { }

        Closed += (_, _) => _tcs.TrySetResult(false);
        Activated += OnFirstActivated;
    }

    private readonly Grid _host;
    private bool _centered;

    /// <summary>放入实际内容（会被套一层主题根容器）。</summary>
    public void SetContent(UIElement content)
    {
        _host.Children.Clear();
        _host.Children.Add(content);
    }

    private void OnFirstActivated(object? sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (_centered) return;
        _centered = true;
        CenterOnWorkArea();
        ApplyTheme();
    }

    private void OnHostThemeChanged(FrameworkElement sender, object args)
    {
        ApplyHostBackground();
        ApplyTitleBarTheme();
    }

    private void CenterOnWorkArea()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            int x = area.X + (area.Width - AppWindow.Size.Width) / 2;
            int y = area.Y + (area.Height - AppWindow.Size.Height) / 2;
            AppWindow.Move(new Windows.Graphics.PointInt32(Math.Max(0, x), Math.Max(0, y)));
        }
        catch { }
    }

    private void ApplyTheme()
    {
        _host.RequestedTheme = AppSettings.Current.ThemeMode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _hwnd = WindowNative.GetWindowHandle(this);
        ApplyHostBackground();
        ApplyTitleBarTheme();
        _host.ActualThemeChanged -= OnHostThemeChanged;
        _host.ActualThemeChanged += OnHostThemeChanged;
    }

    /// <summary>按宿主当前 ActualTheme 解析 AppBg，避免抓到应用级（系统）主题的旧画刷。</summary>
    private void ApplyHostBackground()
    {
        if (AppPrefs.ThemeBrush("AppBg", _host.ActualTheme) is { } b)
            _host.Background = b;
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            int on = _host.ActualTheme == ElementTheme.Dark ? 1 : 0;
            DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch { }
    }

    /// <summary>关闭并回传结果（true=确认/保存）。</summary>
    public void Complete(bool result)
    {
        _tcs.TrySetResult(result);
        try { Close(); } catch { }
    }

    public Task<bool> ShowDialogAsync()
    {
        Activate();
        return _tcs.Task;
    }
}
