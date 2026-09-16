using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace PromptPal_App;

/// <summary>
/// 自定义程序入口：在 XAML 启动前完成单实例仲裁。
/// 第二个进程把激活参数重定向给已运行实例后立即退出，避免多开导致热键互抢 / SQLite 写竞争。
/// （需要在 csproj 中设置 DisableXamlGeneratedMain 以替换 XAML 编译器生成的 Main）
/// </summary>
public static class Program
{
    private const string AppInstanceKey = "PromptPal";

    /// <summary>非打包应用的显式 AUMID：任务栏分组、Alt+Tab、Win11 托盘图标设置、
    /// 开机自启项都依赖它把本进程与 exe 图标/友好名称关联起来，必须在创建任何窗口前设置。</summary>
    private const string AppUserModelId = "PromptPal.App";

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    [STAThread]
    private static void Main(string[] args)
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* 老系统降级为按路径分组，不影响运行 */ }

        var current = AppInstance.FindOrRegisterForKey(AppInstanceKey);
        if (!current.IsCurrent)
        {
            // 已有实例运行：转发本次激活（Launch/文件等），由首个实例的 Activated 事件负责唤起窗口
            try
            {
                var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                current.RedirectActivationToAsync(activationArgs).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Error("激活重定向到已有实例失败", ex);
            }
            return;
        }

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
