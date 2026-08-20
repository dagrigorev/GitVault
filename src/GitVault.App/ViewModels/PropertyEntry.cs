using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>How a property value should be presented.</summary>
internal enum PropertyStyle
{
    /// <summary>Ordinary text.</summary>
    Text = 0,

    /// <summary>Monospaced: paths, fingerprints, configuration values.</summary>
    Mono,

    /// <summary>A boxed one-word state in the neutral colour.</summary>
    Badge,

    /// <summary>A boxed one-word state, good.</summary>
    BadgeOk,

    /// <summary>A boxed one-word state, needs attention.</summary>
    BadgeWarn,
}

/// <summary>
/// One label-and-value row in the properties pane.
/// </summary>
/// <remarks>
/// The label is a resource key rather than text so the pane retranslates with everything else,
/// and the value is a string the page has already formatted. Nothing here is ever a secret: the
/// pages put fingerprints, paths and states into a property entry, and say "hidden by design"
/// where the underlying artifact holds private material.
/// </remarks>
internal sealed class PropertyEntry : ObservableObject
{
    /// <summary>Creates an entry.</summary>
    /// <param name="localizer">Bindable localizer.</param>
    /// <param name="labelKey">Resource key of the label.</param>
    /// <param name="value">Already-formatted value.</param>
    /// <param name="style">How to present the value.</param>
    internal PropertyEntry(Localizer localizer, string labelKey, string? value, PropertyStyle style = PropertyStyle.Text)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        L = localizer;
        LabelKey = labelKey;
        Value = value ?? string.Empty;
        Style = style;
    }

    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; }

    /// <summary>Resource key of the label.</summary>
    public string LabelKey { get; }

    /// <summary>Localized label.</summary>
    public string Label => L[LabelKey];

    /// <summary>The value, as the page formatted it.</summary>
    public string Value { get; }

    /// <summary>How the value is presented.</summary>
    public PropertyStyle Style { get; }

    /// <summary>True when the value is drawn monospaced.</summary>
    public bool IsMono => Style == PropertyStyle.Mono;

    /// <summary>True when the value is drawn as a badge.</summary>
    public bool IsBadge => Style is PropertyStyle.Badge or PropertyStyle.BadgeOk or PropertyStyle.BadgeWarn;

    /// <summary>True when the value is drawn as plain text.</summary>
    public bool IsText => !IsMono && !IsBadge;

    /// <summary>True when the badge is the "good" variant.</summary>
    public bool IsBadgeOk => Style == PropertyStyle.BadgeOk;

    /// <summary>True when the badge is the "needs attention" variant.</summary>
    public bool IsBadgeWarn => Style == PropertyStyle.BadgeWarn;

    /// <summary>True when the badge is neutral.</summary>
    public bool IsBadgeDim => Style == PropertyStyle.Badge;

    /// <summary>Re-reads the label. Called when the culture changes.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));

    /// <summary>Renders the entry as one line, for the clipboard.</summary>
    /// <returns>Label and value separated by a tab.</returns>
    public override string ToString() => Label + "\t" + Value;
}
