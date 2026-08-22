namespace GitVault.Core.Repository;

/// <summary>One stash entry.</summary>
/// <param name="Index">Position in the stash list, as git numbers it now.</param>
/// <param name="Reference">The reflog reference, <c>stash@{n}</c>.</param>
/// <param name="Sha">Commit the entry is stored as.</param>
/// <param name="Message">Message git recorded for it.</param>
/// <param name="Branch">Branch it was made on, when the message says.</param>
/// <param name="Created">When it was made.</param>
public sealed record GitStash(
    int Index,
    string Reference,
    string Sha,
    string Message,
    string? Branch,
    DateTimeOffset Created)
{
    /// <summary>Abbreviated commit, for a dense list.</summary>
    public string ShortSha => Sha.Length >= 8 ? Sha[..8] : Sha;
}

/// <summary>Plans and applies changes to a repository's stash.</summary>
public interface IStashEditor
{
    /// <summary>Lists the stash entries, newest first.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries.</returns>
    Task<IReadOnlyList<GitStash>> ListAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Reads the files one stash entry holds.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="reference">Entry to inspect.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The changes.</returns>
    Task<IReadOnlyList<CommitFileChange>> ReadChangesAsync(
        string repositoryPath,
        string reference,
        CancellationToken cancellationToken);

    /// <summary>Plans putting the working tree's changes aside. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="message">Message to record, or null for git's own.</param>
    /// <param name="includeUntracked">True to take untracked files too.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanPushAsync(
        string repositoryPath,
        string? message,
        bool includeUntracked,
        CancellationToken cancellationToken);

    /// <summary>Plans putting a stash entry's changes back. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="reference">Entry to apply.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanApplyAsync(
        string repositoryPath,
        string reference,
        CancellationToken cancellationToken);

    /// <summary>Plans discarding a stash entry. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="reference">Entry to drop.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanDropAsync(
        string repositoryPath,
        string reference,
        CancellationToken cancellationToken);

    /// <summary>Plans turning a stash entry into a branch. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="reference">Entry to convert.</param>
    /// <param name="branch">Branch to create.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanBranchAsync(
        string repositoryPath,
        string reference,
        string branch,
        CancellationToken cancellationToken);

    /// <summary>Applies a plan.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The outcome.</returns>
    Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// The stash editor.
/// </summary>
/// <remarks>
/// Two decisions shape this class, and both are about what is <em>not</em> offered.
///
/// There is no "pop". Pop is apply followed by drop, and it is the operation that surprises
/// people: when the apply conflicts, some of the work is in the tree, the entry may or may not
/// still exist, and the user is left working out which. Apply and drop are offered separately, so
/// each is a decision with its own preview and its own outcome.
///
/// Applying is refused while the working tree has changes of its own. Git would attempt a merge
/// and can leave conflict markers in files the user was in the middle of editing; refusing is the
/// same posture the content editor takes, and for the same reason — this program does not leave a
/// repository in a state nobody chose.
///
/// Dropping is the one genuinely destructive operation here, because a dropped entry is
/// unreachable and git prunes it eventually. It is backed up as a ref first, which is what makes
/// it recoverable at all, and the plan names the backup.
/// </remarks>
public sealed class StashEditor : IStashEditor
{
    /// <summary>Operation identifier recorded on any backup.</summary>
    public const string OperationId = "Stash";

    /// <summary>Field separator asked of git; a byte no message will contain.</summary>
    private const char FieldSeparator = '';

    private readonly IGitCommandRunner _git;
    private readonly IRepositoryInspector _inspector;
    private readonly IRepositoryPlanApplier _applier;

    /// <summary>Creates the editor.</summary>
    /// <param name="git">Command runner.</param>
    /// <param name="inspector">Inspector used to read the working tree's state.</param>
    /// <param name="applier">Applier that preserves refs and runs the plan.</param>
    public StashEditor(
        IGitCommandRunner git,
        IRepositoryInspector inspector,
        IRepositoryPlanApplier applier)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(applier);

        _git = git;
        _inspector = inspector;
        _applier = applier;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitStash>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var format = string.Join(
            FieldSeparator,
            ["%gd", "%H", "%gs", "%aI"]);

        var output = await _git
            .ReadAsync(repositoryPath, ["stash", "list", "--format=" + format], cancellationToken)
            .ConfigureAwait(false);

        if (output is null)
        {
            return [];
        }

        var stashes = new List<GitStash>();
        var index = 0;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Bounded, so a message containing the separator cannot shift the fields after it.
            var parts = line.TrimEnd('\r').Split(FieldSeparator, 4);
            if (parts.Length < 4)
            {
                continue;
            }

            var message = parts[2];

            stashes.Add(new GitStash(
                index++,
                parts[0],
                parts[1],
                message,
                BranchOf(message),
                DateTimeOffset.TryParse(
                    parts[3],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var created)
                    ? created
                    : DateTimeOffset.MinValue));
        }

        return stashes;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CommitFileChange>> ReadChangesAsync(
        string repositoryPath,
        string reference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var output = await _git
            .ReadAsync(
                repositoryPath,
                ["stash", "show", "--numstat", "--no-color", reference],
                cancellationToken)
            .ConfigureAwait(false);

        var changes = new List<CommitFileChange>();

        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            // A dash where a count should be is git saying the file is binary.
            var added = int.TryParse(parts[0], out var a) ? a : (int?)null;
            var removed = int.TryParse(parts[1], out var r) ? r : (int?)null;

            changes.Add(new CommitFileChange(FileChangeStatus.Modified, parts[2], null, added, removed));
        }

        return changes;
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanPushAsync(
        string repositoryPath,
        string? message,
        bool includeUntracked,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var state = await _inspector.GetStateAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();

        if (!state.HasUncommittedChanges)
        {
            blockers.Add(StashBlockers.NothingToStash);
        }

        if (!state.IsQuiet)
        {
            blockers.Add(RepositoryBlockers.OperationInProgress);
        }

        var arguments = new List<string> { "stash", "push" };

        if (includeUntracked)
        {
            arguments.Add("--include-untracked");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            arguments.Add("-m");
            arguments.Add(message);
        }

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.StashPush,
                    state.CurrentBranch ?? string.Empty,
                    null,
                    message ?? string.Empty,
                    arguments),
            ],
            Blockers = blockers,
            Warnings = includeUntracked ? [StashWarnings.UntrackedFilesMove] : [],
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanApplyAsync(
        string repositoryPath,
        string reference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var state = await _inspector.GetStateAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var entry = await FindAsync(repositoryPath, reference, cancellationToken).ConfigureAwait(false);

        var blockers = new List<string>();

        if (entry is null)
        {
            blockers.Add(StashBlockers.NotFound);
        }

        if (state.HasUncommittedChanges)
        {
            // Merging a stash into work in progress can leave conflict markers in a file the user
            // was in the middle of editing. Refusing is the same posture the content editor takes.
            blockers.Add(StashBlockers.WorkingTreeDirty);
        }

        if (!state.IsQuiet)
        {
            blockers.Add(RepositoryBlockers.OperationInProgress);
        }

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.StashApply,
                    reference,
                    null,
                    entry?.Message ?? reference,

                    // apply, never pop: the entry stays until the user drops it deliberately.
                    ["stash", "apply", "--", reference]),
            ],
            Blockers = blockers,
            Warnings = [StashWarnings.EntryStays, StashWarnings.ApplyMayConflict],
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanDropAsync(
        string repositoryPath,
        string reference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var entry = await FindAsync(repositoryPath, reference, cancellationToken).ConfigureAwait(false);

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.StashDrop,
                    reference,
                    entry?.Message ?? reference,
                    null,
                    ["stash", "drop", "--", reference]),
            ],

            // The stash commit itself, so that dropping is undoable. A dropped entry is otherwise
            // unreachable and git prunes it in its own time.
            RefsToBackUp = entry is null ? [] : [entry.Sha],
            Blockers = entry is null ? [StashBlockers.NotFound] : [],
            Warnings = [StashWarnings.DropIsPermanent],
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanBranchAsync(
        string repositoryPath,
        string reference,
        string branch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var state = await _inspector.GetStateAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var entry = await FindAsync(repositoryPath, reference, cancellationToken).ConfigureAwait(false);

        var blockers = new List<string>();

        if (entry is null)
        {
            blockers.Add(StashBlockers.NotFound);
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            blockers.Add(RepositoryBlockers.RefNameInvalid);
        }

        if (state.HasUncommittedChanges)
        {
            blockers.Add(StashBlockers.WorkingTreeDirty);
        }

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.StashBranch,
                    branch,
                    entry?.Message ?? reference,
                    branch,
                    ["stash", "branch", branch, "--", reference]),
            ],
            Blockers = blockers,

            // git stash branch checks the branch out and drops the entry once it succeeds, which
            // is more than the button's name suggests.
            Warnings = [StashWarnings.BranchChecksOutAndDrops],
        };
    }

    /// <inheritdoc/>
    public Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken) =>
        _applier.ApplyAsync(plan, cancellationToken);

    /// <summary>Finds one entry by its reference.</summary>
    private async Task<GitStash?> FindAsync(
        string repositoryPath,
        string reference,
        CancellationToken cancellationToken)
    {
        var stashes = await ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        return stashes.FirstOrDefault(s => string.Equals(s.Reference, reference, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads the branch out of git's own stash message.
    /// </summary>
    /// <remarks>
    /// git writes "WIP on main: abc1234 Subject" or "On main: message". The branch is worth
    /// showing as its own column, and it is only ever available from this text.
    /// </remarks>
    private static string? BranchOf(string message)
    {
        foreach (var prefix in (string[])["WIP on ", "On "])
        {
            if (!message.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = message[prefix.Length..];
            var colon = rest.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0)
            {
                return rest[..colon];
            }
        }

        return null;
    }
}

/// <summary>Blocker identifiers for stash operations. Localization keys, not text.</summary>
public static class StashBlockers
{
    /// <summary>There is nothing in the working tree to put aside.</summary>
    public const string NothingToStash = "Blocker_StashNothingToStash";

    /// <summary>No stash entry with that reference.</summary>
    public const string NotFound = "Blocker_StashNotFound";

    /// <summary>The working tree has changes of its own.</summary>
    public const string WorkingTreeDirty = "Blocker_StashWorkingTreeDirty";
}

/// <summary>Warning identifiers for stash operations. Localization keys, not text.</summary>
public static class StashWarnings
{
    /// <summary>Applying leaves the entry in place.</summary>
    public const string EntryStays = "Warning_StashEntryStays";

    /// <summary>Applying can still conflict, even into a clean working tree.</summary>
    public const string ApplyMayConflict = "Warning_StashApplyMayConflict";

    /// <summary>Dropping makes the entry unreachable.</summary>
    public const string DropIsPermanent = "Warning_StashDropIsPermanent";

    /// <summary>Untracked files are moved out of the working tree, not copied.</summary>
    public const string UntrackedFilesMove = "Warning_StashUntrackedFilesMove";

    /// <summary>Making a branch checks it out and discards the entry.</summary>
    public const string BranchChecksOutAndDrops = "Warning_StashBranchChecksOutAndDrops";
}
