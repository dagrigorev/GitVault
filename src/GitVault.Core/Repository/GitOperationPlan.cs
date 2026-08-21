using GitVault.Core.Models;
using GitVault.Core.Profiles;

namespace GitVault.Core.Repository;

/// <summary>
/// A set of changes to a repository or to git's configuration, not yet applied.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="ActivationPlan"/> for everything that is not a profile: editing
/// a configuration key, writing GitVault's own section, and — later — the operations that rewrite
/// history. It exists so those operations inherit the property that makes activation safe, which
/// is that planning and applying are separate calls and the thing applied is the object the user
/// was shown.
///
/// <see cref="OperationId"/> is an identifier used as a localization key suffix, never display
/// text. A snapshot taken today has to read correctly in whatever language the user chooses next
/// year.
/// </remarks>
/// <param name="OperationId">Stable identifier of the operation, e.g. <c>ConfigSet</c>.</param>
/// <param name="Scope">Configuration scope the operation writes at.</param>
/// <param name="RepositoryPath">Repository the operation addresses, when it addresses one.</param>
public sealed record GitOperationPlan(string OperationId, GitConfigScope Scope, string? RepositoryPath)
{
    /// <summary>Changes in execution order.</summary>
    public IReadOnlyList<PlannedChange> Changes { get; init; } = [];

    /// <summary>Files copied aside before anything is written.</summary>
    public IReadOnlyList<string> FilesToSnapshot { get; init; } = [];

    /// <summary>Reasons the plan cannot be applied, if any.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Human-readable target, recorded on the snapshot for the list to show.</summary>
    public string SnapshotTarget => RepositoryPath ?? Scope.ToString();

    /// <summary>True when the plan can be applied.</summary>
    public bool CanApply => Blockers.Count == 0 && Changes.Any(c => !c.IsNoOp);

    /// <summary>Renders the plan as the preview the user has to approve.</summary>
    /// <returns>The preview text.</returns>
    public string ToDiff() => PlanDiff.Render(Changes, Blockers);
}

/// <summary>Outcome of applying a <see cref="GitOperationPlan"/>.</summary>
/// <param name="OperationId">The operation that ran.</param>
/// <param name="SnapshotPath">Snapshot taken before the first write, for rollback.</param>
public sealed record GitOperationResult(string OperationId, string? SnapshotPath)
{
    /// <summary>Per-step outcomes, in execution order.</summary>
    public IReadOnlyList<ActivationStepResult> Steps { get; init; } = [];

    /// <summary>True when no step failed.</summary>
    public bool Succeeded => Steps.All(s => s.Outcome != StepOutcome.Failed);
}
