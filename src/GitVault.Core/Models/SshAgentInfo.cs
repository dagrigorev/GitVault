namespace GitVault.Core.Models;

/// <summary>A discovered SSH agent endpoint and its current contents.</summary>
/// <param name="Kind">Agent kind.</param>
/// <param name="Endpoint">Socket path, named pipe name, or window handle descriptor.</param>
/// <param name="IsRunning">True when the endpoint answered a request-identities call.</param>
/// <param name="SupportsAdd">True when the agent accepts <c>SSH_AGENTC_ADD_IDENTITY</c>.</param>
/// <param name="SupportsConstraints">True when the agent accepts lifetime/confirm constraints.</param>
public sealed record SshAgentInfo(
    AgentKind Kind,
    string Endpoint,
    bool IsRunning,
    bool SupportsAdd,
    bool SupportsConstraints)
{
    /// <summary>Keys the agent reported.</summary>
    public IReadOnlyList<AgentKeyEntry> LoadedKeys { get; init; } = [];

    /// <summary>True when the agent reports it is locked.</summary>
    public bool IsLocked { get; init; }

    /// <summary>Human-readable note explaining a degraded state, already localized by the caller.</summary>
    public string? StatusDetail { get; init; }
}

/// <summary>One identity held by an agent.</summary>
/// <param name="Blob">Raw SSH public key blob as returned by the agent.</param>
/// <param name="Comment">Comment the agent stored alongside the key.</param>
/// <param name="FingerprintSha256">Canonical OpenSSH fingerprint of <paramref name="Blob"/>.</param>
/// <param name="Algorithm">Algorithm parsed from the blob's leading key type string.</param>
public sealed record AgentKeyEntry(
    IReadOnlyList<byte> Blob,
    string Comment,
    string FingerprintSha256,
    SshKeyAlgorithm Algorithm);
