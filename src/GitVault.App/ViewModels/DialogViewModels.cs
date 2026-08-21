using GitVault.Core.Abstractions;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using GitVault.Core.Repository;
using GitVault.Core.Settings;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>Lets a dialog view model ask for a folder without knowing what a window is.</summary>
internal interface IFolderPicker
{
    /// <summary>Shows the platform folder picker.</summary>
    /// <param name="titleKey">Resource key of the picker's title.</param>
    /// <param name="startPath">Folder to open at, when it exists.</param>
    /// <returns>The chosen folder, or null when the user cancelled.</returns>
    Task<string?> PickFolderAsync(string titleKey, string? startPath);
}

/// <summary>
/// Base for the modal dialogs.
/// </summary>
/// <remarks>
/// The dialogs are one window with a swappable body rather than seven windows: the classic frame
/// — caption band, body, footer with the confirming button on the right — is identical in all of
/// them, and repeating it seven times would be seven places for it to drift.
/// </remarks>
internal abstract partial class DialogViewModel : ViewModelBase
{
    protected DialogViewModel(Localizer localizer)
        : base(localizer)
    {
    }

    /// <summary>Raised when the dialog wants to close. The argument is the result.</summary>
    internal event EventHandler<bool>? CloseRequested;

    /// <summary>Resource key of the caption.</summary>
    public abstract string TitleKey { get; }

    /// <summary>Resource key of the confirming button.</summary>
    public abstract string ConfirmKey { get; }

    /// <summary>Localized caption.</summary>
    public string DialogTitle => L[TitleKey];

    /// <summary>Localized confirming caption.</summary>
    public string ConfirmCaption => L[ConfirmKey];

    /// <summary>Localized dismissing caption.</summary>
    public string CancelCaption => L[CancelKey];

    /// <summary>Resource key of the dismissing button.</summary>
    public virtual string CancelKey => Keys.Common_Cancel;

    /// <summary>False when the dialog is informational and has nothing to cancel.</summary>
    public virtual bool ShowCancel => true;

    /// <summary>False while the dialog's inputs are incomplete or invalid.</summary>
    public virtual bool CanConfirm => true;

    /// <summary>Width the dialog opens at.</summary>
    public virtual double DialogWidth => 660;

    /// <summary>Height the dialog opens at.</summary>
    public virtual double DialogHeight => 520;

    /// <summary>Accepts the dialog.</summary>
    [RelayCommand]
    protected void Confirm()
    {
        if (CanConfirm)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    /// <summary>Dismisses the dialog without accepting it.</summary>
    [RelayCommand]
    protected void Cancel() => CloseRequested?.Invoke(this, false);
}

/// <summary>One line of a rendered plan, tagged so the view can colour it.</summary>
/// <param name="Text">The line.</param>
/// <param name="IsAddition">True for a line the plan would add.</param>
/// <param name="IsRemoval">True for a line the plan would remove.</param>
public sealed record PlanLine(string Text, bool IsAddition, bool IsRemoval);

/// <summary>
/// The dry-run preview. Nothing has been written when this is on screen, and the only way to
/// reach an apply is to confirm it.
/// </summary>
/// <remarks>
/// Confirming this dialog is what the "reviewed" flag on the profiles page records. Preview and
/// apply stay two separate operations; this dialog is the seam between them, and it exists so
/// that "the user has seen the plan" is a fact the application knows rather than assumes.
/// </remarks>
internal sealed partial class PlanReviewViewModel : DialogViewModel
{
    private readonly ActivationPlan _plan;

    internal PlanReviewViewModel(
        Localizer localizer,
        ActivationPlan plan,
        string scopeLabel,
        int nextSnapshotSequence)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _plan = plan;
        ScopeLabel = scopeLabel;
        SnapshotSequence = nextSnapshotSequence;

        foreach (var line in plan.ToDiff().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            Lines.Add(new PlanLine(line.TrimEnd(), trimmed.StartsWith('+'), trimmed.StartsWith('-')));
        }

        foreach (var blocker in plan.Blockers)
        {
            Blockers.Add(blocker);
        }
    }

    /// <inheritdoc/>
    public override string TitleKey => _plan.IsDeactivation
        ? Keys.Dialog_PreviewDeactivation_Title
        : Keys.Dialog_PreviewActivation_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Dialog_ReviewedPlan;

    /// <summary>Name of the profile the plan belongs to.</summary>
    public string ProfileName => _plan.ProfileName;

    /// <summary>Localized name of the scope the plan targets.</summary>
    public string ScopeLabel { get; }

    /// <summary>Repository the plan targets, when the scope is a repository.</summary>
    public string RepositoryPath => _plan.RepositoryPath ?? string.Empty;

    /// <summary>True when a repository row is worth showing.</summary>
    public bool HasRepository => !string.IsNullOrEmpty(_plan.RepositoryPath);

    /// <summary>Number the snapshot taken before the mutation will carry.</summary>
    public int SnapshotSequence { get; }

    /// <summary>Localized sentence naming the snapshot that will be taken.</summary>
    public string SnapshotCaption => L.Format(Keys.Dialog_SnapshotWillBeCreated, SnapshotSequence);

    /// <summary>The plan, line by line.</summary>
    public ObservableCollection<PlanLine> Lines { get; } = [];

    /// <summary>Reasons the plan cannot be applied.</summary>
    public ObservableCollection<string> Blockers { get; } = [];

    /// <summary>True when the plan is blocked.</summary>
    public bool HasBlockers => Blockers.Count > 0;

    /// <summary>True when the plan would change nothing.</summary>
    public bool IsNoOp => Blockers.Count == 0 && !_plan.Changes.Any(c => !c.IsNoOp);

    /// <summary>Localized note that nothing has been written yet.</summary>
    public string NothingWrittenCaption => L[Keys.Dialog_NothingWritten];

    /// <inheritdoc/>
    public override bool CanConfirm => _plan.CanApply;

    /// <summary>The plan being previewed, handed back to the caller on confirmation.</summary>
    internal ActivationPlan Plan => _plan;
}

/// <summary>
/// What restoring a snapshot would do. Rollback is a mutation like any other, so it gets the
/// same treatment: a preview first, and no single click that writes.
/// </summary>
internal sealed partial class RollbackPreviewViewModel : DialogViewModel
{
    internal RollbackPreviewViewModel(
        Localizer localizer,
        SnapshotInfo snapshot,
        IReadOnlyList<SnapshotFileState> files)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(files);

        Snapshot = snapshot;

        foreach (var file in files)
        {
            Files.Add(new RollbackFileRow(localizer, file));
        }
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Dialog_RollbackPreview_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Dialog_RestoreSnapshot;

    /// <summary>The snapshot that would be restored.</summary>
    internal SnapshotInfo Snapshot { get; }

    /// <summary>Display number of the snapshot.</summary>
    public string SnapshotLabel => L.Format(Keys.Snapshots_Number, Snapshot.Sequence);

    /// <summary>Files the restore would touch.</summary>
    public ObservableCollection<RollbackFileRow> Files { get; } = [];

    /// <summary>Localized note that nothing has been written yet.</summary>
    public string NothingWrittenCaption => L[Keys.Dialog_NothingWritten];

    /// <inheritdoc/>
    public override bool CanConfirm => Files.Count > 0;

    /// <inheritdoc/>
    public override double DialogHeight => 440;
}

/// <summary>One file a rollback would touch.</summary>
internal sealed class RollbackFileRow(Localizer localizer, SnapshotFileState state) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The file's real location.</summary>
    public string Path => state.OriginalPath;

    /// <summary>Localized description of what restoring would do to it.</summary>
    public string Action => state switch
    {
        { WillBeDeleted: true } => L[Keys.Snapshots_Action_Delete],
        { DiffersFromSnapshot: false } => L[Keys.Snapshots_Action_Unchanged],
        { ExistsNow: false } => L[Keys.Snapshots_Action_Recreate],
        _ => L[Keys.Snapshots_Action_Restore],
    };

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>A yes/no question in a classic dialog.</summary>
internal sealed partial class ConfirmationViewModel : DialogViewModel
{
    private readonly string _titleKey;

    internal ConfirmationViewModel(Localizer localizer, string titleKey, string message, string? detail = null)
        : base(localizer)
    {
        _titleKey = titleKey;
        Message = message;
        Detail = detail ?? string.Empty;
    }

    /// <inheritdoc/>
    public override string TitleKey => _titleKey;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>The question.</summary>
    public string Message { get; }

    /// <summary>Extra context under the question.</summary>
    public string Detail { get; }

    /// <summary>True when there is extra context to show.</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <inheritdoc/>
    public override double DialogWidth => 470;

    /// <inheritdoc/>
    public override double DialogHeight => 220;
}

/// <summary>The properties of the selected item, in a dialog rather than the pane.</summary>
internal sealed partial class PropertiesDialogViewModel : DialogViewModel
{
    internal PropertiesDialogViewModel(Localizer localizer, string subject, IEnumerable<PropertyEntry> entries)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Subject = subject;
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Common_Properties;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Close;

    /// <inheritdoc/>
    public override bool ShowCancel => false;

    /// <summary>What the properties belong to.</summary>
    public string Subject { get; }

    /// <summary>The properties.</summary>
    public ObservableCollection<PropertyEntry> Entries { get; } = [];

    /// <inheritdoc/>
    public override double DialogWidth => 520;

    /// <inheritdoc/>
    public override double DialogHeight => 420;
}

/// <summary>The about box.</summary>
internal sealed partial class AboutViewModel : DialogViewModel
{
    internal AboutViewModel(Localizer localizer, string version)
        : base(localizer)
    {
        Version = version;
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Dialog_About_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <inheritdoc/>
    public override bool ShowCancel => false;

    /// <summary>Product name, not translated.</summary>
    public string ProductName => L[Keys.App_Title];

    /// <summary>Assembly version, shown verbatim.</summary>
    public string Version { get; }

    /// <summary>Localized one-line description.</summary>
    public string Description => L[Keys.App_Subtitle];

    /// <summary>Localized statement that no network call is ever made.</summary>
    public string NoNetworkCaption => L[Keys.Settings_TelemetryNone];

    /// <summary>Localized icon attribution.</summary>
    public string IconLicenseCaption => L[Keys.Dialog_About_IconLicense];

    /// <inheritdoc/>
    public override double DialogWidth => 480;

    /// <inheritdoc/>
    public override double DialogHeight => 300;
}

/// <summary>A selectable scan depth, with a localized label.</summary>
internal sealed class ScanDepthOption(Localizer localizer, ScanDepth value, string labelKey) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The depth this option selects.</summary>
    public ScanDepth Value { get; } = value;

    /// <summary>Localized label.</summary>
    public string Label => L[labelKey];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}

/// <summary>A selectable key-folder mode, with a localized label.</summary>
internal sealed class KeyFolderModeOption(Localizer localizer, KeyFolderMode value, string labelKey) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The mode this option selects.</summary>
    public KeyFolderMode Value { get; } = value;

    /// <summary>Localized label.</summary>
    public string Label => L[labelKey];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// Add or edit a scan root.
/// </summary>
/// <remarks>
/// Editing discovery roots changes GitVault's own configuration and nothing else. The dialog says
/// so, because a list of paths next to a tool that can rewrite git config invites the assumption
/// that removing a row does something to the repositories under it.
/// </remarks>
internal sealed partial class ScanRootEditorViewModel : DialogViewModel
{
    private readonly IFolderPicker _folders;
    private readonly bool _isNew;

    [ObservableProperty]
    private string _path;

    [ObservableProperty]
    private ScanDepthOption? _selectedDepth;

    [ObservableProperty]
    private bool _isEnabled;

    internal ScanRootEditorViewModel(Localizer localizer, IFolderPicker folders, ScanRoot? existing)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(folders);

        _folders = folders;
        _isNew = existing is null;
        _path = existing?.Path ?? string.Empty;
        _isEnabled = existing?.Enabled ?? true;

        Depths =
        [
            new ScanDepthOption(localizer, ScanDepth.Recursive, Keys.Options_Depth_Recursive),
            new ScanDepthOption(localizer, ScanDepth.TopLevel, Keys.Options_Depth_TopLevel),
        ];

        _selectedDepth = Depths.First(d => d.Value == (existing?.Depth ?? ScanDepth.Recursive));
    }

    /// <inheritdoc/>
    public override string TitleKey => _isNew ? Keys.Options_AddScanRoot : Keys.Options_EditScanRoot;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Save;

    /// <summary>Available depths.</summary>
    public ObservableCollection<ScanDepthOption> Depths { get; }

    /// <summary>Localized note that this edits application settings only.</summary>
    public string SettingsOnlyCaption => L[Keys.Options_SettingsOnlyNote];

    /// <inheritdoc/>
    public override bool CanConfirm => !string.IsNullOrWhiteSpace(Path);

    /// <inheritdoc/>
    public override double DialogWidth => 520;

    /// <inheritdoc/>
    public override double DialogHeight => 280;

    /// <summary>Builds the edited root.</summary>
    /// <returns>The root as the dialog leaves it.</returns>
    internal ScanRoot ToScanRoot() => new()
    {
        Path = Path.Trim(),
        Depth = SelectedDepth?.Value ?? ScanDepth.Recursive,
        Enabled = IsEnabled,
    };

    /// <summary>Opens the platform folder picker.</summary>
    /// <returns>A task that completes once the picker closes.</returns>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var chosen = await _folders.PickFolderAsync(Keys.Options_PickScanRoot, Path).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(chosen))
        {
            Path = chosen;
        }
    }

    partial void OnPathChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var depth in Depths)
        {
            depth.RefreshCaptions();
        }
    }
}

/// <summary>Add or edit a custom SSH key folder. Same rule as the scan roots: settings only.</summary>
internal sealed partial class KeyFolderEditorViewModel : DialogViewModel
{
    private readonly IFolderPicker _folders;
    private readonly bool _isNew;

    [ObservableProperty]
    private string _path;

    [ObservableProperty]
    private KeyFolderModeOption? _selectedMode;

    [ObservableProperty]
    private bool _isEnabled;

    internal KeyFolderEditorViewModel(Localizer localizer, IFolderPicker folders, KeyFolder? existing)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(folders);

        _folders = folders;
        _isNew = existing is null;
        _path = existing?.Path ?? string.Empty;
        _isEnabled = existing?.Enabled ?? true;

        Modes =
        [
            new KeyFolderModeOption(localizer, KeyFolderMode.PrivateAndPublic, Keys.Options_Mode_PrivateAndPublic),
            new KeyFolderModeOption(localizer, KeyFolderMode.PublicOnly, Keys.Options_Mode_PublicOnly),
        ];

        _selectedMode = Modes.First(m => m.Value == (existing?.Mode ?? KeyFolderMode.PrivateAndPublic));
    }

    /// <inheritdoc/>
    public override string TitleKey => _isNew ? Keys.Options_AddKeyFolder : Keys.Options_EditKeyFolder;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Save;

    /// <summary>Available modes.</summary>
    public ObservableCollection<KeyFolderModeOption> Modes { get; }

    /// <summary>Localized note that this edits application settings only.</summary>
    public string SettingsOnlyCaption => L[Keys.Options_SettingsOnlyNote];

    /// <summary>Localized note that GitVault never writes to a key file.</summary>
    public string ReadOnlyCaption => L[Keys.Options_KeysReadOnlyNote];

    /// <inheritdoc/>
    public override bool CanConfirm => !string.IsNullOrWhiteSpace(Path);

    /// <inheritdoc/>
    public override double DialogWidth => 520;

    /// <inheritdoc/>
    public override double DialogHeight => 300;

    /// <summary>Builds the edited folder.</summary>
    /// <returns>The folder as the dialog leaves it.</returns>
    internal KeyFolder ToKeyFolder() => new()
    {
        Path = Path.Trim(),
        Mode = SelectedMode?.Value ?? KeyFolderMode.PrivateAndPublic,
        Enabled = IsEnabled,
    };

    /// <summary>Opens the platform folder picker.</summary>
    /// <returns>A task that completes once the picker closes.</returns>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var chosen = await _folders.PickFolderAsync(Keys.Options_PickKeyFolder, Path).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(chosen))
        {
            Path = chosen;
        }
    }

    partial void OnPathChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var mode in Modes)
        {
            mode.RefreshCaptions();
        }
    }
}

/// <summary>Names a new profile and picks the identity it applies.</summary>
internal sealed partial class NewProfileViewModel : DialogViewModel
{
    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private GitIdentity? _selectedIdentity;

    internal NewProfileViewModel(Localizer localizer, IReadOnlyList<GitIdentity> identities)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(identities);

        foreach (var identity in identities)
        {
            Identities.Add(identity);
        }

        _selectedIdentity = Identities.FirstOrDefault();
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Profiles_New_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Identities discovered on this machine.</summary>
    public ObservableCollection<GitIdentity> Identities { get; } = [];

    /// <summary>True when no identity has been discovered to base a profile on.</summary>
    public bool HasNoIdentities => Identities.Count == 0;

    /// <summary>Localized explanation shown when there is no identity to pick.</summary>
    public string NoIdentitiesCaption => L[Keys.Profiles_New_NoIdentities];

    /// <inheritdoc/>
    public override bool CanConfirm => !string.IsNullOrWhiteSpace(ProfileName) && SelectedIdentity is not null;

    /// <inheritdoc/>
    public override double DialogWidth => 470;

    /// <inheritdoc/>
    public override double DialogHeight => 240;

    partial void OnProfileNameChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnSelectedIdentityChanged(GitIdentity? value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }
}

/// <summary>
/// The preview for an operation that is not a profile activation.
/// </summary>
/// <remarks>
/// Deliberately the same shape as <see cref="PlanReviewViewModel"/>: nothing has been written, the
/// plan is shown line by line, and confirming is what permits the write. A configuration edit is
/// a smaller change than an activation, but it is a change to the same files, and giving it a
/// quieter path would be the beginning of having two standards.
/// </remarks>
internal sealed partial class OperationReviewViewModel : DialogViewModel
{
    private readonly GitOperationPlan _plan;

    internal OperationReviewViewModel(Localizer localizer, GitOperationPlan plan)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _plan = plan;

        foreach (var line in plan.ToDiff().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            Lines.Add(new PlanLine(line.TrimEnd(), trimmed.StartsWith('+'), trimmed.StartsWith('-')));
        }

        foreach (var blocker in plan.Blockers)
        {
            // A blocker is an identifier so it can be read in the user's language today rather
            // than the one it was produced in. Anything else is shown as it arrived.
            Blockers.Add(blocker.StartsWith(BlockerMessages.Prefix, StringComparison.Ordinal) ? localizer[blocker] : blocker);
        }
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Dialog_PreviewOperation_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Dialog_ReviewedPlan;

    /// <summary>Localized name of the scope the plan writes at.</summary>
    public string ScopeLabel => L[DisplayNames.ScopeKey(_plan.Scope)];

    /// <summary>Repository the plan addresses, when it addresses one.</summary>
    public string RepositoryPath => _plan.RepositoryPath ?? string.Empty;

    /// <summary>True when a repository row is worth showing.</summary>
    public bool HasRepository => !string.IsNullOrEmpty(_plan.RepositoryPath);

    /// <summary>Files the plan will copy aside before writing.</summary>
    public string SnapshotFiles => string.Join(Environment.NewLine, _plan.FilesToSnapshot);

    /// <summary>The plan, line by line.</summary>
    public ObservableCollection<PlanLine> Lines { get; } = [];

    /// <summary>Reasons the plan cannot be applied, localized.</summary>
    public ObservableCollection<string> Blockers { get; } = [];

    /// <summary>True when the plan is blocked.</summary>
    public bool HasBlockers => Blockers.Count > 0;

    /// <summary>True when the plan would change nothing.</summary>
    public bool IsNoOp => Blockers.Count == 0 && !_plan.Changes.Any(c => !c.IsNoOp);

    /// <summary>Localized note that nothing has been written yet.</summary>
    public string NothingWrittenCaption => L[Keys.Dialog_NothingWritten];

    /// <inheritdoc/>
    public override bool CanConfirm => _plan.CanApply;

    /// <inheritdoc/>
    public override double DialogHeight => 480;
}

/// <summary>Add or edit one configuration key.</summary>
internal sealed partial class ConfigEntryEditorViewModel : DialogViewModel
{
    private readonly bool _isNew;

    [ObservableProperty]
    private string _key;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private ScopeChoice? _selectedScope;

    internal ConfigEntryEditorViewModel(Localizer localizer, GitConfigValue? existing, GitConfigScope defaultScope)
        : base(localizer)
    {
        _isNew = existing is null;
        _key = existing?.Key ?? string.Empty;
        _value = existing?.Value ?? string.Empty;

        Scopes =
        [
            new ScopeChoice(localizer, GitConfigScope.Local, Keys.Scope_Local),
            new ScopeChoice(localizer, GitConfigScope.Global, Keys.Scope_Global),
            new ScopeChoice(localizer, GitConfigScope.System, Keys.Scope_System),
        ];

        var wanted = existing?.Scope ?? defaultScope;
        _selectedScope = Scopes.FirstOrDefault(s => s.Value == wanted) ?? Scopes[0];
    }

    /// <inheritdoc/>
    public override string TitleKey => _isNew ? Keys.Config_AddEntry : Keys.Config_EditEntry;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Scopes the key can be written at.</summary>
    public ObservableCollection<ScopeChoice> Scopes { get; }

    /// <summary>The scope the dialog will write at.</summary>
    public GitConfigScope Scope => SelectedScope?.Value ?? GitConfigScope.Local;

    /// <summary>True when the key can only be edited, not renamed.</summary>
    public bool IsKeyFixed => !_isNew;

    /// <summary>Localized warning shown when the system scope is chosen.</summary>
    public string SystemScopeCaption => L[Keys.Config_SystemScopeNote];

    /// <summary>True when the chosen scope needs privileges GitVault will not acquire.</summary>
    public bool IsSystemScope => Scope == GitConfigScope.System;

    /// <inheritdoc/>
    public override bool CanConfirm =>
        !string.IsNullOrWhiteSpace(Key) && Key.Contains('.', StringComparison.Ordinal);

    /// <inheritdoc/>
    public override double DialogWidth => 540;

    /// <inheritdoc/>
    public override double DialogHeight => 300;

    partial void OnKeyChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnSelectedScopeChanged(ScopeChoice? value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsSystemScope));
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var scope in Scopes)
        {
            scope.RefreshCaptions();
        }
    }
}

/// <summary>A configuration scope the editor can write at.</summary>
internal sealed class ScopeChoice(Localizer localizer, GitConfigScope value, string labelKey) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The scope this choice selects.</summary>
    public GitConfigScope Value { get; } = value;

    /// <summary>Localized label.</summary>
    public string Label => L[labelKey];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}
