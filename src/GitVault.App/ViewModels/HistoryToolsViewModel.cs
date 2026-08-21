using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Abstractions;
using GitVault.Core.Git;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// The operations that address a path or an identity rather than a single commit.
/// </summary>
/// <remarks>
/// A page of its own rather than more buttons on the history page, because these three do
/// something categorically different: they reach every commit on the branch at once. Putting them
/// where a commit is selected would invite the reading that they act on that commit.
///
/// Each one goes through the same route as every other write in the application — build a plan,
/// show it, apply only if the user types the branch name — so nothing here has its own idea of
/// what counts as confirmation.
/// </remarks>
internal sealed partial class HistoryToolsViewModel : PageViewModel
{
    private readonly IHistoryTools _tools;
    private readonly IHistoryRewriter _rewriter;
    private readonly IGitConfigService _config;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly RepositoryContext _repository;

    [ObservableProperty]
    private string _removePath = string.Empty;

    [ObservableProperty]
    private string _renamePath = string.Empty;

    [ObservableProperty]
    private string _renameNewPath = string.Empty;

    [ObservableProperty]
    private string _oldEmail = string.Empty;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newEmail = string.Empty;

    public HistoryToolsViewModel(
        Localizer localizer,
        IHistoryTools tools,
        IHistoryRewriter rewriter,
        IGitConfigService config,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(rewriter);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);

        _tools = tools;
        _rewriter = rewriter;
        _config = config;
        _dialogs = dialogs;
        _status = status;
        _repository = repository;

        _repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_HistoryTools;

    /// <inheritdoc/>
    public override string TitleKey => Keys.HistoryTools_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.HistoryTools_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconOptions";

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => _repository.HasRepository;

    /// <summary>Localized prompt shown when no repository is selected.</summary>
    public string NoRepositoryCaption => L[Keys.HistoryTools_Empty];

    /// <summary>Localized description of what removing a path does.</summary>
    public string RemoveExplainsCaption => L[Keys.HistoryTools_Remove_Explains];

    /// <summary>Localized warning that a purge is not a substitute for revoking a secret.</summary>
    public string RemoveWarningCaption => L[Keys.HistoryTools_Remove_Warning];

    /// <summary>Localized description of what moving a path does.</summary>
    public string RenameExplainsCaption => L[Keys.HistoryTools_Rename_Explains];

    /// <summary>Localized description of what replacing an identity does.</summary>
    public string IdentityExplainsCaption => L[Keys.HistoryTools_Identity_Explains];

    /// <summary>Localized note about how paths are spelled.</summary>
    public string PathHintCaption => L[Keys.HistoryTools_PathHint];

    /// <summary>True when a path has been typed to remove.</summary>
    public bool CanRemove => HasRepository && RemovePath.Trim().Length > 0;

    /// <summary>True when both ends of a move have been typed.</summary>
    public bool CanRename =>
        HasRepository && RenamePath.Trim().Length > 0 && RenameNewPath.Trim().Length > 0;

    /// <summary>True when every field of an identity replacement has been typed.</summary>
    public bool CanReplaceIdentity =>
        HasRepository
        && OldEmail.Trim().Length > 0
        && NewName.Trim().Length > 0
        && NewEmail.Trim().Length > 0;

    /// <summary>Removes a path from the whole history, after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || !CanRemove)
        {
            return;
        }

        var plan = await _tools
            .PlanRemovePathAsync(path, RemovePath.Trim(), cancellationToken)
            .ConfigureAwait(true);

        if (await ReviewAndApplyAsync(plan, Keys.Status_PathRemoved, cancellationToken).ConfigureAwait(true))
        {
            RemovePath = string.Empty;
        }
    }

    /// <summary>Moves a path through the whole history, after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task RenameAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || !CanRename)
        {
            return;
        }

        var plan = await _tools
            .PlanRenamePathAsync(path, RenamePath.Trim(), RenameNewPath.Trim(), cancellationToken)
            .ConfigureAwait(true);

        if (await ReviewAndApplyAsync(plan, Keys.Status_PathRenamed, cancellationToken).ConfigureAwait(true))
        {
            RenamePath = string.Empty;
            RenameNewPath = string.Empty;
        }
    }

    /// <summary>Replaces an identity through the whole history, after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task ReplaceIdentityAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || !CanReplaceIdentity)
        {
            return;
        }

        var plan = await _tools
            .PlanReplaceIdentityAsync(
                path, OldEmail.Trim(), NewName.Trim(), NewEmail.Trim(), cancellationToken)
            .ConfigureAwait(true);

        if (await ReviewAndApplyAsync(plan, Keys.Status_IdentityReplaced, cancellationToken).ConfigureAwait(true))
        {
            OldEmail = string.Empty;
        }
    }

    /// <summary>Fills the replacement fields from the identity git would use here.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the fields are filled.</returns>
    [RelayCommand]
    private async Task UseConfiguredIdentityAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        // The effective values, so what is offered is what git would actually write here rather
        // than whatever happens to be in one file.
        var name = await _config
            .GetEffectiveAsync(GitConfigKeys.UserName, path, cancellationToken)
            .ConfigureAwait(true);

        var email = await _config
            .GetEffectiveAsync(GitConfigKeys.UserEmail, path, cancellationToken)
            .ConfigureAwait(true);

        if (name?.Value is { Length: > 0 } configuredName)
        {
            NewName = configuredName;
        }

        if (email?.Value is { Length: > 0 } configuredEmail)
        {
            NewEmail = configuredEmail;
        }
    }

    /// <summary>Shows a plan and applies it only if the user confirms.</summary>
    /// <returns><see langword="true"/> when the rewrite was applied.</returns>
    private async Task<bool> ReviewAndApplyAsync(
        RewritePlan plan,
        string successKey,
        CancellationToken cancellationToken)
    {
        var review = new RewriteReviewViewModel(L, plan);

        if (!await _dialogs.ShowAsync(review).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Ready, Keys.Status_PlanNotApplied);
            return false;
        }

        var result = await _rewriter.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

        if (result.Succeeded)
        {
            _status.Report(StatusKind.Done, successKey);
            return true;
        }

        _status.ReportText(
            StatusKind.Error,
            string.Join(
                L[Keys.Common_ListSeparator],
                result.Steps
                    .Where(s => s.Outcome == GitVault.Core.Models.StepOutcome.Failed)
                    .Select(s => s.Detail)));

        return false;
    }

    partial void OnRemovePathChanged(string value) => Revalidate(value);

    partial void OnRenamePathChanged(string value) => Revalidate(value);

    partial void OnRenameNewPathChanged(string value) => Revalidate(value);

    partial void OnOldEmailChanged(string value) => Revalidate(value);

    partial void OnNewNameChanged(string value) => Revalidate(value);

    partial void OnNewEmailChanged(string value) => Revalidate(value);

    private void Revalidate(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanReplaceIdentity));
    }

    private void OnRepositoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(RepositoryContext.CurrentPath))
        {
            return;
        }

        OnPropertyChanged(nameof(HasRepository));
        Revalidate(string.Empty);
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
}
