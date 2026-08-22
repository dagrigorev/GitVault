using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One working tree, as the grid lists it.</summary>
internal sealed class WorktreeRow(Localizer localizer, GitWorktree worktree) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying working tree.</summary>
    public GitWorktree Worktree { get; } = worktree;

    /// <summary>Directory, shown verbatim.</summary>
    public string Path => Worktree.Path;

    /// <summary>Branch, or an empty cell when the working tree is not on one.</summary>
    public string Branch => Worktree.Branch ?? string.Empty;

    /// <summary>Abbreviated commit.</summary>
    public string Head => Worktree.ShortHead;

    /// <summary>
    /// Localized state, listing everything true of this working tree at once.
    /// </summary>
    /// <remarks>
    /// A working tree can be several of these together — the main one, detached, locked — so the
    /// column joins them rather than picking one and hiding the rest.
    /// </remarks>
    public string State
    {
        get
        {
            var parts = new List<string>();

            if (Worktree.IsMain)
            {
                parts.Add(L[Keys.Worktrees_State_Main]);
            }

            if (Worktree.IsDetached)
            {
                parts.Add(L[Keys.Worktrees_State_Detached]);
            }

            if (Worktree.IsLocked)
            {
                parts.Add(L[Keys.Worktrees_State_Locked]);
            }

            if (Worktree.IsPrunable)
            {
                parts.Add(L[Keys.Worktrees_State_Missing]);
            }

            return string.Join(L[Keys.Common_ListSeparator], parts);
        }
    }

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The working-trees page.
/// </summary>
/// <remarks>
/// Every action here goes through the plan and the preview, and none of them passes
/// <c>--force</c>. Git refuses to remove a working tree holding uncommitted changes, and that
/// refusal reaches the user as a failed step rather than being overridden to make the dialog
/// smoother.
/// </remarks>
internal sealed partial class WorktreesViewModel : ListPageViewModel
{
    private readonly IWorktreeEditor _worktrees;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly RepositoryContext _repository;

    [ObservableProperty]
    private WorktreeRow? _selectedRow;

    public WorktreesViewModel(
        Localizer localizer,
        IWorktreeEditor worktrees,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);

        _worktrees = worktrees;
        _dialogs = dialogs;
        _status = status;
        _repository = repository;

        _repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Worktrees;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Worktrees_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Worktrees_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconRepositories";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Worktrees_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>The working trees attached to this repository.</summary>
    public ObservableCollection<WorktreeRow> Rows { get; } = [];

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => _repository.HasRepository;

    /// <summary>Localized prompt shown when no repository is selected.</summary>
    public string NoRepositoryCaption => L[Keys.Worktrees_Empty];

    /// <summary>True when the selected working tree is one that can be removed.</summary>
    public bool CanRemove => SelectedRow is { Worktree.IsMain: false };

    /// <summary>True when the selected working tree can be locked.</summary>
    public bool CanLock => SelectedRow is { Worktree: { IsMain: false, IsLocked: false } };

    /// <summary>True when the selected working tree can be unlocked.</summary>
    public bool CanUnlock => SelectedRow is { Worktree.IsLocked: true };

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads the working trees.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the grid is rebuilt.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path)
        {
            Rows.Clear();
            Notify();
            return;
        }

        var worktrees = await _worktrees.ListAsync(path, cancellationToken).ConfigureAwait(true);
        var previous = SelectedRow?.Path;

        Rows.Clear();
        foreach (var worktree in worktrees)
        {
            Rows.Add(new WorktreeRow(L, worktree));
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Path == previous) ?? Rows.FirstOrDefault();
        Notify();
    }

    /// <summary>Adds a working tree, after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task AddAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var dialog = new WorktreeEditorViewModel(L, _dialogs);
        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await _worktrees
            .PlanAddAsync(
                path,
                dialog.Directory.Trim(),
                dialog.StartPoint.Trim(),
                dialog.CreatesBranch ? dialog.NewBranch.Trim() : null,
                cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_WorktreeAdded, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Removes the selected working tree, after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var plan = await _worktrees.PlanRemoveAsync(path, row.Path, cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_WorktreeRemoved, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Locks the selected working tree, after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task LockAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var dialog = new WorktreeLockViewModel(L);
        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await _worktrees
            .PlanLockAsync(path, row.Path, true, dialog.Reason.Trim(), cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_WorktreeLocked, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Unlocks the selected working tree, after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task UnlockAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var plan = await _worktrees
            .PlanLockAsync(path, row.Path, false, null, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_WorktreeUnlocked, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Forgets the working trees whose directories are gone.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var plan = await _worktrees.PlanPruneAsync(path, cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_WorktreePruned, cancellationToken).ConfigureAwait(true);
    }

    private async Task ReviewAndApplyAsync(
        RepositoryPlan plan,
        string successKey,
        CancellationToken cancellationToken)
    {
        var review = new RepositoryReviewViewModel(L, plan);

        if (!await _dialogs.ShowAsync(review).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Ready, Keys.Status_PlanNotApplied);
            return;
        }

        var result = await _worktrees.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

        if (result.Succeeded)
        {
            _status.Report(StatusKind.Done, successKey);
        }
        else
        {
            _status.ReportText(
                StatusKind.Error,
                string.Join(
                    L[Keys.Common_ListSeparator],
                    result.Steps
                        .Where(s => s.Outcome == GitVault.Core.Models.StepOutcome.Failed)
                        .Select(s => s.Detail)));
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    partial void OnSelectedRowChanged(WorktreeRow? value)
    {
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        Notify();
        RebuildProperties();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(CanUnlock));
    }

    private void RebuildProperties()
    {
        if (SelectedRow is not { } row)
        {
            SetProperties([]);
            return;
        }

        var entries = new List<PropertyEntry>
        {
            Property(Keys.Worktrees_Column_Path, row.Path, PropertyStyle.Mono),
            Property(Keys.Worktrees_Column_Head, row.Worktree.Head, PropertyStyle.Mono),
        };

        if (row.Worktree.Branch is { Length: > 0 } branch)
        {
            entries.Add(Property(Keys.Worktrees_Column_Branch, branch));
        }

        if (row.Worktree.LockReason is { Length: > 0 } reason)
        {
            entries.Add(Property(Keys.Worktrees_Field_Reason, reason));
        }

        SetProperties(entries);
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

        foreach (var row in Rows)
        {
            row.RefreshCaptions();
        }

        RebuildProperties();
    }

    /// <inheritdoc/>
    internal override void EnsureSelection()
    {
        if (Rows.Count > 0)
        {
            var current = SelectedRow;
            SelectedRow = null;
            SelectedRow = current ?? Rows[0];
        }
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

/// <summary>Adding a working tree.</summary>
internal sealed partial class WorktreeEditorViewModel : DialogViewModel
{
    private readonly IFolderPicker _picker;

    [ObservableProperty]
    private string _directory = string.Empty;

    [ObservableProperty]
    private string _startPoint = GitRevisions.Head;

    [ObservableProperty]
    private string _newBranch = string.Empty;

    [ObservableProperty]
    private bool _createsBranch = true;

    internal WorktreeEditorViewModel(Localizer localizer, IFolderPicker picker)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(picker);
        _picker = picker;
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Worktrees_Editor_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Localized explanation of what a working tree is.</summary>
    public string NoteCaption => L[Keys.Worktrees_Editor_Note];

    /// <inheritdoc/>
    public override bool CanConfirm =>
        Directory.Trim().Length > 0
        && StartPoint.Trim().Length > 0
        && (!CreatesBranch || NewBranch.Trim().Length > 0);

    /// <inheritdoc/>
    public override double DialogWidth => 620;

    /// <summary>Asks for the directory the new working tree should occupy.</summary>
    /// <returns>A task that completes once the picker closes.</returns>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _picker.PickFolderAsync(Keys.Worktrees_Field_Directory, Directory).ConfigureAwait(true)
            is { Length: > 0 } chosen)
        {
            Directory = chosen;
        }
    }

    partial void OnDirectoryChanged(string value) => Revalidate(value);

    partial void OnStartPointChanged(string value) => Revalidate(value);

    partial void OnNewBranchChanged(string value) => Revalidate(value);

    partial void OnCreatesBranchChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }

    private void Revalidate(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }
}

/// <summary>Locking a working tree.</summary>
internal sealed partial class WorktreeLockViewModel(Localizer localizer) : DialogViewModel(localizer)
{
    [ObservableProperty]
    private string _reason = string.Empty;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Worktrees_Lock_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Localized explanation of what locking does.</summary>
    public string NoteCaption => L[Keys.Worktrees_Lock_Note];

    /// <inheritdoc/>
    public override bool CanConfirm => true;

    /// <inheritdoc/>
    public override double DialogWidth => 560;
}
