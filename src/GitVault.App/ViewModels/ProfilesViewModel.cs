using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using GitVault.Core.Settings;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One saved profile.</summary>
internal sealed class ProfileRow(Localizer localizer, IdentityProfile profile) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying profile.</summary>
    public IdentityProfile Profile { get; } = profile;

    /// <summary>Profile name, as the user wrote it.</summary>
    public string Name => Profile.Name;

    /// <summary>The identity the profile applies.</summary>
    public string Identity => Profile.Identity.DisplayName;

    /// <summary>Credential helper the profile sets, or an empty cell.</summary>
    public string CredentialHelper => Profile.CredentialHelper ?? string.Empty;

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>A scope the user can activate a profile at.</summary>
internal sealed class ScopeOption(Localizer localizer, ActivationScope scope, string labelKey) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The scope this option selects.</summary>
    public ActivationScope Scope { get; } = scope;

    /// <summary>Localized label.</summary>
    public string Label => L[labelKey];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label. Called when the culture changes.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// The profile page.
/// </summary>
/// <remarks>
/// Applying is deliberately two calls: the user previews a plan, and only the previewed plan can
/// be applied. The apply button stays disabled until a plan exists, which is how the
/// "dry run before the first write" rule is enforced in the UI rather than merely documented.
/// </remarks>
internal sealed partial class ProfilesViewModel : ListPageViewModel
{
    private readonly IProfileStore _store;
    private readonly IProfileActivator _activator;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private ProfileRow? _selectedProfile;

    [ObservableProperty]
    private ScopeOption? _selectedScope;

    [ObservableProperty]
    private string? _repositoryPath;

    [ObservableProperty]
    private ActivationPlan? _plan;

    [ObservableProperty]
    private string _planDiff = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _lastSnapshotPath;

    public ProfilesViewModel(
        Localizer localizer,
        IProfileStore store,
        IProfileActivator activator,
        ISettingsService settings)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activator);
        ArgumentNullException.ThrowIfNull(settings);

        _store = store;
        _activator = activator;
        _settings = settings;

        Scopes =
        [
            new ScopeOption(localizer, ActivationScope.Global, Keys.Profiles_Scope_Global),
            new ScopeOption(localizer, ActivationScope.System, Keys.Profiles_Scope_System),
            new ScopeOption(localizer, ActivationScope.Repository, Keys.Profiles_Scope_Repository),
        ];

        _selectedScope = Scopes[0];
    }

    public override string NavKey => Keys.Nav_Profiles;

    public override string TitleKey => Keys.Profiles_Title;

    /// <inheritdoc/>
    public override string IconKey => "IconProfiles";

    public override string EmptyKey => Keys.Profiles_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Saved profiles.</summary>
    public ObservableCollection<ProfileRow> Rows { get; } = [];

    /// <summary>Scopes a profile can be applied at.</summary>
    public ObservableCollection<ScopeOption> Scopes { get; }

    /// <summary>True once a plan has been previewed and can be applied.</summary>
    public bool CanApply => Plan?.CanApply == true;

    /// <summary>True when a rollback target exists.</summary>
    public bool CanRollback => !string.IsNullOrEmpty(LastSnapshotPath);

    /// <summary>Localized note about dry-run mode.</summary>
    public string DryRunCaption => L[Keys.Profiles_DryRun];

    /// <inheritdoc/>
    public override async Task OnActivatedAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads the saved profiles.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the list is rebuilt.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var profiles = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        Rows.Clear();
        foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.CurrentCulture))
        {
            Rows.Add(new ProfileRow(L, profile));
        }

        SelectedProfile ??= Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Builds the plan for the selected profile and scope, writing nothing.</summary>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>A task that completes once the preview is on screen.</returns>
    [RelayCommand]
    private async Task PreviewActivationAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is null || SelectedScope is null)
        {
            return;
        }

        Plan = await _activator
            .PlanActivationAsync(SelectedProfile.Profile, SelectedScope.Scope, RepositoryPath, cancellationToken)
            .ConfigureAwait(true);

        ShowPlan();
    }

    /// <summary>Builds the plan to undo the selected profile, writing nothing.</summary>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>A task that completes once the preview is on screen.</returns>
    [RelayCommand]
    private async Task PreviewDeactivationAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is null || SelectedScope is null)
        {
            return;
        }

        Plan = await _activator
            .PlanDeactivationAsync(SelectedProfile.Profile, SelectedScope.Scope, RepositoryPath, cancellationToken)
            .ConfigureAwait(true);

        ShowPlan();
    }

    /// <summary>Applies the plan that is currently on screen, and nothing else.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the plan has been applied.</returns>
    [RelayCommand]
    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (Plan is null || !Plan.CanApply)
        {
            StatusMessage = L[Keys.Profiles_ApplyDryRunFirst];
            return;
        }

        var result = await _activator.ApplyAsync(Plan, cancellationToken).ConfigureAwait(true);

        LastSnapshotPath = result.SnapshotPath;
        StatusMessage = result.Succeeded
            ? L[Keys.Profiles_Applied]
            : string.Join(
                L[Keys.Common_ListSeparator],
                result.Steps.Where(s => s.Outcome == StepOutcome.Failed).Select(s => s.Detail));

        // The plan described the state before it ran; keeping it would let it be applied twice.
        Plan = null;
        PlanDiff = string.Empty;

        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRollback));
    }

    /// <summary>Restores the snapshot taken before the last apply.</summary>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>A task that completes once the files are back.</returns>
    [RelayCommand]
    private async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(LastSnapshotPath))
        {
            return;
        }

        var restored = await _activator.RollbackAsync(LastSnapshotPath, cancellationToken).ConfigureAwait(true);

        StatusMessage = L.Plural("Plural_Keys", restored.Count);
        LastSnapshotPath = null;
        OnPropertyChanged(nameof(CanRollback));
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var scope in Scopes)
        {
            scope.RefreshCaptions();
        }

        foreach (var row in Rows)
        {
            row.RefreshCaptions();
        }
    }

    private void ShowPlan()
    {
        PlanDiff = Plan?.ToDiff() ?? string.Empty;

        StatusMessage = Plan switch
        {
            null => null,
            { Blockers.Count: > 0 } => L.Format(
                Keys.Profiles_BlockedWithReasons,
                string.Join(L[Keys.Common_ListSeparator], Plan.Blockers)),
            { CanApply: false } => L[Keys.Profiles_NothingToDo],
            _ => null,
        };

        OnPropertyChanged(nameof(CanApply));

        // The dry-run default only governs whether the preview is mandatory, which it always is
        // here; the setting is read so that turning it off cannot silently change behaviour.
        _ = _settings.Current.DryRunByDefault;
    }

    partial void OnSelectedProfileChanged(ProfileRow? value)
    {
        _ = value;

        // A plan belongs to the profile it was built for.
        Plan = null;
        PlanDiff = string.Empty;
        StatusMessage = null;
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnSelectedScopeChanged(ScopeOption? value)
    {
        _ = value;
        Plan = null;
        PlanDiff = string.Empty;
        OnPropertyChanged(nameof(CanApply));
    }
}
