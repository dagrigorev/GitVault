using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Settings;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>A selectable UI language. The label is the language's own endonym, never translated.</summary>
/// <param name="Culture">The culture.</param>
/// <param name="Label">Endonym shown in the picker.</param>
internal sealed record LanguageOption(CultureInfo Culture, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>A selectable theme, with a localized label.</summary>
internal sealed class ThemeOption(Localizer localizer, ThemePreference value, string labelKey)
    : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The preference this option selects.</summary>
    public ThemePreference Value { get; } = value;

    /// <summary>Resource key of the label.</summary>
    public string LabelKey { get; } = labelKey;

    /// <summary>Localized label.</summary>
    public string Label => L[LabelKey];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads <see cref="Label"/>. Called when the culture changes.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}

/// <summary>Language, theme, scan and privacy settings. Every change persists immediately.</summary>
internal sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;
    private readonly IShellLauncher _shell;
    private readonly IPlatformPaths _paths;
    private readonly IDiagnosticsBundleBuilder _diagnostics;
    private readonly ScanCoordinator _scans;

    /// <summary>
    /// Set while the persisted settings are being copied into the bound properties. Suppresses
    /// both persistence and the side effects (culture switch, theme switch) that a real user
    /// edit triggers, so merely constructing this page never changes application state.
    /// </summary>
    private bool _isLoading;

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    [ObservableProperty]
    private ThemeOption? _selectedTheme;

    [ObservableProperty]
    private bool _dryRunByDefault;

    [ObservableProperty]
    private bool _watchForChanges;

    [ObservableProperty]
    private string? _diagnosticsStatus;

    public SettingsViewModel(
        Localizer localizer,
        ISettingsService settings,
        ILocalizationService localization,
        IShellLauncher shell,
        IPlatformPaths paths,
        IDiagnosticsBundleBuilder diagnostics,
        ScanCoordinator scans)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(scans);

        _settings = settings;
        _localization = localization;
        _shell = shell;
        _paths = paths;
        _diagnostics = diagnostics;
        _scans = scans;

        Languages =
        [
            new LanguageOption(CultureInfo.GetCultureInfo("en-US"), "English"),
            new LanguageOption(CultureInfo.GetCultureInfo("ru-RU"), "Русский"),
            new LanguageOption(CultureInfo.GetCultureInfo("zh-Hans"), "简体中文"),
        ];

        Themes =
        [
            new ThemeOption(localizer, ThemePreference.System, Keys.Settings_Theme_System),
            new ThemeOption(localizer, ThemePreference.Light, Keys.Settings_Theme_Light),
            new ThemeOption(localizer, ThemePreference.Dark, Keys.Settings_Theme_Dark),
        ];

        LoadFrom(_settings.Current);
    }

    /// <summary>Raised when the user picks a different theme, so the shell can apply it.</summary>
    internal event EventHandler<ThemePreference>? ThemeChangeRequested;

    public override string NavKey => Keys.Nav_Settings;

    public override string TitleKey => Keys.Settings_Title;

    /// <inheritdoc/>
    public override string IconKey => "IconSettings";

    /// <summary>Available UI languages.</summary>
    public ObservableCollection<LanguageOption> Languages { get; }

    /// <summary>Available themes.</summary>
    public ObservableCollection<ThemeOption> Themes { get; }

    /// <summary>Localized statement that GitVault collects nothing.</summary>
    public string TelemetryCaption => L[Keys.Settings_TelemetryNone];

    /// <summary>Localized auto-hide description, with the configured number of seconds.</summary>
    public string AutoHideCaption =>
        L.Format(Keys.Settings_AutoHideSeconds, _settings.Current.RevealPolicy.AutoHideSeconds);

    /// <summary>Localized clipboard-clear description, with the configured number of seconds.</summary>
    public string ClipboardClearCaption =>
        L.Format(Keys.Settings_ClipboardClearSeconds, _settings.Current.RevealPolicy.ClipboardClearSeconds);

    /// <summary>Log directory, shown verbatim.</summary>
    public string LogDirectory => _paths.LogDirectory;

    /// <summary>
    /// Entries the diagnostics bundle would contain. Populated by the preview, and shown in full
    /// so the user can read every byte before deciding to save it.
    /// </summary>
    public ObservableCollection<DiagnosticsItem> DiagnosticsItems { get; } = [];

    /// <summary>True once a bundle has been previewed and can therefore be saved.</summary>
    public bool CanSaveDiagnostics => DiagnosticsItems.Count > 0;

    /// <summary>Localized explanation of what the bundle does and does not contain.</summary>
    public string DiagnosticsNote => L[Keys.Settings_DiagnosticsNote];

    /// <summary>Opens the log directory in the platform file manager.</summary>
    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(_paths.LogDirectory);
        _shell.OpenDirectory(_paths.LogDirectory);
    }

    /// <summary>
    /// Assembles the diagnostics bundle and shows exactly what it contains. Nothing is written.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the preview is on screen.</returns>
    [RelayCommand]
    private async Task PreviewDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var items = await _diagnostics
            .PreviewAsync(_scans.Report, cancellationToken)
            .ConfigureAwait(true);

        DiagnosticsItems.Clear();
        foreach (var item in items)
        {
            DiagnosticsItems.Add(item);
        }

        DiagnosticsStatus = null;
        OnPropertyChanged(nameof(CanSaveDiagnostics));
    }

    /// <summary>Writes the previewed bundle into the log directory.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the archive exists.</returns>
    [RelayCommand]
    private async Task SaveDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (DiagnosticsItems.Count == 0)
        {
            return;
        }

        var destination = Path.Combine(
            _paths.LogDirectory,
            DiagnosticsBundleBuilder.BuildFileName(DateTimeOffset.UtcNow));

        await _diagnostics
            .WriteAsync([.. DiagnosticsItems], destination, cancellationToken)
            .ConfigureAwait(true);

        DiagnosticsStatus = L.Format(Keys.Settings_DiagnosticsSaved, destination);
    }

    /// <summary>Applies persisted settings to the bound properties without re-persisting them.</summary>
    private void LoadFrom(AppSettings settings)
    {
        _isLoading = true;
        try
        {
            // The culture in effect wins over the stored name: startup has already applied it,
            // and re-applying it here would clobber a culture set by anything else.
            var culture = _localization.CurrentCulture;
            SelectedLanguage = Languages.FirstOrDefault(l =>
                string.Equals(l.Culture.Name, culture.Name, StringComparison.OrdinalIgnoreCase))
                ?? Languages[0];
            SelectedTheme = Themes.First(t => t.Value == settings.Theme);
            DryRunByDefault = settings.DryRunByDefault;
            WatchForChanges = settings.WatchForChanges;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void Persist(Action<AppSettings> mutate)
    {
        if (_isLoading)
        {
            return;
        }

        var updated = _settings.Current.Clone();
        mutate(updated);

        // Fire and forget: settings are small, and a failed write must not block the UI thread.
        _ = _settings.SaveAsync(updated, CancellationToken.None);
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value is null || _isLoading)
        {
            return;
        }

        _localization.SetCulture(value.Culture);
        Persist(s => s.Language = value.Culture.Name);
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value is null || _isLoading)
        {
            return;
        }

        ThemeChangeRequested?.Invoke(this, value.Value);
        Persist(s => s.Theme = value.Value);
    }

    partial void OnDryRunByDefaultChanged(bool value) => Persist(s => s.DryRunByDefault = value);

    partial void OnWatchForChangesChanged(bool value) => Persist(s => s.WatchForChanges = value);

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        // The blanket notification from the base class covers this view model's own members;
        // the combo box items are separate objects and need their own notification.
        foreach (var theme in Themes)
        {
            theme.RefreshCaptions();
        }
    }
}
