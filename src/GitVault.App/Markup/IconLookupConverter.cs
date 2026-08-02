using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using GitVault.Core.Models;

namespace GitVault.App.Markup;

/// <summary>
/// Resolves an icon resource key, such as <c>IconKeys</c>, to the geometry behind it.
/// </summary>
/// <remarks>
/// This exists so a view model can say <em>which</em> icon it wants without referencing a
/// drawing type. The view models stay free of visuals, and the icon set stays swappable by
/// regenerating one resource dictionary.
/// </remarks>
internal sealed class IconLookupConverter : IValueConverter
{
    /// <summary>Shared instance, referenced from XAML.</summary>
    public static IconLookupConverter Instance { get; } = new();

    /// <summary>Looks a resource up in the application's dictionaries.</summary>
    /// <param name="key">Resource key.</param>
    /// <returns>The resource, or null when it is not present.</returns>
    internal static object? FindResource(object key)
    {
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        var theme = application.ActualThemeVariant ?? ThemeVariant.Default;
        return application.Resources.TryGetResource(key, theme, out var resource) ? resource : null;
    }

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } key ? FindResource(key) as Geometry : null;

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Icon lookup is one-way.");
}

/// <summary>Maps a warning severity to the icon that represents it.</summary>
internal sealed class SeverityIconConverter : IValueConverter
{
    /// <summary>Shared instance, referenced from XAML.</summary>
    public static SeverityIconConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is WarningSeverity severity
            ? severity switch
            {
                WarningSeverity.High => "IconSeverityHigh",
                WarningSeverity.Medium => "IconSeverityMedium",
                _ => "IconSeverityLow",
            }
            : "IconSeverityLow";

        return IconLookupConverter.FindResource(key) as Geometry;
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Severity lookup is one-way.");
}

/// <summary>Maps a warning severity to the brush that colours its icon.</summary>
internal sealed class SeverityBrushConverter : IValueConverter
{
    /// <summary>Shared instance, referenced from XAML.</summary>
    public static SeverityBrushConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // These are GitVault's own resources, defined per theme variant in App.axaml. Borrowing
        // the theme's internal names instead would risk a silent miss, and a missing brush makes
        // the icon render invisible rather than fail loudly.
        var key = value is WarningSeverity severity
            ? severity switch
            {
                WarningSeverity.High => "SeverityHighBrush",
                WarningSeverity.Medium => "SeverityMediumBrush",
                _ => "SeverityLowBrush",
            }
            : "SeverityLowBrush";

        // Falling back to the inherited foreground beats painting nothing at all.
        return IconLookupConverter.FindResource(key) as IBrush ?? (object)AvaloniaProperty.UnsetValue;
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Severity lookup is one-way.");
}
