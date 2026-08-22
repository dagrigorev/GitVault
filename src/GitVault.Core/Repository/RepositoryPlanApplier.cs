using GitVault.Core.Models;

namespace GitVault.Core.Repository;

/// <summary>Applies a repository plan, preserving the refs it names first.</summary>
public interface IRepositoryPlanApplier
{
    /// <summary>Applies a plan, or refuses a blocked one.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The outcome, including the backup taken.</returns>
    Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// The one place a repository plan turns into git commands.
/// </summary>
/// <remarks>
/// Remotes, branches, tags, worktrees, stashes and submodules all produce the same kind of plan
/// and all need the same three things done with it: preserve the refs it names, run its commands
/// in order, and stop reporting anything as applied once one has failed. Written once rather than
/// six times, because six copies is six places where one could quietly lose the backup.
///
/// A failure stops the run. A plan is a sequence the user approved as a whole, and carrying on
/// past a failed step would produce a repository in a state nobody was shown.
/// </remarks>
public sealed class RepositoryPlanApplier : IRepositoryPlanApplier
{
    private readonly IGitCommandRunner _git;
    private readonly IRefBackupService _backups;

    /// <summary>Creates the applier.</summary>
    /// <param name="git">Command runner.</param>
    /// <param name="backups">Ref backup service.</param>
    public RepositoryPlanApplier(IGitCommandRunner git, IRefBackupService backups)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(backups);

        _git = git;
        _backups = backups;
    }

    /// <inheritdoc/>
    public async Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanApply)
        {
            // The last place a blocked plan can be stopped before it touches anything.
            return new RepositoryResult(plan.OperationId, null);
        }

        string? backupId = null;

        if (plan.RefsToBackUp.Count > 0)
        {
            var backup = await _backups
                .BackupAsync(
                    plan.RepositoryPath,
                    plan.RefsToBackUp,
                    plan.OperationId,
                    string.Join(", ", plan.Changes.Select(c => c.Target).Distinct(StringComparer.Ordinal)),
                    cancellationToken)
                .ConfigureAwait(false);

            backupId = backup.Id;
        }

        var steps = new List<ActivationStepResult>();

        foreach (var change in plan.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _git
                .RunAsync(plan.RepositoryPath, change.Arguments, cancellationToken)
                .ConfigureAwait(false);

            steps.Add(new ActivationStepResult(
                change.Kind.ToString(),
                result.IsSuccess ? StepOutcome.Applied : StepOutcome.Failed,
                result.IsSuccess ? change.Target : result.StandardError.Trim()));

            if (!result.IsSuccess)
            {
                break;
            }
        }

        return new RepositoryResult(plan.OperationId, backupId) { Steps = steps };
    }
}
