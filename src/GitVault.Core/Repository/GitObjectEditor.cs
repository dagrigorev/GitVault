using GitVault.Core.Models;
using GitVault.Core.Profiles;

namespace GitVault.Core.Repository;

/// <summary>What a planned repository change does.</summary>
public enum RepositoryChangeKind
{
    /// <summary>Add a remote.</summary>
    RemoteAdd = 0,

    /// <summary>Rename a remote.</summary>
    RemoteRename,

    /// <summary>Remove a remote.</summary>
    RemoteRemove,

    /// <summary>Change a remote's fetch or push URL.</summary>
    RemoteSetUrl,

    /// <summary>Create a branch.</summary>
    BranchCreate,

    /// <summary>Rename a branch.</summary>
    BranchRename,

    /// <summary>Delete a branch.</summary>
    BranchDelete,

    /// <summary>Set or clear a branch's upstream.</summary>
    BranchUpstream,

    /// <summary>Create a tag.</summary>
    TagCreate,

    /// <summary>Delete a tag.</summary>
    TagDelete,

    /// <summary>Attach a working tree.</summary>
    WorktreeAdd,

    /// <summary>Detach a working tree.</summary>
    WorktreeRemove,

    /// <summary>Mark a working tree so git will not prune or remove it.</summary>
    WorktreeLock,

    /// <summary>Clear that mark.</summary>
    WorktreeUnlock,

    /// <summary>Forget the working trees whose directories are gone.</summary>
    WorktreePrune,

    /// <summary>Put the working tree's changes aside.</summary>
    StashPush,

    /// <summary>Put a stash entry's changes back.</summary>
    StashApply,

    /// <summary>Discard a stash entry.</summary>
    StashDrop,

    /// <summary>Turn a stash entry into a branch.</summary>
    StashBranch,

    /// <summary>Change where a submodule comes from.</summary>
    SubmoduleSetUrl,

    /// <summary>Change which branch a submodule tracks.</summary>
    SubmoduleSetBranch,

    /// <summary>Copy the recorded URLs into the local configuration.</summary>
    SubmoduleSync,

    /// <summary>Remove a submodule's working copy, keeping the record.</summary>
    SubmoduleDeinit,
}

/// <summary>One change to a repository's refs or remotes.</summary>
/// <param name="Kind">What the change does.</param>
/// <param name="Target">Name the change addresses.</param>
/// <param name="Before">State before, or null when the thing did not exist.</param>
/// <param name="After">State after, or null when the change removes it.</param>
/// <param name="Arguments">Git arguments this step will run.</param>
public sealed record RepositoryChange(
    RepositoryChangeKind Kind,
    string Target,
    string? Before,
    string? After,
    IReadOnlyList<string> Arguments);

/// <summary>A planned set of repository changes, with the refs to preserve first.</summary>
/// <param name="OperationId">Operation identifier; a localization key suffix.</param>
/// <param name="RepositoryPath">Repository the plan addresses.</param>
public sealed record RepositoryPlan(string OperationId, string RepositoryPath)
{
    /// <summary>Changes in execution order.</summary>
    public IReadOnlyList<RepositoryChange> Changes { get; init; } = [];

    /// <summary>Refs preserved before the first change.</summary>
    public IReadOnlyList<string> RefsToBackUp { get; init; } = [];

    /// <summary>Reasons the plan cannot be applied.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Things the user should know before confirming, which do not block.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>True when the plan can be applied.</summary>
    public bool CanApply => Blockers.Count == 0 && Changes.Count > 0;

    /// <summary>
    /// Renders the plan as the preview the user has to approve.
    /// </summary>
    /// <remarks>
    /// The command shown is the one that will run, assembled from the very arguments the applier
    /// passes to git. An earlier version borrowed the configuration renderer, so deleting a branch
    /// announced itself as <c>git config --unset</c> — a preview that names a different command
    /// from the one it is about to run is worse than no preview, because it is believed.
    /// </remarks>
    /// <returns>The preview text.</returns>
    public string ToDiff()
    {
        var builder = new System.Text.StringBuilder();

        foreach (var change in Changes)
        {
            builder.Append("git ").Append(string.Join(' ', change.Arguments)).Append('\n');

            if (change.Before is { Length: > 0 } before)
            {
                builder.Append("  - ").Append(before).Append('\n');
            }

            if (change.After is { Length: > 0 } after)
            {
                builder.Append("  + ").Append(after).Append('\n');
            }
        }

        builder.Append(PlanDiff.RenderBlockers(Blockers));

        return builder.ToString();
    }
}

/// <summary>Outcome of applying a <see cref="RepositoryPlan"/>.</summary>
/// <param name="OperationId">The operation that ran.</param>
/// <param name="BackupId">Ref backup taken before the first change, for undo.</param>
public sealed record RepositoryResult(string OperationId, string? BackupId)
{
    /// <summary>Per-step outcomes, in execution order.</summary>
    public IReadOnlyList<ActivationStepResult> Steps { get; init; } = [];

    /// <summary>
    /// True when the work was carried out and no step failed.
    /// </summary>
    /// <remarks>
    /// A refused plan runs nothing and returns no steps, and "no step failed" is vacuously true of
    /// an empty list. Requiring at least one step keeps a refusal from reading as a success.
    /// </remarks>
    public bool Succeeded => Steps.Count > 0 && Steps.All(s => s.Outcome != StepOutcome.Failed);
}

/// <summary>Plans and applies changes to remotes, branches and tags.</summary>
public interface IGitObjectEditor
{
    /// <summary>Plans adding a remote.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Remote name.</param>
    /// <param name="url">Fetch URL.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanAddRemoteAsync(
        string repositoryPath, string name, string url, CancellationToken cancellationToken);

    /// <summary>Plans renaming a remote.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="oldName">Current name.</param>
    /// <param name="newName">New name.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanRenameRemoteAsync(
        string repositoryPath, string oldName, string newName, CancellationToken cancellationToken);

    /// <summary>Plans removing a remote.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Remote name.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanRemoveRemoteAsync(
        string repositoryPath, string name, CancellationToken cancellationToken);

    /// <summary>Plans changing a remote's URLs.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Remote name.</param>
    /// <param name="fetchUrl">New fetch URL.</param>
    /// <param name="pushUrl">New push URL, or null to leave it following the fetch URL.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanSetRemoteUrlAsync(
        string repositoryPath, string name, string fetchUrl, string? pushUrl, CancellationToken cancellationToken);

    /// <summary>Plans creating a branch at a starting point.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Branch name.</param>
    /// <param name="startPoint">Commit or ref to start from; HEAD when empty.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanCreateBranchAsync(
        string repositoryPath, string name, string? startPoint, CancellationToken cancellationToken);

    /// <summary>Plans renaming a branch.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="oldName">Current name.</param>
    /// <param name="newName">New name.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanRenameBranchAsync(
        string repositoryPath, string oldName, string newName, CancellationToken cancellationToken);

    /// <summary>Plans deleting a branch.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Branch name.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanDeleteBranchAsync(
        string repositoryPath, string name, CancellationToken cancellationToken);

    /// <summary>Plans setting or clearing a branch's upstream.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Branch name.</param>
    /// <param name="upstream">Upstream ref, or null to clear it.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanSetUpstreamAsync(
        string repositoryPath, string name, string? upstream, CancellationToken cancellationToken);

    /// <summary>Plans creating a tag.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Tag name.</param>
    /// <param name="target">Commit or ref to tag; HEAD when empty.</param>
    /// <param name="message">Annotation message, or null for a lightweight tag.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanCreateTagAsync(
        string repositoryPath, string name, string? target, string? message, CancellationToken cancellationToken);

    /// <summary>Plans deleting a tag.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Tag name.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<RepositoryPlan> PlanDeleteTagAsync(
        string repositoryPath, string name, CancellationToken cancellationToken);

    /// <summary>Applies a plan, preserving the refs it names first.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A per-step result.</returns>
    Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// The remotes, branches and tags editor.
/// </summary>
/// <remarks>
/// Every plan is built by asking git what is there now, so the preview describes the repository
/// as it actually stands rather than as the last scan remembered it.
///
/// What separates a blocker from a warning is whether the user can decide. Deleting the branch
/// that is checked out cannot be done at all, so it blocks. Deleting a branch whose commits exist
/// nowhere else can be done and is sometimes exactly what is wanted, so it warns and lets the
/// person judge — with a ref backup taken first so the judgement is reversible.
/// </remarks>
public sealed class GitObjectEditor : IGitObjectEditor
{
    /// <summary>Operation identifiers, recorded on backups and used as localization key suffixes.</summary>
    public const string RemoteOperationId = "RemoteEdit";

    /// <summary>Operation identifier for branch changes.</summary>
    public const string BranchOperationId = "BranchEdit";

    /// <summary>Operation identifier for tag changes.</summary>
    public const string TagOperationId = "TagEdit";

    private readonly IGitCommandRunner _git;
    private readonly IRepositoryInspector _inspector;
    private readonly IRepositoryPlanApplier _applier;

    /// <summary>Creates the editor.</summary>
    /// <param name="git">Command runner.</param>
    /// <param name="inspector">Inspector used to read current state.</param>
    /// <param name="applier">Applier that preserves refs and runs the plan.</param>
    public GitObjectEditor(
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
    public async Task<RepositoryPlan> PlanAddRemoteAsync(
        string repositoryPath, string name, string url, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        ValidateName(name, RepositoryBlockers.RemoteNameInvalid, blockers);

        if (string.IsNullOrWhiteSpace(url))
        {
            blockers.Add(RepositoryBlockers.RemoteUrlRequired);
        }

        var remotes = await _inspector.ListRemotesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (remotes.Any(r => r.Name == name))
        {
            blockers.Add(RepositoryBlockers.RemoteExists);
        }

        return new RepositoryPlan(RemoteOperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.RemoteAdd, name, null, url, ["remote", "add", name, url]),
            ],
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanRenameRemoteAsync(
        string repositoryPath, string oldName, string newName, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        ValidateName(newName, RepositoryBlockers.RemoteNameInvalid, blockers);

        var remotes = await _inspector.ListRemotesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (remotes.All(r => r.Name != oldName))
        {
            blockers.Add(RepositoryBlockers.RemoteMissing);
        }

        if (remotes.Any(r => r.Name == newName))
        {
            blockers.Add(RepositoryBlockers.RemoteExists);
        }

        return new RepositoryPlan(RemoteOperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.RemoteRename,
                    oldName,
                    oldName,
                    newName,
                    ["remote", "rename", oldName, newName]),
            ],
            Blockers = blockers,

            // Renaming a remote rewrites every remote-tracking ref under it.
            Warnings = [RepositoryWarnings.RemoteRenameMovesTrackingRefs],
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanRemoveRemoteAsync(
        string repositoryPath, string name, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var remotes = await _inspector.ListRemotesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var remote = remotes.FirstOrDefault(r => r.Name == name);

        if (remote is null)
        {
            blockers.Add(RepositoryBlockers.RemoteMissing);
        }

        var branches = await _inspector.ListBranchesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var tracking = branches
            .Where(b => b.IsRemote && b.Name.StartsWith(name + "/", StringComparison.Ordinal))
            .Select(b => b.FullName)
            .ToList();

        return new RepositoryPlan(RemoteOperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.RemoteRemove,
                    name,
                    remote?.FetchUrl,
                    null,
                    ["remote", "remove", name]),
            ],
            RefsToBackUp = tracking,
            Blockers = blockers,
            Warnings = tracking.Count > 0 ? [RepositoryWarnings.RemoteRemoveDropsTrackingRefs] : [],
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanSetRemoteUrlAsync(
        string repositoryPath, string name, string fetchUrl, string? pushUrl, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();

        if (string.IsNullOrWhiteSpace(fetchUrl))
        {
            blockers.Add(RepositoryBlockers.RemoteUrlRequired);
        }

        var remotes = await _inspector.ListRemotesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var remote = remotes.FirstOrDefault(r => r.Name == name);

        if (remote is null)
        {
            blockers.Add(RepositoryBlockers.RemoteMissing);
        }

        var changes = new List<RepositoryChange>
        {
            new(
                RepositoryChangeKind.RemoteSetUrl,
                name,
                remote?.FetchUrl,
                fetchUrl,
                ["remote", "set-url", name, fetchUrl]),
        };

        if (!string.IsNullOrWhiteSpace(pushUrl))
        {
            changes.Add(new RepositoryChange(
                RepositoryChangeKind.RemoteSetUrl,
                name,
                remote?.PushUrl,
                pushUrl,
                ["remote", "set-url", "--push", name, pushUrl]));
        }

        return new RepositoryPlan(RemoteOperationId, repositoryPath)
        {
            Changes = changes,
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanCreateBranchAsync(
        string repositoryPath, string name, string? startPoint, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        ValidateRefName(name, blockers);

        var branches = await _inspector.ListBranchesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (branches.Any(b => !b.IsRemote && b.Name == name))
        {
            blockers.Add(RepositoryBlockers.BranchExists);
        }

        var from = string.IsNullOrWhiteSpace(startPoint) ? "HEAD" : startPoint;
        var resolved = await _git
            .ReadAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", from], cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(resolved))
        {
            blockers.Add(RepositoryBlockers.StartPointMissing);
        }

        return new RepositoryPlan(BranchOperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.BranchCreate,
                    name,
                    null,
                    resolved ?? from,

                    // The terminating -- keeps a branch called "-f" from being read as an option.
                    ["branch", name, from, "--"]),
            ],
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanRenameBranchAsync(
        string repositoryPath, string oldName, string newName, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        ValidateRefName(newName, blockers);

        var branches = await _inspector.ListBranchesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var branch = branches.FirstOrDefault(b => !b.IsRemote && b.Name == oldName);

        if (branch is null)
        {
            blockers.Add(RepositoryBlockers.BranchMissing);
        }

        if (branches.Any(b => !b.IsRemote && b.Name == newName))
        {
            blockers.Add(RepositoryBlockers.BranchExists);
        }

        await AddStateBlockersAsync(repositoryPath, blockers, cancellationToken).ConfigureAwait(false);

        return new RepositoryPlan(BranchOperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.BranchRename,
                    oldName,
                    oldName,
                    newName,
                    ["branch", "--move", oldName, newName]),
            ],
            RefsToBackUp = branch is null ? [] : [branch.FullName],
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanDeleteBranchAsync(
        string repositoryPath, string name, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        var branches = await _inspector.ListBranchesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var branch = branches.FirstOrDefault(b => !b.IsRemote && b.Name == name);

        if (branch is null)
        {
            blockers.Add(RepositoryBlockers.BranchMissing);
        }
        else if (branch.IsCurrent)
        {
            // Git refuses this too, but saying so in the preview is better than letting the user
            // confirm a plan that cannot run.
            blockers.Add(RepositoryBlockers.BranchIsCurrent);
        }

        await AddStateBlockersAsync(repositoryPath, blockers, cancellationToken).ConfigureAwait(false);

        if (branch is not null && await IsUnmergedAsync(repositoryPath, branch, cancellationToken).ConfigureAwait(false))
        {
            // Not a blocker: deleting an unmerged branch is sometimes the point. The ref backup
            // is what makes it a decision rather than a loss.
            warnings.Add(RepositoryWarnings.BranchNotMerged);
        }

        return new RepositoryPlan(BranchOperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.BranchDelete,
                    name,
                    branch?.TipCommit,
                    null,

                    // --delete --force, because refusing here and then refusing again in git
                    // would leave the user unable to act on a warning they already accepted.
                    ["branch", "--delete", "--force", name]),
            ],
            RefsToBackUp = branch is null ? [] : [branch.FullName],
            Blockers = blockers,
            Warnings = warnings,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanSetUpstreamAsync(
        string repositoryPath, string name, string? upstream, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();

        var branches = await _inspector.ListBranchesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var branch = branches.FirstOrDefault(b => !b.IsRemote && b.Name == name);

        if (branch is null)
        {
            blockers.Add(RepositoryBlockers.BranchMissing);
        }

        if (!string.IsNullOrWhiteSpace(upstream) && branches.All(b => b.Name != upstream))
        {
            blockers.Add(RepositoryBlockers.UpstreamMissing);
        }

        var arguments = string.IsNullOrWhiteSpace(upstream)
            ? new List<string> { "branch", "--unset-upstream", name }
            : ["branch", "--set-upstream-to=" + upstream, name];

        return new RepositoryPlan(BranchOperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.BranchUpstream, name, branch?.Upstream, upstream, arguments),
            ],
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanCreateTagAsync(
        string repositoryPath, string name, string? target, string? message, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        ValidateRefName(name, blockers);

        var tags = await _inspector.ListTagsAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (tags.Any(t => t.Name == name))
        {
            blockers.Add(RepositoryBlockers.TagExists);
        }

        var at = string.IsNullOrWhiteSpace(target) ? "HEAD" : target;
        var resolved = await _git
            .ReadAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", at], cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(resolved))
        {
            blockers.Add(RepositoryBlockers.StartPointMissing);
        }

        var arguments = string.IsNullOrWhiteSpace(message)
            ? new List<string> { "tag", name, at }
            : ["tag", "--annotate", "--message=" + message, name, at];

        return new RepositoryPlan(TagOperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.TagCreate, name, null, resolved ?? at, arguments),
            ],
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<RepositoryPlan> PlanDeleteTagAsync(
        string repositoryPath, string name, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        var tags = await _inspector.ListTagsAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var tag = tags.FirstOrDefault(t => t.Name == name);

        if (tag is null)
        {
            blockers.Add(RepositoryBlockers.TagMissing);
        }
        else if (tag.IsSigned)
        {
            // A signature cannot be recreated by GitVault. Deleting one is a decision, not a slip.
            warnings.Add(RepositoryWarnings.TagIsSigned);
        }

        return new RepositoryPlan(TagOperationId, repositoryPath)
        {
            Changes =
            [
                new RepositoryChange(
                    RepositoryChangeKind.TagDelete, name, tag?.TargetCommit, null, ["tag", "--delete", name]),
            ],
            RefsToBackUp = tag is null ? [] : ["refs/tags/" + name],
            Blockers = blockers,
            Warnings = warnings,
        };
    }

    /// <inheritdoc/>
    public Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken) =>
        _applier.ApplyAsync(plan, cancellationToken);

    /// <summary>True when a branch has commits that exist on no other branch.</summary>
    private async Task<bool> IsUnmergedAsync(
        string repositoryPath,
        GitBranch branch,
        CancellationToken cancellationToken)
    {
        var merged = await _git
            .ReadAsync(repositoryPath, ["branch", "--format=%(refname:short)", "--merged", "HEAD"], cancellationToken)
            .ConfigureAwait(false);

        if (merged is null)
        {
            return false;
        }

        return !merged
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Contains(branch.Name, StringComparer.Ordinal);
    }

    /// <summary>Adds the blockers that come from the repository being mid-operation.</summary>
    private async Task AddStateBlockersAsync(
        string repositoryPath,
        List<string> blockers,
        CancellationToken cancellationToken)
    {
        var state = await _inspector.GetStateAsync(repositoryPath, cancellationToken).ConfigureAwait(false);

        if (!state.IsQuiet)
        {
            blockers.Add(RepositoryBlockers.OperationInProgress);
        }
    }

    /// <summary>Rejects a name git would refuse or that could be read as an option.</summary>
    private static void ValidateName(string name, string blocker, List<string> blockers)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.StartsWith('-')
            || name.Any(c => char.IsControl(c) || c is ' ' or '\\' or '~' or '^' or ':' or '?' or '*' or '['))
        {
            blockers.Add(blocker);
        }
    }

    /// <summary>Rejects a ref name git's own rules would reject.</summary>
    private static void ValidateRefName(string name, List<string> blockers)
    {
        ValidateName(name, RepositoryBlockers.RefNameInvalid, blockers);

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (name.StartsWith('/')
            || name.EndsWith('/')
            || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || name.Contains("..", StringComparison.Ordinal)
            || name.Contains("@{", StringComparison.Ordinal))
        {
            blockers.Add(RepositoryBlockers.RefNameInvalid);
        }
    }
}

/// <summary>Blocker identifiers for repository operations. Localization keys, not text.</summary>
public static class RepositoryBlockers
{
    /// <summary>The remote name is empty or contains characters git will not accept.</summary>
    public const string RemoteNameInvalid = "Blocker_RemoteNameInvalid";

    /// <summary>A remote of that name already exists.</summary>
    public const string RemoteExists = "Blocker_RemoteExists";

    /// <summary>No remote of that name exists.</summary>
    public const string RemoteMissing = "Blocker_RemoteMissing";

    /// <summary>A remote needs a URL.</summary>
    public const string RemoteUrlRequired = "Blocker_RemoteUrlRequired";

    /// <summary>The ref name is empty or breaks git's naming rules.</summary>
    public const string RefNameInvalid = "Blocker_RefNameInvalid";

    /// <summary>A branch of that name already exists.</summary>
    public const string BranchExists = "Blocker_BranchExists";

    /// <summary>No branch of that name exists.</summary>
    public const string BranchMissing = "Blocker_BranchMissing";

    /// <summary>The branch is the one currently checked out.</summary>
    public const string BranchIsCurrent = "Blocker_BranchIsCurrent";

    /// <summary>The proposed upstream does not exist.</summary>
    public const string UpstreamMissing = "Blocker_UpstreamMissing";

    /// <summary>A tag of that name already exists.</summary>
    public const string TagExists = "Blocker_TagExists";

    /// <summary>No tag of that name exists.</summary>
    public const string TagMissing = "Blocker_TagMissing";

    /// <summary>The starting point could not be resolved.</summary>
    public const string StartPointMissing = "Blocker_StartPointMissing";

    /// <summary>Git is part-way through a rebase, merge, cherry-pick, revert or bisect.</summary>
    public const string OperationInProgress = "Blocker_OperationInProgress";
}

/// <summary>Warning identifiers for repository operations. Localization keys, not text.</summary>
public static class RepositoryWarnings
{
    /// <summary>Prefix every warning identifier carries.</summary>
    public const string Prefix = "Warning_";

    /// <summary>The branch has commits that exist on no other branch.</summary>
    public const string BranchNotMerged = "Warning_BranchNotMerged";

    /// <summary>The tag carries a signature that cannot be recreated.</summary>
    public const string TagIsSigned = "Warning_TagIsSigned";

    /// <summary>Renaming a remote moves every remote-tracking ref under it.</summary>
    public const string RemoteRenameMovesTrackingRefs = "Warning_RemoteRenameMovesTrackingRefs";

    /// <summary>Removing a remote discards its remote-tracking refs.</summary>
    public const string RemoteRemoveDropsTrackingRefs = "Warning_RemoteRemoveDropsTrackingRefs";
}
