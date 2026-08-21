using System.Globalization;
using GitVault.Core.Models;

namespace GitVault.Core.Repository;

/// <summary>What the user wants changed about one commit.</summary>
/// <param name="Sha">Commit to change.</param>
public sealed record CommitEdit(string Sha)
{
    /// <summary>New full message, or null to keep the original.</summary>
    public string? Message { get; init; }

    /// <summary>New author name, or null to keep the original.</summary>
    public string? AuthorName { get; init; }

    /// <summary>New author e-mail, or null to keep the original.</summary>
    public string? AuthorEmail { get; init; }

    /// <summary>New author date, or null to keep the original.</summary>
    public DateTimeOffset? AuthorDate { get; init; }

    /// <summary>New committer name, or null to keep the original.</summary>
    public string? CommitterName { get; init; }

    /// <summary>New committer e-mail, or null to keep the original.</summary>
    public string? CommitterEmail { get; init; }

    /// <summary>New committer date, or null to keep the original.</summary>
    public DateTimeOffset? CommitterDate { get; init; }

    /// <summary>Files whose content the user changed at this commit.</summary>
    public IReadOnlyList<FileEdit> Files { get; init; } = [];

    /// <summary>True when nothing was actually changed.</summary>
    public bool IsEmpty =>
        Message is null
        && AuthorName is null && AuthorEmail is null && AuthorDate is null
        && CommitterName is null && CommitterEmail is null && CommitterDate is null
        && Files.Count == 0;
}

/// <summary>One commit in a rewrite, and what will happen to it.</summary>
/// <param name="Original">The commit as it stands.</param>
/// <param name="Edit">What the user changed, when anything.</param>
/// <param name="IsDirectlyEdited">True when the user changed this commit itself.</param>
public sealed record RewriteStep(GitCommit Original, CommitEdit? Edit, bool IsDirectlyEdited)
{
    /// <summary>Files this commit ends up with, when a content edit reached it.</summary>
    public IReadOnlyList<ResolvedFile> Files { get; init; } = [];

    /// <summary>True when this commit's tree changes, whether the user edited it or not.</summary>
    public bool ChangesContent => Files.Count > 0;

    /// <summary>
    /// True when a content edit made somewhere earlier lands in this commit's files.
    /// </summary>
    /// <remarks>
    /// Worth naming separately from a commit that is merely rebuilt. Both get a new identifier,
    /// but this one also holds different bytes than it did, which is a larger thing to be told
    /// about in a preview.
    /// </remarks>
    public bool CarriesContent => !IsDirectlyEdited && Files.Count > 0;

    /// <summary>
    /// True when this commit only moves because something before it did.
    /// </summary>
    /// <remarks>
    /// The distinction matters to the person confirming. Editing one commit in the middle of a
    /// branch gives every commit after it a new identifier, which is the part people are
    /// surprised by; naming those separately is what makes the surprise avoidable.
    /// </remarks>
    public bool IsCarriedAlong => !IsDirectlyEdited;
}

/// <summary>A planned rewrite of a branch's history.</summary>
/// <param name="RepositoryPath">Repository the rewrite addresses.</param>
/// <param name="BranchName">Branch whose tip will move.</param>
/// <param name="BranchRef">Full ref of that branch.</param>
public sealed record RewritePlan(string RepositoryPath, string BranchName, string BranchRef)
{
    /// <summary>Commits that will be rebuilt, oldest first.</summary>
    public IReadOnlyList<RewriteStep> Steps { get; init; } = [];

    /// <summary>Refs preserved before anything is written.</summary>
    public IReadOnlyList<string> RefsToBackUp { get; init; } = [];

    /// <summary>Reasons the rewrite cannot proceed.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Things the user should know before confirming, which do not block.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Other refs pointing into the range, which the rewrite leaves behind.</summary>
    public IReadOnlyList<string> StrandedRefs { get; init; } = [];

    /// <summary>Later commits whose own change to an edited file needs the user's decision.</summary>
    public IReadOnlyList<ContentConflict> Conflicts { get; init; } = [];

    /// <summary>Commit the branch points at now.</summary>
    public string OriginalTip { get; init; } = string.Empty;

    /// <summary>How many commits the user changed directly.</summary>
    public int EditedCount => Steps.Count(s => s.IsDirectlyEdited);

    /// <summary>How many commits get a new identifier only because an earlier one changed.</summary>
    public int CarriedCount => Steps.Count(s => s.IsCarriedAlong);

    /// <summary>How many commits end up holding different file content than they did.</summary>
    public int ContentCount => Steps.Count(s => s.ChangesContent);

    /// <summary>True when the rewrite can be applied.</summary>
    public bool CanApply => Blockers.Count == 0 && EditedCount > 0;

    /// <summary>
    /// What the user has to type to confirm.
    /// </summary>
    /// <remarks>
    /// The branch name, in the tradition of destructive dialogs that ask you to name the thing.
    /// Rewriting history is the most consequential action in this application, and a plain
    /// confirming button is too easy to press by habit.
    /// </remarks>
    public string ConfirmationPhrase => BranchName;
}

/// <summary>Outcome of a rewrite.</summary>
/// <param name="BackupId">Ref backup taken before the first write, for undo.</param>
/// <param name="NewTip">Commit the branch points at afterwards.</param>
public sealed record RewriteResult(string? BackupId, string? NewTip)
{
    /// <summary>Old commit to new commit, for every rebuilt commit.</summary>
    public IReadOnlyDictionary<string, string> Mapping { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Per-step outcomes.</summary>
    public IReadOnlyList<ActivationStepResult> Steps { get; init; } = [];

    /// <summary>True when no step failed.</summary>
    public bool Succeeded => Steps.All(s => s.Outcome != StepOutcome.Failed);
}

/// <summary>Plans and applies rewrites of commit metadata.</summary>
public interface IHistoryRewriter
{
    /// <summary>Works out what rewriting these commits would change. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="edits">What the user changed.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RewritePlan> PlanAsync(
        string repositoryPath,
        IReadOnlyList<CommitEdit> edits,
        CancellationToken cancellationToken);

    /// <summary>
    /// Works out what rewriting these commits would change, given what the user decided about
    /// conflicts an earlier plan reported. Writes nothing.
    /// </summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="edits">What the user changed.</param>
    /// <param name="resolutions">Content the user settled on for conflicted files.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RewritePlan> PlanAsync(
        string repositoryPath,
        IReadOnlyList<CommitEdit> edits,
        IReadOnlyList<ConflictResolution> resolutions,
        CancellationToken cancellationToken);

    /// <summary>Applies a rewrite, preserving the affected refs first.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The outcome, including the old-to-new mapping.</returns>
    Task<RewriteResult> ApplyAsync(RewritePlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// Rewrites commit metadata by rebuilding the commit chain.
/// </summary>
/// <remarks>
/// The method is <c>git commit-tree</c>, walked from the oldest affected commit to the tip. Each
/// commit is rebuilt against its already-rebuilt parents, with the same tree it always had, and
/// the identities and dates supplied through the environment. Trees never change, so there is no
/// merge and there can be no conflict — which is what makes this operation deterministic enough
/// to preview honestly.
///
/// Two consequences fall out of that and are worth stating rather than discovering.
///
/// A commit whose inputs are unchanged rebuilds to the same object name, because a commit's name
/// is a hash of exactly those inputs. So rebuilding a whole range costs nothing for the parts the
/// user did not touch, and the mapping records identity for them.
///
/// Everything after the earliest edit gets a new name whether or not the user meant to touch it.
/// That is inherent to git rather than a choice made here, and the plan counts those commits
/// separately so the preview can say how far the change reaches.
///
/// What this deliberately does not do: sign anything. GitVault holds no signing key, so a signed
/// commit that is rebuilt loses its signature, and the plan warns rather than pretending.
/// </remarks>
public sealed class HistoryRewriter : IHistoryRewriter
{
    /// <summary>Operation identifier recorded on the ref backup.</summary>
    public const string OperationId = "HistoryRewrite";

    /// <summary>Step identifier used for each rebuilt commit.</summary>
    public const string StepId = "RewriteCommit";


    private readonly IGitCommandRunner _git;
    private readonly ICommitReader _commits;
    private readonly IRepositoryInspector _inspector;
    private readonly IRefBackupService _backups;
    private readonly IContentMerger _merger;
    private readonly ITreeBuilder _trees;

    /// <summary>Creates the rewriter.</summary>
    /// <param name="git">Command runner.</param>
    /// <param name="commits">Commit reader.</param>
    /// <param name="inspector">Inspector used to read repository state.</param>
    /// <param name="backups">Ref backup service.</param>
    /// <param name="merger">Merger that carries a content edit through later commits.</param>
    /// <param name="trees">Builder that writes the trees a content edit needs.</param>
    public HistoryRewriter(
        IGitCommandRunner git,
        ICommitReader commits,
        IRepositoryInspector inspector,
        IRefBackupService backups,
        IContentMerger merger,
        ITreeBuilder trees)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(merger);
        ArgumentNullException.ThrowIfNull(trees);

        _git = git;
        _commits = commits;
        _inspector = inspector;
        _backups = backups;
        _merger = merger;
        _trees = trees;
    }

    /// <inheritdoc/>
    public Task<RewritePlan> PlanAsync(
        string repositoryPath,
        IReadOnlyList<CommitEdit> edits,
        CancellationToken cancellationToken) =>
        PlanAsync(repositoryPath, edits, [], cancellationToken);

    /// <inheritdoc/>
    public async Task<RewritePlan> PlanAsync(
        string repositoryPath,
        IReadOnlyList<CommitEdit> edits,
        IReadOnlyList<ConflictResolution> resolutions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(edits);

        var state = await _inspector.GetStateAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (state.CurrentBranch is not { Length: > 0 } branch)
        {
            // Nothing to move: a detached HEAD has no branch whose tip the rewrite would update.
            return Blocked(repositoryPath, string.Empty, string.Empty, RewriteBlockers.DetachedHead);
        }

        var branchRef = "refs/heads/" + branch;

        if (!state.IsQuiet)
        {
            blockers.Add(RepositoryBlockers.OperationInProgress);
        }

        if (state.HasUncommittedChanges)
        {
            // The rewrite itself never touches the working tree, but moving the checked-out
            // branch under uncommitted work leaves the index describing commits that no longer
            // exist. Refusing is kinder than explaining that afterwards.
            blockers.Add(RewriteBlockers.WorkingTreeDirty);
        }

        var real = edits.Where(e => !e.IsEmpty).ToList();
        if (real.Count == 0)
        {
            blockers.Add(RewriteBlockers.NothingToChange);
        }

        var order = await ReadRangeAsync(repositoryPath, branch, real, cancellationToken).ConfigureAwait(false);
        if (order.Count == 0 && real.Count > 0)
        {
            blockers.Add(RewriteBlockers.CommitNotOnBranch);
        }

        var byIndex = real.ToDictionary(e => e.Sha, StringComparer.Ordinal);

        // The content side is worked out first, because it can refuse the whole rewrite and
        // because each commit needs to know which files it ends up holding.
        var content = await _merger
            .ResolveAsync(repositoryPath, order, byIndex, resolutions, cancellationToken)
            .ConfigureAwait(false);

        blockers.AddRange(content.Blockers);

        var steps = new List<RewriteStep>();

        foreach (var commit in order)
        {
            byIndex.TryGetValue(commit.Sha, out var edit);

            steps.Add(new RewriteStep(commit, edit, edit is not null)
            {
                Files = content.FilesByCommit.TryGetValue(commit.Sha, out var files) ? files : [],
            });
        }

        if (steps.Any(s => s.Original.Signature.IsPresent))
        {
            warnings.Add(RewriteWarnings.SignaturesWillBeLost);
        }

        if (steps.Any(s => s.Original.IsMerge))
        {
            warnings.Add(RewriteWarnings.RangeContainsMerges);
        }

        if (await IsPublishedAsync(repositoryPath, branch, steps, cancellationToken).ConfigureAwait(false))
        {
            warnings.Add(RewriteWarnings.CommitsAlreadyPublished);
        }

        var stranded = await FindStrandedRefsAsync(repositoryPath, branchRef, steps, cancellationToken)
            .ConfigureAwait(false);

        if (stranded.Count > 0)
        {
            warnings.Add(RewriteWarnings.OtherRefsPointIntoRange);
        }

        return new RewritePlan(repositoryPath, branch, branchRef)
        {
            Steps = steps,
            RefsToBackUp = [branchRef],
            Blockers = blockers,
            Warnings = warnings,
            StrandedRefs = stranded,
            Conflicts = content.Conflicts,
            OriginalTip = state.HeadCommit ?? string.Empty,
        };
    }

    /// <inheritdoc/>
    public async Task<RewriteResult> ApplyAsync(RewritePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanApply)
        {
            // The last place a blocked rewrite can be stopped before it touches an object.
            return new RewriteResult(null, null);
        }

        var backup = await _backups
            .BackupAsync(
                plan.RepositoryPath,
                plan.RefsToBackUp,
                OperationId,
                plan.BranchName,
                cancellationToken)
            .ConfigureAwait(false);

        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<ActivationStepResult>();
        string? newTip = null;

        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rebuilt = await RebuildAsync(plan.RepositoryPath, step, mapping, cancellationToken)
                .ConfigureAwait(false);

            if (rebuilt is null)
            {
                steps.Add(new ActivationStepResult(StepId, StepOutcome.Failed, step.Original.ShortSha));

                // Stopping at the first failure leaves the branch where it was: nothing has been
                // moved yet, and the objects written so far are unreferenced.
                return new RewriteResult(backup.Id, null) { Mapping = mapping, Steps = steps };
            }

            mapping[step.Original.Sha] = rebuilt;
            newTip = rebuilt;

            steps.Add(new ActivationStepResult(
                StepId,
                rebuilt == step.Original.Sha ? StepOutcome.Skipped : StepOutcome.Applied,
                step.Original.ShortSha));
        }

        if (newTip is null)
        {
            return new RewriteResult(backup.Id, null) { Mapping = mapping, Steps = steps };
        }

        // Moving the branch is the single moment the rewrite becomes visible. The old value is
        // passed so git refuses if anything moved the branch while the plan was on screen.
        var update = await _git
            .RunAsync(
                plan.RepositoryPath,
                ["update-ref", plan.BranchRef, newTip, plan.OriginalTip],
                cancellationToken)
            .ConfigureAwait(false);

        if (!update.IsSuccess)
        {
            steps.Add(new ActivationStepResult(StepId, StepOutcome.Failed, update.StandardError.Trim()));
            return new RewriteResult(backup.Id, null) { Mapping = mapping, Steps = steps };
        }

        // The index and working tree still describe the old commit, which is the same content;
        // resetting the index keeps git status from reporting a difference that is not one.
        await _git
            .RunAsync(plan.RepositoryPath, ["reset", "--mixed", "--quiet"], cancellationToken)
            .ConfigureAwait(false);

        return new RewriteResult(backup.Id, newTip) { Mapping = mapping, Steps = steps };
    }

    /// <summary>Rebuilds one commit against its already-rebuilt parents.</summary>
    private async Task<string?> RebuildAsync(
        string repositoryPath,
        RewriteStep step,
        IReadOnlyDictionary<string, string> mapping,
        CancellationToken cancellationToken)
    {
        var original = step.Original;
        var edit = step.Edit;

        // A metadata-only rewrite reuses the tree the commit already had, which is what keeps it
        // conflict-free. A content edit needs a tree of its own, written from the resolved files.
        var tree = original.TreeSha;

        if (step.Files.Count > 0)
        {
            var built = await _trees
                .BuildAsync(repositoryPath, original.TreeSha, step.Files, cancellationToken)
                .ConfigureAwait(false);

            if (built is null)
            {
                return null;
            }

            tree = built;
        }

        var arguments = new List<string> { "commit-tree", tree };

        foreach (var parent in original.Parents)
        {
            // A parent outside the rewritten range keeps its original name, which is what makes
            // a merge from an untouched side branch survive intact.
            arguments.Add("-p");
            arguments.Add(mapping.TryGetValue(parent, out var mapped) ? mapped : parent);
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = edit?.AuthorName ?? original.AuthorName,
            ["GIT_AUTHOR_EMAIL"] = edit?.AuthorEmail ?? original.AuthorEmail,
            ["GIT_AUTHOR_DATE"] = FormatDate(edit?.AuthorDate ?? original.AuthorDate),
            ["GIT_COMMITTER_NAME"] = edit?.CommitterName ?? original.CommitterName,
            ["GIT_COMMITTER_EMAIL"] = edit?.CommitterEmail ?? original.CommitterEmail,
            ["GIT_COMMITTER_DATE"] = FormatDate(edit?.CommitterDate ?? original.CommitterDate),
        };

        var message = edit?.Message ?? original.FullMessage;

        var result = await _git
            .RunWithInputAsync(repositoryPath, arguments, message, environment, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? result.StandardOutput.Trim() : null;
    }

    /// <summary>Reads the commits from the earliest edit to the branch tip, oldest first.</summary>
    private async Task<IReadOnlyList<GitCommit>> ReadRangeAsync(
        string repositoryPath,
        string branch,
        IReadOnlyList<CommitEdit> edits,
        CancellationToken cancellationToken)
    {
        if (edits.Count == 0)
        {
            return [];
        }

        // Which edited commit is earliest is a question about the graph, not about the order the
        // user clicked things in, so git answers it.
        var names = await _git
            .ReadAsync(
                repositoryPath,
                ["rev-list", "--topo-order", branch],
                cancellationToken)
            .ConfigureAwait(false);

        if (names is null)
        {
            return [];
        }

        var onBranch = names.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        var positions = onBranch
            .Select((sha, index) => (sha, index))
            .ToDictionary(p => p.sha, p => p.index, StringComparer.Ordinal);

        var deepest = -1;
        foreach (var edit in edits)
        {
            if (!positions.TryGetValue(edit.Sha, out var index))
            {
                // An edit naming a commit that is not on this branch cannot be planned.
                return [];
            }

            deepest = Math.Max(deepest, index);
        }

        // rev-list is newest first, so the deepest index is the earliest commit.
        var range = onBranch.Take(deepest + 1).Reverse().ToList();
        var commits = new List<GitCommit>();

        foreach (var sha in range)
        {
            var commit = await _commits.ReadOneAsync(repositoryPath, sha, cancellationToken).ConfigureAwait(false);
            if (commit is not null)
            {
                commits.Add(commit);
            }
        }

        return commits;
    }

    /// <summary>True when any commit in the range is already on the branch's upstream.</summary>
    private async Task<bool> IsPublishedAsync(
        string repositoryPath,
        string branch,
        IReadOnlyList<RewriteStep> steps,
        CancellationToken cancellationToken)
    {
        if (steps.Count == 0)
        {
            return false;
        }

        var upstream = await _git
            .ReadAsync(
                repositoryPath,
                ["rev-parse", "--verify", "--quiet", "--abbrev-ref", branch + "@{upstream}"],
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(upstream))
        {
            return false;
        }

        var oldest = steps[0].Original.Sha;

        var result = await _git
            .RunAsync(repositoryPath, ["merge-base", "--is-ancestor", oldest, upstream], cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess;
    }

    /// <summary>Finds tags and other branches pointing at commits the rewrite will replace.</summary>
    private async Task<IReadOnlyList<string>> FindStrandedRefsAsync(
        string repositoryPath,
        string branchRef,
        IReadOnlyList<RewriteStep> steps,
        CancellationToken cancellationToken)
    {
        if (steps.Count == 0)
        {
            return [];
        }

        var inRange = steps.Select(s => s.Original.Sha).ToHashSet(StringComparer.Ordinal);

        var output = await _git
            .ReadAsync(
                repositoryPath,
                ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(*objectname)",
                 "refs/heads", "refs/tags"],
                cancellationToken)
            .ConfigureAwait(false);

        var stranded = new List<string>();

        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\0');
            if (parts.Length < 2 || parts[0] == branchRef)
            {
                continue;
            }

            // An annotated tag's own name is the tag object; the dereferenced name is the commit.
            var target = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : parts[1];

            if (inRange.Contains(target))
            {
                stranded.Add(parts[0]);
            }
        }

        return stranded;
    }

    /// <summary>Formats a date the way git parses it, keeping the offset.</summary>
    private static string FormatDate(DateTimeOffset value) =>
        value.ToString(GitDateFormats.Iso, CultureInfo.InvariantCulture);

    private static RewritePlan Blocked(string repositoryPath, string branch, string branchRef, string blocker) =>
        new(repositoryPath, branch, branchRef) { Blockers = [blocker] };
}

/// <summary>
/// The date spellings GitVault and git exchange.
/// </summary>
/// <remarks>
/// Published rather than kept private because two places have to agree on them: the rewriter
/// writes dates in the first form, and the editing dialog accepts the same forms back. Two
/// private copies of a format string are two things that can drift apart.
/// </remarks>
public static class GitDateFormats
{
    /// <summary>What git is given, and what it round-trips, offset included.</summary>
    public const string Iso = "yyyy-MM-dd'T'HH:mm:sszzz";

    /// <summary>The same, spelled with <c>Z</c> for UTC as several git commands print it.</summary>
    public const string IsoUtc = "yyyy-MM-dd'T'HH:mm:ssK";
}

/// <summary>Blocker identifiers for a rewrite. Localization keys, not text.</summary>
public static class RewriteBlockers
{
    /// <summary>HEAD is not on a branch, so there is no tip to move.</summary>
    public const string DetachedHead = "Blocker_DetachedHead";

    /// <summary>The working tree or index has changes that are not committed.</summary>
    public const string WorkingTreeDirty = "Blocker_WorkingTreeDirty";

    /// <summary>Nothing was actually changed.</summary>
    public const string NothingToChange = "Blocker_NothingToChange";

    /// <summary>An edited commit is not reachable from the current branch.</summary>
    public const string CommitNotOnBranch = "Blocker_CommitNotOnBranch";

    /// <summary>File content was edited on a merge commit, where it has no single meaning.</summary>
    public const string ContentEditOnMerge = "Blocker_ContentEditOnMerge";

    /// <summary>The commit chosen for the edit does not contain that path.</summary>
    public const string PathNotInCommit = "Blocker_PathNotInCommit";

    /// <summary>A later commit deletes or renames the edited path.</summary>
    public const string PathRemovedLater = "Blocker_PathRemovedLater";

    /// <summary>A later commit adds the path, so the edit was not made against what it holds.</summary>
    public const string PathAddedLater = "Blocker_PathAddedLater";

    /// <summary>The path is a symbolic link or a submodule rather than a file.</summary>
    public const string PathIsNotAPlainFile = "Blocker_PathIsNotAPlainFile";

    /// <summary>The file is binary, too large, or not text this can carry back unchanged.</summary>
    public const string PathIsNotEditableText = "Blocker_PathIsNotEditableText";

    /// <summary>Git could not attempt the three-way merge at all.</summary>
    public const string MergeFailed = "Blocker_MergeFailed";

    /// <summary>A conflict is still waiting for the user to settle it.</summary>
    public const string UnresolvedConflicts = "Blocker_UnresolvedConflicts";
}

/// <summary>Warning identifiers for a rewrite. Localization keys, not text.</summary>
public static class RewriteWarnings
{
    /// <summary>A commit in the range is signed and the signature cannot be reproduced.</summary>
    public const string SignaturesWillBeLost = "Warning_SignaturesWillBeLost";

    /// <summary>The range contains merge commits.</summary>
    public const string RangeContainsMerges = "Warning_RangeContainsMerges";

    /// <summary>The commits are already on the branch's upstream.</summary>
    public const string CommitsAlreadyPublished = "Warning_CommitsAlreadyPublished";

    /// <summary>Tags or other branches point at commits that will be replaced.</summary>
    public const string OtherRefsPointIntoRange = "Warning_OtherRefsPointIntoRange";
}
