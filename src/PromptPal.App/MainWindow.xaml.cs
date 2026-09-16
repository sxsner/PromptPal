using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace PromptPal_App;

/// <summary>
/// 主窗口：全局热键 / WndProc 钩子 / 最小尺寸 / 系统托盘 / 主题联动
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly HotkeyService _hotkey;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private delegate bool EnumMonitorsDelegate(IntPtr hMon, IntPtr hdc, IntPtr lprcClip, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // 最小尺寸（DIP）：720p 的 1/4
    private const int MinWidthDip = 640;
    private const int MinHeightDip = 360;
    // 首次启动默认尺寸（DIP）
    private const int DefaultWidthDip = 920;
    private const int DefaultHeightDip = 620;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    private IntPtr _hwnd;
    private IntPtr _prevWndProc;
    // 必须以字段持有委托：函数指针在委托被 GC 回收后失效；thunk 由运行时管理，绝不能 FreeHGlobal
    private WndProcDelegate? _wndProcDelegate;

    private TrayIcon? _tray;
    private bool _reallyExit;

    /// <summary>当前是否处于「总在最前」状态</summary>
    public bool IsTopmost { get; private set; }

    /// <summary>主窗口 Win32 句柄（供设置窗/弹窗设为 owner，不进任务栏）。</summary>
    public IntPtr Hwnd => _hwnd;

    public MainWindow()
    {
        Logger.Milestone("MainWindow.InitializeComponent begin");
        InitializeComponent();
        Logger.Milestone("MainWindow.InitializeComponent ok; Navigate MainPage");
        RootFrame.Navigate(typeof(MainPage));
        Logger.Milestone("MainPage navigated");

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowInterop.TrySetIcon(AppWindow, WindowInterop.ResolveIconPath());

        // 先按 DPI 给一个够用的默认尺寸（AppWindow.Resize 单位是物理像素，不能直接传 DIP）；
        // 随后 RestoreWindowBounds 命中有效记录时会再 MoveAndResize 覆盖为用户尺寸。
        // 不能先按存储值 Resize：旧版本遗留的 640×360 会让首次居中沿用错误尺寸。
        double scale = WindowInterop.GetScale(_hwnd);
        try
        {
            AppWindow.Resize(new SizeInt32(
                (int)(DefaultWidthDip * scale), (int)(DefaultHeightDip * scale)));
        }
        catch (Exception ex) { Logger.Error("初始窗口尺寸设置失败", ex); }

        ApplyTheme();
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            ApplyTitleBarTheme();
            AppPrefs.ApplyAccent(); // 强调色可能随主题资源刷新，重算文字色
        };

        _hotkey = new HotkeyService(this);
        _hotkey.HotkeyPressed += OnGlobalHotkey;
        if (AppSettings.Current.HotkeyEnabled)
        {
            try
            {
                if (!_hotkey.Register())
                {
                    HotkeyStartupFailed = true;
                    Logger.Error("全局热键 Ctrl+Shift+P 注册失败 — 可能已被其他应用占用");
                }
            }
            catch (Exception ex) { HotkeyStartupFailed = true; Logger.Error("RegisterHotKey 异常", ex); }
        }

        HookWndProc();
        InitBoundsAutosave();
        if (!RestoreWindowBounds())
            CenterFirstRun();
        Logger.Milestone("WndProc hooked; InitTray begin");
        InitTray();
        Logger.Milestone("InitTray ok");

        Activated += OnFirstActivated;

        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) =>
        {
            UnhookWndProc();
            _hotkey.Dispose();
            DisposeTray();
        };
    }

    /// <summary>首次启动（没有保存过位置）时在主屏工作区居中。</summary>
    private void CenterFirstRun()
    {
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea
                .GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            var size = AppWindow.Size;
            AppWindow.MoveAndResize(new RectInt32(
                area.X + Math.Max(0, (area.Width - size.Width) / 2),
                area.Y + Math.Max(0, (area.Height - size.Height) / 2),
                size.Width, size.Height));
        }
        catch (Exception ex) { Logger.Error("首次居中失败", ex); }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _boundsTimer;

    /// <summary>拖动/缩放结束 500ms 后落盘一次，避免丢失"最小化到托盘时直接退进程"等场景的尺寸记忆。</summary>
    private void InitBoundsAutosave()
    {
        try
        {
            _boundsTimer = DispatcherQueue.CreateTimer();
            _boundsTimer.Interval = TimeSpan.FromMilliseconds(500);
            _boundsTimer.IsRepeating = false;
            _boundsTimer.Tick += (_, _) => SaveWindowBounds();
            AppWindow.Changed += (_, e) =>
            {
                if (e.DidPositionChange || e.DidSizeChange)
                {
                    _boundsTimer.Stop();
                    _boundsTimer.Start();
                }
            };
        }
        catch (Exception ex) { Logger.Error("窗口尺寸自动保存初始化失败", ex); }
    }

    // ========== 主题：跟随系统 / 强制明暗 + 标题栏深色模式 ==========

    public void ApplyTheme()
    {
        RootGrid.RequestedTheme = AppSettings.Current.ThemeMode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        int on = dark ? 1 : 0;
        try { DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)); }
        catch (Exception ex) { Logger.Error("标题栏深色模式设置失败", ex); }
    }

    // ========== 热键 ==========

    private void OnGlobalHotkey()
    {
        try
        {
            // 已显示、未最小化且正在前台 → 再按一次隐藏到托盘（真正的呼出/隐藏切换）；
            // 其余情况（托盘隐藏 / 任务栏最小化 / 在后台）一律唤起并置前
            var visible = IsWindowVisible(_hwnd) && !IsIconic(_hwnd);
            if (visible && GetForegroundWindow() == _hwnd)
                HideToTray();
            else
                RestoreFromTray();
        }
        catch (Exception ex) { Logger.Error("热键切换窗口失败", ex); }
    }

    private void OnFirstActivated(object? sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (_startupHandled) return;
        _startupHandled = true;
        if (AppSettings.Current.MinimizeOnStartup)
            HideToTray();
    }

    private bool _startupHandled;

    /// <summary>启动时热键注册是否失败（供页面 Loaded 后弹出可见提示）。</summary>
    public bool HotkeyStartupFailed { get; }

    /// <summary>设置里切换「全局热键」时调用。返回 false 表示注册失败（热键被占用），调用方应回滚开关。</summary>
    public bool SetHotkeyEnabled(bool enabled)
    {
        if (enabled)
        {
            bool ok;
            try { ok = _hotkey.Register(); }
            catch (Exception ex)
            {
                Logger.Error("RegisterHotKey 异常", ex);
                return false;
            }
            if (!ok) Logger.Error("全局热键 Ctrl+Shift+P 注册失败 — 可能已被其他应用占用");
            return ok;
        }
        _hotkey.Unregister();
        return true;
    }

    /// <summary>设置变更后通知页面刷新字号/显示次数等。</summary>
    public event Action? SettingsApplied;

    public void NotifySettingsChanged()
    {
        ApplyTheme();
        SettingsApplied?.Invoke();
    }

    /// <summary>数据（提示词/分类）变更后通知页面重载。</summary>
    public event Action? DataChanged;
    public void NotifyDataChanged() => DataChanged?.Invoke();

    /// <summary>复制后自动隐藏到托盘。</summary>
    public void MinimizeToTrayNow() => HideToTray();

    // ========== 置顶 ==========

    public void ToggleTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        IsTopmost = !IsTopmost;
        try
        {
            SetWindowPos(_hwnd, IsTopmost ? HWND_TOPMOST : HWND_NOTOPMOST,
                0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch (Exception ex) { Logger.Error("切换置顶失败", ex); }
    }

    // ========== 系统托盘 + 关闭最小化 ==========

    /// <summary>托盘「新建」请求（已恢复窗口）。</summary>
    public event Action? NewRequested;
    /// <summary>托盘「设置」请求（已恢复窗口）。</summary>
    public event Action? TraySettingsRequested;

    private void InitTray()
    {
        _tray = new TrayIcon(_hwnd, "PromptPal — 提示词快捷工具", WindowInterop.ResolveIconPath());
        if (!_tray.IsAdded)
            Logger.Error("托盘图标 Shell_NotifyIcon(NIM_ADD) 失败：检查资源浏览器服务/图标文件");
        _tray.RestoreRequested += RestoreFromTray;
        _tray.ExitRequested += ExitApp;
        _tray.NewRequested += () => { RestoreFromTray(); NewRequested?.Invoke(); };
        _tray.SettingsRequested += () => { RestoreFromTray(); TraySettingsRequested?.Invoke(); };
    }

    private void OnAppWindowClosing(object? sender, AppWindowClosingEventArgs e)
    {
        // 无论随后是真退出还是取消（最小化到托盘），位置此刻有效，顺手持久化
        SaveWindowBounds();

        if (_reallyExit || !AppSettings.Current.MinimizeToTray) return;
        e.Cancel = true;
        HideToTray();
    }

    // ========== 窗口位置/尺寸记忆 ==========

    /// <summary>恢复保存的窗口位置/尺寸。返回 true 表示已恢复；false 表示无有效记录应走首次居中。</summary>
    private bool RestoreWindowBounds()
    {
        var s = AppSettings.Current;
        if (s.WindowX is not int x || s.WindowY is not int y
            || s.WindowWidth is not int w || s.WindowHeight is not int h
            || w <= 0 || h <= 0
            // 旧版本首次启动把 640x360 的程序默认值（物理像素）当成用户尺寸存了下来，
            // 尺寸等于最小时视为遗留默认值，一次性回退到新默认尺寸
            || (w <= MinWidthDip && h <= MinHeightDip))
            return false;

        try
        {
            // 钳制到最小尺寸（AppWindow 坐标为物理像素，MoveAndResize 不触发 WM_GETMINMAXINFO）
            double scale = 1.0;
            try { uint dpi = GetDpiForWindow(_hwnd); if (dpi > 0) scale = dpi / 96.0; } catch { }
            w = Math.Max(w, (int)(MinWidthDip * scale));
            h = Math.Max(h, (int)(MinHeightDip * scale));

            var rect = new RectInt32(x, y, w, h);

            // 多显示器校验：矩形必须与某块屏幕工作区相交，否则（如外接显示器已拔除）放弃恢复
            if (!IntersectsAnyMonitorWorkArea(rect))
            {
                Logger.Info("保存的窗口位置已不在任何屏幕内，回退默认位置");
                return false;
            }

            AppWindow.MoveAndResize(rect);
            return true;
        }
        catch (Exception ex) { Logger.Error("恢复窗口位置失败", ex); return false; }
    }

    /// <summary>判断矩形是否与任一显示器工作区相交（WindowsAppSDK 的 DisplayArea 无枚举 API，走 Win32）。</summary>
    private static bool IntersectsAnyMonitorWorkArea(RectInt32 rect)
    {
        var found = false;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                var work = mi.rcWork;
                if (rect.X < work.Right && rect.X + rect.Width > work.Left &&
                    rect.Y < work.Bottom && rect.Y + rect.Height > work.Top)
                {
                    found = true;
                    return false; // 已命中，停止枚举
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void SaveWindowBounds()
    {
        try
        {
            var p = AppWindow.Position;
            var size = AppWindow.Size;
            // 隐藏到托盘时尺寸不应持久化（部分平台 Hide 后给 0），异常值直接跳过
            if (size.Width <= 0 || size.Height <= 0) return;
            var s = AppSettings.Current;
            s.WindowX = p.X;
            s.WindowY = p.Y;
            s.WindowWidth = size.Width;
            s.WindowHeight = size.Height;
            s.Save();
        }
        catch (Exception ex) { Logger.Error("保存窗口位置失败", ex); }
    }

    private void HideToTray()
    {
        try { AppWindow.Hide(); }
        catch (Exception ex) { Logger.Error("隐藏到托盘失败", ex); }
    }

    public void RestoreFromTray()
    {
        try
        {
            // 任务栏最小化（IsIconic）时 AppWindow.Show 不会还原窗口，必须先 SW_RESTORE
            if (IsIconic(_hwnd)) ShowWindow(_hwnd, SW_RESTORE);
            AppWindow.Show();
            this.Activate();
        }
        catch (Exception ex) { Logger.Error("从托盘恢复窗口失败", ex); }
    }

    public void ExitApp()
    {
        _reallyExit = true;
        DisposeTray();
        try { Close(); }
        catch (Exception ex) { Logger.Error("退出时关闭窗口失败", ex); }
        Environment.Exit(0);
    }

    private void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    // ========== WndProc 钩子（热键 + 最小尺寸） ==========

    private void HookWndProc()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        _wndProcDelegate = WndProc;
        var wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _prevWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, wndProcPtr);
    }

    private void UnhookWndProc()
    {
        if (_prevWndProc != IntPtr.Zero && _hwnd != IntPtr.Zero)
        {
            try { SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _prevWndProc); }
            catch (Exception ex) { Logger.Error("恢复原 WndProc 失败", ex); }
            _prevWndProc = IntPtr.Zero;
        }
        _wndProcDelegate = null;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        _hotkey.HandleMessage(msg, wParam, lParam);

        if (_tray?.HandleMessage(msg, wParam, lParam) == true)
            return IntPtr.Zero;

        // 系统关机/重启/注销：必须放行。否则"关闭最小化到托盘"会取消 WM_CLOSE，
        // 导致系统弹"此应用阻止关机"或强制结束进程。标记后 Closing 不再拦截
        if (msg == WM_QUERYENDSESSION)
        {
            _reallyExit = true;
            return (IntPtr)1; // TRUE：同意结束会话
        }
        if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
            _reallyExit = true;

        // Explorer 重启（含崩溃恢复/用户手动重启）后任务栏重建：重新挂托盘图标，
        // 否则图标会永久消失直到应用重启
        if (TrayIcon.TaskbarCreatedMessage != 0 && msg == TrayIcon.TaskbarCreatedMessage)
        {
            _tray?.OnExplorerRestarted();
            return IntPtr.Zero;
        }

        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            double scale = 1.0;
            try { uint dpi = GetDpiForWindow(hWnd); if (dpi > 0) scale = dpi / 96.0; } catch { }
            mmi.ptMinTrackSize.X = (int)(MinWidthDip * scale);
            mmi.ptMinTrackSize.Y = (int)(MinHeightDip * scale);
            Marshal.StructureToPtr(mmi, lParam, true);
            return IntPtr.Zero;
        }

        return _prevWndProc == IntPtr.Zero
            ? DefWindowProc(hWnd, msg, wParam, lParam)
            : CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
    }
}
