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

    /// <summary>Localized description of what kind of commit this is.</summary>
    public string Shape => Commit switch
    {
        { IsMerge: true } => L[Keys.Commits_Shape_Merge],
        { IsRoot: true } => L[Keys.Commits_Shape_Root],
        _ => string.Empty,
    };

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
/// The history of one repository, read-only.
/// </summary>
/// <remarks>
/// Deliberately read-only. Everything a rewrite would need is on screen — both identities, both
/// dates with their offsets, the parents, the tree and the signature state — so that when editing
/// arrives the user is deciding about facts they can already see rather than about a summary.
///
/// The signature column is not decoration. A rewrite reproduces a commit from its parts, and
/// GitVault holds no key with which to sign; a signed commit that is rewritten loses its
/// signature. Showing that here means the loss is visible before anyone asks for the edit.
/// </remarks>
internal sealed partial class CommitsViewModel : ListPageViewModel
{
    private readonly ICommitReader _commits;
    private readonly IRepositoryInspector _inspector;
    private readonly RepositoryContext _repository;

    [ObservableProperty]
    private CommitRow? _selectedRow;

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
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(repository);

        _commits = commits;
        _inspector = inspector;
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

    /// <summary>Localized note that this page only reads.</summary>
    public string ReadOnlyCaption => L[Keys.Commits_ReadOnlyNote];

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
                Rows.Add(new CommitRow(L, commit));
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

        RebuildProperties();
        _ = LoadFilesAsync(value);
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

    private async Task LoadFilesAsync(CommitRow? row)
    {
        Files.Clear();

        if (row is null || _repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var changes = await _commits
            .ReadChangesAsync(path, row.Commit.Sha, CancellationToken.None)
            .ConfigureAwait(true);

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
