using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace OnePace.App;

public enum ChipKind { None, Neutral, Sub, Dub, Ok }

/// <summary>Small tag shown on rows and cards.</summary>
public sealed class Chip : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(Chip), new PropertyMetadata(""));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(ChipKind), typeof(Chip), new PropertyMetadata(ChipKind.Neutral));

    static Chip() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Chip), new FrameworkPropertyMetadata(typeof(Chip)));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public ChipKind Kind { get => (ChipKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool set = value is string s ? s.Length > 0 : value != null;
        return set ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>True becomes "Selected", which the NavButton style highlights.</summary>
public sealed class SelectedTag : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "Selected" : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Colours WPF-UI has no token for, kept in step with the theme and
/// the Windows accent colour.</summary>
public static class ThemeBrushes
{
    public static void Apply(bool dark)
    {
        var res = Application.Current.Resources;
        var accent = res["SystemAccentColor"] is Color c ? c : Color.FromRgb(0x00, 0x78, 0xD4);
        var light1 = res["SystemAccentColorLight1"] is Color l1 ? l1 : accent;
        var light2 = res["SystemAccentColorLight2"] is Color l2 ? l2 : accent;
        var dark1 = res["SystemAccentColorDark1"] is Color d1 ? d1 : accent;
        var dark2 = res["SystemAccentColorDark2"] is Color d2 ? d2 : accent;

        Set("SelectionPillBrush", dark ? light2 : dark1);
        Set("ChipSubBackground", dark ? Color.FromArgb(0x40, light1.R, light1.G, light1.B)
                                      : Color.FromArgb(0x26, accent.R, accent.G, accent.B));
        Set("ChipSubForeground", dark ? light2 : dark2);
        Set("ChipDubBackground", dark ? Color.FromRgb(0x3D, 0x2F, 0x57) : Color.FromRgb(0xEE, 0xE5, 0xFA));
        Set("ChipDubForeground", dark ? Color.FromRgb(0xD7, 0xB8, 0xFF) : Color.FromRgb(0x6B, 0x3F, 0xA0));

        void Set(string key, Color color)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            res[key] = b;
        }
    }
}
