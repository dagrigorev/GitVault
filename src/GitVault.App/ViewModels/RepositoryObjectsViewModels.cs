using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// Shared behaviour of the pages that edit a repository's refs and remotes.
/// </summary>
/// <remarks>
/// Three pages, one route to a change: build a plan, show it, apply only if the user confirms.
/// Putting that here rather than repeating it three times is what stops one of the three from
/// quietly acquiring a shortcut.
/// </remarks>
internal abstract partial class RepositoryObjectPageViewModel : ListPageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;

    protected RepositoryObjectPageViewModel(
        Localizer localizer,
        IGitObjectEditor editor,
        IRepositoryInspector inspector,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);

        Editor = editor;
        Inspector = inspector;
        _dialogs = dialogs;
        _status = status;
        Repository = repository;

        Repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <summary>Editor used to plan and apply.</summary>
    protected IGitObjectEditor Editor { get; }

    /// <summary>Inspector used to read the repository.</summary>
    protected IRepositoryInspector Inspector { get; }

    /// <summary>Which repository is being shown.</summary>
    protected RepositoryContext Repository { get; }

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => Repository.HasRepository;

    /// <summary>Name of the repository being shown.</summary>
    public string RepositoryName => Repository.CurrentName;

    /// <summary>Localized prompt shown when no repository is selected.</summary>
    public string NoRepositoryCaption => L[Keys.Project_NoRepository];

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads whatever this page lists.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the grid is rebuilt.</returns>
    internal abstract Task ReloadAsync(CancellationToken cancellationToken);

    /// <summary>Shows a plan and applies it only if the user confirms.</summary>
    /// <param name="plan">Plan to review.</param>
    /// <param name="successKey">Resource key of the status message on success.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes and any apply has run.</returns>
    protected async Task ReviewAndApplyAsync(
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

        var result = await Editor.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

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
                    result.Steps.Where(s => s.Outcome == GitVault.Core.Models.StepOutcome.Failed).Select(s => s.Detail)));
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Shows a dialog.</summary>
    /// <param name="dialog">Dialog to show.</param>
    /// <returns><see langword="true"/> when the user accepted it.</returns>
    protected Task<bool> ShowAsync(DialogViewModel dialog) => _dialogs.ShowAsync(dialog);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Repository.PropertyChanged -= OnRepositoryChanged;
        }

        base.Dispose(disposing);
    }

    /// <summary>Called after the repository changed, so the page can refresh its own state.</summary>
    protected virtual void OnRepositorySelected()
    {
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(RepositoryName));
    }

    private void OnRepositoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepositoryContext.CurrentPath))
        {
            OnRepositorySelected();
            _ = ReloadAsync(CancellationToken.None);
        }
    }
}

/// <summary>One remote, as the grid lists it.</summary>
internal sealed class RemoteRow(GitRemote remote) : ObservableObject
{
    /// <summary>The underlying remote.</summary>
    public GitRemote Remote { get; } = remote;

    /// <summary>Remote name, shown verbatim.</summary>
    public string Name => Remote.Name;

    /// <summary>Fetch URL, shown verbatim.</summary>
    public string FetchUrl => Remote.FetchUrl;

    /// <summary>Push URL, shown verbatim.</summary>
    public string PushUrl => Remote.PushUrl;

    /// <summary>True when pushes go somewhere other than fetches.</summary>
    public bool HasSeparatePushUrl => !string.Equals(FetchUrl, PushUrl, StringComparison.Ordinal);
}

/// <summary>The remotes of one repository.</summary>
internal sealed partial class RemotesViewModel : RepositoryObjectPageViewModel
{
    [ObservableProperty]
    private RemoteRow? _selectedRow;

    public RemotesViewModel(
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
    public override string NavKey => Keys.Nav_Remotes;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Remotes_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Remotes_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconClients";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Remotes_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>The remotes.</summary>
    public ObservableCollection<RemoteRow> Rows { get; } = [];

    /// <summary>True when a remote is selected, so the editing verbs apply.</summary>
    public bool HasSelectedRemote => SelectedRow is not null;

    /// <summary>Localized note that GitVault never contacts a remote.</summary>
    public string NoNetworkCaption => L[Keys.Remotes_NoNetworkNote];

    /// <inheritdoc/>
    internal override async Task ReloadAsync(CancellationToken cancellationToken)
    {
        Rows.Clear();

        if (Repository.CurrentPath is { Length: > 0 } path)
        {
            foreach (var remote in await Inspector.ListRemotesAsync(path, cancellationToken).ConfigureAwait(true))
            {
                Rows.Add(new RemoteRow(remote));
            }
        }

        SelectedRow = Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Adds a remote after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task AddAsync(CancellationToken cancellationToken)
    {
        if (Repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var dialog = new RemoteEditorViewModel(L, null);
        if (!await ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await Editor
            .PlanAddRemoteAsync(path, dialog.Name.Trim(), dialog.FetchUrl.Trim(), cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_RemoteSaved, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Edits the selected remote's URLs, and renames it when the name changed.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task EditAsync(CancellationToken cancellationToken)
    {
        if (Repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var dialog = new RemoteEditorViewModel(L, row.Remote);
        if (!await ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        // A rename and a URL change are separate git operations, so they are separate reviews.
        // Bundling them would mean showing one plan and running two.
        if (!string.Equals(dialog.Name.Trim(), row.Name, StringComparison.Ordinal))
        {
            var rename = await Editor
                .PlanRenameRemoteAsync(path, row.Name, dialog.Name.Trim(), cancellationToken)
                .ConfigureAwait(true);

            await ReviewAndApplyAsync(rename, Keys.Status_RemoteSaved, cancellationToken).ConfigureAwait(true);
        }

        var plan = await Editor
            .PlanSetRemoteUrlAsync(
                path,
                dialog.Name.Trim(),
                dialog.FetchUrl.Trim(),
                dialog.UsesSeparatePushUrl ? dialog.PushUrl.Trim() : null,
                cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_RemoteSaved, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Removes the selected remote after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (Repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var plan = await Editor.PlanRemoveRemoteAsync(path, row.Name, cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_RemoteRemoved, cancellationToken).ConfigureAwait(true);
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

    partial void OnSelectedRowChanged(RemoteRow? value)
    {
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        OnPropertyChanged(nameof(HasSelectedRemote));
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
            Property(Keys.Remotes_Column_Name, row.Name),
            Property(Keys.Remotes_Column_FetchUrl, row.FetchUrl, PropertyStyle.Mono),
            Property(Keys.Remotes_Column_PushUrl, row.PushUrl, PropertyStyle.Mono),
        ]);
    }
}
