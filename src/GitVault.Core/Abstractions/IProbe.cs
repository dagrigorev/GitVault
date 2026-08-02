using GitVault.Core.Diagnostics;
using GitVault.Core.Models;

namespace GitVault.Core.Abstractions;

/// <summary>
/// A read-only discovery unit. Implementations must never write to disk, the registry,
/// a keychain or a running agent.
/// </summary>
public interface IProbe
{
    /// <summary>Stable identifier, used in the probe status matrix and in logs.</summary>
    string ProbeId { get; }

    /// <summary>Name shown in the UI. Product names are intentionally not localized.</summary>
    string DisplayName { get; }

    /// <summary>True when the probe can run on the current platform.</summary>
    bool IsSupportedOnThisPlatform { get; }

    /// <summary>Time budget for one run. The orchestrator enforces it.</summary>
    TimeSpan Timeout => TimeSpan.FromSeconds(5);

    /// <summary>Runs the probe.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The probe's contribution to the scan.</returns>
    Task<ProbeResult<ProbePayload>> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Everything a single probe can contribute to a scan.</summary>
public sealed record ProbePayload
{
    /// <summary>Identities the probe found.</summary>
    public IReadOnlyList<GitIdentity> Identities { get; init; } = [];

    /// <summary>Keys the probe found.</summary>
    public IReadOnlyList<SshKey> Keys { get; init; } = [];

    /// <summary>Agents the probe found.</summary>
    public IReadOnlyList<SshAgentInfo> Agents { get; init; } = [];

    /// <summary>Credential metadata the probe found.</summary>
    public IReadOnlyList<CredentialEntry> Credentials { get; init; } = [];

    /// <summary>Clients the probe found.</summary>
    public IReadOnlyList<DetectedClient> Clients { get; init; } = [];

    /// <summary>Health findings the probe raised.</summary>
    public IReadOnlyList<KeyWarning> Warnings { get; init; } = [];

    /// <summary>A payload with nothing in it.</summary>
    public static ProbePayload Empty { get; } = new();
}

/// <summary>A probe specialised for detecting a third-party Git client.</summary>
public interface IClientProbe : IProbe
{
    /// <summary>Which client this probe looks for.</summary>
    GitClientKind ClientKind { get; }
}

/// <summary>Runs every registered probe and merges the results.</summary>
public interface IDiscoveryOrchestrator
{
    /// <summary>Runs all applicable probes in parallel and merges their output.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>A merged, deduplicated report.</returns>
    Task<DiscoveryReport> ScanAsync(CancellationToken cancellationToken);
}
