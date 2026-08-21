using GitVault.Core.Abstractions;
using GitVault.Core.Git;
using GitVault.Core.Models;
using GitVault.Core.Profiles;

namespace GitVault.Core.Repository;

/// <summary>Plans and applies edits to git's configuration.</summary>
public interface IConfigEditor
{
    /// <summary>Works out what setting a key would change. Writes nothing.</summary>
    /// <param name="key">Configuration key, e.g. <c>user.email</c>.</param>
    /// <param name="value">Value to set.</param>
    /// <param name="scope">Scope to write at.</param>
    /// <param name="repositoryPath">Repository, for local and worktree scope.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<GitOperationPlan> PlanSetAsync(
        string key,
        string value,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken);

    /// <summary>Works out what removing a key would change. Writes nothing.</summary>
    /// <param name="key">Configuration key.</param>
    /// <param name="scope">Scope to remove from.</param>
    /// <param name="repositoryPath">Repository, for local and worktree scope.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<GitOperationPlan> PlanUnsetAsync(
        string key,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken);

    /// <summary>Plans several changes at one scope as a single reviewable unit.</summary>
    /// <param name="operationId">Identifier recorded on the snapshot.</param>
    /// <param name="values">Key to value; a null value means remove the key.</param>
    /// <param name="scope">Scope to write at.</param>
    /// <param name="repositoryPath">Repository, for local and worktree scope.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<GitOperationPlan> PlanBatchAsync(
        string operationId,
        IReadOnlyDictionary<string, string?> values,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken);

    /// <summary>Applies a plan, snapshotting the affected file first.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A per-step result.</returns>
    Task<GitOperationResult> ApplyAsync(GitOperationPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// The configuration editor.
/// </summary>
/// <remarks>
/// Two properties are worth stating because the rest of the class is arranged to keep them true.
///
/// Planning reads and never writes. The "before" value is read from the <em>requested scope</em>
/// rather than from the effective configuration: the effective value may come from a different
/// file, and showing it as the thing being replaced would tell the user their global identity is
/// about to be overwritten when in fact a local key is being added. The same mistake, made in the
/// profile activator, once caused a deactivation to write a global identity into a repository.
///
/// Applying snapshots first. The file recorded is the one <see cref="IGitConfigService"/> resolves
/// for the scope, and git is pinned to that same file, so the snapshot and the write cannot
/// address different files.
/// </remarks>
public sealed class ConfigEditor : IConfigEditor
{
    /// <summary>Operation identifier for a single-key edit.</summary>
    public const string ConfigEditOperationId = "ConfigEdit";

    /// <summary>Step identifier used for every configuration step.</summary>
    public const string ConfigStepId = "ConfigKey";

    private readonly IGitConfigService _config;
    private readonly ISnapshotService _snapshots;

    /// <summary>Creates the editor.</summary>
    /// <param name="config">Configuration service used to read and write.</param>
    /// <param name="snapshots">Snapshot service used before any write.</param>
    public ConfigEditor(IGitConfigService config, ISnapshotService snapshots)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(snapshots);

        _config = config;
        _snapshots = snapshots;
    }

    /// <inheritdoc/>
    public Task<GitOperationPlan> PlanSetAsync(
        string key,
        string value,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        return PlanBatchAsync(
            ConfigEditOperationId,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [key] = value },
            scope,
            repositoryPath,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<GitOperationPlan> PlanUnsetAsync(
        string key,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return PlanBatchAsync(
            ConfigEditOperationId,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [key] = null },
            scope,
            repositoryPath,
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<GitOperationPlan> PlanBatchAsync(
        string operationId,
        IReadOnlyDictionary<string, string?> values,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(values);

        var blockers = new List<string>();

        if (scope is GitConfigScope.Local or GitConfigScope.Worktree && string.IsNullOrWhiteSpace(repositoryPath))
        {
            blockers.Add(BlockerMessages.RepositoryRequired);
        }

        var file = _config.ResolveConfigFilePath(scope, repositoryPath);
        if (file is null)
        {
            blockers.Add(BlockerMessages.NoConfigurationFile);
        }

        var current = await ReadScopedAsync(scope, repositoryPath, cancellationToken).ConfigureAwait(false);
        var changes = new List<PlannedChange>();

        foreach (var (key, after) in values)
        {
            current.TryGetValue(key, out var before);

            changes.Add(new PlannedChange(
                ConfigStepId,
                after is null ? ChangeKind.GitConfigUnset : ChangeKind.GitConfigSet,
                key,
                before,
                after));
        }

        return new GitOperationPlan(operationId, scope, repositoryPath)
        {
            Changes = changes,
            FilesToSnapshot = file is null ? [] : [file],
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<GitOperationResult> ApplyAsync(GitOperationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanApply)
        {
            // Refusing here rather than trusting the caller: this is the last place that can stop
            // a blocked or empty plan from touching a file.
            return new GitOperationResult(plan.OperationId, null);
        }

        var snapshot = await _snapshots
            .CaptureAsync(
                plan.FilesToSnapshot,
                new SnapshotMetadata(plan.OperationId, string.Empty, plan.SnapshotTarget),
                cancellationToken)
            .ConfigureAwait(false);

        var steps = new List<ActivationStepResult>();

        foreach (var change in plan.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (change.IsNoOp)
            {
                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Skipped, change.Target));
                continue;
            }

            try
            {
                if (change.After is null)
                {
                    await _config
                        .UnsetAsync(change.Target, plan.Scope, plan.RepositoryPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _config
                        .SetAsync(change.Target, change.After, plan.Scope, plan.RepositoryPath, cancellationToken)
                        .ConfigureAwait(false);
                }

                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Applied, change.Target));
            }
            catch (GitConfigException ex)
            {
                // A refusal — usually the system scope without elevation — is a normal outcome
                // that the caller reports. The snapshot above is what makes it recoverable.
                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Failed, ex.Message));
            }
        }

        return new GitOperationResult(plan.OperationId, snapshot.Path) { Steps = steps };
    }

    /// <summary>
    /// Reads the values set at one scope, ignoring what other scopes contribute.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing detail of the whole class. Reading the effective value would make
    /// the preview claim that a value inherited from a wider scope is about to be replaced, and
    /// removing a key would appear to remove something it cannot reach.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> ReadScopedAsync(
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        var all = await _config.ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in all.Where(v => v.Scope == scope))
        {
            result[value.Key] = value.Value;
        }

        return result;
    }
}

/// <summary>
/// Blocker texts.
/// </summary>
/// <remarks>
/// Identifiers rather than sentences: the view layer maps them to localized text, so a plan built
/// today reads in whatever language the user is using when they look at it.
/// </remarks>
public static class BlockerMessages
{
    /// <summary>
    /// Prefix every blocker identifier carries.
    /// </summary>
    /// <remarks>
    /// Published here rather than spelled out where blockers are rendered, so the convention has
    /// one definition. A plan may also carry a blocker that arrived as plain text from somewhere
    /// else, and the prefix is how the view tells the two apart.
    /// </remarks>
    public const string Prefix = "Blocker_";

    /// <summary>A repository-scoped operation was requested without a repository.</summary>
    public const string RepositoryRequired = "Blocker_RepositoryRequired";

    /// <summary>No configuration file exists for the requested scope.</summary>
    public const string NoConfigurationFile = "Blocker_NoConfigurationFile";
}
