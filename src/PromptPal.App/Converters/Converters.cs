using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace PromptPal_App.Converters;

/// <summary>非空字符串 → Visible（空串/null = Collapsed），用于状态条瞬时反馈</summary>
public sealed class StringToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>bool → 状态条画刷：true(错误)=DangerText，false(成功)=SuccessText，跟随当前主题</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b
            ? ThemeRes.Brush("DangerText", Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C))
            : ThemeRes.Brush("SuccessText", Windows.UI.Color.FromArgb(0xFF, 0x10, 0x7C, 0x10));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility（true = Visible）</summary>
public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility（反转：true = Collapsed）</summary>
public sealed class InvertBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>bool → 导航选中态画刷（true = 选中浅蓝，false = 透明）</summary>
public sealed class SelectedNavBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b
            ? ThemeRes.Brush("SelectionBg", Windows.UI.Color.FromArgb(0xFF, 0xCC, 0xE4, 0xF7))
            : new SolidColorBrush(Colors.Transparent);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>object → Visibility（非 null = Visible；parameter=invert 反转）</summary>
public sealed class ObjectToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is not null;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

// ========== 主题感知配色（读取 App.xaml 主题字典，自动跟随 Windows 明暗/强调色） ==========

internal static class ThemeRes
{
    // 必须按“应用内实际主题”取色：Application.Current.Resources[key] 只跟随系统主题，
    // 系统深色 + 应用内强制浅色时会错取成白字/深底（转换器没有元素上下文可用）。
    public static Brush Brush(string key, Windows.UI.Color fallback)
        => AppPrefs.ThemeBrush(key, AppPrefs.UiTheme)
           ?? new SolidColorBrush(fallback);
}

/// <summary>chip 选中态背景：true = 强调色，false = chip 常规底色</summary>
public sealed class ChipBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b
            ? ThemeRes.Brush("Accent", Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD7))
            : ThemeRes.Brush("ChipBg", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>chip 选中态文字：true = 强调色上的对比色（随强调色亮度自动白/黑），false = 正文色</summary>
public sealed class ChipForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b
            ? ThemeRes.Brush("AccentOnText", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
            : ThemeRes.Brush("TextDefault", Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>分类胶囊/排序按钮背景：true = 选中浅蓝，false = chip 常规底色</summary>
public sealed class SelectedOverlayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b
            ? ThemeRes.Brush("SelectionBg", Windows.UI.Color.FromArgb(0xFF, 0xCC, 0xE4, 0xF7))
            : ThemeRes.Brush("ChipBg", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>导航文字色：选中 = 正文色；未选中默认次级色，parameter="sort" 时三级色</summary>
public sealed class NavForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b && b)
            return ThemeRes.Brush("TextDefault", Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
        return (parameter as string) == "sort"
            ? ThemeRes.Brush("TextTertiary", Windows.UI.Color.FromArgb(0xFF, 0x76, 0x76, 0x76))
            : ThemeRes.Brush("TextSecondary", Windows.UI.Color.FromArgb(0xFF, 0x40, 0x40, 0x40));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
