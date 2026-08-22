using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One of the repository's plain-text control files, as the list shows it.</summary>
internal sealed class RepositoryFileRow(Localizer localizer, RepositoryFileKind kind) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>Which file this row is.</summary>
    public RepositoryFileKind Kind { get; } = kind;

    /// <summary>The file as it was last read, or null when it cannot be edited here.</summary>
    internal RepositoryFile? File { get; private set; }

    /// <summary>True when the file was read successfully.</summary>
    public bool IsReadable { get; private set; }

    /// <summary>Localized name of the file, including the path git knows it by.</summary>
    public string Name => L[Kind switch
    {
        RepositoryFileKind.Ignore => Keys.RepositoryFiles_Kind_Ignore,
        RepositoryFileKind.Exclude => Keys.RepositoryFiles_Kind_Exclude,
        RepositoryFileKind.Attributes => Keys.RepositoryFiles_Kind_Attributes,
        _ => Keys.RepositoryFiles_Kind_Mailmap,
    }];

    /// <summary>Localized state: missing, committed, private, or not editable.</summary>
    public string State => L[this switch
    {
        { IsReadable: false } => Keys.RepositoryFiles_State_Unreadable,
        { File.Exists: false } => Keys.RepositoryFiles_State_Missing,
        { File.IsTracked: true } => Keys.RepositoryFiles_State_Tracked,
        _ => Keys.RepositoryFiles_State_Untracked,
    }];

    /// <summary>Records what was read for this file.</summary>
    /// <param name="file">The file, or null when it cannot be edited here.</param>
    internal void SetFile(RepositoryFile? file)
    {
        File = file;
        IsReadable = file is not null;

        OnPropertyChanged(nameof(IsReadable));
        OnPropertyChanged(nameof(State));
    }

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The editor for <c>.gitignore</c>, <c>.gitattributes</c>, <c>.mailmap</c> and the private
/// exclude file.
/// </summary>
/// <remarks>
/// Four files on one page because they are the same kind of thing: plain text that changes how
/// git treats this repository, edited in place rather than through history.
///
/// The page says which of them are committed. Editing <c>.gitignore</c> changes what everyone
/// working on the project sees once the change is committed; editing the exclude file changes
/// nothing outside this clone. Those are different decisions, and a page that presented them
/// identically would be hiding the difference.
///
/// Writing is not committing, and the page says that too. GitVault writes the file and stops
/// there; whether the change becomes part of the project is the user's own next action.
/// </remarks>
internal sealed partial class RepositoryFilesViewModel : ListPageViewModel
{
    private readonly IRepositoryFileEditor _files;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly RepositoryContext _repository;

    [ObservableProperty]
    private RepositoryFileRow? _selectedRow;

    [ObservableProperty]
    private string _text = string.Empty;

    public RepositoryFilesViewModel(
        Localizer localizer,
        IRepositoryFileEditor files,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);

        _files = files;
        _dialogs = dialogs;
        _status = status;
        _repository = repository;

        foreach (var kind in Enum.GetValues<RepositoryFileKind>())
        {
            Rows.Add(new RepositoryFileRow(localizer, kind));
        }

        _selectedRow = Rows[0];
        _repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_RepositoryFiles;

    /// <inheritdoc/>
    public override string TitleKey => Keys.RepositoryFiles_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.RepositoryFiles_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconOptions";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.RepositoryFiles_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => !HasRepository;

    /// <summary>The four files.</summary>
    public ObservableCollection<RepositoryFileRow> Rows { get; } = [];

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => _repository.HasRepository;

    /// <summary>Localized prompt shown when no repository is selected.</summary>
    public string NoRepositoryCaption => L[Keys.RepositoryFiles_Empty];

    /// <summary>True when the selected file can be edited here.</summary>
    public bool CanEdit => HasRepository && SelectedRow is { IsReadable: true };

    /// <summary>True when the text differs from what is on disk.</summary>
    public bool HasChanges =>
        SelectedRow?.File is { } file && !string.Equals(Text, file.Text, StringComparison.Ordinal);

    /// <summary>True when the selected file is committed, so a change reaches other people.</summary>
    public bool IsTracked => SelectedRow?.File is { IsTracked: true };

    /// <summary>True when the selected file cannot be edited here.</summary>
    public bool IsUnreadable => SelectedRow is { IsReadable: false };

    /// <summary>Localized note that a change to a committed file reaches everyone.</summary>
    public string TrackedNoteCaption => L[Keys.RepositoryFiles_TrackedNote];

    /// <summary>Localized note that this file never leaves the clone.</summary>
    public string UntrackedNoteCaption => L[Keys.RepositoryFiles_UntrackedNote];

    /// <summary>Localized explanation of why a file is not offered for editing.</summary>
    public string UnreadableNoteCaption => L[Keys.RepositoryFiles_UnreadableNote];

    /// <summary>Path of the selected file, shown verbatim.</summary>
    public string SelectedPath => SelectedRow?.File?.Path ?? string.Empty;

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads all four files.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the list is rebuilt.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path)
        {
            foreach (var row in Rows)
            {
                row.SetFile(null);
            }

            Text = string.Empty;
            Notify();
            return;
        }

        foreach (var row in Rows)
        {
            row.SetFile(await _files.ReadAsync(path, row.Kind, cancellationToken).ConfigureAwait(true));
        }

        Text = SelectedRow?.File?.Text ?? string.Empty;
        Notify();
    }

    /// <summary>Re-reads the files, discarding anything typed.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the list is rebuilt.</returns>
    [RelayCommand]
    private Task RevertAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Previews the change and writes it only if the user confirms.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path
            || SelectedRow is not { IsReadable: true } row)
        {
            return;
        }

        var plan = await _files
            .PlanWriteAsync(path, row.Kind, Text, cancellationToken)
            .ConfigureAwait(true);

        var review = new OperationReviewViewModel(L, plan);

        if (!await _dialogs.ShowAsync(review).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Ready, Keys.Status_PlanNotApplied);
            return;
        }

        var result = await _files.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

        if (result.Succeeded)
        {
            _status.Report(StatusKind.Done, Keys.Status_RepositoryFileSaved);
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

    partial void OnSelectedRowChanged(RepositoryFileRow? value)
    {
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        Text = value?.File?.Text ?? string.Empty;
        Notify();
    }

    partial void OnTextChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasChanges));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(IsTracked));
        OnPropertyChanged(nameof(IsUnreadable));
        OnPropertyChanged(nameof(SelectedPath));
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
