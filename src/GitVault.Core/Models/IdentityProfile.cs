namespace GitVault.Core.Models;

/// <summary>
/// A user-authored bundle of identity, key, agent, credential helper and SSH host aliases.
/// This is the unit that gets activated and deactivated.
/// </summary>
/// <param name="Id">Stable identifier, persisted.</param>
/// <param name="Name">User-chosen profile name. Also used in <c>~/.ssh/config</c> markers.</param>
/// <param name="Identity">Identity to write into git config.</param>
/// <param name="SshKeyId">Key to make available, referencing an <see cref="SshKey.Id"/>.</param>
/// <param name="PreferredAgent">Agent to load the key into.</param>
/// <param name="CredentialHelper">Value for <c>credential.helper</c>.</param>
/// <param name="Scope">Where activation writes to.</param>
/// <param name="RepositoryPath">Target repository when <paramref name="Scope"/> is
/// <see cref="ActivationScope.Repository"/>.</param>
public sealed record IdentityProfile(
    Guid Id,
    string Name,
    GitIdentity Identity,
    Guid? SshKeyId,
    AgentKind? PreferredAgent,
    string? CredentialHelper,
    ActivationScope Scope,
    string? RepositoryPath)
{
    /// <summary>Host blocks to write into <c>~/.ssh/config</c>.</summary>
    public IReadOnlyList<SshHostAlias> HostAliases { get; init; } = [];

    /// <summary>Absolute path of the private key, resolved at activation time.</summary>
    public string? SshKeyPath { get; init; }

    /// <summary>Per-host credential routing: host to username.</summary>
    public IReadOnlyDictionary<string, string> CredentialUserNames { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, activation also writes <c>core.sshCommand</c> pinning the key.</summary>
    public bool WriteCoreSshCommand { get; init; } = true;

    /// <summary>Opening marker line written into <c>~/.ssh/config</c>.</summary>
    /// <returns>The marker text.</returns>
    public string BeginMarker() => $"# >>> GitVault managed: {Name} >>>";

    /// <summary>Closing marker line written into <c>~/.ssh/config</c>.</summary>
    /// <returns>The marker text.</returns>
    public string EndMarker() => $"# <<< GitVault managed: {Name} <<<";
}

/// <summary>Result of one step of an activation or deactivation.</summary>
/// <param name="StepId">Stable step identifier, used as a localization key suffix.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Target">File, config key or vault target the step touched.</param>
/// <param name="Detail">Extra diagnostic text; never contains secrets.</param>
public sealed record ActivationStepResult(
    string StepId,
    StepOutcome Outcome,
    string Target,
    string? Detail = null);

/// <summary>Outcome of a single activation step.</summary>
public enum StepOutcome
{
    /// <summary>The step was skipped because the profile does not require it.</summary>
    Skipped = 0,

    /// <summary>The step would have run; reported in dry-run mode.</summary>
    Planned,

    /// <summary>The step ran successfully.</summary>
    Applied,

    /// <summary>The step failed. <see cref="ActivationStepResult.Detail"/> explains why.</summary>
    Failed,
}

/// <summary>Aggregate result of activating or deactivating a profile.</summary>
/// <param name="ProfileId">Profile that was applied.</param>
/// <param name="Scope">Scope that was targeted.</param>
/// <param name="WasDryRun">True when nothing was written.</param>
/// <param name="SnapshotPath">Directory holding the pre-change snapshot, when one was taken.</param>
/// <param name="StartedUtc">When the operation started.</param>
public sealed record ActivationResult(
    Guid ProfileId,
    ActivationScope Scope,
    bool WasDryRun,
    string? SnapshotPath,
    DateTimeOffset StartedUtc)
{
    /// <summary>Per-step outcomes, in execution order.</summary>
    public IReadOnlyList<ActivationStepResult> Steps { get; init; } = [];

    /// <summary>True when no step failed.</summary>
    public bool Succeeded => Steps.All(s => s.Outcome != StepOutcome.Failed);
}
