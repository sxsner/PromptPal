using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using PromptPal.Core.Data;
using PromptPal.Core.Entities;
using PromptPal.Core.Services;

namespace PromptPal_App;

/// <summary>设置窗口（独立顶层窗口，对应 design/pages/settings.html）。</summary>
public sealed partial class SettingsWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private static SettingsWindow? _instance;

    public static void ShowOrCreate()
    {
        if (_instance is not null)
        {
            try { _instance.Activate(); }
            catch (Exception ex) { Logger.Error("激活已有设置窗口失败", ex); }
            return;
        }
        _instance = new SettingsWindow();
        _instance.Activate();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _boundsTimer;
    private bool? _mcpReady;

    public SettingsWindow()
    {
        InitializeComponent();

        // 归属主窗口且不进任务栏（必须在 Activate 前），避免用户以为开了两个应用；
        // 不用 WS_EX_TOOLWINDOW，保留与主窗一致的原生标题栏和标准标题按钮
        var hwnd = WindowNative.GetWindowHandle(this);
        if (App.MainWindowInstance is { } owner)
            WindowInterop.SetWindowOwner(hwnd, owner.Hwnd);
        WindowInterop.TrySetIcon(AppWindow, WindowInterop.ResolveIconPath());

        InitWindowBounds(hwnd);
        ApplyTheme();
        Closed += (_, _) =>
        {
            SaveBounds();
            _instance = null;
        };

        LoadFromSettings();
        ThemeBox.SelectionChanged += (_, _) => OnThemeChanged();
        FontBox.SelectionChanged += (_, _) => OnFontChanged();
        // 新分类输入框回车即添加（焦点仍在输入框时不必去点按钮）
        NewCatBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                AddCategory_Click(NewCatBox, new RoutedEventArgs());
        };
        InitMcpSection();
        InitAboutSection();
        _ = LoadCategoriesAsync();
    }

    private void InitAboutSection()
    {
        VersionText.Text = "v" + (typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0.0");
        LogPathText.Text = Logger.Enabled
            ? $"{Logger.LogDirectory}\\PromptPal-yyyy-MM-dd.log（每天一个文件，今天：{System.IO.Path.GetFileName(Logger.LogPath)}）"
            : "日志已关闭（exe 同目录放 nolog.txt，或设环境变量 PROMPTPAL_LOG=0）";
    }

    private const int SettingsDefaultWidthDip = 860;
    private const int SettingsDefaultHeightDip = 620;

    /// <summary>恢复保存的尺寸/位置，没有则按 DPI 默认尺寸并居中。</summary>
    private void InitWindowBounds(IntPtr hwnd)
    {
        try
        {
            double scale = WindowInterop.GetScale(hwnd);
            var s = AppSettings.Current;
            if (s.SettingsWidth is int w && s.SettingsHeight is int h && w > 0 && h > 0
                && s.SettingsX is int x && s.SettingsY is int y)
            {
                AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(
                    (int)(SettingsDefaultWidthDip * scale), (int)(SettingsDefaultHeightDip * scale)));
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                var size = AppWindow.Size;
                AppWindow.Move(new PointInt32(
                    area.X + Math.Max(0, (area.Width - size.Width) / 2),
                    area.Y + Math.Max(0, (area.Height - size.Height) / 2)));
            }

            // 拖动/缩放 400ms 后自动落盘
            _boundsTimer = DispatcherQueue.CreateTimer();
            _boundsTimer.Interval = TimeSpan.FromMilliseconds(400);
            _boundsTimer.IsRepeating = false;
            _boundsTimer.Tick += (_, _) => SaveBounds();
            AppWindow.Changed += (_, e) =>
            {
                if (e.DidPositionChange || e.DidSizeChange) { _boundsTimer.Stop(); _boundsTimer.Start(); }
            };
        }
        catch (Exception ex) { Logger.Error("设置窗口位置初始化失败", ex); }
    }

    private void SaveBounds()
    {
        try
        {
            var size = AppWindow.Size;
            if (size.Width <= 0 || size.Height <= 0) return;
            var p = AppWindow.Position;
            var s = AppSettings.Current;
            s.SettingsX = p.X; s.SettingsY = p.Y;
            s.SettingsWidth = size.Width; s.SettingsHeight = size.Height;
            s.Save();
        }
        catch (Exception ex) { Logger.Error("保存设置窗口位置失败", ex); }
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = AppSettings.Current.ThemeMode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // RequestedTheme 改后 ActualTheme 是异步刷新的：立即刷一次兜底，真正生效靠 ActualThemeChanged
        ApplyTitleBarTheme();
        ApplyMcpStatusBrush();
        Root.ActualThemeChanged -= OnRootActualThemeChanged;
        Root.ActualThemeChanged += OnRootActualThemeChanged;
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarTheme();
        ApplyMcpStatusBrush();
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            int on = Root.ActualTheme == ElementTheme.Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch (Exception ex) { Logger.Error("设置窗口标题栏深色模式失败", ex); }
    }

    /// <summary>MCP 就绪/失败状态色随当前主题取 SuccessText/DangerText，避免深色下深红字看不清。</summary>
    private void ApplyMcpStatusBrush()
    {
        if (_mcpReady is not bool ready || McpStatusText is null) return;
        var key = ready ? "SuccessText" : "DangerText";
        if (AppPrefs.ThemeBrush(key, Root.ActualTheme) is { } brush)
            McpStatusText.Foreground = brush;
    }

    private void LoadFromSettings()
    {
        var s = AppSettings.Current;
        ThemeBox.SelectedIndex = s.ThemeMode switch { "light" => 1, "dark" => 2, _ => 0 };
        FontBox.SelectedIndex = s.FontSize switch { "small" => 0, "large" => 2, _ => 1 };
        StartupHideToggle.IsOn = s.MinimizeOnStartup;
        TrayCloseToggle.IsOn = s.MinimizeToTray;
        AutoHideCopyToggle.IsOn = s.AutoHideAfterCopy;
        ShowCountToggle.IsOn = s.ShowUseCount;
        HotkeyToggle.IsOn = s.HotkeyEnabled;
        StartupRunToggle.IsOn = s.RunAtStartup;

        StartupHideToggle.Toggled += (_, _) => { s.MinimizeOnStartup = StartupHideToggle.IsOn; s.Save(); };
        TrayCloseToggle.Toggled += (_, _) => { s.MinimizeToTray = TrayCloseToggle.IsOn; s.Save(); };
        AutoHideCopyToggle.Toggled += (_, _) => { s.AutoHideAfterCopy = AutoHideCopyToggle.IsOn; s.Save(); };
        ShowCountToggle.Toggled += (_, _) =>
        {
            s.ShowUseCount = ShowCountToggle.IsOn; s.Save();
            AppPrefs.ApplyUseCount();
            App.MainWindowInstance?.NotifySettingsChanged();
        };
        HotkeyToggle.Toggled += (_, _) => OnHotkeyToggle();
        StartupRunToggle.Toggled += (_, _) => OnStartupRunToggle();
    }

    // 回拨 ToggleSwitch.IsOn 会再次触发 Toggled，用此标志抑制递归
    private bool _syncingToggle;

    private void OnHotkeyToggle()
    {
        if (_syncingToggle) return;
        var want = HotkeyToggle.IsOn;
        var ok = App.MainWindowInstance?.SetHotkeyEnabled(want) ?? true;
        if (!ok)
        {
            DataStatus.Text = "全局热键注册失败，可能已被其他程序占用";
            RollbackToggle(HotkeyToggle, want);
            return;
        }
        AppSettings.Current.HotkeyEnabled = want;
        AppSettings.Current.Save();
    }

    private void OnStartupRunToggle()
    {
        if (_syncingToggle) return;
        var want = StartupRunToggle.IsOn;
        if (!TrySetRunAtStartup(want))
        {
            DataStatus.Text = "开机自启设置失败（注册表不可用）";
            RollbackToggle(StartupRunToggle, want);
            return;
        }
        AppSettings.Current.RunAtStartup = want;
        AppSettings.Current.Save();
    }

    private void RollbackToggle(ToggleSwitch sw, bool attemptedValue)
    {
        _syncingToggle = true;
        try { sw.IsOn = !attemptedValue; }
        finally { _syncingToggle = false; }
    }

    private void OnThemeChanged()
    {
        if (ThemeBox.SelectedIndex < 0) return;
        AppSettings.Current.ThemeMode = ThemeBox.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        AppSettings.Current.Save();
        ApplyTheme();
        // 同时刷新主窗主题并重跑经转换器取色的绑定（chip/胶囊/排序/状态文字）
        App.MainWindowInstance?.NotifySettingsChanged();
    }

    private void OnFontChanged()
    {
        if (FontBox.SelectedIndex < 0) return;
        AppSettings.Current.FontSize = FontBox.SelectedIndex switch { 0 => "small", 2 => "large", _ => "default" };
        AppSettings.Current.Save();
        AppPrefs.ApplyFonts();
        // 通知主窗：主页面字号走 VM 的 INPC 绑定，靠这个事件实时刷新
        App.MainWindowInstance?.NotifySettingsChanged();
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        FrameworkElement? target = name switch
        {
            "SecAppearance" => SecAppearance,
            "SecHotkey" => SecHotkey,
            "SecData" => SecData,
            "SecCats" => SecCats,
            "SecMcp" => SecMcp,
            "SecAbout" => SecAbout,
            _ => null,
        };
        if (target is null) return;
        try
        {
            var p = target.TransformToVisual(ContentScroll)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            ContentScroll.ChangeView(null, p.Y, null);
        }
        catch (Exception ex) { Logger.Error("设置页导航滚动失败", ex); }
    }

    // ========== 数据管理 ==========

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        picker.FileTypeChoices.Add("SQLite 数据库", new[] { ".db" });
        picker.SuggestedFileName = $"promptpal-{DateTime.Now:yyyyMMdd-HHmm}";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            using var scope = App.Services.CreateScope();
            // VACUUM INTO 直接生成单文件一致性快照，运行中也能导出，可拷到新设备直接使用
            await scope.ServiceProvider.GetRequiredService<IDbTransferService>().ExportToFileAsync(file.Path);
            DataStatus.Text = $"已导出数据库到 {file.Path}";
        }
        catch (Exception ex)
        {
            Logger.Error("导出数据库失败", ex);
            DataStatus.Text = $"导出失败：{ex.Message}";
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".db");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        // 读入前先查文件大小：服务端也会校验，这里提前拦截避免误选超大文件
        long fileSize;
        try { fileSize = new FileInfo(file.Path).Length; }
        catch (Exception ex) { DataStatus.Text = $"无法读取文件：{ex.Message}"; return; }

        if (fileSize > IDbTransferService.MaxImportBytes)
        {
            DataStatus.Text = $"导入文件过大（{fileSize / 1024 / 1024}MB），最大允许 100MB";
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "导入数据库",
            Content = "将用所选文件整体替换当前全部提示词、分类和标签，当前数据会先自动备份（.bak）。确定继续吗？",
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            using var scope = App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDbTransferService>().ImportFromFileAsync(file.Path);
            DataStatus.Text = "数据库导入完成";
            await LoadCategoriesAsync();
            App.MainWindowInstance?.NotifyDataChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("导入数据库失败", ex);
            DataStatus.Text = $"导入失败：{ex.Message}";
        }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "重置所有数据",
            Content = "将删除所有提示词、分类和标签，恢复到初始状态。此操作不可撤销，确定继续吗？",
            PrimaryButtonText = "重置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 事务内清空并重新写入内置分类（EnsureCreated 不会重放种子，必须显式重置）
            await DatabaseInitializer.ResetToDefaultsAsync(db);
            DataStatus.Text = "已重置为初始状态";
            await LoadCategoriesAsync();
            App.MainWindowInstance?.NotifyDataChanged();
        }
        catch (Exception ex) { DataStatus.Text = $"重置失败：{ex.Message}"; }
    }

    // ========== 分类管理 ==========

    private async Task LoadCategoriesAsync()
    {
        try
        {
            using var scope = App.Services.CreateScope();
            var cats = await scope.ServiceProvider.GetRequiredService<ICategoryService>().GetAllAsync();
            RenderCategories(cats);
        }
        catch (Exception ex) { Logger.Error("加载分类列表失败", ex); }
    }

    // 分类卡片绑定的集合：ListView 原生拖拽重排会直接移动其中元素，拖完按此顺序整体落库
    private readonly ObservableCollection<Category> _categories = [];

    private void RenderCategories(IReadOnlyList<Category> cats)
    {
        if (CatList.ItemsSource is null)
            CatList.ItemsSource = _categories;
        _categories.Clear();
        foreach (var c in cats) _categories.Add(c);
    }

    /// <summary>拖拽重排完成：ListView 已就地移动集合元素，这里把新顺序一次性持久化。</summary>
    private async void CatList_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        if (e.Items.Count == 0) return;
        var orderedIds = _categories.Select(c => c.Id).ToList();
        try
        {
            using var scope = App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ICategoryService>().ReorderAsync(orderedIds);
            CatStatus.Text = "分类顺序已更新";
            App.MainWindowInstance?.NotifyDataChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("拖拽保存分类顺序失败", ex);
            CatStatus.Text = $"排序保存失败：{ex.Message}";
            await LoadCategoriesAsync(); // 落库失败时以数据库顺序回滚界面
        }
    }

    private void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Category cat })
            _ = PromptRenameCategoryAsync(cat);
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Category cat })
            _ = ConfirmDeleteCategoryAsync(cat);
    }

    // 同一时刻只允许一个 ContentDialog（重命名/删除共用），防止并发 ShowAsync 抛异常
    private bool _categoryDialogOpen;

    private async Task PromptRenameCategoryAsync(Category cat)
    {
        if (_categoryDialogOpen) return;
        _categoryDialogOpen = true;
        try
        {
            var box = new TextBox { Text = cat.Name, MaxLength = 64 };
            box.SelectAll();
            var dialog = new ContentDialog
            {
                Title = "重命名分类",
                Content = box,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.ActualTheme,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            using var scope = App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ICategoryService>().RenameAsync(cat.Id, box.Text);
            CatStatus.Text = $"已重命名为「{box.Text.Trim()}」";
            await LoadCategoriesAsync();
            App.MainWindowInstance?.NotifyDataChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("重命名分类失败", ex);
            CatStatus.Text = $"重命名失败：{ex.Message}";
        }
        finally
        {
            _categoryDialogOpen = false;
        }
    }

    private async Task ConfirmDeleteCategoryAsync(Category cat)
    {
        if (_categoryDialogOpen) return;
        _categoryDialogOpen = true;
        try
        {
            var confirm = new ContentDialog
            {
                Title = "删除分类",
                Content = $"确定删除分类「{cat.Name}」吗？该分类下的提示词将变为「未分类」，不会被删除。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.ActualTheme
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            using var scope = App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ICategoryService>().DeleteAsync(cat.Id);
            CatStatus.Text = $"已删除分类「{cat.Name}」";
            await LoadCategoriesAsync();
            App.MainWindowInstance?.NotifyDataChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("删除分类失败", ex);
            CatStatus.Text = $"删除分类失败：{ex.Message}";
        }
        finally
        {
            _categoryDialogOpen = false;
        }
    }

    private async void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = NewCatBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            using var scope = App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ICategoryService>()
                .CreateAsync(new Category { Name = name });
            NewCatBox.Text = "";
            CatStatus.Text = $"已添加分类「{name}」";
            await LoadCategoriesAsync();
            App.MainWindowInstance?.NotifyDataChanged();
        }
        catch (Exception ex) { CatStatus.Text = $"添加分类失败：{ex.Message}"; }
    }

    // ========== MCP 接入 ==========

    /// <summary>全部 MCP 工具名，autoApprove 列表与实际注册保持一致（顺序无关）。</summary>
    private static readonly string[] McpToolNames =
    [
        "list_categories", "list_tags", "search_prompts", "get_prompt", "copy_prompt",
        "create_prompt", "update_prompt", "toggle_favorite", "delete_prompt",
        "create_category", "rename_category", "move_category", "delete_category",
    ];

    private string? _mcpExePath;

    private void InitMcpSection()
    {
        try
        {
            _mcpExePath = ResolveMcpExePath();
            if (_mcpExePath is not null && File.Exists(_mcpExePath))
            {
                _mcpReady = true;
                McpPathText.Text = _mcpExePath;
                McpStatusText.Text = "状态：已就绪，可直接复制配置注册到 Trae";
                ApplyMcpStatusBrush();
                McpTip.Text = $"数据库与日志与主程序共用：{DatabaseInitializer.DbPath}";
            }
            else
            {
                _mcpReady = false;
                McpPathText.Text = "（未找到 Sidecar）";
                McpStatusText.Text = "状态：未找到 PromptPal.Mcp.exe（发布版应位于程序目录下 mcp 子文件夹）";
                ApplyMcpStatusBrush();
                CopyMcpConfigBtn.IsEnabled = false;
                OpenMcpFolderBtn.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("初始化 MCP 设置区块失败", ex);
        }
    }

    /// <summary>
    /// 定位 sidecar：发布版在 exe 同目录 mcp 子文件夹；F5 调试时回退到 MCP 项目的 Debug 输出。
    /// App 调试目录深度固定：src\PromptPal.App\bin\Debug\net10.0-...\win-x64 → 上溯 5 级到 src。
    /// </summary>
    private static string? ResolveMcpExePath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "mcp", "PromptPal.Mcp.exe"),
            Path.GetFullPath(Path.Combine(
                baseDir, "..", "..", "..", "..", "..",
                "PromptPal.Mcp", "bin", "Debug", "net10.0", "PromptPal.Mcp.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>生成与模板一致的 mcp.json 内容，路径随当前机器/发布目录自动填充。</summary>
    private static string BuildMcpConfig(string exePath)
    {
        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["promptpal"] = new
                {
                    command = exePath,
                    args = Array.Empty<string>(),
                    env = new { },
                    autoApprove = McpToolNames,
                },
            },
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private void CopyMcpConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpExePath is null) return;
        try
        {
            var package = new DataPackage();
            package.SetText(BuildMcpConfig(_mcpExePath));
            Clipboard.SetContent(package);
            McpTip.Text = "已复制 mcp.json 配置。粘贴到 Trae 的 MCP 添加面板，或保存为项目 .trae\\mcp.json 后重新加载窗口。";
            Logger.Info("MCP 注册配置已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Logger.Error("复制 MCP 配置失败", ex);
            McpTip.Text = $"复制失败：{ex.Message}";
        }
    }

    private void OpenMcpFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpExePath is null) return;
        try
        {
            // /select 直接在资源管理器里高亮 sidecar，便于用户确认文件确实存在
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_mcpExePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("打开 sidecar 目录失败", ex);
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(DatabaseInitializer.DbPath)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("打开数据目录失败", ex);
        }
    }

    /// <summary>写 HKCU\Run 开机自启项。返回 false 表示注册表不可用/写入失败，调用方应回滚 UI。</summary>
    internal static bool TrySetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return false;
            if (enable) key.SetValue("PromptPal", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("PromptPal", throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("开机自启注册表写入失败", ex);
            return false;
        }
    }
}
