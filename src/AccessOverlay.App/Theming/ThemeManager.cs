using System.Windows;

namespace AccessOverlay.App.Theming;

/// <summary>
/// Swaps a window's theme ResourceDictionary at runtime.
/// Theme dictionaries live in the Themes folder: Light, Dark, HighContrast.
/// </summary>
public static class ThemeManager
{
    private const string ThemeKey = "AccessOverlayTheme";

    public static readonly string[] Names = { "Light", "Dark", "HighContrast" };

    public static void Apply(FrameworkElement target, string name)
    {
        var existing = target.Resources.MergedDictionaries.FirstOrDefault(d => d.Contains(ThemeKey));
        if (existing is not null)
            target.Resources.MergedDictionaries.Remove(existing);

        var uri = new Uri($"/AccessOverlay.App;component/Themes/{name}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        dict[ThemeKey] = true;
        target.Resources.MergedDictionaries.Add(dict);
    }
}
