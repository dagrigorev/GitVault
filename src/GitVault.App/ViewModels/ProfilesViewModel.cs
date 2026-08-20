using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One saved profile.</summary>
internal sealed class ProfileRow(Localizer localizer, IdentityProfile profile) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying profile.</summary>
    public IdentityProfile Profile { get; set; } = profile;

    /// <summary>Profile name, as the user wrote it.</summary>
    public string Name => Profile.Name;

    /// <summary>The identity the profile applies.</summary>
    public string Identity => Profile.Identity.DisplayName;

    /// <summary>Credential helper the profile sets, or an empty cell.</summary>
    public string CredentialHelper => Profile.CredentialHelper ?? string.Empty;

    /// <summary>Localized name of the scope the profile is stored with.</summary>
    public string StoredScope => L[DisplayNames.ScopeKey(Profile.Scope)];

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

/// <summary>An SSH key the profile can pin, or the "none" entry.</summary>
internal sealed class KeyOption(Localizer localizer, SshKey? key) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The key, or null for "no key".</summary>
    public SshKey? Key { get; } = key;

    /// <summary>Label: the file name, or the localized "none".</summary>
    public string Label => Key is null
        ? L[Keys.Common_None]
        : System.IO.Path.GetFileName(Key.PrivatePath ?? Key.PublicPath ?? string.Empty);

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}

/// <summary>A credential helper the profile can set, or the "none" entry.</summary>
internal sealed class HelperOption(Localizer localizer, string? helper) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The helper name, or null for "none".</summary>
    public string? Helper { get; } = helper;

    /// <summary>Label: the helper name verbatim, or the localized "none".</summary>
    public string Label => Helper ?? L[Keys.Common_None];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// The profiles page: a list, an editor, and the activation controls.
/// </summary>
/// <remarks>
/// Three rules hold here, and each is enforced by the shape of the code rather than by comments.
///
/// First, a profile's stored scope is what the activation controls open with. The earlier version
/// reset the scope selector to Global whenever a profile was selected, so a profile saved against
/// one repository would quietly plan a change to the user's global configuration; the dry-run
/// preview was the only thing standing between that and a wrong write.
///
/// Second, previewing and applying stay two separate operations, and the preview must be
/// <em>reviewed</em> — the user confirms the dialog — before Apply becomes available.
///
/// Third, changing anything the plan was built from invalidates it. A plan describes the state it
/// was computed against; letting an edited scope apply an old plan would mean applying something
/// nobody saw.
/// </remarks>
internal sealed partial class ProfilesViewModel : ListPageViewModel
{
    private readonly IProfileStore _store;
    private readonly IProfileActivator _activator;
    private readonly ISnapshotService _snapshots;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly ScanCoordinator _scans;

    /// <summary>Set while the editor is being filled from a profile, so loading is not an edit.</summary>
    private bool _isLoadingEditor;

    [ObservableProperty]
    private ProfileRow? _selectedProfile;

    [ObservableProperty]
    private ScopeOption? _selectedScope;

    [ObservableProperty]
    private string? _repositoryPath;

    [ObservableProperty]
    private string _editorName = string.Empty;

    [ObservableProperty]
    private GitIdentity? _editorIdentity;

    [ObservableProperty]
    private KeyOption? _editorKey;

    [ObservableProperty]
    private HelperOption? _editorHelper;

    [ObservableProperty]
    private bool _editorWritesSshCommand = true;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private ActivationPlan? _plan;

    [ObservableProperty]
    private bool _hasReviewedPlan;

    [ObservableProperty]
    private string? _lastSnapshotPath;

    public ProfilesViewModel(
        Localizer localizer,
        IProfileStore store,
        IProfileActivator activator,
        ISnapshotService snapshots,
        IDialogService dialogs,
        StatusService status,
        ScanCoordinator scans)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activator);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(scans);

        _store = store;
        _activator = activator;
        _snapshots = snapshots;
        _dialogs = dialogs;
        _status = status;
        _scans = scans;

        Scopes =
        [
            new ScopeOption(localizer, ActivationScope.Global, Keys.Profiles_Scope_Global),
            new ScopeOption(localizer, ActivationScope.System, Keys.Profiles_Scope_System),
            new ScopeOption(localizer, ActivationScope.Repository, Keys.Profiles_Scope_Repository),
        ];

        _selectedScope = Scopes[0];

        Helpers =
        [
            new HelperOption(localizer, null),
            new HelperOption(localizer, "manager"),
            new HelperOption(localizer, "store"),
            new HelperOption(localizer, "cache"),
        ];

        _editorHelper = Helpers[0];

        _scans.ScanCompleted += OnScanCompleted;
        RebuildKeyOptions();
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Profiles;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Profiles_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Profiles_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconProfiles";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Profiles_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Saved profiles.</summary>
    public ObservableCollection<ProfileRow> Rows { get; } = [];

    /// <summary>Scopes a profile can be applied at.</summary>
    public ObservableCollection<ScopeOption> Scopes { get; }

    /// <summary>Identities discovered on this machine, for the editor.</summary>
    public ObservableCollection<GitIdentity> Identities { get; } = [];

    /// <summary>Keys discovered on this machine, plus a "none" entry.</summary>
    public ObservableCollection<KeyOption> KeyOptions { get; } = [];

    /// <summary>Credential helpers the editor offers.</summary>
    public ObservableCollection<HelperOption> Helpers { get; }

    /// <summary>True when a profile is selected and can be edited.</summary>
    public bool HasSelectedProfile => SelectedProfile is not null;

    /// <summary>True when the activation controls should ask for a repository path.</summary>
    public bool IsRepositoryScope => SelectedScope?.Scope == ActivationScope.Repository;

    /// <summary>
    /// True once a plan has been previewed <em>and</em> the user confirmed the preview dialog.
    /// </summary>
    public bool CanApply => Plan?.CanApply == true && HasReviewedPlan;

    /// <summary>True when a rollback target exists.</summary>
    public bool CanRollback => !string.IsNullOrEmpty(LastSnapshotPath);

    /// <summary>Localized state of the current plan, shown beside the activation controls.</summary>
    public string PlanStateCaption => this switch
    {
        { HasReviewedPlan: true } => L[Keys.Profiles_State_Reviewed],
        { Plan: not null } => L[Keys.Profiles_State_NotReviewed],
        _ => L[Keys.Profiles_State_NotPreviewed],
    };

    /// <summary>Localized reminder of what happens before a mutation.</summary>
    public string SnapshotNoteCaption => L[Keys.Profiles_SnapshotNote];

    /// <summary>Localized note that a stored scope is honoured.</summary>
    public string StoredScopeNoteCaption => L[Keys.Profiles_StoredScopeNote];

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads the saved profiles.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the list is rebuilt.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var previous = SelectedProfile?.Profile.Id;
        var profiles = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        Rows.Clear();
        foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.CurrentCulture))
        {
            Rows.Add(new ProfileRow(L, profile));
        }

        SelectedProfile = Rows.FirstOrDefault(r => r.Profile.Id == previous) ?? Rows.FirstOrDefault();

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Creates a profile from a name and an identity, then selects it for editing.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the profile has been saved.</returns>
    [RelayCommand]
    private async Task NewProfileAsync(CancellationToken cancellationToken)
    {
        var dialog = new NewProfileViewModel(L, [.. _scans.Report.Identities]);

        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true) || dialog.SelectedIdentity is null)
        {
            return;
        }

        var profile = new IdentityProfile(
            Guid.NewGuid(),
            dialog.ProfileName.Trim(),
            dialog.SelectedIdentity,
            SshKeyId: null,
            PreferredAgent: null,
            CredentialHelper: null,
            ActivationScope.Global,
            RepositoryPath: null);

        await _store.SaveAsync(profile, cancellationToken).ConfigureAwait(true);
        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        SelectedProfile = Rows.FirstOrDefault(r => r.Profile.Id == profile.Id);
        _status.Report(StatusKind.Done, Keys.Status_ProfileCreated);
    }

    /// <summary>Saves the editor's contents over the selected profile.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the profile has been saved.</returns>
    [RelayCommand]
    private async Task SaveProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is not { } row || EditorIdentity is null)
        {
            return;
        }

        var key = EditorKey?.Key;

        var updated = row.Profile with
        {
            Name = EditorName.Trim(),
            Identity = EditorIdentity,
            SshKeyId = key?.Id,
            CredentialHelper = EditorHelper?.Helper,
            Scope = SelectedScope?.Scope ?? ActivationScope.Global,
            RepositoryPath = string.IsNullOrWhiteSpace(RepositoryPath) ? null : RepositoryPath.Trim(),
        };

        updated = updated with
        {
            SshKeyPath = key?.PrivatePath ?? key?.PublicPath,
            WriteCoreSshCommand = EditorWritesSshCommand,
            HostAliases = row.Profile.HostAliases,
            CredentialUserNames = row.Profile.CredentialUserNames,
        };

        await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(true);
        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        IsDirty = false;
        _status.Report(StatusKind.Done, Keys.Status_ProfileSaved);
    }

    /// <summary>Deletes the selected profile, after asking.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the profile is gone.</returns>
    [RelayCommand]
    private async Task DeleteProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is not { } row)
        {
            return;
        }

        // Deleting a profile removes a reference, never a key or a credential. The dialog says
        // so, because next to a tool that can rewrite git config the opposite is a fair worry.
        var confirmation = new ConfirmationViewModel(
            L,
            Keys.Profiles_Delete_Title,
            L.Format(Keys.Profiles_Delete_Message, row.Name),
            L[Keys.Profiles_Delete_Detail]);

        if (!await _dialogs.ShowAsync(confirmation).ConfigureAwait(true))
        {
            return;
        }

        await _store.DeleteAsync(row.Profile.Id, cancellationToken).ConfigureAwait(true);
        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        _status.Report(StatusKind.Done, Keys.Status_ProfileDeleted);
    }

    /// <summary>Builds the plan for the selected profile and scope, writing nothing.</summary>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>A task that completes once the preview closes.</returns>
    [RelayCommand]
    private async Task PreviewActivationAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is null || SelectedScope is null)
        {
            return;
        }

        var plan = await _activator
            .PlanActivationAsync(SelectedProfile.Profile, SelectedScope.Scope, RepositoryPath, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAsync(plan).ConfigureAwait(true);
    }

    /// <summary>Builds the plan to undo the selected profile, writing nothing.</summary>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>A task that completes once the preview closes.</returns>
    [RelayCommand]
    private async Task PreviewDeactivationAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is null || SelectedScope is null)
        {
            return;
        }

        var plan = await _activator
            .PlanDeactivationAsync(SelectedProfile.Profile, SelectedScope.Scope, RepositoryPath, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAsync(plan).ConfigureAwait(true);
    }

    /// <summary>Applies the plan the user reviewed, and nothing else.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the plan has been applied.</returns>
    [RelayCommand]
    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (Plan is not { } plan || !CanApply)
        {
            _status.Report(StatusKind.Warning, Keys.Profiles_ApplyDryRunFirst);
            return;
        }

        var result = await _activator.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

        LastSnapshotPath = result.SnapshotPath;

        if (result.Succeeded)
        {
            _status.Report(StatusKind.Done, Keys.Status_PlanApplied);
        }
        else
        {
            _status.ReportText(
                StatusKind.Error,
                string.Join(
                    L[Keys.Common_ListSeparator],
                    result.Steps.Where(s => s.Outcome == StepOutcome.Failed).Select(s => s.Detail)));
        }

        // The plan described the state before it ran; keeping it would let it be applied twice.
        InvalidatePlan();
        OnPropertyChanged(nameof(CanRollback));
    }

    /// <summary>Restores the snapshot taken before the last apply, after previewing it.</summary>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>A task that completes once the files are back.</returns>
    [RelayCommand]
    private async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(LastSnapshotPath))
        {
            return;
        }

        var info = _snapshots.ListSnapshotsDetailed().FirstOrDefault(s => s.Path == LastSnapshotPath);
        if (info is null)
        {
            return;
        }

        var files = await _snapshots.DescribeAsync(LastSnapshotPath, cancellationToken).ConfigureAwait(true);

        if (!await _dialogs.ShowAsync(new RollbackPreviewViewModel(L, info, files)).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Ready, Keys.Status_RollbackCancelled);
            return;
        }

        var restored = await _activator.RollbackAsync(LastSnapshotPath, cancellationToken).ConfigureAwait(true);

        _status.Report(StatusKind.Done, Keys.Status_RollbackRestored, restored.Count);
        LastSnapshotPath = null;
        OnPropertyChanged(nameof(CanRollback));
    }

    /// <summary>Shows the plan and records whether the user reviewed it.</summary>
    private async Task ReviewAsync(ActivationPlan plan)
    {
        Plan = plan;
        HasReviewedPlan = false;

        var dialog = new PlanReviewViewModel(
            L,
            plan,
            L[DisplayNames.ScopeKey(plan.Scope)],
            _snapshots.PeekNextSequence());

        var reviewed = await _dialogs.ShowAsync(dialog).ConfigureAwait(true);

        HasReviewedPlan = reviewed && plan.CanApply;

        _status.Report(
            HasReviewedPlan ? StatusKind.Warning : StatusKind.Ready,
            HasReviewedPlan ? Keys.Status_PlanReviewed : Keys.Status_PlanNotApplied);

        NotifyPlanState();
    }

    /// <summary>Drops the current plan, so nothing can be applied until a new one is reviewed.</summary>
    private void InvalidatePlan()
    {
        Plan = null;
        HasReviewedPlan = false;
        NotifyPlanState();
    }

    private void NotifyPlanState()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(PlanStateCaption));
        OnPropertyChanged(nameof(HasReviewedPlan));
    }

    private void OnScanCompleted(object? sender, DiscoveryReport report) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(RebuildKeyOptions);

    /// <summary>Refreshes the editor's identity and key pickers from the last scan.</summary>
    internal void RebuildKeyOptions()
    {
        var report = _scans.Report;

        Identities.Clear();
        foreach (var identity in report.Identities)
        {
            Identities.Add(identity);
        }

        KeyOptions.Clear();
        KeyOptions.Add(new KeyOption(L, null));
        foreach (var key in report.Keys)
        {
            KeyOptions.Add(new KeyOption(L, key));
        }

        // Re-select whatever the current profile refers to, now that the options exist.
        LoadEditor(SelectedProfile?.Profile);
    }

    /// <summary>Fills the editor from a profile without treating the fill as an edit.</summary>
    private void LoadEditor(IdentityProfile? profile)
    {
        _isLoadingEditor = true;
        try
        {
            if (profile is null)
            {
                EditorName = string.Empty;
                EditorIdentity = null;
                EditorKey = KeyOptions.FirstOrDefault();
                EditorHelper = Helpers[0];
                EditorWritesSshCommand = true;
                RepositoryPath = null;
                return;
            }

            EditorName = profile.Name;

            EditorIdentity = Identities.FirstOrDefault(i => i.Key == profile.Identity.Key)
                ?? Identities.FirstOrDefault(i => i.Id == profile.Identity.Id);

            // A profile stores the identity it was created with, so an identity that is no
            // longer on the machine must still appear rather than silently becoming blank.
            if (EditorIdentity is null)
            {
                Identities.Insert(0, profile.Identity);
                EditorIdentity = profile.Identity;
            }

            EditorKey = KeyOptions.FirstOrDefault(k => k.Key?.Id == profile.SshKeyId)
                ?? KeyOptions.FirstOrDefault(k =>
                    profile.SshKeyPath is not null
                    && string.Equals(k.Key?.PrivatePath, profile.SshKeyPath, StringComparison.OrdinalIgnoreCase))
                ?? KeyOptions.FirstOrDefault();

            EditorHelper = Helpers.FirstOrDefault(h =>
                string.Equals(h.Helper, profile.CredentialHelper, StringComparison.OrdinalIgnoreCase))
                ?? Helpers[0];

            EditorWritesSshCommand = profile.WriteCoreSshCommand;

            // The stored scope, not a default. This is the fix for the defect where a profile
            // saved against a repository planned a change to the global configuration instead.
            SelectedScope = Scopes.First(s => s.Scope == profile.Scope);
            RepositoryPath = profile.RepositoryPath;
        }
        finally
        {
            _isLoadingEditor = false;
            IsDirty = false;
            OnPropertyChanged(nameof(IsRepositoryScope));
        }
    }

    private void MarkEdited()
    {
        if (_isLoadingEditor)
        {
            return;
        }

        IsDirty = true;

        // Anything the plan was computed from has changed, so the plan no longer describes what
        // would happen. Apply goes back to disabled until a fresh preview is reviewed.
        InvalidatePlan();
    }


    /// <inheritdoc/>
    internal override void EnsureSelection()
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var current = SelectedProfile;
        SelectedProfile = null;
        SelectedProfile = current ?? Rows[0];
    }
    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();

        foreach (var scope in Scopes)
        {
            scope.RefreshCaptions();
        }

        foreach (var option in KeyOptions)
        {
            option.RefreshCaptions();
        }

        foreach (var helper in Helpers)
        {
            helper.RefreshCaptions();
        }

        foreach (var row in Rows)
        {
            row.RefreshCaptions();
        }

        NotifyPlanState();
        RebuildProperties();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scans.ScanCompleted -= OnScanCompleted;
        }

        base.Dispose(disposing);
    }

    partial void OnSelectedProfileChanged(ProfileRow? value)
    {
        // A plan belongs to the profile it was built for.
        InvalidatePlan();

        LoadEditor(value?.Profile);
        RebuildProperties();

        OnPropertyChanged(nameof(HasSelectedProfile));
    }

    partial void OnSelectedScopeChanged(ScopeOption? value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsRepositoryScope));
        MarkEdited();
    }

    partial void OnRepositoryPathChanged(string? value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnEditorNameChanged(string value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnEditorIdentityChanged(GitIdentity? value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnEditorKeyChanged(KeyOption? value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnEditorHelperChanged(HelperOption? value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnEditorWritesSshCommandChanged(bool value)
    {
        _ = value;
        MarkEdited();
    }

    partial void OnHasReviewedPlanChanged(bool value)
    {
        _ = value;
        NotifyPlanState();
    }

    private void RebuildProperties()
    {
        if (SelectedProfile is not { } row)
        {
            SetProperties([]);
            return;
        }

        var entries = new List<PropertyEntry>
        {
            Property(Keys.Profiles_Column_Name, row.Name),
            Property(Keys.Profiles_Column_Identity, row.Identity),
            Property(Keys.Profiles_Column_Helper, row.CredentialHelper),
            Property(Keys.Profiles_Column_StoredScope, row.StoredScope, PropertyStyle.Badge),
        };

        if (!string.IsNullOrEmpty(row.Profile.RepositoryPath))
        {
            entries.Add(Property(Keys.Profiles_Repository, row.Profile.RepositoryPath, PropertyStyle.Mono));
        }

        if (!string.IsNullOrEmpty(row.Profile.SshKeyPath))
        {
            entries.Add(Property(Keys.Profiles_Field_SshKey, row.Profile.SshKeyPath, PropertyStyle.Mono));
        }

        entries.Add(Property(
            Keys.Profiles_State,
            PlanStateCaption,
            HasReviewedPlan ? PropertyStyle.BadgeWarn : PropertyStyle.Badge));

        SetProperties(entries);
    }
}
