using Avalonia.Data;
using Avalonia.Markup.Xaml;
using GitVault.Localization;

namespace GitVault.App.Markup;

/// <summary>
/// Holds the process-wide <see cref="Localizer"/> so that <see cref="TrExtension"/> can reach it
/// from XAML, where constructor injection is not available.
/// </summary>
internal static class LocalizationHost
{
    /// <summary>The localizer every <c>{loc:Tr}</c> binding attaches to.</summary>
    internal static Localizer? Current { get; set; }
}

/// <summary>
/// XAML markup extension producing a live binding to a localized string:
/// <c>&lt;TextBlock Text="{loc:Tr Keys_Fingerprint}" /&gt;</c>.
/// </summary>
/// <remarks>
/// It returns a binding rather than a string so that switching the language re-evaluates
/// every use without recreating the visual tree.
/// </remarks>
internal sealed class TrExtension : MarkupExtension
{
    /// <summary>Creates an extension with no key yet; the key is set by the XAML parser.</summary>
    public TrExtension()
    {
    }

    /// <summary>Creates an extension for a key.</summary>
    /// <param name="key">Resource key.</param>
    public TrExtension(string key) => Key = key;

    /// <summary>Resource key to look up, normally a <see cref="Keys"/> constant name.</summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var localizer = LocalizationHost.Current;
        if (localizer is null)
        {
            // Design time, or a view built before the host is up: show the key, so an omission
            // is obvious rather than silently rendering an empty label.
            return "!" + Key + "!";
        }

        // Bind to the entry's plain Value property, not to Localizer["key"]. An indexer binding
        // relies on the framework re-evaluating when the source raises a blanket
        // PropertyChanged("Item[]"); Avalonia does not, and the visible result was a window whose
        // navigation rail translated while every caption written in XAML stayed in English.
        return new Binding(nameof(LocalizedString.Value))
        {
            Source = localizer.Entry(Key),
            Mode = BindingMode.OneWay,
        };
    }
}
