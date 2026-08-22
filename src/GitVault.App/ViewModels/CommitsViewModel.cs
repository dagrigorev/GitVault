using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One commit, as the grid lists it.</summary>
internal sealed class CommitRow(Localizer localizer, GitCommit commit) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying commit.</summary>
    public GitCommit Commit { get; } = commit;

    /// <summary>Abbreviated object name, shown verbatim.</summary>
    public string ShortSha => Commit.ShortSha;

    /// <summary>Subject line, shown verbatim.</summary>
    public string Subject => Commit.Subject;

    /// <summary>Author, as git records the identity.</summary>
    public string Author => Commit.AuthorIdentity;

    /// <summary>Author date in the active culture, keeping the offset git recorded.</summary>
    public string AuthorDate => Commit.AuthorDate == DateTimeOffset.MinValue
        ? string.Empty
        : Commit.AuthorDate.ToString("g", L.Service.CurrentCulture);

    /// <summary>Localized signature state.</summary>
    public string Signature => L[DisplayNames.SignatureKey(Commit.Signature.State)];

    /// <summary>True when the user has edited this commit but not applied the change.</summary>
    public bool IsPending { get; private set; }

    /// <summary>Localized mark shown in the pending column, or an empty cell.</summary>
    public string Pending => IsPending ? L[Keys.Commits_PendingMark] : string.Empty;

    /// <summary>Localized description of what kind of commit this is.</summary>
    public string Shape => Commit switch
    {
        { IsMerge: true } => L[Keys.Commits_Shape_Merge],
        { IsRoot: true } => L[Keys.Commits_Shape_Root],
        _ => string.Empty,
    };

    /// <summary>Records whether an unapplied edit is waiting for this commit.</summary>
    /// <param name="pending">True when an edit is waiting.</param>
    internal void SetPending(bool pending)
    {
        IsPending = pending;
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(Pending));
    }

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>One file a commit touched, as the details grid lists it.</summary>
internal sealed class CommitFileRow(Localizer localizer, CommitFileChange change) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying change.</summary>
    public CommitFileChange Change { get; } = change;

    /// <summary>Path after the change; a rename shows both paths.</summary>
    public string Path => Change.OldPath is { Length: > 0 } old
        ? L.Format(Keys.Commits_RenamedPath, old, Change.Path)
        : Change.Path;

    /// <summary>Localized status.</summary>
    public string Status => L[DisplayNames.FileChangeKey(Change.Status)];

    /// <summary>Line counts, or the localized "binary".</summary>
    public string Lines => Change.IsBinary
        ? L[Keys.Commits_Binary]
        : L.Format(Keys.Commits_LineCounts, Change.Added ?? 0, Change.Removed ?? 0);

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The history of one repository, and the page from which commits are edited.
/// </summary>
/// <remarks>
/// Everything a rewrite needs is on screen — both identities, both dates with their offsets, the
/// parents, the tree and the signature state — so the user is deciding about facts they can see
/// rather than about a summary.
///
/// Editing is deliberately in two stages. Each edit is collected here and nothing is written; the
/// rewrite happens once, when the user applies them. That is not only caution: rewriting a branch
/// twice in a row would rebuild the same commits twice and produce a second set of identifiers
/// for no reason, so collecting the edits is also the correct way to do the work.
///
/// The signature column is not decoration. A rewrite reproduces a commit from its parts, and
/// GitVault holds no key with which to sign; a signed commit that is rewritten loses its
/// signature. Showing that here means the loss is visible before anyone asks for the edit.
/// </remarks>
internal sealed partial class CommitsViewModel : ListPageViewModel
{
    private readonly ICommitReader _commits;
    private readonly IRepositoryInspector _inspector;
    private readonly IHistoryRewriter _rewriter;
    private readonly IFileContentReader _files;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly RepositoryContext _repository;

    /// <summary>
    /// Edits the user has made and not yet applied, keyed by commit.
    /// </summary>
    /// <remarks>
    /// Keyed rather than appended, so editing the same commit twice replaces the earlier edit
    /// instead of queueing two changes whose order would decide the outcome.
    /// </remarks>
    private readonly Dictionary<string, CommitEdit> _pendingEdits = new(StringComparer.Ordinal);

    /// <summary>
    /// What the user decided about conflicts an earlier plan reported.
    /// </summary>
    /// <remarks>
    /// Kept across re-planning so that settling one conflict does not put the previous ones back
    /// on screen. Discarding the edits clears these too: a resolution only means anything next to
    /// the edit it was made for.
    /// </remarks>
    private readonly List<ConflictResolution> _resolutions = [];

    /// <summary>Counts file reads, so only the newest one is allowed to fill the list.</summary>
    private int _fileGeneration;

    [ObservableProperty]
    private CommitRow? _selectedRow;

    [ObservableProperty]
    private CommitFileRow? _selectedFile;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _authorFilter = string.Empty;

    [ObservableProperty]
    private CommitLimitOption? _selectedLimit;

    [ObservableProperty]
    private bool _firstParentOnly;

    [ObservableProperty]
    private bool _isLoading;

    public CommitsViewModel(
        Localizer localizer,
        ICommitReader commits,
        IRepositoryInspector inspector,
        IHistoryRewriter rewriter,
        IFileContentReader files,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(rewriter);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);

        _commits = commits;
        _inspector = inspector;
        _rewriter = rewriter;
        _files = files;
        _dialogs = dialogs;
        _status = status;
        _repository = repository;

        Limits =
        [
            new CommitLimitOption(localizer, 100, Keys.Commits_Limit_100),
            new CommitLimitOption(localizer, 500, Keys.Commits_Limit_500),
            new CommitLimitOption(localizer, 5000, Keys.Commits_Limit_5000),
        ];

        _selectedLimit = Limits[0];
        _repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Commits;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Commits_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Commits_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconLogs";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Commits_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Commits currently listed, newest first.</summary>
    public ObservableCollection<CommitRow> Rows { get; } = [];

    /// <summary>Files the selected commit touched.</summary>
    public ObservableCollection<CommitFileRow> Files { get; } = [];

    /// <summary>How many commits to read.</summary>
    public ObservableCollection<CommitLimitOption> Limits { get; }

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => _repository.HasRepository;

    /// <summary>Localized prompt shown when no repository is selected.</summary>
    public string NoRepositoryCaption => L[Keys.Project_NoRepository];

    /// <summary>Message body of the selected commit, shown verbatim.</summary>
    public string SelectedBody => SelectedRow?.Commit.Body ?? string.Empty;

    /// <summary>True when the selected commit has more message than its subject.</summary>
    public bool HasBody => SelectedBody.Length > 0;

    /// <summary>True when the selected commit carries a signature a rewrite would drop.</summary>
    public bool SelectionIsSigned => SelectedRow?.Commit.Signature.IsPresent == true;

    /// <summary>Localized warning that rewriting a signed commit loses its signature.</summary>
    public string SignatureWarningCaption => L[Keys.Commits_SignatureWarning];

    /// <summary>Localized note that edits are collected before anything is written.</summary>
    public string EditingCaption => L[Keys.Commits_EditingNote];

    /// <summary>True when a commit is selected, so it can be edited.</summary>
    public bool HasSelectedCommit => SelectedRow is not null;

    /// <summary>True when a file of the selected commit is selected, so it can be edited.</summary>
    public bool HasSelectedFile => SelectedRow is not null && SelectedFile is not null;

    /// <summary>True when edits are waiting to be applied.</summary>
    public bool HasPendingEdits => _pendingEdits.Count > 0;

    /// <summary>Localized count of the edits waiting to be applied.</summary>
    public string PendingCountCaption => L.Format(Keys.Commits_PendingCount, _pendingEdits.Count);

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads the history with the current filters.</summary>
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

        // An empty repository has no HEAD, and asking git to log it is an error rather than an
        // empty answer. Checking first keeps the empty state showing instead of a failure.
        var state = await _inspector.GetStateAsync(path, cancellationToken).ConfigureAwait(true);
        if (state.HeadCommit is not { Length: > 0 })
        {
            Rows.Clear();
            Files.Clear();
            Notify();
            return;
        }

        IsLoading = true;
        try
        {
            var query = new CommitQuery(Limit: SelectedLimit?.Value ?? 100)
            {
                MessageFilter = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                AuthorFilter = string.IsNullOrWhiteSpace(AuthorFilter) ? null : AuthorFilter.Trim(),
                FirstParentOnly = FirstParentOnly,
            };

            var previous = SelectedRow?.Commit.Sha;
            var commits = await _commits.ReadAsync(path, query, cancellationToken).ConfigureAwait(true);

            Rows.Clear();
            foreach (var commit in commits)
            {
                var row = new CommitRow(L, commit);
                row.SetPending(_pendingEdits.ContainsKey(commit.Sha));
                Rows.Add(row);
            }

            SelectedRow = Rows.FirstOrDefault(r => r.Commit.Sha == previous) ?? Rows.FirstOrDefault();
        }
        finally
        {
            IsLoading = false;
            Notify();
        }
    }

    /// <summary>Re-reads the history.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the grid is rebuilt.</returns>
    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Edits the selected commit, collecting the change without writing anything.</summary>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task EditAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var dialog = new CommitEditorViewModel(L, row.Commit);
        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var edit = dialog.ToEdit();

        Stage(row, existing => existing with
        {
            Message = edit.Message,
            AuthorName = edit.AuthorName,
            AuthorEmail = edit.AuthorEmail,
            AuthorDate = edit.AuthorDate,
            CommitterName = edit.CommitterName,
            CommitterEmail = edit.CommitterEmail,
            CommitterDate = edit.CommitterDate,
        });
    }

    /// <summary>
    /// Edits the content of the selected file as of the selected commit.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task EditFileAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is not { } row
            || SelectedFile is not { } file
            || _repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var content = await _files
            .ReadAsync(path, row.Commit.Sha, file.Change.Path, cancellationToken)
            .ConfigureAwait(true);

        if (content is null)
        {
            // The same rules the rewrite would apply, applied before the user types anything.
            _status.Report(StatusKind.Error, Keys.Commits_FileNotEditable);
            return;
        }

        var dialog = new FileEditorViewModel(L, row.Commit, content);
        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        Stage(row, edit => edit with { Files = Replace(edit.Files, dialog.ToEdit()) });
    }

    /// <summary>Throws away every collected edit.</summary>
    [RelayCommand]
    private void DiscardEdits()
    {
        if (_pendingEdits.Count == 0)
        {
            return;
        }

        _pendingEdits.Clear();
        _resolutions.Clear();

        foreach (var row in Rows)
        {
            row.SetPending(false);
        }

        NotifyPending();
        _status.Report(StatusKind.Ready, Keys.Status_EditsDiscarded);
    }

    /// <summary>
    /// Plans the rewrite, shows it, and applies it only if the user types the branch name.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes and any rewrite has run.</returns>
    [RelayCommand]
    private async Task ApplyEditsAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || _pendingEdits.Count == 0)
        {
            return;
        }

        var plan = await _rewriter
            .PlanAsync(path, [.. _pendingEdits.Values], [.. _resolutions], cancellationToken)
            .ConfigureAwait(true);

        // A conflict is a question, not a failure. Each one is asked before the preview, so what
        // the preview finally shows is a rewrite that can actually be carried out.
        while (plan.Conflicts.Count > 0)
        {
            var conflict = plan.Conflicts[0];
            var resolver = new ConflictResolutionViewModel(L, conflict);

            if (!await _dialogs.ShowAsync(resolver).ConfigureAwait(true))
            {
                _status.Report(StatusKind.Ready, Keys.Status_PlanNotApplied);
                return;
            }

            _resolutions.RemoveAll(r =>
                string.Equals(r.Sha, conflict.Sha, StringComparison.Ordinal)
                && string.Equals(r.Path, conflict.Path, StringComparison.Ordinal));

            _resolutions.Add(resolver.ToResolution());

            plan = await _rewriter
                .PlanAsync(path, [.. _pendingEdits.Values], [.. _resolutions], cancellationToken)
                .ConfigureAwait(true);
        }

        var review = new RewriteReviewViewModel(L, plan);
        if (!await _dialogs.ShowAsync(review).ConfigureAwait(true))
        {
            // The edits stay collected: the user declined this rewrite, not their own work.
            _status.Report(StatusKind.Ready, Keys.Status_PlanNotApplied);
            return;
        }

        var result = await _rewriter.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

        if (result.Succeeded)
        {
            _pendingEdits.Clear();
            _resolutions.Clear();
            NotifyPending();
            _status.Report(StatusKind.Done, Keys.Status_HistoryRewritten);
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

    /// <summary>Records an edit against a commit, replacing whatever was collected before.</summary>
    private void Stage(CommitRow row, Func<CommitEdit, CommitEdit> change)
    {
        var existing = _pendingEdits.TryGetValue(row.Commit.Sha, out var found)
            ? found
            : new CommitEdit(row.Commit.Sha);

        var edited = change(existing);

        if (edited.IsEmpty)
        {
            _pendingEdits.Remove(row.Commit.Sha);
        }
        else
        {
            _pendingEdits[row.Commit.Sha] = edited;
        }

        row.SetPending(_pendingEdits.ContainsKey(row.Commit.Sha));
        NotifyPending();
    }

    /// <summary>Puts one file edit into a list, replacing an earlier edit of the same path.</summary>
    private static IReadOnlyList<FileEdit> Replace(IReadOnlyList<FileEdit> files, FileEdit edit)
    {
        var kept = files.Where(f => !string.Equals(f.Path, edit.Path, StringComparison.Ordinal)).ToList();
        kept.Add(edit);
        return kept;
    }

    private void NotifyPending()
    {
        OnPropertyChanged(nameof(HasPendingEdits));
        OnPropertyChanged(nameof(PendingCountCaption));
        ApplyEditsCommand.NotifyCanExecuteChanged();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRepository));
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

    partial void OnSelectedRowChanged(CommitRow? value)
    {
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        OnPropertyChanged(nameof(SelectedBody));
        OnPropertyChanged(nameof(HasBody));
        OnPropertyChanged(nameof(SelectionIsSigned));
        OnPropertyChanged(nameof(HasSelectedCommit));
        EditCommand.NotifyCanExecuteChanged();

        RebuildProperties();
        _ = LoadFilesAsync(value);
    }

    partial void OnSelectedFileChanged(CommitFileRow? value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasSelectedFile));
        EditFileCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLimitChanged(CommitLimitOption? value)
    {
        _ = value;
        _ = ReloadAsync(CancellationToken.None);
    }

    partial void OnFirstParentOnlyChanged(bool value)
    {
        _ = value;
        _ = ReloadAsync(CancellationToken.None);
    }

    /// <summary>
    /// Reads the files a commit touched, and ignores its own answer if the selection moved on.
    /// </summary>
    /// <remarks>
    /// Selecting a row can raise the change more than once — a grid pushes a null selection back
    /// while it is attaching, and the guard above restores it — so several reads can be in flight
    /// at once. Each clears the list before its own await and appends after it, which means the
    /// later clears happen first and every answer is appended: the same file appeared two or three
    /// times over. The generation counter is what makes only the newest read allowed to write.
    /// </remarks>
    private async Task LoadFilesAsync(CommitRow? row)
    {
        var generation = ++_fileGeneration;

        SelectedFile = null;
        Files.Clear();

        if (row is null || _repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var changes = await _commits
            .ReadChangesAsync(path, row.Commit.Sha, CancellationToken.None)
            .ConfigureAwait(true);

        if (generation != _fileGeneration)
        {
            return;
        }

        foreach (var change in changes)
        {
            Files.Add(new CommitFileRow(L, change));
        }
    }

    private void RebuildProperties()
    {
        if (SelectedRow is not { } row)
        {
            SetProperties([]);
            return;
        }

        var commit = row.Commit;

        var entries = new List<PropertyEntry>
        {
            Property(Keys.Commits_Column_Sha, commit.Sha, PropertyStyle.Mono),
            Property(Keys.Commits_Detail_Tree, commit.TreeSha, PropertyStyle.Mono),
            Property(Keys.Commits_Column_Author, commit.AuthorIdentity),
            Property(Keys.Commits_Detail_AuthorDate, Format(commit.AuthorDate), PropertyStyle.Mono),
        };

        // The committer is shown always, not only when it differs: a rewrite writes both, and a
        // field that appears only sometimes is a field people stop looking for.
        entries.Add(Property(Keys.Commits_Detail_Committer, commit.CommitterIdentity));
        entries.Add(Property(Keys.Commits_Detail_CommitterDate, Format(commit.CommitterDate), PropertyStyle.Mono));

        entries.Add(Property(
            Keys.Commits_Detail_Parents,
            commit.Parents.Count == 0 ? L[Keys.Commits_Shape_Root] : string.Join(Environment.NewLine, commit.Parents),
            PropertyStyle.Mono));

        entries.Add(Property(
            Keys.Commits_Column_Signature,
            row.Signature,
            commit.Signature.State switch
            {
                SignatureState.None => PropertyStyle.Badge,
                SignatureState.Good => PropertyStyle.BadgeOk,
                _ => PropertyStyle.BadgeWarn,
            }));

        if (commit.Signature.Signer is { Length: > 0 })
        {
            entries.Add(Property(Keys.Commits_Detail_Signer, commit.Signature.Signer));
        }

        SetProperties(entries);
    }

    /// <summary>
    /// Formats a date keeping the offset git recorded, which local time would discard.
    /// </summary>
    /// <remarks>
    /// The pattern lives in the resource file so it is a translation decision rather than a
    /// literal buried here, but it is deliberately the same in every language: the point of this
    /// field is to show the exact recorded offset unambiguously before a rewrite reproduces it,
    /// and a locale-dependent arrangement would work against that.
    /// </remarks>
    private string Format(DateTimeOffset value) => value == DateTimeOffset.MinValue
        ? string.Empty
        : value.ToString(L[Keys.Commits_DateFormat], L.Service.CurrentCulture);
}

/// <summary>How many commits the history page reads at once.</summary>
internal sealed class CommitLimitOption(Localizer localizer, int value, string labelKey) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The limit.</summary>
    public int Value { get; } = value;

    /// <summary>Localized label.</summary>
    public string Label => L[labelKey];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}
