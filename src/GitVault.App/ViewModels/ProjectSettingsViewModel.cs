using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// GitVault's own settings for one repository, edited in place.
/// </summary>
/// <remarks>
/// These live in a <c>[gitvault]</c> section of the repository's configuration, so saving them is
/// a write to <c>.git/config</c> like any other. It therefore takes the same route: plan, preview,
/// confirm, snapshot, apply. There is no quieter path for GitVault's own settings, because a
/// quieter path is how an application ends up with one standard for the user's data and another
/// for its own.
/// </remarks>
internal sealed partial class ProjectSettingsViewModel : PageViewModel
{
    private readonly IProjectSettingsStore _store;
    private readonly IConfigEditor _editor;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly RepositoryContext _repository;
    private readonly ScanCoordinator _scans;
    private readonly IProfileStore _profiles;

    /// <summary>Set while the form is being filled, so loading is not treated as an edit.</summary>
    private bool _isLoading;

    [ObservableProperty]
    private ProfileChoice? _selectedProfile;

    [ObservableProperty]
    private KeyChoice? _selectedKey;

    [ObservableProperty]
    private HelperOption? _selectedHelper;

    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    private bool _excludeFromScans;

    [ObservableProperty]
    private bool _isDirty;

    public ProjectSettingsViewModel(
        Localizer localizer,
        IProjectSettingsStore store,
        IConfigEditor editor,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository,
        ScanCoordinator scans,
        IProfileStore profiles)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(profiles);

        _store = store;
        _editor = editor;
        _dialogs = dialogs;
        _status = status;
        _repository = repository;
        _scans = scans;
        _profiles = profiles;

        Helpers =
        [
            new HelperOption(localizer, null),
            new HelperOption(localizer, "manager"),
            new HelperOption(localizer, "store"),
            new HelperOption(localizer, "cache"),
        ];

        _selectedHelper = Helpers[0];
        _repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_ProjectSettings;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Project_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Project_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconProfiles";

    /// <summary>Profiles this repository can be pinned to, plus a "none" entry.</summary>
    public ObservableCollection<ProfileChoice> Profiles { get; } = [];

    /// <summary>Keys this repository can be pinned to, plus a "none" entry.</summary>
    public ObservableCollection<KeyChoice> KeyChoices { get; } = [];

    /// <summary>Credential helpers on offer.</summary>
    public ObservableCollection<HelperOption> Helpers { get; }

    /// <summary>Name of the repository being edited.</summary>
    public string RepositoryName => _repository.CurrentName;

    /// <summary>Path of the repository being edited.</summary>
    public string RepositoryPath => _repository.CurrentPath ?? string.Empty;

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => _repository.HasRepository;

    /// <summary>Localized note stating where these settings are stored.</summary>
    public string StorageNoteCaption => L[Keys.Project_StorageNote];

    /// <summary>Localized prompt shown when no repository is selected.</summary>
    public string NoRepositoryCaption => L[Keys.Project_NoRepository];

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads the settings and refills the pickers.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the form is filled.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await RebuildChoicesAsync(cancellationToken).ConfigureAwait(true);

        if (!_repository.HasRepository)
        {
            Fill(null);
            return;
        }

        var settings = await _store
            .LoadAsync(_repository.CurrentPath!, cancellationToken)
            .ConfigureAwait(true);

        Fill(settings);
    }

    /// <summary>Saves the form after previewing exactly what it will write.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!_repository.HasRepository)
        {
            return;
        }

        var plan = await _store.PlanSaveAsync(Current(), cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_ProjectSettingsSaved, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Removes GitVault's section from the repository, after previewing it.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        if (!_repository.HasRepository)
        {
            return;
        }

        var plan = await _store.PlanClearAsync(_repository.CurrentPath!, cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_ProjectSettingsCleared, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Discards unsaved edits by re-reading what is stored.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the form is refilled.</returns>
    [RelayCommand]
    private Task RevertAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    private async Task ReviewAndApplyAsync(
        GitOperationPlan plan,
        string successKey,
        CancellationToken cancellationToken)
    {
        var review = new OperationReviewViewModel(L, plan);

        if (!await _dialogs.ShowAsync(review).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Ready, Keys.Status_PlanNotApplied);
            return;
        }

        var result = await _editor.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

        _status.Report(
            result.Succeeded ? StatusKind.Done : StatusKind.Error,
            result.Succeeded ? successKey : Keys.Status_ConfigFailed);

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The settings the form currently describes.</summary>
    private ProjectSettings Current() => new(_repository.CurrentPath!)
    {
        ProfileId = SelectedProfile?.Profile?.Id,
        ProfileName = SelectedProfile?.Profile?.Name,
        SshKeyPath = SelectedKey?.Path,
        CredentialHelper = SelectedHelper?.Helper,
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
        ExcludeFromScans = ExcludeFromScans,
    };

    private async Task RebuildChoicesAsync(CancellationToken cancellationToken)
    {
        var saved = await _profiles.LoadAsync(cancellationToken).ConfigureAwait(true);

        Profiles.Clear();
        Profiles.Add(new ProfileChoice(L, null));
        foreach (var profile in saved.OrderBy(p => p.Name, StringComparer.CurrentCulture))
        {
            Profiles.Add(new ProfileChoice(L, profile));
        }

        KeyChoices.Clear();
        KeyChoices.Add(new KeyChoice(L, null));
        foreach (var key in _scans.Report.Keys)
        {
            var path = key.PrivatePath ?? key.PublicPath;
            if (!string.IsNullOrEmpty(path))
            {
                KeyChoices.Add(new KeyChoice(L, path));
            }
        }
    }

    private void Fill(ProjectSettings? settings)
    {
        _isLoading = true;
        try
        {
            SelectedProfile = Profiles.FirstOrDefault(p => p.Profile?.Id == settings?.ProfileId) ?? Profiles[0];

            // A key or profile the repository names but the machine no longer has must still show,
            // rather than silently becoming "none" and then being saved that way.
            if (settings?.SshKeyPath is { Length: > 0 } keyPath
                && KeyChoices.All(k => !string.Equals(k.Path, keyPath, StringComparison.OrdinalIgnoreCase)))
            {
                KeyChoices.Add(new KeyChoice(L, keyPath));
            }

            SelectedKey = KeyChoices.FirstOrDefault(k =>
                string.Equals(k.Path, settings?.SshKeyPath, StringComparison.OrdinalIgnoreCase))
                ?? KeyChoices[0];

            SelectedHelper = Helpers.FirstOrDefault(h =>
                string.Equals(h.Helper, settings?.CredentialHelper, StringComparison.OrdinalIgnoreCase))
                ?? Helpers[0];

            Note = settings?.Note ?? string.Empty;
            ExcludeFromScans = settings?.ExcludeFromScans ?? false;
        }
        finally
        {
            _isLoading = false;
            IsDirty = false;

            OnPropertyChanged(nameof(RepositoryName));
            OnPropertyChanged(nameof(RepositoryPath));
            OnPropertyChanged(nameof(HasRepository));

            RebuildProperties();
        }
    }

    private void MarkEdited()
    {
        if (!_isLoading)
        {
            IsDirty = true;
        }
    }

    private void OnRepositoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepositoryContext.CurrentPath))
        {
            _ = ReloadAsync(CancellationToken.None);
        }
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();

        foreach (var profile in Profiles)
        {
            profile.RefreshCaptions();
        }

        foreach (var key in KeyChoices)
        {
            key.RefreshCaptions();
        }

        foreach (var helper in Helpers)
        {
            helper.RefreshCaptions();
        }

        RebuildProperties();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _repository.PropertyChanged -= OnRepositoryChanged;
        }

        base.Dispose(disposing);
    }

    partial void OnSelectedProfileChanged(ProfileChoice? value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnSelectedKeyChanged(KeyChoice? value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnSelectedHelperChanged(HelperOption? value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnNoteChanged(string value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnExcludeFromScansChanged(bool value)
    {
        _ = value;
        MarkEdited();
    }

    private void RebuildProperties()
    {
        if (!_repository.HasRepository)
        {
            SetProperties([]);
            return;
        }

        SetProperties(
        [
            Property(Keys.Repositories_Column_Name, RepositoryName),
            Property(Keys.Keys_Column_Path, RepositoryPath, PropertyStyle.Mono),
            Property(Keys.Project_Field_Profile, SelectedProfile?.Label ?? string.Empty),
            Property(Keys.Project_Field_Key, SelectedKey?.Path ?? string.Empty, PropertyStyle.Mono),
            Property(Keys.Project_Field_Helper, SelectedHelper?.Label ?? string.Empty),
            Property(
                Keys.Project_Field_Exclude,
                L[ExcludeFromScans ? Keys.Common_Yes : Keys.Common_No],
                PropertyStyle.Badge),
        ]);
    }
}

/// <summary>A profile a repository can be pinned to, or the "none" entry.</summary>
internal sealed class ProfileChoice(Localizer localizer, IdentityProfile? profile) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The profile, or null for "none".</summary>
    public IdentityProfile? Profile { get; } = profile;

    /// <summary>Label: the profile name, or the localized "none".</summary>
    public string Label => Profile?.Name ?? L[Keys.Common_None];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}

/// <summary>A key path a repository can be pinned to, or the "none" entry.</summary>
internal sealed class KeyChoice(Localizer localizer, string? path) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>Absolute path of the key, or null for "none".</summary>
    public string? Path { get; } = path;

    /// <summary>Label: the file name, or the localized "none".</summary>
    public string Label => Path is null ? L[Keys.Common_None] : System.IO.Path.GetFileName(Path);

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}
