using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AccessOverlay.App.Rendering;

/// <summary>Indents blocks by their nesting depth so hierarchy is visible.</summary>
public sealed class DepthMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var depth = value is int d ? d : 0;
        var unit = parameter is string s && int.TryParse(s, out var u) ? u : 22;
        return new Thickness(depth * unit, 2, 8, 2);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Scales a font size for heading levels (Level 1 = biggest).</summary>
public sealed class HeadingFontSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is int l ? l : 1;
        var baseSize = 20.0;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            baseSize = b;
        return Math.Max(14, baseSize + (4 - level) * 2.5);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Heading font size from (heading level, base size) for live font-size changes.</summary>
public sealed class HeadingFontSizeMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var level = values.Length > 0 && values[0] is int l ? l : 1;
        var baseSize = values.Length > 1 && values[1] is double d ? d : 20.0;
        return Math.Max(14, baseSize + (4 - level) * 2.5);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
