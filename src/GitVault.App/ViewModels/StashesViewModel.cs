using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One stash entry, as the grid lists it.</summary>
internal sealed class StashRow(Localizer localizer, GitStash stash) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying entry.</summary>
    public GitStash Stash { get; } = stash;

    /// <summary>Reference git knows it by, shown verbatim.</summary>
    public string Reference => Stash.Reference;

    /// <summary>Message git recorded, shown verbatim.</summary>
    public string Message => Stash.Message;

    /// <summary>Branch it was made on, or an empty cell.</summary>
    public string Branch => Stash.Branch ?? string.Empty;

    /// <summary>When it was made, in the active culture.</summary>
    public string Created => Stash.Created == DateTimeOffset.MinValue
        ? string.Empty
        : Stash.Created.ToString("g", L.Service.CurrentCulture);

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The stashes page.
/// </summary>
/// <remarks>
/// Putting an entry back and discarding it are separate buttons, because git's combined "pop" is
/// the operation that leaves people working out what happened: when putting back runs into a
/// conflict, some of the work is in the tree and the entry may or may not still exist. The page
/// says so rather than leaving the absence of a "pop" button to be noticed.
/// </remarks>
internal sealed partial class StashesViewModel : ListPageViewModel
{
    private readonly IStashEditor _stashes;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly RepositoryContext _repository;

    [ObservableProperty]
    private StashRow? _selectedRow;

    public StashesViewModel(
        Localizer localizer,
        IStashEditor stashes,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(stashes);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);

        _stashes = stashes;
        _dialogs = dialogs;
        _status = status;
        _repository = repository;

        _repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Stashes;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Stashes_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Stashes_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconSnapshots";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Stashes_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>The stash entries, newest first.</summary>
    public ObservableCollection<StashRow> Rows { get; } = [];

    /// <summary>Files the selected entry holds.</summary>
    public ObservableCollection<CommitFileRow> Files { get; } = [];

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => _repository.HasRepository;

    /// <summary>Localized prompt shown when no repository is selected.</summary>
    public string NoRepositoryCaption => L[Keys.Project_NoRepository];

    /// <summary>Localized explanation of why there is no combined "pop".</summary>
    public string NoPopCaption => L[Keys.Stashes_NoPopNote];

    /// <summary>True when an entry is selected.</summary>
    public bool HasSelectedStash => SelectedRow is not null;

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads the stash entries.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the grid is rebuilt.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path)
        {
            Rows.Clear();
            Files.Clear();
            Notify();
            return;
        }

        var stashes = await _stashes.ListAsync(path, cancellationToken).ConfigureAwait(true);
        var previous = SelectedRow?.Reference;

        Rows.Clear();
        foreach (var stash in stashes)
        {
            Rows.Add(new StashRow(L, stash));
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Reference == previous) ?? Rows.FirstOrDefault();
        Notify();
    }

    /// <summary>Sets the working tree's changes aside, after previewing.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task PushAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var dialog = new StashPushViewModel(L);
        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await _stashes
            .PlanPushAsync(path, dialog.Message.Trim(), dialog.IncludeUntracked, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_StashPushed, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Puts the selected entry's changes back, after previewing.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task ApplyEntryAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var plan = await _stashes.PlanApplyAsync(path, row.Reference, cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_StashApplied, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Discards the selected entry, after previewing.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task DropAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var plan = await _stashes.PlanDropAsync(path, row.Reference, cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_StashDropped, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Turns the selected entry into a branch, after previewing.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task BranchAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var dialog = new StashBranchViewModel(L);
        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await _stashes
            .PlanBranchAsync(path, row.Reference, dialog.Branch.Trim(), cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_StashBranched, cancellationToken).ConfigureAwait(true);
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

        var result = await _stashes.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

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

    partial void OnSelectedRowChanged(StashRow? value)
    {
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        OnPropertyChanged(nameof(HasSelectedStash));
        RebuildProperties();
        _ = LoadFilesAsync(value);
    }

    private async Task LoadFilesAsync(StashRow? row)
    {
        Files.Clear();

        if (row is null || _repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var changes = await _stashes
            .ReadChangesAsync(path, row.Reference, CancellationToken.None)
            .ConfigureAwait(true);

        foreach (var change in changes)
        {
            Files.Add(new CommitFileRow(L, change));
        }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(HasSelectedStash));
    }

    private void RebuildProperties()
    {
        if (SelectedRow is not { } row)
        {
            SetProperties([]);
            return;
        }

        SetProperties(
        [
            Property(Keys.Stashes_Column_Reference, row.Reference, PropertyStyle.Mono),
            Property(Keys.Commits_Column_Sha, row.Stash.Sha, PropertyStyle.Mono),
            Property(Keys.Stashes_Column_Message, row.Message),
            Property(Keys.Stashes_Column_Date, row.Created),
        ]);
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

        foreach (var file in Files)
        {
            file.RefreshCaptions();
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

/// <summary>Setting the working tree's changes aside.</summary>
internal sealed partial class StashPushViewModel(Localizer localizer) : DialogViewModel(localizer)
{
    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _includeUntracked;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Stashes_Push_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Localized explanation of what setting aside does.</summary>
    public string NoteCaption => L[Keys.Stashes_Push_Note];

    /// <summary>Localized warning that untracked files move rather than being copied.</summary>
    public string UntrackedCaption => L[StashWarnings.UntrackedFilesMove];

    /// <inheritdoc/>
    public override bool CanConfirm => true;

    /// <inheritdoc/>
    public override double DialogWidth => 580;
}

/// <summary>Turning a stash entry into a branch.</summary>
internal sealed partial class StashBranchViewModel(Localizer localizer) : DialogViewModel(localizer)
{
    [ObservableProperty]
    private string _branch = string.Empty;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Stashes_Branch_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Localized warning about what making a branch actually does.</summary>
    public string NoteCaption => L[StashWarnings.BranchChecksOutAndDrops];

    /// <inheritdoc/>
    public override bool CanConfirm => Branch.Trim().Length > 0;

    /// <inheritdoc/>
    public override double DialogWidth => 560;

    partial void OnBranchChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }
}
