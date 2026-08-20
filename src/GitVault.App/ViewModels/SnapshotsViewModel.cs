using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Profiles;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One retained snapshot.</summary>
internal sealed class SnapshotRow(Localizer localizer, SnapshotInfo info) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying snapshot.</summary>
    public SnapshotInfo Info { get; } = info;

    /// <summary>Display number, formatted the way a classic log numbers entries.</summary>
    public string Label => L.Format(Keys.Snapshots_Number, Info.Sequence);

    /// <summary>When it was taken, in the active culture's short format.</summary>
    public string Created => Info.TakenUtc == DateTimeOffset.MinValue
        ? string.Empty
        : Info.TakenUtc.ToLocalTime().ToString("g", L.Service.CurrentCulture);

    /// <summary>
    /// Localized name of the operation. The stored value is an identifier, not text, so a
    /// snapshot taken in one language reads correctly in another.
    /// </summary>
    public string Operation => Info.OperationId switch
    {
        ProfileActivator.ActivateOperationId => L.Format(Keys.Snapshots_Operation_Activate, Info.ProfileName),
        ProfileActivator.DeactivateOperationId => L.Format(Keys.Snapshots_Operation_Deactivate, Info.ProfileName),
        _ => L[Keys.Snapshots_Operation_Unknown],
    };

    /// <summary>Scope or repository the operation addressed.</summary>
    public string Target => Info.Target;

    /// <summary>Localized restorability.</summary>
    public string State => Info.IsRestorable
        ? L[Keys.Snapshots_State_Available]
        : L[Keys.Snapshots_State_Incomplete];

    /// <summary>Number of files preserved.</summary>
    public string FileCount => Info.FileCount.ToString(L.Service.CurrentCulture);

    /// <summary>Directory holding the copies.</summary>
    public string Path => Info.Path;

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The snapshots page.
/// </summary>
/// <remarks>
/// Rolling back is a mutation, so it gets the same two-step treatment as activation: a preview
/// listing every file and what would happen to it, and only then a restore. There is deliberately
/// no one-click rollback anywhere in the interface.
/// </remarks>
internal sealed partial class SnapshotsViewModel : ListPageViewModel
{
    private readonly ISnapshotService _snapshots;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;

    [ObservableProperty]
    private SnapshotRow? _selectedRow;

    public SnapshotsViewModel(
        Localizer localizer,
        ISnapshotService snapshots,
        IDialogService dialogs,
        StatusService status)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);

        _snapshots = snapshots;
        _dialogs = dialogs;
        _status = status;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Snapshots;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Snapshots_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Snapshots_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconSnapshots";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Snapshots_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Retained snapshots, newest first.</summary>
    public ObservableCollection<SnapshotRow> Rows { get; } = [];

    /// <summary>True when there is at least one snapshot to restore.</summary>
    public bool HasSnapshots => Rows.Any(r => r.Info.IsRestorable);

    /// <summary>How many snapshots are kept before the oldest is pruned.</summary>
    public string RetentionCaption => L.Format(Keys.Snapshots_Retention, SnapshotService.RetainedSnapshots);

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken)
    {
        Reload();
        return Task.CompletedTask;
    }

    /// <summary>Re-reads the snapshot directory.</summary>
    internal void Reload()
    {
        var previous = SelectedRow?.Path;

        Rows.Clear();
        foreach (var info in _snapshots.ListSnapshotsDetailed())
        {
            Rows.Add(new SnapshotRow(L, info));
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Path == previous) ?? Rows.FirstOrDefault();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSnapshots));
    }

    /// <summary>Previews rolling back the newest snapshot, then restores it if confirmed.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the preview closes.</returns>
    internal async Task RollbackLatestAsync(CancellationToken cancellationToken)
    {
        Reload();

        SelectedRow = Rows.FirstOrDefault(r => r.Info.IsRestorable);
        if (SelectedRow is not null)
        {
            await PreviewRollbackAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Reloads the list.</summary>
    [RelayCommand]
    private void Refresh() => Reload();

    /// <summary>
    /// Shows what restoring the selected snapshot would do, and restores it only if the user
    /// confirms. Nothing is written while the preview is on screen.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the preview closes.</returns>
    [RelayCommand]
    private async Task PreviewRollbackAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is not { Info.IsRestorable: true } row)
        {
            return;
        }

        var files = await _snapshots.DescribeAsync(row.Path, cancellationToken).ConfigureAwait(true);
        var dialog = new RollbackPreviewViewModel(L, row.Info, files);

        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Ready, Keys.Status_RollbackCancelled);
            return;
        }

        var restored = await _snapshots.RestoreAsync(row.Path, cancellationToken).ConfigureAwait(true);
        _status.Report(StatusKind.Done, Keys.Status_RollbackRestored, restored.Count);

        Reload();
    }


    /// <inheritdoc/>
    internal override void EnsureSelection()
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var current = SelectedRow;
        SelectedRow = null;
        SelectedRow = current ?? Rows[0];
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

    partial void OnSelectedRowChanged(SnapshotRow? value)
    {
        // A DataGrid pushes null back through the binding when it is first attached. A classic
        // list always has a current item, so re-assert the first row instead of letting the
        // properties pane blank itself the moment the page is shown.
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        _ = value;
        RebuildProperties();
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
            Property(Keys.Snapshots_Column_Snapshot, row.Label),
            Property(Keys.Snapshots_Column_Created, row.Created),
            Property(Keys.Snapshots_Column_Operation, row.Operation),
            Property(Keys.Snapshots_Column_Target, row.Target, PropertyStyle.Mono),
            Property(Keys.Snapshots_Detail_Files, row.FileCount),
            Property(Keys.Keys_Column_Path, row.Path, PropertyStyle.Mono),
            Property(
                Keys.Snapshots_Column_State,
                row.State,
                row.Info.IsRestorable ? PropertyStyle.BadgeOk : PropertyStyle.BadgeWarn),
        ]);
    }
}
