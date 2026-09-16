using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PromptPal_App;

/// <summary>把字号/显示次数/强调色上的文字色偏好写入 Application 资源。</summary>
public static class AppPrefs
{
    public static void ApplyFonts()
    {
        var r = Application.Current.Resources;
        var f = AppSettings.Current.FontSize;
        r["BodyFont"] = f switch { "small" => 13.0, "large" => 18.0, _ => 15.0 };
        r["ListTitleFont"] = f switch { "small" => 13.0, "large" => 16.0, _ => 14.0 };
        r["ListBodyFont"] = f switch { "small" => 11.0, "large" => 14.0, _ => 12.0 };
        r["DetailTitleFont"] = f switch { "small" => 16.0, "large" => 21.0, _ => 18.0 };
    }

    public static void ApplyUseCount()
        => Application.Current.Resources["UseCountVisibility"] =
            AppSettings.Current.ShowUseCount ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 用户可能在系统设置里选很浅（或很深）的强调色：强调色底上的文字必须按强调色亮度取黑/白，
    /// 否则浅色强调色 + 白字会出现"字体和背景同色"。相对亮度按 sRGB 线性化近似计算。
    /// </summary>
    public static void ApplyAccent()
    {
        try
        {
            var r = Application.Current.Resources;
            if (r["SystemAccentColor"] is not Color accent) return;
            double lum = 0.2126 * Lin(accent.R) + 0.7152 * Lin(accent.G) + 0.0722 * Lin(accent.B);
            Color on = lum > 0.55 ? Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A) : Colors.White;
            if (r["AccentOnText"] is SolidColorBrush b) b.Color = on;
            else r["AccentOnText"] = new SolidColorBrush(on);
        }
        catch { /* 拿不到系统强调色时沿用默认白字 */ }

        static double Lin(byte v)
        {
            double c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    public static void ApplyAll()
    {
        ApplyAccent();
        ApplyFonts();
        ApplyUseCount();
    }

    /// <summary>
    /// 全应用当前实际生效的元素主题（用户设置 light/dark，跟随系统时取应用级 RequestedTheme）。
    /// 转换器拿不到目标元素的 ActualTheme，统一用它从 ThemeDictionaries 取色，
    /// 否则系统深色 + 应用内强制浅色时会错误地取到深色字典的白字/浅线。
    /// </summary>
    public static ElementTheme UiTheme
    {
        get
        {
            var mode = AppSettings.Current?.ThemeMode;
            return mode switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark
                    ? ElementTheme.Dark
                    : ElementTheme.Light,
            };
        }
    }

    /// <summary>
    /// 按元素的 ActualTheme 从 ThemeDictionaries 取画刷。代码后置无法使用 {ThemeResource}，
    /// 直接读 Application.Current.Resources 只会拿到应用级（跟随系统）主题的画刷，
    /// 在用户手动选浅/深色时会错色，故统一走这里。
    /// </summary>
    public static Brush? ThemeBrush(string key, ElementTheme theme)
    {
        try
        {
            string dictKey = theme == ElementTheme.Light ? "Light" : "Dark";
            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(dictKey, out var d)
                && d is ResourceDictionary rd
                && rd.TryGetValue(key, out var v) && v is Brush b)
            {
                return b;
            }
        }
        catch { }
        return Application.Current.Resources.TryGetValue(key, out var fb) && fb is Brush fb2 ? fb2 : null;
    }
}
