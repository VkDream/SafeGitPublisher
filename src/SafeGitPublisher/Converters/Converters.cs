using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Converters;

/// <summary>主题色定义（集中管理，便于统一改色）。</summary>
public static class UiPalette
{
    public static Brush Pass = Hex("#2E7D32");      // 绿色
    public static Brush Warning = Hex("#E65100");   // 橙色
    public static Brush Blocked = Hex("#C62828");   // 红色
    public static Brush Info = Hex("#455A64");      // 蓝灰
    public static Brush Accent = Hex("#1565C0");    // 主蓝
    public static Brush TextPrimary = Hex("#1F2328");
    public static Brush TextSecondary = Hex("#57606A");
    public static Brush Border = Hex("#DDE1E6");
    public static Brush CardBackground = Hex("#FFFFFF");
    public static Brush PageBackground = Hex("#F5F6F8");

    private static Brush Hex(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>CheckStatus → 前景色。</summary>
public sealed class CheckStatusToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            CheckStatus.Pass => UiPalette.Pass,
            CheckStatus.Warning => UiPalette.Warning,
            CheckStatus.Blocked => UiPalette.Blocked,
            _ => UiPalette.Info
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>CheckStatus → Segoe MDL2 图标。</summary>
public sealed class CheckStatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            CheckStatus.Pass => "\uE73E",   // CheckMark
            CheckStatus.Warning => "\uE7BA", // Warning
            CheckStatus.Blocked => "\uEA39", // Blocked
            _ => "\uE946"                    // Info
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>RiskLevel → 前景色。</summary>
public sealed class RiskToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            RiskLevel.Warning => UiPalette.Warning,
            RiskLevel.Blocked => UiPalette.Blocked,
            _ => UiPalette.TextSecondary
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>RiskLevel → 图标（! 或 x）。</summary>
public sealed class RiskToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            RiskLevel.Warning => "\uE7BA",
            RiskLevel.Blocked => "\uEA39",
            _ => string.Empty
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>ScanSeverity → 颜色。</summary>
public sealed class SeverityToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            ScanSeverity.Blocked => UiPalette.Blocked,
            ScanSeverity.High or ScanSeverity.Warning => UiPalette.Warning,
            _ => UiPalette.Info
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>LogLevel → 颜色。</summary>
public sealed class LogLevelToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            LogLevel.Pass => UiPalette.Pass,
            LogLevel.Warn => UiPalette.Warning,
            LogLevel.Blocked => UiPalette.Blocked,
            LogLevel.Error => UiPalette.Blocked,
            LogLevel.Ready => UiPalette.Accent,
            _ => UiPalette.TextSecondary
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>bool → Visibility（可选反转）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>字符串非空 → Visibility。</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>集合 Count 大于 0 → Visible（用于列表空状态切换）。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is int i && i > 0;
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Count 大于等于 Threshold（默认 50）→ Visible（用于“数量巨大仅展示前 N 个”提示）。</summary>
public sealed class CountGreaterThanConverter : IValueConverter
{
    public int Threshold { get; set; } = 50;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var exceed = value is int i && i > Threshold;
        return exceed ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>RiskLevel → 文字（NORMAL/WARNING/BLOCKED）。</summary>
public sealed class RiskToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            RiskLevel.Warning => "WARNING",
            RiskLevel.Blocked => "BLOCKED",
            _ => "NORMAL"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>字符串 → 系统列显示（默认是空字符串的小圆点）。</summary>
public sealed class StringFallbackConverter : IValueConverter
{
    public string Fallback { get; set; } = "-";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Fallback : value!;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}