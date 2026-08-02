using System.Globalization;
using System.Resources;

namespace GitVault.Localization;

/// <summary>Supplies localized strings and owns the current UI culture.</summary>
public interface ILocalizationService
{
    /// <summary>Cultures the application ships translations for.</summary>
    IReadOnlyList<CultureInfo> SupportedCultures { get; }

    /// <summary>Culture currently in effect.</summary>
    CultureInfo CurrentCulture { get; }

    /// <summary>Raised after <see cref="CurrentCulture"/> changes.</summary>
    event EventHandler? CultureChanged;

    /// <summary>Switches the UI culture. No-op when the culture is already active.</summary>
    /// <param name="culture">Culture to switch to. Unsupported cultures fall back to the neutral one.</param>
    void SetCulture(CultureInfo culture);

    /// <summary>Looks up a string.</summary>
    /// <param name="key">Resource key, normally a <see cref="Keys"/> constant.</param>
    /// <returns>The translated string, or <c>!key!</c> when the key is missing.</returns>
    string Get(string key);

    /// <summary>Looks up a composite format string and fills it in.</summary>
    /// <param name="key">Resource key of a format string.</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>The formatted string, using <see cref="CurrentCulture"/> for numbers and dates.</returns>
    string Format(string key, params object?[] args);

    /// <summary>
    /// Formats a count using the plural rules of the current culture. Looks up
    /// <c>{keyPrefix}_One</c>, <c>_Few</c>, <c>_Many</c> or <c>_Other</c>.
    /// </summary>
    /// <param name="keyPrefix">Key prefix, e.g. <c>Plural_Keys</c>.</param>
    /// <param name="count">The number being described.</param>
    /// <returns>The pluralized, formatted string.</returns>
    string Plural(string keyPrefix, long count);
}

/// <summary>
/// <see cref="ResourceManager"/>-backed implementation. Lookups never throw: a missing key
/// produces a visible marker so the parity test and a human reviewer both catch it.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    /// <summary>Base name of the embedded resource set.</summary>
    public const string ResourceBaseName = "GitVault.Localization.Resources.Strings";

    private static readonly CultureInfo[] Supported =
    [
        CultureInfo.GetCultureInfo("en-US"),
        CultureInfo.GetCultureInfo("ru-RU"),
        CultureInfo.GetCultureInfo("zh-Hans"),
    ];

    private readonly ResourceManager _resources;
    private readonly IPluralizer _pluralizer;
    private CultureInfo _current = Supported[0];

    /// <summary>Creates the service.</summary>
    /// <param name="pluralizer">Plural rule provider.</param>
    public LocalizationService(IPluralizer pluralizer)
    {
        ArgumentNullException.ThrowIfNull(pluralizer);
        _pluralizer = pluralizer;
        _resources = new ResourceManager(ResourceBaseName, typeof(LocalizationService).Assembly);
    }

    /// <inheritdoc/>
    public IReadOnlyList<CultureInfo> SupportedCultures => Supported;

    /// <inheritdoc/>
    public CultureInfo CurrentCulture => _current;

    /// <inheritdoc/>
    public event EventHandler? CultureChanged;

    /// <summary>Resolves a culture name to a supported culture, falling back to English.</summary>
    /// <param name="name">BCP-47 culture name.</param>
    /// <returns>A supported culture.</returns>
    public static CultureInfo ResolveSupported(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Supported[0];
        }

        foreach (var candidate in Supported)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        // Accept a neutral name such as "ru" or "zh" and map it to the shipped variant.
        foreach (var candidate in Supported)
        {
            if (string.Equals(candidate.TwoLetterISOLanguageName, name[..Math.Min(2, name.Length)],
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return Supported[0];
    }

    /// <inheritdoc/>
    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var resolved = ResolveSupported(culture.Name);
        if (string.Equals(resolved.Name, _current.Name, StringComparison.Ordinal))
        {
            return;
        }

        _current = resolved;
        CultureInfo.DefaultThreadCurrentCulture = resolved;
        CultureInfo.DefaultThreadCurrentUICulture = resolved;
        CultureInfo.CurrentCulture = resolved;
        CultureInfo.CurrentUICulture = resolved;

        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        try
        {
            return _resources.GetString(key, _current) ?? Missing(key);
        }
        catch (MissingManifestResourceException)
        {
            return Missing(key);
        }
    }

    /// <inheritdoc/>
    public string Format(string key, params object?[] args)
    {
        var template = Get(key);
        if (args is null || args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(_current, template, args);
        }
        catch (FormatException)
        {
            // A translation with a broken placeholder must not crash the UI.
            return template;
        }
    }

    /// <inheritdoc/>
    public string Plural(string keyPrefix, long count)
    {
        var suffix = _pluralizer.Select(_current, count) switch
        {
            PluralCategory.One => "_One",
            PluralCategory.Few => "_Few",
            PluralCategory.Many => "_Many",
            _ => "_Other",
        };

        return Format(keyPrefix + suffix, count);
    }

    private static string Missing(string key) => "!" + key + "!";
}
