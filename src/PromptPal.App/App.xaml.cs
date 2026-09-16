using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using PromptPal.App.ViewModels;
using PromptPal.Core;
using PromptPal.Core.Services;

namespace PromptPal_App;

/// <summary>
/// 应用入口：配置 DI 容器 + 初始化数据库
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>全局 DI 服务容器</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>主窗口实例（供页面调用置顶等窗口级能力）</summary>
    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        Logger.SessionHeader();
        Logger.Milestone("App ctor begin");

        // 全局兜底：任何未处理异常都落到桌面日志，避免"进程在跑但没窗口"却无从排查
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error($"未处理异常 IsTerminating={e.IsTerminating}", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Task 未观察异常", e.Exception);
            e.SetObserved();
        };
        // WinUI UI 线程异常（async void 按钮处理器等不走 TaskScheduler，必须单独挂），
        // 记录后标记已处理，避免一次偶发失败直接拖垮整个进程
        UnhandledException += (_, e) =>
        {
            Logger.Error("UI 线程未处理异常", e.Exception);
            e.Handled = true;
        };

        try
        {
            InitializeComponent();
            Logger.Milestone("InitializeComponent ok");
        }
        catch (Exception ex)
        {
            Logger.Error("InitializeComponent 失败", ex);
            throw;
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Logger.Milestone("OnLaunched enter");
        try
        {
            AppSettings.Load();
            AppPrefs.ApplyAll();
            Logger.Milestone("settings loaded");
        }
        catch (Exception ex) { Logger.Error("设置加载失败", ex); }

        // 配置 DI
        var services = new ServiceCollection();
        services.AddPromptPalCore();
        services.AddSingleton<IClipboardService, WinAppClipboardService>();
        services.AddTransient<MainViewModel>();

        Services = services.BuildServiceProvider();
        Logger.Milestone("DI built");

        _ = LaunchCoreAsync();
    }

    /// <summary>初始化数据库并启动主窗口。与之分离，避免 async void 逃逸异常导致崩溃。</summary>
    private async Task LaunchCoreAsync()
    {
        // 对账开机自启：设置为开时重写一次注册表项，确保指向当前 exe（自包含目录可能被移动）
        try
        {
            if (AppSettings.Current.RunAtStartup && !SettingsWindow.TrySetRunAtStartup(true))
                Logger.Error("开机自启对账失败：注册表写入未成功");
        }
        catch (Exception ex) { Logger.Error("开机自启对账异常", ex); }

        try
        {
            Logger.Milestone("InitDatabase begin");
            await Services.InitializeDatabaseAsync();
            if (PromptPal.Core.Data.DatabaseInitializer.LastAclError is { } aclEx)
                Logger.Error("数据目录 ACL 加固失败（已降级为默认权限）", aclEx);
            Logger.Milestone("InitDatabase ok");
        }
        catch (Exception ex)
        {
            // 数据库初始化失败不阻止应用启动，记录并继续；运行期的具体查询会给出更明确的错误
            Logger.Error("数据库初始化失败", ex);
        }

        try
        {
            Logger.Milestone("new MainWindow begin");
            var win = new MainWindow();
            _window = win;
            MainWindowInstance = win;
            Logger.Milestone("new MainWindow ok");

            // 第二个实例被单实例仲裁重定向到本进程时唤起窗口（非打包模式下事件可能不在 UI 线程）
            Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, _) =>
            {
                MainWindowInstance?.DispatcherQueue.TryEnqueue(() => MainWindowInstance.RestoreFromTray());
            };

            _window.Activate();
            Logger.Milestone("Activate called");
        }
        catch (Exception ex)
        {
            Logger.Error("创建/激活主窗口失败", ex);
        }
    }
}
