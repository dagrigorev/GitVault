namespace GitVault.Core.Repository;

/// <summary>What a rewrite does to one path in one commit's tree.</summary>
public enum PathOperationKind
{
    /// <summary>The path is dropped from the tree.</summary>
    Remove,

    /// <summary>The path's entry moves, keeping its mode and its content.</summary>
    Rename,
}

/// <summary>One path's fate in one commit's tree.</summary>
/// <param name="Kind">What happens to it.</param>
/// <param name="Path">Path as the tree records it now.</param>
/// <param name="NewPath">Where it moves to, for a rename.</param>
public sealed record PathOperation(PathOperationKind Kind, string Path, string? NewPath = null);

/// <summary>Operations that reach across a branch's whole history.</summary>
public interface IHistoryTools
{
    /// <summary>Plans removing a path from every commit that holds it. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="path">File or directory to remove, as git spells it.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RewritePlan> PlanRemovePathAsync(
        string repositoryPath,
        string path,
        CancellationToken cancellationToken);

    /// <summary>Plans moving a path in every commit that holds it. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="path">File or directory to move.</param>
    /// <param name="newPath">Where it should be instead.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RewritePlan> PlanRenamePathAsync(
        string repositoryPath,
        string path,
        string newPath,
        CancellationToken cancellationToken);

    /// <summary>Plans replacing one identity everywhere it appears. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="oldEmail">E-mail address to look for, in author and committer alike.</param>
    /// <param name="name">Name to put in its place.</param>
    /// <param name="email">E-mail address to put in its place.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RewritePlan> PlanReplaceIdentityAsync(
        string repositoryPath,
        string oldEmail,
        string name,
        string email,
        CancellationToken cancellationToken);
}

/// <summary>
/// The operations that address a path or an identity rather than a commit.
/// </summary>
/// <remarks>
/// These are the jobs people reach for <c>filter-repo</c> to do: take a file out of every commit
/// that ever held it, move a file as though it had always lived somewhere else, or correct an
/// address that was committed wrongly a hundred times. Each one is expressed here as a set of
/// per-commit edits and handed to the same rewriter as everything else, so it inherits the same
/// preview, the same typed confirmation, the same ref backup and the same undo.
///
/// One deliberate difference from <c>filter-repo</c>: a commit whose only change was to the
/// removed path is kept, holding the same tree as its parent, rather than being dropped. Dropping
/// it would be a second change nobody asked for — the commit's message and authorship are
/// history too — so the plan counts those commits and warns instead. The user who wants them gone
/// can then remove them deliberately.
///
/// The other deliberate difference is what the preview says about removal. Taking a file out of
/// history does not destroy what it contained: the old objects survive in the object database
/// until git prunes them, in every other clone until its owner rewrites too, and on any server it
/// reached. For the case this feature exists for — a key or a token committed by accident — the
/// only real remedy is to revoke the secret, and the plan says so rather than letting a green
/// result imply otherwise.
/// </remarks>
public sealed class HistoryTools : IHistoryTools
{
    private readonly IGitCommandRunner _git;
    private readonly ICommitReader _commits;
    private readonly IHistoryRewriter _rewriter;
    private readonly IRepositoryInspector _inspector;

    /// <summary>Creates the tools.</summary>
    /// <param name="git">Command runner.</param>
    /// <param name="commits">Commit reader.</param>
    /// <param name="rewriter">Rewriter the plans are built for.</param>
    /// <param name="inspector">Inspector used to find the current branch.</param>
    public HistoryTools(
        IGitCommandRunner git,
        ICommitReader commits,
        IHistoryRewriter rewriter,
        IRepositoryInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(rewriter);
        ArgumentNullException.ThrowIfNull(inspector);

        _git = git;
        _commits = commits;
        _rewriter = rewriter;
        _inspector = inspector;
    }

    /// <inheritdoc/>
    public async Task<RewritePlan> PlanRemovePathAsync(
        string repositoryPath,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var branch = await CurrentBranchAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return Blocked(repositoryPath, RewriteBlockers.DetachedHead);
        }

        var holders = await FindHoldersAsync(repositoryPath, branch, path, cancellationToken).ConfigureAwait(false);
        if (holders.Count == 0)
        {
            return Blocked(repositoryPath, RewriteBlockers.PathNotInHistory);
        }

        var edits = holders
            .Select(h => new CommitEdit(h.Sha)
            {
                Paths = [.. h.Files.Select(f => new PathOperation(PathOperationKind.Remove, f))],
            })
            .ToList();

        var plan = await _rewriter.PlanAsync(repositoryPath, edits, cancellationToken).ConfigureAwait(false);

        var emptied = await CountEmptiedAsync(repositoryPath, holders, cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>(plan.Warnings) { RewriteWarnings.RemovedContentSurvives };

        if (emptied > 0)
        {
            warnings.Add(RewriteWarnings.CommitsBecomeEmpty);
        }

        return plan with { Warnings = warnings };
    }

    /// <inheritdoc/>
    public async Task<RewritePlan> PlanRenamePathAsync(
        string repositoryPath,
        string path,
        string newPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        if (!IsAcceptablePath(newPath) || string.Equals(path, newPath, StringComparison.Ordinal))
        {
            return Blocked(repositoryPath, RewriteBlockers.PathNotValid);
        }

        var branch = await CurrentBranchAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return Blocked(repositoryPath, RewriteBlockers.DetachedHead);
        }

        var holders = await FindHoldersAsync(repositoryPath, branch, path, cancellationToken).ConfigureAwait(false);
        if (holders.Count == 0)
        {
            return Blocked(repositoryPath, RewriteBlockers.PathNotInHistory);
        }

        var edits = new List<CommitEdit>();

        foreach (var holder in holders)
        {
            var operations = new List<PathOperation>();

            foreach (var file in holder.Files)
            {
                var moved = Moved(file, path, newPath);

                if (holder.AllFiles.Contains(moved, StringComparer.Ordinal))
                {
                    // Landing on something the commit already holds would silently replace it.
                    return Blocked(repositoryPath, RewriteBlockers.RenameTargetExists);
                }

                operations.Add(new PathOperation(PathOperationKind.Rename, file, moved));
            }

            edits.Add(new CommitEdit(holder.Sha) { Paths = operations });
        }

        return await _rewriter.PlanAsync(repositoryPath, edits, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<RewritePlan> PlanReplaceIdentityAsync(
        string repositoryPath,
        string oldEmail,
        string name,
        string email,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var branch = await CurrentBranchAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return Blocked(repositoryPath, RewriteBlockers.DetachedHead);
        }

        // The whole branch is read, because an address committed wrongly is usually committed
        // wrongly from the beginning and the user should not have to say how far back to look.
        var all = await _commits
            .ReadAsync(repositoryPath, new CommitQuery(branch, int.MaxValue), cancellationToken)
            .ConfigureAwait(false);

        var edits = new List<CommitEdit>();

        foreach (var commit in all)
        {
            var authorMatches = Matches(commit.AuthorEmail, oldEmail);
            var committerMatches = Matches(commit.CommitterEmail, oldEmail);

            if (!authorMatches && !committerMatches)
            {
                continue;
            }

            // Both sides are corrected when both carry the address, and neither is touched when
            // it does not: an identity replacement should not quietly reassign authorship.
            edits.Add(new CommitEdit(commit.Sha)
            {
                AuthorName = authorMatches && !string.Equals(commit.AuthorName, name, StringComparison.Ordinal)
                    ? name
                    : null,
                AuthorEmail = authorMatches && !string.Equals(commit.AuthorEmail, email, StringComparison.Ordinal)
                    ? email
                    : null,
                CommitterName = committerMatches && !string.Equals(commit.CommitterName, name, StringComparison.Ordinal)
                    ? name
                    : null,
                CommitterEmail = committerMatches && !string.Equals(commit.CommitterEmail, email, StringComparison.Ordinal)
                    ? email
                    : null,
            });
        }

        edits.RemoveAll(e => e.IsEmpty);

        return edits.Count == 0
            ? Blocked(repositoryPath, RewriteBlockers.IdentityNotFound)
            : await _rewriter.PlanAsync(repositoryPath, edits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One commit that holds the path, and what it holds under it.</summary>
    /// <param name="Sha">The commit.</param>
    /// <param name="Files">Files at or under the path.</param>
    /// <param name="AllFiles">Every file the commit holds, for collision checks.</param>
    private sealed record Holder(string Sha, IReadOnlyList<string> Files, IReadOnlySet<string> AllFiles);

    /// <summary>Finds every commit on the branch that holds the path, oldest last.</summary>
    /// <remarks>
    /// A directory is as good an answer as a file: whoever committed a key usually committed the
    /// folder it sat in, so the path is expanded per commit into the files under it. Doing that
    /// per commit rather than once matters, because what a directory contained changed over time.
    /// </remarks>
    private async Task<IReadOnlyList<Holder>> FindHoldersAsync(
        string repositoryPath,
        string branch,
        string path,
        CancellationToken cancellationToken)
    {
        var names = await _git
            .ReadAsync(repositoryPath, ["rev-list", "--topo-order", branch], cancellationToken)
            .ConfigureAwait(false);

        var holders = new List<Holder>();

        foreach (var sha in (names ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var commit = sha.Trim();
            var listing = await _git
                .RunAsync(repositoryPath, ["ls-tree", "-r", "--name-only", "-z", commit], cancellationToken)
                .ConfigureAwait(false);

            if (!listing.IsSuccess)
            {
                continue;
            }

            var all = listing.StandardOutput
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);

            var matching = all.Where(f => IsAtOrUnder(f, path)).OrderBy(f => f, StringComparer.Ordinal).ToList();

            if (matching.Count > 0)
            {
                holders.Add(new Holder(commit, matching, all));
            }
        }

        return holders;
    }

    /// <summary>Counts commits whose only change was to the path being removed.</summary>
    /// <remarks>
    /// Answered from the names a commit changed rather than by building trees, so that planning
    /// stays a read. A commit that changed nothing else will hold exactly what its parent holds
    /// once the path is gone.
    /// </remarks>
    private async Task<int> CountEmptiedAsync(
        string repositoryPath,
        IReadOnlyList<Holder> holders,
        CancellationToken cancellationToken)
    {
        var emptied = 0;

        foreach (var holder in holders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changed = await _git
                .RunAsync(
                    repositoryPath,
                    ["diff-tree", "-r", "--name-only", "--no-commit-id", "-z", holder.Sha],
                    cancellationToken)
                .ConfigureAwait(false);

            if (!changed.IsSuccess)
            {
                continue;
            }

            var names = changed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);

            if (names.Length > 0 && names.All(n => holder.Files.Contains(n, StringComparer.Ordinal)))
            {
                emptied++;
            }
        }

        return emptied;
    }

    private async Task<string?> CurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var state = await _inspector.GetStateAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        return state.CurrentBranch is { Length: > 0 } branch ? branch : null;
    }

    /// <summary>True when a file sits at the path or inside it.</summary>
    private static bool IsAtOrUnder(string file, string path) =>
        string.Equals(file, path, StringComparison.Ordinal)
        || file.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>Rewrites a file's path when its prefix moves.</summary>
    private static string Moved(string file, string path, string newPath) =>
        string.Equals(file, path, StringComparison.Ordinal)
            ? newPath
            : newPath.TrimEnd('/') + file[path.TrimEnd('/').Length..];

    /// <summary>
    /// True when git would accept this as a path inside a repository.
    /// </summary>
    /// <remarks>
    /// Checked here rather than left to git, because a rename that git rejects half-way through
    /// would leave a plan that fails on some commits and not others. Refusing an absolute path, a
    /// path that climbs out of the repository, or one naming git's own directory is enough: those
    /// are the ways a typed path stops meaning what it looks like it means.
    /// </remarks>
    private static bool IsAcceptablePath(string path) =>
        !path.StartsWith('/')
        && !path.StartsWith('-')
        && !path.Contains(':', StringComparison.Ordinal)
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Split('/').Any(part => part is ".." or "." or ".git" or "");

    /// <summary>Compares addresses the way git treats them: exactly, but ignoring case.</summary>
    private static bool Matches(string value, string wanted) =>
        string.Equals(value.Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase);

    private static RewritePlan Blocked(string repositoryPath, string blocker) =>
        new(repositoryPath, string.Empty, string.Empty) { Blockers = [blocker] };
}
