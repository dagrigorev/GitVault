namespace GitVault.Core.Repository;

/// <summary>One working tree attached to a repository.</summary>
/// <param name="Path">Directory the working tree occupies.</param>
/// <param name="Head">Commit it has checked out.</param>
/// <param name="Branch">Branch it has checked out, or null when detached.</param>
/// <param name="IsMain">True for the repository's original working tree.</param>
/// <param name="IsLocked">True when it is marked so that git refuses to prune or remove it.</param>
/// <param name="LockReason">Why it was locked, when a reason was given.</param>
/// <param name="IsPrunable">True when git reports the directory as gone.</param>
/// <param name="IsBare">True for a bare repository's entry.</param>
public sealed record GitWorktree(
    string Path,
    string Head,
    string? Branch,
    bool IsMain,
    bool IsLocked,
    string? LockReason,
    bool IsPrunable,
    bool IsBare)
{
    /// <summary>Abbreviated commit, for a dense list.</summary>
    public string ShortHead => Head.Length >= 8 ? Head[..8] : Head;

    /// <summary>True when the working tree is not on a branch.</summary>
    public bool IsDetached => Branch is null && !IsBare;
}

/// <summary>Plans and applies changes to a repository's working trees.</summary>
public interface IWorktreeEditor
{
    /// <summary>Lists the working trees attached to this repository.</summary>
    /// <param name="repositoryPath">Any working tree of the repository.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The working trees, the main one first.</returns>
    Task<IReadOnlyList<GitWorktree>> ListAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Plans adding a working tree. Writes nothing.</summary>
    /// <param name="repositoryPath">Repository to attach it to.</param>
    /// <param name="path">Directory the new working tree should occupy.</param>
    /// <param name="startPoint">Branch or commit to check out.</param>
    /// <param name="createBranch">Name of a branch to create there, or null to check out directly.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanAddAsync(
        string repositoryPath,
        string path,
        string startPoint,
        string? createBranch,
        CancellationToken cancellationToken);

    /// <summary>Plans removing a working tree. Writes nothing.</summary>
    /// <param name="repositoryPath">Repository it belongs to.</param>
    /// <param name="path">Working tree to remove.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanRemoveAsync(
        string repositoryPath,
        string path,
        CancellationToken cancellationToken);

    /// <summary>Plans locking or unlocking a working tree. Writes nothing.</summary>
    /// <param name="repositoryPath">Repository it belongs to.</param>
    /// <param name="path">Working tree to lock or unlock.</param>
    /// <param name="locked">True to lock it.</param>
    /// <param name="reason">Why it is being locked, when locking.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanLockAsync(
        string repositoryPath,
        string path,
        bool locked,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>Plans forgetting the working trees whose directories are gone. Writes nothing.</summary>
    /// <param name="repositoryPath">Repository to tidy.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanPruneAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Applies a plan.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The outcome.</returns>
    Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// The working-tree editor.
/// </summary>
/// <remarks>
/// A working tree is a directory with a checkout in it, so the operations here touch files rather
/// than only refs — which changes what the safety net has to be. Removing one deletes a directory
/// that git will not recreate, so the rule is simple and absolute: <c>--force</c> is never passed.
/// Git refuses to remove a working tree holding uncommitted changes, and that refusal is exactly
/// the behaviour to keep rather than override for the sake of a smoother dialog.
///
/// Adding one creates a branch as well, when asked, so the plan names both. Locking is included
/// because it is the thing that stops an automatic prune from discarding a working tree on a
/// removable disk that happens not to be mounted today.
/// </remarks>
public sealed class WorktreeEditor : IWorktreeEditor
{
    /// <summary>Operation identifier recorded on any backup.</summary>
    public const string OperationId = "Worktree";

    private readonly IGitCommandRunner _git;
    private readonly IRepositoryPlanApplier _applier;

    /// <summary>Creates the editor.</summary>
    /// <param name="git">Command runner.</param>
    /// <param name="applier">Applier that runs the plan.</param>
    public WorktreeEditor(IGitCommandRunner git, IRepositoryPlanApplier applier)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(applier);

        _git = git;
        _applier = applier;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitWorktree>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        // The porcelain form is the documented, stable one: blank-line separated records of
        // "key value" lines, with the main working tree first.
        var output = await _git
            .ReadAsync(repositoryPath, ["worktree", "list", "--porcelain"], cancellationToken)
            .ConfigureAwait(false);

        if (output is null)
        {
            return [];
        }

        var worktrees = new List<GitWorktree>();

        foreach (var record in output.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var path = string.Empty;
            var head = string.Empty;
            string? branch = null;
            var locked = false;
            string? reason = null;
            var prunable = false;
            var bare = false;

            foreach (var line in record.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                var key = space < 0 ? line : line[..space];
                var value = space < 0 ? string.Empty : line[(space + 1)..];

                switch (key)
                {
                    case "worktree":
                        path = value;
                        break;
                    case "HEAD":
                        head = value;
                        break;
                    case "branch":
                        branch = ShortBranch(value);
                        break;
                    case "locked":
                        locked = true;
                        reason = value.Length > 0 ? value : null;
                        break;
                    case "prunable":
                        prunable = true;
                        break;
                    case "bare":
                        bare = true;
                        break;
                    default:
                        break;
                }
            }

            if (path.Length > 0)
            {
                worktrees.Add(new GitWorktree(
                    path, head, branch, worktrees.Count == 0, locked, reason, prunable, bare));
            }
        }

        return worktrees;
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanAddAsync(
        string repositoryPath,
        string path,
        string startPoint,
        string? createBranch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var blockers = new List<string>();

        if (string.IsNullOrWhiteSpace(path))
        {
            blockers.Add(WorktreeBlockers.PathRequired);
        }
        else if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            // git refuses too, but saying so before the preview is kinder than showing a plan
            // that cannot run.
            blockers.Add(WorktreeBlockers.DirectoryNotEmpty);
        }

        if (createBranch is { Length: > 0 } name && !IsAcceptableRefName(name))
        {
            blockers.Add(RepositoryBlockers.RefNameInvalid);
        }

        var arguments = new List<string> { "worktree", "add" };

        if (createBranch is { Length: > 0 } branch)
        {
            arguments.Add("-b");
            arguments.Add(branch);
        }

        arguments.Add("--");
        arguments.Add(path);

        if (!string.IsNullOrWhiteSpace(startPoint))
        {
            arguments.Add(startPoint);
        }

        var warnings = new List<string>();

        if (createBranch is not { Length: > 0 } && !string.IsNullOrWhiteSpace(startPoint))
        {
            var existing = await _git
                .RunAsync(
                    repositoryPath,
                    ["rev-parse", "--verify", "--quiet", "refs/heads/" + startPoint],
                    cancellationToken)
                .ConfigureAwait(false);

            if (!existing.IsSuccess)
            {
                // Checking out something that is not a branch gives a detached working tree, which
                // is a legitimate thing to want and a surprising thing to get by accident.
                warnings.Add(WorktreeWarnings.WillBeDetached);
            }
        }

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.WorktreeAdd, path, null, startPoint, arguments),
            ],
            Blockers = blockers,
            Warnings = warnings,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanRemoveAsync(
        string repositoryPath,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var worktrees = await ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var target = worktrees.FirstOrDefault(w => PathsMatch(w.Path, path));
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (target is null)
        {
            blockers.Add(WorktreeBlockers.NotFound);
        }
        else if (target.IsMain)
        {
            // The main working tree is the repository. Removing it is not a worktree operation.
            blockers.Add(WorktreeBlockers.CannotRemoveMain);
        }
        else if (target.IsLocked)
        {
            blockers.Add(WorktreeBlockers.Locked);
        }

        if (target is { Branch: { Length: > 0 } })
        {
            // The branch survives; only the checkout goes. Saying so avoids the reading that
            // removing a working tree throws the work away.
            warnings.Add(WorktreeWarnings.BranchSurvives);
        }

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.WorktreeRemove,
                    path,
                    target?.Branch ?? target?.ShortHead,
                    null,

                    // No --force, ever. Git refuses to remove a working tree holding uncommitted
                    // changes, and that refusal is the point rather than an obstacle.
                    ["worktree", "remove", "--", path]),
            ],
            Blockers = blockers,
            Warnings = warnings,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanLockAsync(
        string repositoryPath,
        string path,
        bool locked,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var worktrees = await ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var target = worktrees.FirstOrDefault(w => PathsMatch(w.Path, path));

        var blockers = new List<string>();
        if (target is null)
        {
            blockers.Add(WorktreeBlockers.NotFound);
        }
        else if (target.IsMain)
        {
            blockers.Add(WorktreeBlockers.CannotLockMain);
        }

        var arguments = new List<string> { "worktree", locked ? "lock" : "unlock" };

        if (locked && !string.IsNullOrWhiteSpace(reason))
        {
            arguments.Add("--reason");
            arguments.Add(reason);
        }

        arguments.Add("--");
        arguments.Add(path);

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    locked ? RepositoryChangeKind.WorktreeLock : RepositoryChangeKind.WorktreeUnlock,
                    path,
                    target?.IsLocked == true ? target.LockReason ?? string.Empty : null,
                    locked ? reason ?? string.Empty : null,
                    arguments),
            ],
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanPruneAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var worktrees = await ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var prunable = worktrees.Where(w => w.IsPrunable).ToList();

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes = prunable.Count == 0
                ? []
                : [new RepositoryChange(
                    RepositoryChangeKind.WorktreePrune,
                    string.Join(", ", prunable.Select(w => w.Path)),
                    string.Join(", ", prunable.Select(w => w.Path)),
                    null,
                    ["worktree", "prune"])],
            Blockers = prunable.Count == 0 ? [WorktreeBlockers.NothingToPrune] : [],
        };
    }

    /// <inheritdoc/>
    public Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken) =>
        _applier.ApplyAsync(plan, cancellationToken);

    /// <summary>Turns a full ref name into the short one people use.</summary>
    private static string ShortBranch(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : reference;

    /// <summary>
    /// Compares two paths as the same directory.
    /// </summary>
    /// <remarks>
    /// git reports paths with forward slashes whatever the platform, and the interface hands back
    /// whatever the picker produced, so the two have to be normalised before they can be compared.
    /// </remarks>
    private static bool PathsMatch(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when git would accept this as a branch name.</summary>
    private static bool IsAcceptableRefName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith('-')
        && !name.StartsWith('/')
        && !name.EndsWith('/')
        && !name.EndsWith(".lock", StringComparison.Ordinal)
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.Contains("//", StringComparison.Ordinal)
        && !name.Contains('~', StringComparison.Ordinal)
        && !name.Contains('^', StringComparison.Ordinal)
        && !name.Contains(':', StringComparison.Ordinal)
        && !name.Contains('?', StringComparison.Ordinal)
        && !name.Contains('*', StringComparison.Ordinal)
        && !name.Contains('[', StringComparison.Ordinal)
        && !name.Contains('\\', StringComparison.Ordinal)
        && !name.Any(char.IsControl);
}

/// <summary>
/// Revision names git understands, spelled once.
/// </summary>
/// <remarks>
/// Not user-visible text and not translated: <c>HEAD</c> means the same thing in every language,
/// and a field offering a translated version of it would be offering something git rejects.
/// </remarks>
public static class GitRevisions
{
    /// <summary>Whatever is checked out now.</summary>
    public const string Head = "HEAD";
}

/// <summary>Blocker identifiers for working-tree operations. Localization keys, not text.</summary>
public static class WorktreeBlockers
{
    /// <summary>No directory was given for the new working tree.</summary>
    public const string PathRequired = "Blocker_WorktreePathRequired";

    /// <summary>The directory already holds something.</summary>
    public const string DirectoryNotEmpty = "Blocker_WorktreeDirectoryNotEmpty";

    /// <summary>No working tree of the repository sits at that path.</summary>
    public const string NotFound = "Blocker_WorktreeNotFound";

    /// <summary>The main working tree is the repository, and is not removed this way.</summary>
    public const string CannotRemoveMain = "Blocker_WorktreeCannotRemoveMain";

    /// <summary>The main working tree cannot be locked.</summary>
    public const string CannotLockMain = "Blocker_WorktreeCannotLockMain";

    /// <summary>The working tree is locked, so git will not remove it.</summary>
    public const string Locked = "Blocker_WorktreeLocked";

    /// <summary>No working tree's directory is missing.</summary>
    public const string NothingToPrune = "Blocker_WorktreeNothingToPrune";
}

/// <summary>Warning identifiers for working-tree operations. Localization keys, not text.</summary>
public static class WorktreeWarnings
{
    /// <summary>The new working tree will not be on a branch.</summary>
    public const string WillBeDetached = "Warning_WorktreeWillBeDetached";

    /// <summary>Removing the working tree leaves its branch alone.</summary>
    public const string BranchSurvives = "Warning_WorktreeBranchSurvives";
}
