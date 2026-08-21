using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// The preview for a change to remotes, branches or tags.
/// </summary>
/// <remarks>
/// Carries warnings as well as blockers, which the configuration preview does not need. The
/// distinction is the point: a blocker is something the user cannot do, a warning is something
/// they may not want to. Deleting an unmerged branch belongs in the second category, and folding
/// it into the first would make GitVault refuse work that is legitimately someone's to do.
/// </remarks>
internal sealed partial class RepositoryReviewViewModel : DialogViewModel
{
    private readonly RepositoryPlan _plan;

    internal RepositoryReviewViewModel(Localizer localizer, RepositoryPlan plan)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _plan = plan;

        foreach (var line in plan.ToDiff().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            Lines.Add(new PlanLine(line.TrimEnd(), trimmed.StartsWith('+'), trimmed.StartsWith('-')));
        }

        foreach (var blocker in plan.Blockers)
        {
            Blockers.Add(Localize(localizer, blocker, BlockerMessages.Prefix));
        }

        foreach (var warning in plan.Warnings)
        {
            Warnings.Add(Localize(localizer, warning, RepositoryWarnings.Prefix));
        }
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Dialog_PreviewOperation_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Dialog_ReviewedPlan;

    /// <summary>Repository the plan addresses.</summary>
    public string RepositoryPath => _plan.RepositoryPath;

    /// <summary>The plan, line by line.</summary>
    public ObservableCollection<PlanLine> Lines { get; } = [];

    /// <summary>Reasons the plan cannot be applied, localized.</summary>
    public ObservableCollection<string> Blockers { get; } = [];

    /// <summary>Things worth knowing before confirming, localized.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>True when the plan is blocked.</summary>
    public bool HasBlockers => Blockers.Count > 0;

    /// <summary>True when there is something to warn about.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>True when refs will be preserved before anything changes.</summary>
    public bool HasBackup => _plan.RefsToBackUp.Count > 0;

    /// <summary>The refs that will be preserved.</summary>
    public string BackedUpRefs => string.Join(Environment.NewLine, _plan.RefsToBackUp);

    /// <summary>Localized note that nothing has been written yet.</summary>
    public string NothingWrittenCaption => L[Keys.Dialog_NothingWritten];

    /// <summary>Localized explanation of what a ref backup buys.</summary>
    public string BackupExplainsCaption => L[Keys.Dialog_RefBackupExplains];

    /// <inheritdoc/>
    public override bool CanConfirm => _plan.CanApply;

    /// <inheritdoc/>
    public override double DialogHeight => 520;

    /// <summary>Turns an identifier into text, and leaves anything else alone.</summary>
    private static string Localize(Localizer localizer, string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? localizer[value] : value;
}

/// <summary>Add or edit a remote.</summary>
internal sealed partial class RemoteEditorViewModel : DialogViewModel
{
    private readonly bool _isNew;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _fetchUrl;

    [ObservableProperty]
    private string _pushUrl;

    [ObservableProperty]
    private bool _usesSeparatePushUrl;

    internal RemoteEditorViewModel(Localizer localizer, GitRemote? existing)
        : base(localizer)
    {
        _isNew = existing is null;
        _name = existing?.Name ?? string.Empty;
        _fetchUrl = existing?.FetchUrl ?? string.Empty;
        _pushUrl = existing?.PushUrl ?? string.Empty;
        _usesSeparatePushUrl = existing is not null
            && !string.Equals(existing.FetchUrl, existing.PushUrl, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override string TitleKey => _isNew ? Keys.Remotes_Add : Keys.Remotes_Edit;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Localized note that GitVault never contacts the remote.</summary>
    public string NoNetworkCaption => L[Keys.Remotes_NoNetworkNote];

    /// <inheritdoc/>
    public override bool CanConfirm => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(FetchUrl);

    /// <inheritdoc/>
    public override double DialogWidth => 560;

    /// <inheritdoc/>
    public override double DialogHeight => 320;

    partial void OnNameChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnFetchUrlChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }
}

/// <summary>Create or rename a branch.</summary>
internal sealed partial class BranchEditorViewModel : DialogViewModel
{
    private readonly bool _isNew;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _startPoint;

    internal BranchEditorViewModel(Localizer localizer, GitBranch? existing, IReadOnlyList<string> knownRefs)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(knownRefs);

        _isNew = existing is null;
        _name = existing?.Name ?? string.Empty;
        _startPoint = string.Empty;

        foreach (var reference in knownRefs)
        {
            KnownRefs.Add(reference);
        }
    }

    /// <inheritdoc/>
    public override string TitleKey => _isNew ? Keys.Branches_Create : Keys.Branches_Rename;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Refs that can be used as a starting point, offered as suggestions.</summary>
    public ObservableCollection<string> KnownRefs { get; } = [];

    /// <summary>True when a starting point is relevant, i.e. the branch is being created.</summary>
    public bool ShowStartPoint => _isNew;

    /// <summary>Localized hint that an empty starting point means HEAD.</summary>
    public string StartPointHintCaption => L[Keys.Branches_StartPointHint];

    /// <inheritdoc/>
    public override bool CanConfirm => !string.IsNullOrWhiteSpace(Name);

    /// <inheritdoc/>
    public override double DialogWidth => 520;

    /// <inheritdoc/>
    public override double DialogHeight => 280;

    partial void OnNameChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }
}

/// <summary>An upstream a branch can track, or the "none" entry.</summary>
internal sealed class UpstreamChoice(Localizer localizer, string? value) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The upstream ref, or null for "none".</summary>
    public string? Value { get; } = value;

    /// <summary>Label: the ref name, or the localized "none".</summary>
    public string Label => Value ?? L[Keys.Common_None];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}

/// <summary>Set or clear a branch's upstream.</summary>
internal sealed partial class UpstreamEditorViewModel : DialogViewModel
{
    [ObservableProperty]
    private UpstreamChoice? _selectedUpstream;

    internal UpstreamEditorViewModel(
        Localizer localizer,
        string branchName,
        string? currentUpstream,
        IReadOnlyList<string> candidates)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        BranchName = branchName;

        Candidates.Add(new UpstreamChoice(localizer, null));
        foreach (var candidate in candidates)
        {
            Candidates.Add(new UpstreamChoice(localizer, candidate));
        }

        _selectedUpstream = Candidates.FirstOrDefault(c => c.Value == currentUpstream) ?? Candidates[0];
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Branches_SetUpstream;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Branch whose upstream is being set.</summary>
    public string BranchName { get; }

    /// <summary>Upstreams on offer, plus a "none" entry.</summary>
    public ObservableCollection<UpstreamChoice> Candidates { get; } = [];

    /// <summary>True when the repository has no remote-tracking branches to choose from.</summary>
    public bool HasNoCandidates => Candidates.Count <= 1;

    /// <summary>Localized explanation shown when there is nothing to track.</summary>
    public string NoCandidatesCaption => L[Keys.Branches_NoUpstreamCandidates];

    /// <inheritdoc/>
    public override double DialogWidth => 500;

    /// <inheritdoc/>
    public override double DialogHeight => 250;

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var candidate in Candidates)
        {
            candidate.RefreshCaptions();
        }
    }
}

/// <summary>Create a tag.</summary>
internal sealed partial class TagEditorViewModel : DialogViewModel
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isAnnotated = true;

    internal TagEditorViewModel(Localizer localizer)
        : base(localizer)
    {
    }

    /// <inheritdoc/>
    public override string TitleKey => Keys.Tags_Create;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Localized hint that an empty target means HEAD.</summary>
    public string TargetHintCaption => L[Keys.Tags_TargetHint];

    /// <summary>Localized note that GitVault does not sign the tags it creates.</summary>
    public string SigningNoteCaption => L[Keys.Tags_SigningNote];

    /// <inheritdoc/>
    public override bool CanConfirm =>
        !string.IsNullOrWhiteSpace(Name) && (!IsAnnotated || !string.IsNullOrWhiteSpace(Message));

    /// <inheritdoc/>
    public override double DialogWidth => 540;

    /// <inheritdoc/>
    public override double DialogHeight => 340;

    partial void OnNameChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnMessageChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnIsAnnotatedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }
}
