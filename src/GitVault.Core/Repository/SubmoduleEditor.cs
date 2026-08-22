namespace GitVault.Core.Repository;

/// <summary>What state a submodule's working copy is in.</summary>
public enum SubmoduleState
{
    /// <summary>Recorded in <c>.gitmodules</c> but never checked out here.</summary>
    NotInitialized = 0,

    /// <summary>Checked out at the commit the parent records.</summary>
    UpToDate,

    /// <summary>Checked out at a different commit than the parent records.</summary>
    Moved,

    /// <summary>Checked out, with a merge conflict inside it.</summary>
    Conflicted,
}

/// <summary>One submodule of a repository.</summary>
/// <param name="Name">Name the <c>.gitmodules</c> section uses.</param>
/// <param name="Path">Path inside the parent repository.</param>
/// <param name="Url">Where the parent says it comes from.</param>
/// <param name="Branch">Branch the parent tracks, when one is named.</param>
/// <param name="RecordedSha">Commit the parent records for it.</param>
/// <param name="State">What state the working copy is in.</param>
public sealed record GitSubmodule(
    string Name,
    string Path,
    string Url,
    string? Branch,
    string RecordedSha,
    SubmoduleState State)
{
    /// <summary>Abbreviated commit, for a dense list.</summary>
    public string ShortSha => RecordedSha.Length >= 8 ? RecordedSha[..8] : RecordedSha;

    /// <summary>True when the working copy exists here.</summary>
    public bool IsInitialized => State != SubmoduleState.NotInitialized;
}

/// <summary>Reads a repository's submodules and edits what the parent records about them.</summary>
public interface ISubmoduleEditor
{
    /// <summary>Lists the submodules the parent records.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The submodules, by path.</returns>
    Task<IReadOnlyList<GitSubmodule>> ListAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Plans changing where a submodule comes from. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Submodule name.</param>
    /// <param name="url">New URL.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanSetUrlAsync(
        string repositoryPath,
        string name,
        string url,
        CancellationToken cancellationToken);

    /// <summary>Plans changing which branch a submodule tracks. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Submodule name.</param>
    /// <param name="branch">Branch to track, or null to stop tracking one.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanSetBranchAsync(
        string repositoryPath,
        string name,
        string? branch,
        CancellationToken cancellationToken);

    /// <summary>Plans copying the recorded URLs into the local configuration. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Submodule to synchronise, or null for all of them.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanSyncAsync(
        string repositoryPath,
        string? name,
        CancellationToken cancellationToken);

    /// <summary>Plans removing a submodule's working copy, keeping the record. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Submodule name.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanDeinitAsync(
        string repositoryPath,
        string name,
        CancellationToken cancellationToken);

    /// <summary>Applies a plan.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The outcome.</returns>
    Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// The submodule editor.
/// </summary>
/// <remarks>
/// What is here is bounded by a rule that comes from outside this file: GitVault makes no network
/// calls. Initialising or updating a submodule means cloning or fetching, so those are not offered
/// and are not quietly attempted — the user runs <c>git submodule update</c> themselves, with
/// their own credentials, in their own terminal.
///
/// What is left is the part that is genuinely local, and it happens to be the part that goes wrong
/// most often: the URL the parent records. A submodule pointing at an address that has moved, or
/// at HTTPS when the user authenticates over SSH, fails at exactly the moment it is least
/// convenient. Correcting it is a text edit in <c>.gitmodules</c>, and telling the local
/// configuration about the correction is <c>git submodule sync</c> — which is worth being a
/// separate, named step, because editing the file alone changes nothing about what git will do
/// next.
///
/// Deinitialising removes a working copy without forgetting the submodule. It is offered without
/// <c>--force</c>, so git refuses when there is uncommitted work inside — which is the behaviour
/// worth keeping rather than overriding.
/// </remarks>
public sealed class SubmoduleEditor : ISubmoduleEditor
{
    /// <summary>Operation identifier recorded on any backup.</summary>
    public const string OperationId = "Submodule";

    /// <summary>The file the parent records its submodules in.</summary>
    public const string ModulesFile = ".gitmodules";

    private readonly IGitCommandRunner _git;
    private readonly IRepositoryPlanApplier _applier;

    /// <summary>Creates the editor.</summary>
    /// <param name="git">Command runner.</param>
    /// <param name="applier">Applier that runs the plan.</param>
    public SubmoduleEditor(IGitCommandRunner git, IRepositoryPlanApplier applier)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(applier);

        _git = git;
        _applier = applier;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitSubmodule>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        if (!File.Exists(Path.Combine(repositoryPath, ModulesFile)))
        {
            return [];
        }

        // The record comes from .gitmodules, which is the parent's own statement about them, read
        // through git so that its own parsing rules apply rather than a second implementation.
        var listing = await _git
            .ReadAsync(
                repositoryPath,
                ["config", "--file", ModulesFile, "--list", "-z"],
                cancellationToken)
            .ConfigureAwait(false);

        var byName = new Dictionary<string, (string? Path, string? Url, string? Branch)>(StringComparer.Ordinal);

        foreach (var entry in (listing ?? string.Empty).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // Each record is "key\nvalue" in the NUL-terminated form.
            var newline = entry.IndexOf('\n', StringComparison.Ordinal);
            if (newline < 0)
            {
                continue;
            }

            var key = entry[..newline];
            var value = entry[(newline + 1)..];

            if (!key.StartsWith("submodule.", StringComparison.Ordinal))
            {
                continue;
            }

            var lastDot = key.LastIndexOf('.');
            if (lastDot <= "submodule.".Length)
            {
                continue;
            }

            var name = key["submodule.".Length..lastDot];
            var field = key[(lastDot + 1)..];

            byName.TryGetValue(name, out var current);

            byName[name] = field switch
            {
                "path" => (value, current.Url, current.Branch),
                "url" => (current.Path, value, current.Branch),
                "branch" => (current.Path, current.Url, value),
                _ => current,
            };
        }

        var states = await ReadStatesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var submodules = new List<GitSubmodule>();

        foreach (var (name, record) in byName.OrderBy(p => p.Value.Path ?? p.Key, StringComparer.Ordinal))
        {
            if (record.Path is not { Length: > 0 } path)
            {
                continue;
            }

            states.TryGetValue(path, out var status);

            submodules.Add(new GitSubmodule(
                name,
                path,
                record.Url ?? string.Empty,
                record.Branch,
                status.Sha ?? string.Empty,
                status.State));
        }

        return submodules;
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanSetUrlAsync(
        string repositoryPath,
        string name,
        string url,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var existing = await FindAsync(repositoryPath, name, cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();

        if (existing is null)
        {
            blockers.Add(SubmoduleBlockers.NotFound);
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            blockers.Add(RepositoryBlockers.RemoteUrlRequired);
        }

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.SubmoduleSetUrl,
                    name,
                    existing?.Url,
                    url,
                    ["config", "--file", ModulesFile, "submodule." + name + ".url", url]),
            ],
            Blockers = blockers,

            // Editing the file is not the same as telling git about the edit.
            Warnings = [SubmoduleWarnings.SyncNeeded, SubmoduleWarnings.FileIsCommitted],
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanSetBranchAsync(
        string repositoryPath,
        string name,
        string? branch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var existing = await FindAsync(repositoryPath, name, cancellationToken).ConfigureAwait(false);
        var key = "submodule." + name + ".branch";

        var arguments = string.IsNullOrWhiteSpace(branch)
            ? (IReadOnlyList<string>)["config", "--file", ModulesFile, "--unset", key]
            : ["config", "--file", ModulesFile, key, branch];

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.SubmoduleSetBranch,
                    name,
                    existing?.Branch,
                    string.IsNullOrWhiteSpace(branch) ? null : branch,
                    arguments),
            ],
            Blockers = existing is null ? [SubmoduleBlockers.NotFound] : [],
            Warnings = [SubmoduleWarnings.FileIsCommitted],
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanSyncAsync(
        string repositoryPath,
        string? name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var submodules = await ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var target = name is { Length: > 0 }
            ? submodules.FirstOrDefault(s => s.Name == name)
            : null;

        var arguments = new List<string> { "submodule", "sync" };

        if (target is not null)
        {
            arguments.Add("--");
            arguments.Add(target.Path);
        }

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.SubmoduleSync,
                    target?.Name ?? string.Empty,
                    null,
                    target?.Url ?? string.Empty,
                    arguments),
            ],
            Blockers = submodules.Count == 0
                ? [SubmoduleBlockers.NotFound]
                : name is { Length: > 0 } && target is null ? [SubmoduleBlockers.NotFound] : [],
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanDeinitAsync(
        string repositoryPath,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var existing = await FindAsync(repositoryPath, name, cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();

        if (existing is null)
        {
            blockers.Add(SubmoduleBlockers.NotFound);
        }
        else if (!existing.IsInitialized)
        {
            blockers.Add(SubmoduleBlockers.NotInitialized);
        }

        return new RepositoryPlan(OperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.SubmoduleDeinit,
                    name,
                    existing?.Path,
                    null,

                    // No --force: git refuses while there is uncommitted work inside, which is the
                    // behaviour worth keeping rather than overriding.
                    ["submodule", "deinit", "--", existing?.Path ?? name]),
            ],
            Blockers = blockers,
            Warnings = [SubmoduleWarnings.DeinitNeedsNetworkToUndo],
        };
    }

    /// <inheritdoc/>
    public Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken) =>
        _applier.ApplyAsync(plan, cancellationToken);

    /// <summary>Reads what each submodule's working copy is doing.</summary>
    /// <remarks>
    /// <c>git submodule status</c> prefixes each line with a character saying what it found: a
    /// dash for never checked out, a plus for a different commit, a capital U for a conflict, and
    /// a space for agreement with the parent.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, (string? Sha, SubmoduleState State)>> ReadStatesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var output = await _git
            .ReadAsync(repositoryPath, ["submodule", "status"], cancellationToken)
            .ConfigureAwait(false);

        var states = new Dictionary<string, (string? Sha, SubmoduleState State)>(StringComparer.Ordinal);

        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length < 2)
            {
                continue;
            }

            var marker = trimmed[0];
            var rest = trimmed[1..].Trim();
            var space = rest.IndexOf(' ', StringComparison.Ordinal);

            if (space <= 0)
            {
                continue;
            }

            var sha = rest[..space];
            var remainder = rest[(space + 1)..].Trim();

            // The path may be followed by the described ref in parentheses.
            var bracket = remainder.LastIndexOf(" (", StringComparison.Ordinal);
            var path = bracket > 0 ? remainder[..bracket] : remainder;

            states[path] = (sha, marker switch
            {
                '-' => SubmoduleState.NotInitialized,
                '+' => SubmoduleState.Moved,
                'U' => SubmoduleState.Conflicted,
                _ => SubmoduleState.UpToDate,
            });
        }

        return states;
    }

    private async Task<GitSubmodule?> FindAsync(
        string repositoryPath,
        string name,
        CancellationToken cancellationToken)
    {
        var submodules = await ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        return submodules.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
    }
}

/// <summary>Blocker identifiers for submodule operations. Localization keys, not text.</summary>
public static class SubmoduleBlockers
{
    /// <summary>The repository records no such submodule.</summary>
    public const string NotFound = "Blocker_SubmoduleNotFound";

    /// <summary>The submodule has no working copy here to remove.</summary>
    public const string NotInitialized = "Blocker_SubmoduleNotInitialized";
}

/// <summary>Warning identifiers for submodule operations. Localization keys, not text.</summary>
public static class SubmoduleWarnings
{
    /// <summary>Editing the file changes nothing until the local configuration is told.</summary>
    public const string SyncNeeded = "Warning_SubmoduleSyncNeeded";

    /// <summary>The file is committed, so the change reaches everyone once committed.</summary>
    public const string FileIsCommitted = "Warning_SubmoduleFileIsCommitted";

    /// <summary>Putting the working copy back needs a network fetch GitVault will not make.</summary>
    public const string DeinitNeedsNetworkToUndo = "Warning_SubmoduleDeinitNeedsNetwork";
}
