using System.IO;
using System.Text.Json;

namespace PromptPal_App;

/// <summary>应用偏好（存于 %LocalAppData%\PromptPal\settings.json）</summary>
public sealed class AppSettings
{
    public string ThemeMode { get; set; } = "system";   // system | light | dark
    public string FontSize { get; set; } = "default";   // small | default | large
    public bool MinimizeToTray { get; set; } = true;
    public bool MinimizeOnStartup { get; set; }
    public bool AutoHideAfterCopy { get; set; }
    public bool ShowUseCount { get; set; } = true;
    public bool HotkeyEnabled { get; set; } = true;
    public bool RunAtStartup { get; set; }

    // 主窗口位置/尺寸（物理像素，null 表示从未保存过，使用默认窗口大小）
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    // 设置窗口位置/尺寸（物理像素；null 用默认 860×620 居中）
    public int? SettingsX { get; set; }
    public int? SettingsY { get; set; }
    public int? SettingsWidth { get; set; }
    public int? SettingsHeight { get; set; }

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptPal");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { Current = new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 原子写：先写临时文件再替换，避免写一半进程崩溃/断电导致 settings.json 截断损坏
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Logger.Error("设置保存失败", ex); }
    }
}
