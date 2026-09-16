using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using PromptPal.Core;
using PromptPal.Core.Data;
using PromptPal.Mcp;

// Core 的 %LOCALAPPDATA% 路径 API 仅支持 Windows；sidecar 随 Trae 在 Windows 本机以 stdio 拉起。
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

var builder = Host.CreateApplicationBuilder(args);

// stdio 传输中 stdout 只允许出现 MCP JSON-RPC 帧：把所有日志强制改走 stderr，绝不写 stdout。
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o =>
{
    o.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// 复用 App 的数据层：同一 SQLite 文件、同一 WAL/忙等设置与种子数据。
builder.Services.AddPromptPalCore();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(Program).Assembly);

var host = builder.Build();

// 跨机排障兜底：未处理异常写入 %LOCALAPPDATA%\PromptPal\mcp.log（stdio 下用户看不到控制台）
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    SidecarLog.Error($"未处理异常 IsTerminating={e.IsTerminating}",
        e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    SidecarLog.Error("Task 未观察异常", e.Exception);
    e.SetObserved();
};

SidecarLog.SessionHeader(DatabaseInitializer.DbPath);

try
{
    // 首次运行（App 尚未启动过）也能直接建库 + 写 WAL + 播种 5 个内置分类。
    await using (var scope = host.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await DatabaseInitializer.InitializeAsync(db);
    }
    SidecarLog.Info("数据库初始化完成，MCP stdio 宿主启动");

    await host.RunAsync();
    SidecarLog.Info("宿主正常退出");
}
catch (Exception ex)
{
    SidecarLog.Error("启动/运行失败", ex);
    throw;
}
