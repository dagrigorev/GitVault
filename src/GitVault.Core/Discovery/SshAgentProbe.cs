using System.Diagnostics;
using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Models;
using GitVault.Core.Ssh;
using GitVault.Core.Ssh.Agent;

namespace GitVault.Core.Discovery;

/// <summary>
/// Contacts every candidate agent endpoint and reports which ones answered, along with the keys
/// they hold. Reads only: no key is ever added or removed during a scan.
/// </summary>
public sealed class SshAgentProbe : IProbe
{
    private readonly IAgentEndpointProvider _endpoints;
    private readonly IReadOnlyList<ISshAgentTransportFactory> _factories;

    /// <summary>Creates the probe.</summary>
    /// <param name="endpoints">Endpoint provider for this platform.</param>
    /// <param name="factories">Transport factories, tried in order.</param>
    public SshAgentProbe(IAgentEndpointProvider endpoints, IEnumerable<ISshAgentTransportFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(factories);

        _endpoints = endpoints;
        _factories = [.. factories];
    }

    /// <inheritdoc/>
    public string ProbeId => "ssh.agents";

    /// <inheritdoc/>
    public string DisplayName => "SSH agents";

    /// <inheritdoc/>
    public bool IsSupportedOnThisPlatform => true;

    /// <inheritdoc/>
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);

    /// <inheritdoc/>
    public async Task<ProbeResult<ProbePayload>> ProbeAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var agents = new List<SshAgentInfo>();
        var keys = new List<SshKey>();
        var warnings = new List<KeyWarning>();

        foreach (var endpoint in _endpoints.GetEndpoints())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var factory = _factories.FirstOrDefault(f => f.CanHandle(endpoint));
            if (factory is null)
            {
                continue;
            }

            using var client = new SshAgentClient(endpoint, factory);
            var info = await client.ProbeAsync(cancellationToken).ConfigureAwait(false);

            // A candidate that is simply not there is noise, not information.
            if (!info.IsRunning)
            {
                continue;
            }

            agents.Add(info);
            keys.AddRange(info.LoadedKeys.Select(entry => ToKey(entry, endpoint)));
        }

        if (agents.Count == 0)
        {
            warnings.Add(new KeyWarning(AgentNotRunningCode, WarningSeverity.Low, string.Empty));
        }

        var payload = new ProbePayload { Agents = agents, Keys = keys, Warnings = warnings };
        return ProbeResult<ProbePayload>.Ok(ProbeId, payload, stopwatch.Elapsed);
    }

    /// <summary>Warning code raised when no agent answered anywhere.</summary>
    public const string AgentNotRunningCode = "AgentNotRunning";

    /// <summary>
    /// Represents an agent-held identity as a key. It has no path: the private half lives in the
    /// agent's memory, which is exactly what <see cref="SshKey.IsAgentOnly"/> means.
    /// </summary>
    /// <param name="entry">Identity the agent reported.</param>
    /// <param name="endpoint">Endpoint that reported it.</param>
    /// <returns>The key.</returns>
    internal static SshKey ToKey(AgentKeyEntry entry, AgentEndpoint endpoint)
    {
        var blob = entry.Blob.ToArray();
        int? bits = null;

        try
        {
            bits = SshPublicKeyReader.FromBlob(blob).BitLength;
        }
        catch (SshWireException)
        {
            // An unmodelled key type still has a usable fingerprint.
        }

        return new SshKey(
            Guid.NewGuid(),
            PrivatePath: null,
            PublicPath: null,
            entry.Algorithm,
            bits,
            entry.FingerprintSha256,
            SshFingerprint.Md5(blob),
            string.IsNullOrEmpty(entry.Comment) ? null : entry.Comment,
            SshKeyFormat.Unknown,
            IsEncrypted: false,
            KdfRounds: null,
            IsHardwareBacked: entry.Algorithm is SshKeyAlgorithm.Ed25519Sk or SshKeyAlgorithm.EcdsaSk)
        {
            PublicKeyBlob = blob,
            LoadedInAgents = [new AgentRef(endpoint.Kind, endpoint.Endpoint)],
        };
    }
}
