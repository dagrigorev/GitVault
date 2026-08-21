using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One branch, as the grid lists it.</summary>
internal sealed class BranchRow(Localizer localizer, GitBranch branch) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying branch.</summary>
    public GitBranch Branch { get; } = branch;

    /// <summary>Short name, shown verbatim.</summary>
    public string Name => Branch.Name;

    /// <summary>Upstream branch, or an empty cell.</summary>
    public string Upstream => Branch.Upstream ?? string.Empty;

    /// <summary>Abbreviated tip commit.</summary>
    public string Tip => Branch.TipCommit.Length >= 8 ? Branch.TipCommit[..8] : Branch.TipCommit;

    /// <summary>Subject of the tip commit.</summary>
    public string Subject => Branch.TipSubject;

    /// <summary>Localized kind: local or remote-tracking.</summary>
    public string Kind => L[Branch.IsRemote ? Keys.Branches_Kind_Remote : Keys.Branches_Kind_Local];

    /// <summary>Localized ahead/behind summary, or an empty cell when there is no upstream.</summary>
    public string Tracking => Branch.Upstream is null
        ? string.Empty
        : L.Format(Keys.Branches_Tracking, Branch.Ahead, Branch.Behind);

    /// <summary>True when this is the checked-out branch.</summary>
    public bool IsCurrent => Branch.IsCurrent;

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The branches of one repository.
/// </summary>
/// <remarks>
/// Local branches are editable; remote-tracking branches are listed because they answer "where is
/// my upstream" but are not offered for editing here. Deleting a remote-tracking ref is a way of
/// pretending a remote changed, and the honest place to change a remote is the remotes page.
/// </remarks>
internal sealed partial class BranchesViewModel : RepositoryObjectPageViewModel
{
    [ObservableProperty]
    private BranchRow? _selectedRow;

    public BranchesViewModel(
        Localizer localizer,
        IGitObjectEditor editor,
        IRepositoryInspector inspector,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer, editor, inspector, dialogs, status, repository)
    {
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Branches;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Branches_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Branches_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconRepositories";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Branches_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>The branches, local first.</summary>
    public ObservableCollection<BranchRow> Rows { get; } = [];

    /// <summary>True when a local branch is selected, so the editing verbs apply.</summary>
    public bool CanEditSelection => SelectedRow is { Branch.IsRemote: false };

    /// <summary>True when the selected branch can be deleted.</summary>
    public bool CanDeleteSelection => CanEditSelection && SelectedRow?.IsCurrent != true;

    /// <summary>Localized note about what deleting a branch does and does not lose.</summary>
    public string BackupNoteCaption => L[Keys.Branches_BackupNote];

    /// <inheritdoc/>
    internal override async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var previous = SelectedRow?.Name;

        Rows.Clear();

        if (Repository.CurrentPath is { Length: > 0 } path)
        {
            foreach (var branch in await Inspector.ListBranchesAsync(path, cancellationToken).ConfigureAwait(true))
            {
                Rows.Add(new BranchRow(L, branch));
            }
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Name == previous) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Creates a branch after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task CreateAsync(CancellationToken cancellationToken)
    {
        if (Repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var dialog = new BranchEditorViewModel(L, null, [.. Rows.Select(r => r.Name)]);
        if (!await ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await Editor
            .PlanCreateBranchAsync(path, dialog.Name.Trim(), dialog.StartPoint.Trim(), cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_BranchCreated, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Renames the selected branch after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task RenameAsync(CancellationToken cancellationToken)
    {
        if (Repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var dialog = new BranchEditorViewModel(L, row.Branch, [.. Rows.Select(r => r.Name)]);
        if (!await ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await Editor
            .PlanRenameBranchAsync(path, row.Name, dialog.Name.Trim(), cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_BranchRenamed, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Deletes the selected branch after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (Repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var plan = await Editor.PlanDeleteBranchAsync(path, row.Name, cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_BranchDeleted, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Sets or clears the selected branch's upstream after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task SetUpstreamAsync(CancellationToken cancellationToken)
    {
        if (Repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var candidates = Rows.Where(r => r.Branch.IsRemote).Select(r => r.Name).ToList();
        var dialog = new UpstreamEditorViewModel(L, row.Name, row.Branch.Upstream, candidates);

        if (!await ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await Editor
            .PlanSetUpstreamAsync(path, row.Name, dialog.SelectedUpstream?.Value, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_BranchUpstreamSet, cancellationToken).ConfigureAwait(true);
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

    partial void OnSelectedRowChanged(BranchRow? value)
    {
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(CanDeleteSelection));
        RebuildProperties();
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
            Property(Keys.Branches_Column_Name, row.Name),
            Property(Keys.Branches_Column_Kind, row.Kind, PropertyStyle.Badge),
            Property(Keys.Branches_Column_Tip, row.Branch.TipCommit, PropertyStyle.Mono),
            Property(Keys.Branches_Column_Subject, row.Subject),
        };

        if (row.Branch.Upstream is { Length: > 0 })
        {
            entries.Add(Property(Keys.Branches_Column_Upstream, row.Upstream));
            entries.Add(Property(Keys.Branches_Column_Tracking, row.Tracking));
        }

        if (row.IsCurrent)
        {
            entries.Add(Property(Keys.Branches_Current, L[Keys.Common_Yes], PropertyStyle.BadgeOk));
        }

        SetProperties(entries);
    }
}
