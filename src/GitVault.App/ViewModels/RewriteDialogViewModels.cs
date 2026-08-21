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
    public string Reason => L[Step.IsDirectlyEdited ? Keys.Rewrite_Reason_Edited : Keys.Rewrite_Reason_Carried];

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

    /// <summary>True when the plan is blocked.</summary>
    public bool HasBlockers => Blockers.Count > 0;

    /// <summary>True when there is something to warn about.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>True when some refs will be left behind.</summary>
    public bool HasStrandedRefs => StrandedRefs.Count > 0;

    /// <summary>Localized summary of how far the rewrite reaches.</summary>
    public string ReachCaption => L.Format(Keys.Rewrite_Reach, _plan.EditedCount, _plan.CarriedCount);

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
