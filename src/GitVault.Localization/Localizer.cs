using CommunityToolkit.Mvvm.ComponentModel;

namespace GitVault.Localization;

/// <summary>
/// Bindable façade over <see cref="ILocalizationService"/>. XAML binds to the indexer;
/// when the culture changes the indexer-wide change notification makes every binding re-read.
/// </summary>
public sealed partial class Localizer : ObservableObject, IDisposable
{
    /// <summary>
    /// The property name WPF and Avalonia interpret as "every indexed value changed".
    /// Mirrors <c>System.Windows.Data.Binding.IndexerName</c>, which is not available here.
    /// </summary>
    public const string IndexerName = "Item[]";

    private readonly ILocalizationService _localization;
    private readonly Dictionary<string, LocalizedString> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates the façade and subscribes to culture changes.</summary>
    /// <param name="localization">Backing localization service.</param>
    public Localizer(ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        _localization = localization;
        _localization.CultureChanged += OnCultureChanged;
    }

    /// <summary>Translated string for <paramref name="key"/>.</summary>
    /// <param name="key">Resource key, normally a <see cref="Keys"/> constant.</param>
    /// <returns>The translated string.</returns>
    public string this[string key] => _localization.Get(key);

    /// <summary>
    /// A per-key object whose <see cref="LocalizedString.Value"/> holds the current translation
    /// and raises a normal property change after every culture change.
    /// </summary>
    /// <remarks>
    /// XAML binds through this rather than through the indexer. An indexer binding relies on the
    /// host framework re-evaluating when the source raises a blanket
    /// <c>PropertyChanged("Item[]")</c>; Avalonia does not, and the visible result was a window
    /// whose navigation rail translated while every caption written in XAML stayed in English.
    /// A plain property leaves nothing to infer.
    ///
    /// Instances are cached per key, so the same caption used in ten views shares one object.
    /// </remarks>
    /// <param name="key">Resource key.</param>
    /// <returns>The bindable entry for that key.</returns>
    public LocalizedString Entry(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = entry = new LocalizedString(_localization.Get(key));
            }

            return entry;
        }
    }

    /// <summary>The underlying service, for callers that need formatting or plurals.</summary>
    public ILocalizationService Service => _localization;

    /// <summary>Formats a composite string. See <see cref="ILocalizationService.Format"/>.</summary>
    /// <param name="key">Resource key of a format string.</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>The formatted string.</returns>
    public string Format(string key, params object?[] args) => _localization.Format(key, args);

    /// <summary>Formats a count. See <see cref="ILocalizationService.Plural"/>.</summary>
    /// <param name="keyPrefix">Key prefix, e.g. <c>Plural_Keys</c>.</param>
    /// <param name="count">The number being described.</param>
    /// <returns>The pluralized string.</returns>
    public string Plural(string keyPrefix, long count) => _localization.Plural(keyPrefix, count);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _localization.CultureChanged -= OnCultureChanged;
        _disposed = true;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        // The indexer notification keeps working for anything that binds through it.
        OnPropertyChanged(IndexerName);

        // Every cached entry re-reads and raises its own property change, which is what actually
        // moves the captions written in XAML.
        lock (_entries)
        {
            foreach (var (key, entry) in _entries)
            {
                entry.Update(_localization.Get(key));
            }
        }
    }
}

/// <summary>
/// One translated caption, exposed as an ordinary bindable property.
/// </summary>
/// <remarks>
/// This exists because an indexer binding is not enough: it depends on the host framework
/// re-evaluating when the source raises a blanket <c>PropertyChanged("Item[]")</c>, and Avalonia
/// does not. A plain property raising a plain change notification works everywhere.
/// </remarks>
public sealed partial class LocalizedString : ObservableObject
{
    [ObservableProperty]
    private string _value;

    /// <summary>Creates the entry.</summary>
    /// <param name="value">Initial translation.</param>
    internal LocalizedString(string value) => _value = value;

    /// <summary>Replaces the translation, notifying anything bound to it.</summary>
    /// <param name="value">New translation.</param>
    internal void Update(string value) => Value = value;

    /// <inheritdoc/>
    public override string ToString() => Value;
}
