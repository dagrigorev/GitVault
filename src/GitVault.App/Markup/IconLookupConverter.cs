using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using GitVault.Core.Models;

namespace GitVault.App.Markup;

/// <summary>
/// Resolves an icon resource key, such as <c>IconKeys</c>, to the image behind it.
/// </summary>
/// <remarks>
/// This exists so a view model can say <em>which</em> icon it wants without referencing a drawing
/// type. The view models stay free of visuals, and the icon set stays swappable by regenerating
/// one resource dictionary — which is exactly what happened when the set changed from Material
/// Symbols to Tango, turning every icon from a geometry into a bitmap without touching a single
/// view model.
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
        value is string { Length: > 0 } key ? FindResource(key) as IImage : null;

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
        // Tango has a dialog icon per severity, which is precisely the classic vocabulary: a red
        // cross, a yellow triangle and a blue "i". No colour needs to be applied on top.
        var key = value is WarningSeverity severity
            ? severity switch
            {
                WarningSeverity.High => "IconSeverityHigh",
                WarningSeverity.Medium => "IconSeverityMedium",
                _ => "IconSeverityLow",
            }
            : "IconSeverityLow";

        return IconLookupConverter.FindResource(key) as IImage;
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Severity lookup is one-way.");
}
