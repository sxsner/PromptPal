using System.Runtime.InteropServices;

namespace PromptPal_App;

/// <summary>基于 Win32 Shell_NotifyIcon 的系统托盘图标（避免 WinForms 与 WinUI 冲突）。</summary>
public sealed class TrayIcon : IDisposable
{
    private const uint WM_APP = 0x8000;
    public const uint CallbackMsg = WM_APP + 1;

    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_GUID = 0x20;
    private const uint NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2, NIM_SETVERSION = 0x4;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const uint LR_LOADFROMFILE = 0x0010, IMAGE_ICON = 1;
    private const int ID_OPEN = 1001, ID_EXIT = 1002, ID_NEW = 1003, ID_SETTINGS = 1004;
    private const uint MF_SEPARATOR = 0x0800;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        // guidItem 16 字节；其后真实结构还有 4 字节对齐 + hBalloonIcon(IntPtr 8)，
        // x64 上完整 NOTIFYICONDATAW 总大小必须正好 984（V3 的 960 不含 guidItem，不能用）
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA pnid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr m, uint flags, IntPtr id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr reserved2);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private NOTIFYICONDATA _nid;
    private bool _added;

    /// <summary>托盘图标是否注册成功（失败时主窗口写日志，便于排查"系统设置里看不到程序"）。</summary>
    public bool IsAdded => _added;

    public event Action? RestoreRequested;
    public event Action? ExitRequested;
    public event Action? NewRequested;
    public event Action? SettingsRequested;

    // 固定 GUID：让 Windows 以稳定身份记住本程序托盘图标的显示/隐藏偏好与系统设置列表项
    private static readonly Guid ItemGuid = new("7A9F4C21-3B6E-4A2D-9E8F-1C5B7D8E2A04");

    /// <summary>Explorer 重启后向所有顶层窗广播的消息（任务栏重建后需重新注册托盘图标）。</summary>
    public static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    public TrayIcon(IntPtr hwnd, string title, string? iconPath)
    {
        _hwnd = hwnd;
        _hIcon = iconPath is not null && System.IO.File.Exists(iconPath)
            ? LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | 0x0040 /*LR_DEFAULTSIZE*/)
            : IntPtr.Zero;

        _nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID,
            uCallbackMessage = CallbackMsg,
            hIcon = _hIcon,
            szTip = title,
            guidItem = ItemGuid.ToByteArray(),
        };
        EnsureAdded();
    }

    /// <summary>Explorer/任务栏重启后重新注册图标（响应 TaskbarCreated 广播）。</summary>
    public void OnExplorerRestarted()
    {
        _added = false;
        EnsureAdded();
    }

    private void EnsureAdded()
    {
        _added = Shell_NotifyIcon(NIM_ADD, ref _nid);
        if (!_added)
        {
            // 固定 GUID 可能已被强杀/未正常退出的旧实例以"幽灵图标"占用，Explorer 拒绝同 GUID 再注册。
            // 先按 GUID 尝试清掉幽灵再注册一次（GUID 身份最有利于 Win11 任务栏设置列表持久化）；
            // 仍失败才退回 hWnd+uID 身份，保证图标一定能出现
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _added = Shell_NotifyIcon(NIM_ADD, ref _nid);
            if (!_added)
            {
                _nid.uFlags &= ~NIF_GUID;
                _added = Shell_NotifyIcon(NIM_ADD, ref _nid);
                if (_added) Logger.Info("托盘 GUID 注册失败，已回退为 hWnd+uID 身份注册");
            }
            else
            {
                Logger.Info("清理托盘幽灵图标后 GUID 注册成功");
            }
        }
        if (_added)
        {
            // 协商 V4：获得更完整的事件（NIN_KEYSELECT 等），也是现代任务栏识别的推荐姿势
            _nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            if (!Shell_NotifyIcon(NIM_SETVERSION, ref _nid))
            {
                // 版本协商失败不致命：V0 行为下左键/右键消息仍然可用
                System.Diagnostics.Debug.WriteLine("NIM_SETVERSION failed");
            }
        }
    }

    /// <summary>由主窗口 WndProc 转发托盘消息；返回 true 表示已处理。</summary>
    public bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != CallbackMsg) return false;
        uint evt = (uint)((long)lParam & 0xFFFF);
        // 单击与双击等效唤起（双击时 Windows 也会先发 LBUTTONUP，两次 Restore 均为 Show+Activate，幂等无副作用）
        if (evt == WM_LBUTTONUP || evt == WM_LBUTTONDBLCLK) RestoreRequested?.Invoke();
        else if (evt == WM_RBUTTONUP) ShowMenu();
        return true;
    }

    private void ShowMenu()
    {
        SetForegroundWindow(_hwnd);
        GetCursorPos(out var p);
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, 0, ID_OPEN, "打开 PromptPal");
        AppendMenu(menu, 0, ID_NEW, "新建提示词");
        AppendMenu(menu, 0, ID_SETTINGS, "设置");
        AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, "");
        AppendMenu(menu, 0, ID_EXIT, "退出");
        int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd)
        {
            case ID_OPEN: RestoreRequested?.Invoke(); break;
            case ID_NEW: NewRequested?.Invoke(); break;
            case ID_SETTINGS: SettingsRequested?.Invoke(); break;
            case ID_EXIT: ExitRequested?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        if (_added) { Shell_NotifyIcon(NIM_DELETE, ref _nid); _added = false; }
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }
}
