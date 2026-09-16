using System.IO;

namespace PromptPal_App;

/// <summary>
/// 极简文件日志：写到数据目录 logs 子目录（%LOCALAPPDATA%\PromptPal\logs），
/// 按天分文件：PromptPal-yyyy-MM-dd.log（每天只创建一个文件，跨午夜自动切到新文件）。
/// 关闭方式（任一即可，调试完选一种）：
///   1) 在 exe 同目录新建空文件 nolog.txt；
///   2) 设环境变量 PROMPTPAL_LOG=0；
/// 日志写入本身永不抛异常，绝不影响主程序。
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();

    /// <summary>日志目录（启动时确定一次；目录创建失败则降级到临时目录）。</summary>
    private static readonly string LogsDir = ResolveLogsDir();

    /// <summary>当天日志文件的完整路径；按 DateTime.Now 实时计算，跨天自动换文件。</summary>
    public static string LogPath => System.IO.Path.Combine(LogsDir, $"PromptPal-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>日志目录（设置界面展示用）。</summary>
    public static string LogDirectory => LogsDir;

    public static bool Enabled { get; } = ResolveEnabled();

    private static bool ResolveEnabled()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("PROMPTPAL_LOG");
            if (!string.IsNullOrEmpty(env)) return env.Trim() != "0";
            if (File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "nolog.txt"))) return false;
        }
        catch { }
        return true;
    }

    private static string ResolveLogsDir()
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PromptPal", "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch { }
        try
        {
            var fallback = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PromptPal", "logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
        catch { return System.IO.Path.GetTempPath(); }
    }

    public static void Milestone(string name) => Write("STEP ", name);
    public static void Info(string msg) => Write("INFO ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(string msg, Exception ex) => Write("ERROR", msg + Environment.NewLine + ex);

    /// <summary>每次启动写一条分隔头，附带版本/架构/系统信息，方便区分是第几次运行。</summary>
    public static void SessionHeader()
    {
        try
        {
            var ver = typeof(Logger).Assembly.GetName().Version?.ToString() ?? "?";
            Write("=====", $"PromptPal 启动 | exe={AppContext.BaseDirectory} | 版本={ver} | " +
                  $"64位={Environment.Is64BitProcess} | OS={Environment.OSVersion} | .NET={Environment.Version} | " +
                  $"日志目录={LogsDir}（按天一个文件 PromptPal-yyyy-MM-dd.log）");
        }
        catch { }
    }

    private static void Write(string level, string msg)
    {
        if (!Enabled) return;
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}";
            lock (Gate) { File.AppendAllText(LogPath, line); }
        }
        catch { /* 日志失败绝不能拖垮程序 */ }
    }
}
