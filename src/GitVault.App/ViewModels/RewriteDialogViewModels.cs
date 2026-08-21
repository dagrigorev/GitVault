using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// Editing one commit's metadata.
/// </summary>
/// <remarks>
/// Every field starts at the commit's current value, and the dialog reports only what actually
/// differs. Sending an unchanged value as an edit would make the rewriter rebuild commits for no
/// reason, and would make the preview claim a change the user did not ask for.
/// </remarks>
internal sealed partial class CommitEditorViewModel : DialogViewModel
{
    private readonly GitCommit _original;

    [ObservableProperty]
    private string _message;

    [ObservableProperty]
    private string _authorName;

    [ObservableProperty]
    private string _authorEmail;

    [ObservableProperty]
    private string _authorDate;

    [ObservableProperty]
    private string _committerName;

    [ObservableProperty]
    private string _committerEmail;

    [ObservableProperty]
    private string _committerDate;

    internal CommitEditorViewModel(Localizer localizer, GitCommit original)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(original);

        _original = original;
        _message = original.FullMessage;
        _authorName = original.AuthorName;
        _authorEmail = original.AuthorEmail;
        _authorDate = Format(original.AuthorDate);
        _committerName = original.CommitterName;
        _committerEmail = original.CommitterEmail;
        _committerDate = Format(original.CommitterDate);
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Rewrite_EditCommit;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Abbreviated name of the commit being edited.</summary>
    public string ShortSha => _original.ShortSha;

    /// <summary>True when the commit is signed, so the signature will be lost.</summary>
    public bool IsSigned => _original.Signature.IsPresent;

    /// <summary>Localized warning about losing a signature.</summary>
    public string SignatureWarningCaption => L[Keys.Commits_SignatureWarning];

    /// <summary>Localized note about the date format the fields expect.</summary>
    public string DateHintCaption => L[Keys.Rewrite_DateHint];

    /// <summary>True when at least one field differs from the original.</summary>
    public bool HasChanges => !ToEdit().IsEmpty;

    /// <summary>False while a date field cannot be read as a date.</summary>
    public bool DatesAreValid => TryParse(AuthorDate, out _) && TryParse(CommitterDate, out _);

    /// <inheritdoc/>
    public override bool CanConfirm => DatesAreValid && HasChanges;

    /// <inheritdoc/>
    public override double DialogWidth => 620;

    /// <inheritdoc/>
    public override double DialogHeight => 520;

    /// <summary>
    /// Builds the edit, carrying only the fields that actually differ.
    /// </summary>
    /// <returns>The edit; empty when nothing changed.</returns>
    internal CommitEdit ToEdit()
    {
        var message = Message.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        var originalMessage = _original.FullMessage.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

        return new CommitEdit(_original.Sha)
        {
            Message = string.Equals(message, originalMessage, StringComparison.Ordinal) ? null : message,
            AuthorName = Changed(AuthorName, _original.AuthorName),
            AuthorEmail = Changed(AuthorEmail, _original.AuthorEmail),
            AuthorDate = ChangedDate(AuthorDate, _original.AuthorDate),
            CommitterName = Changed(CommitterName, _original.CommitterName),
            CommitterEmail = Changed(CommitterEmail, _original.CommitterEmail),
            CommitterDate = ChangedDate(CommitterDate, _original.CommitterDate),
        };
    }

    partial void OnMessageChanged(string value) => Revalidate(value);

    partial void OnAuthorNameChanged(string value) => Revalidate(value);

    partial void OnAuthorEmailChanged(string value) => Revalidate(value);

    partial void OnAuthorDateChanged(string value) => Revalidate(value);

    partial void OnCommitterNameChanged(string value) => Revalidate(value);

    partial void OnCommitterEmailChanged(string value) => Revalidate(value);

    partial void OnCommitterDateChanged(string value) => Revalidate(value);

    private void Revalidate(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(DatesAreValid));
        OnPropertyChanged(nameof(CanConfirm));
    }

    private static string? Changed(string edited, string original) =>
        string.Equals(edited.Trim(), original, StringComparison.Ordinal) ? null : edited.Trim();

    private DateTimeOffset? ChangedDate(string edited, DateTimeOffset original) =>
        TryParse(edited, out var parsed) && parsed != original ? parsed : null;

    /// <summary>Formats a date the way the field expects it back, offset included.</summary>
    private string Format(DateTimeOffset value) =>
        value.ToString(L[Keys.Commits_DateFormat], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a date typed into a field.
    /// </summary>
    /// <remarks>
    /// Only formats that carry an explicit offset are accepted. A date typed without one would be
    /// read in the machine's timezone and then written into history as though the author had been
    /// sitting there — the same silent conversion this project refuses everywhere else. Rejecting
    /// it is better than guessing, so the field stays invalid until an offset is present.
    ///
    /// The first accepted format is the one the field was filled with, taken from the same
    /// resource, so what the dialog writes and what it reads back cannot drift apart. The other
    /// two are the ISO spellings someone is likely to paste from a git command.
    /// </remarks>
    private bool TryParse(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value.Trim(),
            [L[Keys.Commits_DateFormat], GitDateFormats.Iso, GitDateFormats.IsoUtc],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out parsed);
}

/// <summary>
/// Editing what one file contains as of one commit.
/// </summary>
/// <remarks>
/// The dialog holds the whole file rather than a diff, because that is the thing the user is
/// deciding: what this commit should contain. What happens to the commits after it is worked out
/// afterwards by the plan, and said out loud in the note rather than left to be discovered.
/// </remarks>
internal sealed partial class FileEditorViewModel : DialogViewModel
{
    private readonly FileContent _original;

    [ObservableProperty]
    private string _text;

    internal FileEditorViewModel(Localizer localizer, GitCommit commit, FileContent original)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(original);

        Commit = commit;
        _original = original;
        _text = original.Text;
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Rewrite_EditFile;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>The commit whose copy of the file is being edited.</summary>
    internal GitCommit Commit { get; }

    /// <summary>Path being edited, shown verbatim.</summary>
    public string Path => _original.Path;

    /// <summary>Abbreviated name of the commit being edited.</summary>
    public string ShortSha => Commit.ShortSha;

    /// <summary>Localized explanation of what happens to later commits.</summary>
    public string FileNoteCaption => L[Keys.Rewrite_FileNote];

    /// <summary>True when the commit carries a signature a rewrite would drop.</summary>
    public bool IsSigned => Commit.Signature.IsPresent;

    /// <summary>Localized warning about losing a signature.</summary>
    public string SignatureWarningCaption => L[Keys.Commits_SignatureWarning];

    /// <inheritdoc/>
    public override bool CanConfirm => !string.Equals(Text, _original.Text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override double DialogWidth => 720;

    /// <inheritdoc/>
    public override double DialogHeight => 560;

    /// <summary>The edit this dialog describes.</summary>
    /// <returns>The file edit.</returns>
    internal FileEdit ToEdit() => new(_original.Path, Text);

    partial void OnTextChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }
}

/// <summary>
/// Settling one commit's own change against the edit carried into it.
/// </summary>
/// <remarks>
/// The text arrives as git left it, conflict markers included, and the user edits it into what
/// the commit should hold. Confirming is refused while a marker is still present — not as
/// pedantry but because a marker committed into history is a broken file, and this is the one
/// moment where catching it costs nothing.
///
/// Nothing has been written when this appears. The conflict was found while planning, so closing
/// the dialog leaves the repository exactly as it was — which is the difference between this and
/// a rebase that stops half-way and hands the user a conflicted working tree.
/// </remarks>
internal sealed partial class ConflictResolutionViewModel : DialogViewModel
{
    private readonly ContentConflict _conflict;

    [ObservableProperty]
    private string _text;

    internal ConflictResolutionViewModel(Localizer localizer, ContentConflict conflict)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        _conflict = conflict;
        _text = conflict.MergedText;
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Rewrite_Conflict_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Path that conflicts, shown verbatim.</summary>
    public string Path => _conflict.Path;

    /// <summary>The commit whose own change disagrees, named as the user would recognise it.</summary>
    public string ConflictingCommit =>
        L.Format(Keys.Rewrite_CommitLabel, _conflict.ShortSha, _conflict.Subject);

    /// <summary>Localized explanation of what has to be done here.</summary>
    public string ExplainsCaption => L[Keys.Rewrite_Conflict_Explains];

    /// <summary>Localized note that markers are still present.</summary>
    public string MarkersLeftCaption => L[Keys.Rewrite_Conflict_MarkersLeft];

    /// <summary>True while the text still contains a conflict marker.</summary>
    public bool HasMarkers => MergeLabels.HasMarkers(Text);

    /// <inheritdoc/>
    public override bool CanConfirm => !HasMarkers;

    /// <inheritdoc/>
    public override double DialogWidth => 760;

    /// <inheritdoc/>
    public override double DialogHeight => 600;

    /// <summary>The resolution this dialog describes.</summary>
    /// <returns>The resolution.</returns>
    internal ConflictResolution ToResolution() => new(_conflict.Sha, _conflict.Path, Text);

    partial void OnTextChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasMarkers));
        OnPropertyChanged(nameof(CanConfirm));
    }
}

/// <summary>One commit in the rewrite preview.</summary>
internal sealed class RewriteRow(Localizer localizer, RewriteStep step) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The step this row describes.</summary>
    public RewriteStep Step { get; } = step;

    /// <summary>Abbreviated name the commit has now.</summary>
    public string ShortSha => Step.Original.ShortSha;

    /// <summary>Subject the commit will end up with.</summary>
    public string Subject => Step.Edit?.Message is { Length: > 0 } message
        ? message.Split('\n')[0]
        : Step.Original.Subject;

    /// <summary>Localized description of why this commit is being rebuilt.</summary>
    public string Reason => L[Step switch
    {
        { IsDirectlyEdited: true } => Keys.Rewrite_Reason_Edited,
        { CarriesContent: true } => Keys.Rewrite_Reason_Content,
        _ => Keys.Rewrite_Reason_Carried,
    }];

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The preview and the gate for rewriting history.
/// </summary>
/// <remarks>
/// This is the most consequential dialog in the application, and it is deliberately harder to get
/// past than any other. Confirming requires typing the branch name, in the tradition of dialogs
/// that ask you to name the thing you are about to change — a plain button is too easy to press
/// by habit, and the consequence here reaches every clone of the repository.
///
/// The preview says how far the change reaches, not only what was edited. Rewriting one commit in
/// the middle of a branch gives every commit after it a new identifier, and that is the part
/// people are surprised by.
/// </remarks>
internal sealed partial class RewriteReviewViewModel : DialogViewModel
{
    private readonly RewritePlan _plan;

    [ObservableProperty]
    private string _typedConfirmation = string.Empty;

    internal RewriteReviewViewModel(Localizer localizer, RewritePlan plan)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _plan = plan;

        foreach (var step in plan.Steps)
        {
            Rows.Add(new RewriteRow(localizer, step));
        }

        foreach (var blocker in plan.Blockers)
        {
            Blockers.Add(Localize(localizer, blocker, BlockerMessages.Prefix));
        }

        foreach (var warning in plan.Warnings)
        {
            Warnings.Add(Localize(localizer, warning, RepositoryWarnings.Prefix));
        }

        foreach (var reference in plan.StrandedRefs)
        {
            StrandedRefs.Add(reference);
        }

        foreach (var conflict in plan.Conflicts)
        {
            Conflicts.Add(localizer.Format(Keys.Rewrite_CommitLabel, conflict.ShortSha, conflict.Path));
        }
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Rewrite_Preview_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Rewrite_Confirm;

    /// <summary>Branch whose tip will move.</summary>
    public string BranchName => _plan.BranchName;

    /// <summary>Commits that will be rebuilt.</summary>
    public ObservableCollection<RewriteRow> Rows { get; } = [];

    /// <summary>Reasons the rewrite cannot proceed, localized.</summary>
    public ObservableCollection<string> Blockers { get; } = [];

    /// <summary>Things worth knowing before confirming, localized.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>Refs that will keep pointing at replaced commits.</summary>
    public ObservableCollection<string> StrandedRefs { get; } = [];

    /// <summary>Commits whose own change to an edited file is still unsettled.</summary>
    public ObservableCollection<string> Conflicts { get; } = [];

    /// <summary>True when a conflict is still waiting.</summary>
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>True when the plan is blocked.</summary>
    public bool HasBlockers => Blockers.Count > 0;

    /// <summary>True when there is something to warn about.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>True when some refs will be left behind.</summary>
    public bool HasStrandedRefs => StrandedRefs.Count > 0;

    /// <summary>Localized summary of how far the rewrite reaches.</summary>
    public string ReachCaption => L.Format(Keys.Rewrite_Reach, _plan.EditedCount, _plan.CarriedCount);

    /// <summary>Localized summary of how many commits end up holding different content.</summary>
    public string ContentReachCaption => L.Format(Keys.Rewrite_ContentReach, _plan.ContentCount);

    /// <summary>True when the rewrite changes what any file contains.</summary>
    public bool ChangesContent => _plan.ContentCount > 0;

    /// <summary>Localized note that nothing has been written yet.</summary>
    public string NothingWrittenCaption => L[Keys.Dialog_NothingWritten];

    /// <summary>Localized instruction naming what has to be typed.</summary>
    public string ConfirmationPromptCaption => L.Format(Keys.Rewrite_TypeBranchName, _plan.ConfirmationPhrase);

    /// <summary>Localized explanation of what the backup ref buys.</summary>
    public string BackupExplainsCaption => L[Keys.Dialog_RefBackupExplains];

    /// <summary>Localized statement that GitVault will not push the result anywhere.</summary>
    public string NoPushCaption => L[Keys.Rewrite_NoPushNote];

    /// <summary>True when the typed text matches the branch name exactly.</summary>
    public bool ConfirmationMatches =>
        string.Equals(TypedConfirmation.Trim(), _plan.ConfirmationPhrase, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool CanConfirm => _plan.CanApply && ConfirmationMatches;

    /// <inheritdoc/>
    public override double DialogWidth => 680;

    /// <inheritdoc/>
    public override double DialogHeight => 620;

    partial void OnTypedConfirmationChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(ConfirmationMatches));
        OnPropertyChanged(nameof(CanConfirm));
    }

    /// <summary>Turns an identifier into text, and leaves anything else alone.</summary>
    private static string Localize(Localizer localizer, string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? localizer[value] : value;
}
