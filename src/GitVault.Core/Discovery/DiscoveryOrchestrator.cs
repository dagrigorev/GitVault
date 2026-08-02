using System.Diagnostics;
using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Models;

namespace GitVault.Core.Discovery;

/// <summary>
/// Runs every registered probe in parallel with a per-probe time budget and merges the results.
/// No single probe can abort the scan: a throw, a hang or a refusal each become a status entry.
/// </summary>
public sealed class DiscoveryOrchestrator : IDiscoveryOrchestrator
{
    private readonly IReadOnlyList<IProbe> _probes;

    /// <summary>Creates the orchestrator.</summary>
    /// <param name="probes">Probes to run.</param>
    public DiscoveryOrchestrator(IEnumerable<IProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = [.. probes];
    }

    /// <inheritdoc/>
    public async Task<DiscoveryReport> ScanAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var results = await Task.WhenAll(_probes.Select(p => RunProbeAsync(p, cancellationToken)))
            .ConfigureAwait(false);

        stopwatch.Stop();
        return Merge(results, started, stopwatch.Elapsed);
    }

    /// <summary>Merges probe outcomes into one deduplicated report.</summary>
    /// <param name="results">Per-probe outcomes.</param>
    /// <param name="started">Scan start time.</param>
    /// <param name="duration">Total scan duration.</param>
    /// <returns>The merged report.</returns>
    internal static DiscoveryReport Merge(
        IReadOnlyList<(IProbe Probe, ProbeResult<ProbePayload> Result)> results,
        DateTimeOffset started,
        TimeSpan duration)
    {
        var identities = new List<GitIdentity>();
        var keys = new List<SshKey>();
        var agents = new List<SshAgentInfo>();
        var credentials = new List<CredentialEntry>();
        var clients = new List<DetectedClient>();
        var warnings = new List<KeyWarning>();
        var statuses = new List<ProbeStatusEntry>();

        foreach (var (probe, result) in results)
        {
            statuses.Add(new ProbeStatusEntry(
                result.ProbeId, probe.DisplayName, result.Status, result.Diagnostics, result.Elapsed));

            if (!result.IsSuccess)
            {
                continue;
            }

            var payload = result.Value!;
            identities.AddRange(payload.Identities);
            keys.AddRange(payload.Keys);
            agents.AddRange(payload.Agents);
            credentials.AddRange(payload.Credentials);
            clients.AddRange(payload.Clients);
            warnings.AddRange(payload.Warnings);
        }

        return new DiscoveryReport(started, duration)
        {
            Identities = DeduplicateIdentities(identities),
            Keys = DeduplicateKeys(keys),
            Agents = agents,
            Credentials = credentials,
            Clients = clients,
            Warnings = warnings,
            ProbeStatuses = statuses,
        };
    }

    /// <summary>
    /// Merges identities that share a (name, e-mail) pair, unioning their sources and hosts.
    /// The highest-confidence occurrence wins for the surviving record's own fields.
    /// </summary>
    /// <param name="identities">Identities from every probe.</param>
    /// <returns>Deduplicated identities.</returns>
    internal static IReadOnlyList<GitIdentity> DeduplicateIdentities(IReadOnlyList<GitIdentity> identities)
    {
        var merged = new Dictionary<IdentityKey, GitIdentity>();
        var order = new List<IdentityKey>();

        foreach (var identity in identities)
        {
            var key = identity.Key;
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = identity;
                order.Add(key);
                continue;
            }

            var hosts = existing.Hosts.Concat(identity.Hosts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var occurrences = existing.Occurrences.Concat(identity.Occurrences)
                .DistinctBy(o => (o.Source, o.Path))
                .ToArray();

            var winner = identity.Confidence > existing.Confidence ? identity : existing;

            merged[key] = winner with
            {
                Id = existing.Id,
                Hosts = hosts,
                Occurrences = occurrences,
                SigningKeyId = existing.SigningKeyId ?? identity.SigningKeyId,
            };
        }

        return [.. order.Select(k => merged[k])];
    }

    /// <summary>
    /// Merges keys that share a SHA256 fingerprint. The same key is routinely referenced from
    /// several places, so path is not an identity.
    /// </summary>
    /// <param name="keys">Keys from every probe.</param>
    /// <returns>Deduplicated keys.</returns>
    internal static IReadOnlyList<SshKey> DeduplicateKeys(IReadOnlyList<SshKey> keys)
    {
        var merged = new Dictionary<string, SshKey>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var key in keys)
        {
            var fingerprint = key.FingerprintSha256;
            if (string.IsNullOrEmpty(fingerprint))
            {
                order.Add(Guid.NewGuid().ToString("N"));
                merged[order[^1]] = key;
                continue;
            }

            if (!merged.TryGetValue(fingerprint, out var existing))
            {
                merged[fingerprint] = key;
                order.Add(fingerprint);
                continue;
            }

            merged[fingerprint] = existing with
            {
                PrivatePath = existing.PrivatePath ?? key.PrivatePath,
                PublicPath = existing.PublicPath ?? key.PublicPath,
                Comment = existing.Comment ?? key.Comment,
                Permissions = existing.Permissions ?? key.Permissions,
                LoadedInAgents = [.. existing.LoadedInAgents.Concat(key.LoadedInAgents).DistinctBy(a => (a.Kind, a.Endpoint))],
                Warnings = [.. existing.Warnings.Concat(key.Warnings).DistinctBy(w => (w.Code, w.Subject))],
            };
        }

        return [.. order.Distinct(StringComparer.Ordinal).Select(k => merged[k])];
    }

    private static async Task<(IProbe Probe, ProbeResult<ProbePayload> Result)> RunProbeAsync(
        IProbe probe,
        CancellationToken cancellationToken)
    {
        if (!probe.IsSupportedOnThisPlatform)
        {
            return (probe, ProbeResult<ProbePayload>.Fail(probe.ProbeId, ProbeStatus.NotApplicable));
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probe.Timeout);

        try
        {
            var result = await probe.ProbeAsync(timeout.Token).ConfigureAwait(false);
            return (probe, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (probe, ProbeResult<ProbePayload>.Fail(
                probe.ProbeId, ProbeStatus.Timeout, null, stopwatch.Elapsed));
        }
        catch (UnauthorizedAccessException ex)
        {
            return (probe, ProbeResult<ProbePayload>.Fail(
                probe.ProbeId, ProbeStatus.AccessDenied, ex.Message, stopwatch.Elapsed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe is third-party-ish code touching foreign file formats. One that throws is
            // a bug worth reporting, never a reason to lose the rest of the scan.
            return (probe, ProbeResult<ProbePayload>.Fail(
                probe.ProbeId, ProbeStatus.Failed, ex.Message, stopwatch.Elapsed));
        }
    }
}
