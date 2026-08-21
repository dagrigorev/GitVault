using GitVault.Core.Models;

namespace GitVault.Core.Profiles;

/// <summary>What kind of change a planned step makes.</summary>
public enum ChangeKind
{
    /// <summary>Set a git configuration key.</summary>
    GitConfigSet = 0,

    /// <summary>Remove a git configuration key.</summary>
    GitConfigUnset,

    /// <summary>Write or replace the managed block in <c>~/.ssh/config</c>.</summary>
    SshConfigBlock,

    /// <summary>Remove the managed block from <c>~/.ssh/config</c>.</summary>
    SshConfigBlockRemoval,

    /// <summary>Load a key into an agent.</summary>
    AgentLoad,
}

/// <summary>One change GitVault intends to make.</summary>
/// <param name="StepId">Stable identifier, used as a localization key suffix.</param>
/// <param name="Kind">What kind of change it is.</param>
/// <param name="Target">Key or file the change applies to.</param>
/// <param name="Before">Current value, or null when there is none.</param>
/// <param name="After">Value after the change, or null when it removes something.</param>
public sealed record PlannedChange(
    string StepId,
    ChangeKind Kind,
    string Target,
    string? Before,
    string? After)
{
    /// <summary>True when applying this would not actually change anything.</summary>
    public bool IsNoOp => string.Equals(Before, After, StringComparison.Ordinal);
}

/// <summary>
/// The complete set of changes an activation or deactivation would make.
/// </summary>
/// <remarks>
/// Planning and applying are separate calls on purpose. The plan is what the dry-run preview
/// renders, and it is the same object that <c>Apply</c> executes, so what the user approved is
/// exactly what runs.
/// </remarks>
public sealed record ActivationPlan(
    Guid ProfileId,
    string ProfileName,
    ActivationScope Scope,
    string? RepositoryPath,
    bool IsDeactivation)
{
    /// <summary>Changes in execution order.</summary>
    public IReadOnlyList<PlannedChange> Changes { get; init; } = [];

    /// <summary>Files that will be snapshotted before anything is written.</summary>
    public IReadOnlyList<string> FilesToSnapshot { get; init; } = [];

    /// <summary>Reasons the plan cannot be applied, if any.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>True when the plan can be applied.</summary>
    public bool CanApply => Blockers.Count == 0 && Changes.Any(c => !c.IsNoOp);

    /// <summary>
    /// Renders the plan as a unified-diff-like preview.
    /// </summary>
    /// <returns>The preview text.</returns>
    public string ToDiff() => PlanDiff.Render(Changes, Blockers);
}
