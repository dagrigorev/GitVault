using GitVault.Core.Diagnostics;

namespace GitVault.Core.Models;

/// <summary>The merged, deduplicated outcome of a full scan.</summary>
/// <param name="StartedUtc">When the scan started.</param>
/// <param name="Duration">How long the scan took end to end.</param>
public sealed record DiscoveryReport(DateTimeOffset StartedUtc, TimeSpan Duration)
{
    /// <summary>Deduplicated identities, merged across sources.</summary>
    public IReadOnlyList<GitIdentity> Identities { get; init; } = [];

    /// <summary>Keys deduplicated by SHA256 fingerprint.</summary>
    public IReadOnlyList<SshKey> Keys { get; init; } = [];

    /// <summary>Agents that answered, plus known endpoints that did not.</summary>
    public IReadOnlyList<SshAgentInfo> Agents { get; init; } = [];

    /// <summary>Credential metadata from every reachable vault.</summary>
    public IReadOnlyList<CredentialEntry> Credentials { get; init; } = [];

    /// <summary>Third-party clients found.</summary>
    public IReadOnlyList<DetectedClient> Clients { get; init; } = [];

    /// <summary>Per-probe status, including probes that found nothing.</summary>
    public IReadOnlyList<ProbeStatusEntry> ProbeStatuses { get; init; } = [];

    /// <summary>Health findings aggregated from every source.</summary>
    public IReadOnlyList<KeyWarning> Warnings { get; init; } = [];

    /// <summary>An empty report, used before the first scan completes.</summary>
    public static DiscoveryReport Empty { get; } =
        new(DateTimeOffset.MinValue, TimeSpan.Zero);
}
