using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One submodule, as the grid lists it.</summary>
internal sealed class SubmoduleRow(Localizer localizer, GitSubmodule submodule) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying submodule.</summary>
    public GitSubmodule Submodule { get; } = submodule;

    /// <summary>Path inside the parent, shown verbatim.</summary>
    public string Path => Submodule.Path;

    /// <summary>Where the parent says it comes from, shown verbatim.</summary>
    public string Url => Submodule.Url;

    /// <summary>Branch the parent tracks, or an empty cell.</summary>
    public string Branch => Submodule.Branch ?? string.Empty;

    /// <summary>Localized state of the working copy.</summary>
    public string State => L[Submodule.State switch
    {
        SubmoduleState.NotInitialized => Keys.Submodules_State_NotInitialized,
        SubmoduleState.Moved => Keys.Submodules_State_Moved,
        SubmoduleState.Conflicted => Keys.Submodules_State_Conflicted,
        _ => Keys.Submodules_State_UpToDate,
    }];

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The submodules page.
/// </summary>
/// <remarks>
/// Bounded by a rule from outside this page: GitVault makes no network calls, so it neither
/// initialises nor updates a submodule. What it edits is what the parent records about them, and
/// the page says so plainly rather than offering buttons that would have to fail.
///
/// The address is the useful part. A submodule pointing at a repository that has moved, or at
/// HTTPS when the user authenticates over SSH, fails at the least convenient moment; correcting
/// it is a text edit, and telling this clone about the correction is a second, named step.
/// </remarks>
internal sealed partial class SubmodulesViewModel : ListPageViewModel
{
    private readonly ISubmoduleEditor _submodules;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly RepositoryContext _repository;

    [ObservableProperty]
    private SubmoduleRow? _selectedRow;

    public SubmodulesViewModel(
        Localizer localizer,
        ISubmoduleEditor submodules,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(submodules);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);

        _submodules = submodules;
        _dialogs = dialogs;
        _status = status;
        _repository = repository;

        _repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Submodules;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Submodules_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Submodules_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconRepositories";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Submodules_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>The submodules the parent records.</summary>
    public ObservableCollection<SubmoduleRow> Rows { get; } = [];

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => _repository.HasRepository;

    /// <summary>Localized prompt shown when no repository is selected.</summary>
    public string NoRepositoryCaption => L[Keys.Project_NoRepository];

    /// <summary>Localized statement that GitVault will not fetch or check anything out.</summary>
    public string NoNetworkCaption => L[Keys.Submodules_NoNetworkNote];

    /// <summary>True when a submodule is selected.</summary>
    public bool HasSelectedSubmodule => SelectedRow is not null;

    /// <summary>True when the selected submodule has a working copy that could be removed.</summary>
    public bool CanDeinit => SelectedRow is { Submodule.IsInitialized: true };

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads the submodules.</summary>
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

        var submodules = await _submodules.ListAsync(path, cancellationToken).ConfigureAwait(true);
        var previous = SelectedRow?.Submodule.Name;

        Rows.Clear();
        foreach (var submodule in submodules)
        {
            Rows.Add(new SubmoduleRow(L, submodule));
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Submodule.Name == previous) ?? Rows.FirstOrDefault();
        Notify();
    }

    /// <summary>Edits what the parent records about the selected submodule.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task EditAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var dialog = new SubmoduleEditorViewModel(L, row.Submodule);
        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        if (!string.Equals(dialog.Url.Trim(), row.Submodule.Url, StringComparison.Ordinal))
        {
            var plan = await _submodules
                .PlanSetUrlAsync(path, row.Submodule.Name, dialog.Url.Trim(), cancellationToken)
                .ConfigureAwait(true);

            if (!await ReviewAndApplyAsync(plan, Keys.Status_SubmoduleSaved, cancellationToken).ConfigureAwait(true))
            {
                return;
            }
        }

        if (!string.Equals(dialog.Branch.Trim(), row.Submodule.Branch ?? string.Empty, StringComparison.Ordinal))
        {
            var plan = await _submodules
                .PlanSetBranchAsync(path, row.Submodule.Name, dialog.Branch.Trim(), cancellationToken)
                .ConfigureAwait(true);

            await ReviewAndApplyAsync(plan, Keys.Status_SubmoduleSaved, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Applies the recorded address to this clone's own configuration.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var plan = await _submodules
            .PlanSyncAsync(path, SelectedRow?.Submodule.Name, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_SubmoduleSynced, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Removes the selected submodule's working copy, keeping the record.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task DeinitAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var plan = await _submodules
            .PlanDeinitAsync(path, row.Submodule.Name, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_SubmoduleDeinited, cancellationToken).ConfigureAwait(true);
    }

    private async Task<bool> ReviewAndApplyAsync(
        RepositoryPlan plan,
        string successKey,
        CancellationToken cancellationToken)
    {
        var review = new RepositoryReviewViewModel(L, plan);

        if (!await _dialogs.ShowAsync(review).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Ready, Keys.Status_PlanNotApplied);
            return false;
        }

        var result = await _submodules.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

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
        return result.Succeeded;
    }

    partial void OnSelectedRowChanged(SubmoduleRow? value)
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
        OnPropertyChanged(nameof(HasSelectedSubmodule));
        OnPropertyChanged(nameof(CanDeinit));
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
            Property(Keys.Submodules_Column_Path, row.Path, PropertyStyle.Mono),
            Property(Keys.Submodules_Column_Url, row.Url, PropertyStyle.Mono),
            Property(Keys.Commits_Column_Sha, row.Submodule.RecordedSha, PropertyStyle.Mono),
            Property(Keys.Submodules_Column_State, row.State, PropertyStyle.Badge),
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

/// <summary>Editing what the parent records about a submodule.</summary>
internal sealed partial class SubmoduleEditorViewModel : DialogViewModel
{
    private readonly GitSubmodule _original;

    [ObservableProperty]
    private string _url;

    [ObservableProperty]
    private string _branch;

    internal SubmoduleEditorViewModel(Localizer localizer, GitSubmodule original)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(original);

        _original = original;
        _url = original.Url;
        _branch = original.Branch ?? string.Empty;
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Submodules_Editor_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Path inside the parent, shown verbatim.</summary>
    public string Path => _original.Path;

    /// <summary>Localized statement that GitVault will not fetch anything.</summary>
    public string NoNetworkCaption => L[Keys.Submodules_NoNetworkNote];

    /// <inheritdoc/>
    public override bool CanConfirm =>
        Url.Trim().Length > 0
        && (!string.Equals(Url.Trim(), _original.Url, StringComparison.Ordinal)
            || !string.Equals(Branch.Trim(), _original.Branch ?? string.Empty, StringComparison.Ordinal));

    /// <inheritdoc/>
    public override double DialogWidth => 640;

    partial void OnUrlChanged(string value) => Revalidate(value);

    partial void OnBranchChanged(string value) => Revalidate(value);

    private void Revalidate(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }
}
