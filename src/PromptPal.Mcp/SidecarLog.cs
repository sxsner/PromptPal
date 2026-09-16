using System.IO;

namespace PromptPal.Mcp;

/// <summary>
/// Sidecar 极简文件日志：写到数据目录 logs 子目录（%LOCALAPPDATA%\PromptPal\logs），
/// 按天分文件：mcp-yyyy-MM-dd.log，与主程序日志同目录、互补，
/// 便于在别的电脑上排查"Trae 拉起失败/工具无响应"类问题（Trae 面板只保留近期 stderr）。
/// 日志写入永不抛异常，绝不影响 stdio JSON-RPC（stdout 严格只走协议帧）。
/// 关闭方式：exe 同目录放 nolog.txt，或设环境变量 PROMPTPAL_MCP_LOG=0。
/// </summary>
public static class SidecarLog
{
    private static readonly object Gate = new();

    /// <summary>日志目录（启动时确定一次；目录创建失败则降级到临时目录）。</summary>
    private static readonly string LogsDir = ResolveLogsDir();

    /// <summary>当天日志文件的完整路径；按 DateTime.Now 实时计算，跨天自动换文件。</summary>
    public static string LogPath => Path.Combine(LogsDir, $"mcp-{DateTime.Now:yyyy-MM-dd}.log");

    private static bool ResolveEnabled()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("PROMPTPAL_MCP_LOG");
            if (!string.IsNullOrEmpty(env)) return env.Trim() != "0";
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "nolog.txt"))) return false;
        }
        catch { }
        return true;
    }

    public static bool Enabled { get; } = ResolveEnabled();

    private static string ResolveLogsDir()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PromptPal", "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch { }
        return Path.GetTempPath();
    }

    public static void SessionHeader(string dbPath)
    {
        var ver = typeof(SidecarLog).Assembly.GetName().Version?.ToString() ?? "?";
        Write("=====", $"PromptPal.Mcp 启动 | exe={AppContext.BaseDirectory} | 版本={ver} | " +
                  $"OS={Environment.OSVersion} | .NET={Environment.Version} | db={dbPath} | " +
                  $"日志目录={LogsDir}（按天一个文件 mcp-yyyy-MM-dd.log）");
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("ERROR", ex is null ? msg : msg + Environment.NewLine + ex);

    private static void Write(string level, string msg)
    {
        if (!Enabled) return;
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}";
            lock (Gate) { File.AppendAllText(LogPath, line); }
        }
        catch { }
    }
}
