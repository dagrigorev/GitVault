using System.Diagnostics;
using GitVault.Core.Abstractions;
using GitVault.Core.Credentials;
using GitVault.Core.Diagnostics;
using GitVault.Core.Models;

namespace GitVault.Core.Discovery;

/// <summary>
/// Enumerates every reachable credential vault. Metadata only: no secret is read during a scan,
/// which is what makes it safe to run automatically at start-up.
/// </summary>
public sealed class CredentialProbe : IProbe
{
    /// <summary>Warning code raised when credentials are found in a plaintext store.</summary>
    public const string PlaintextStoreCode = "PlaintextCredentialStore";

    private readonly IReadOnlyList<ICredentialVault> _vaults;

    /// <summary>Creates the probe.</summary>
    /// <param name="vaults">Vaults registered for this platform.</param>
    public CredentialProbe(IEnumerable<ICredentialVault> vaults)
    {
        ArgumentNullException.ThrowIfNull(vaults);
        _vaults = [.. vaults];
    }

    /// <inheritdoc/>
    public string ProbeId => "credentials";

    /// <inheritdoc/>
    public string DisplayName => "Credential stores";

    /// <inheritdoc/>
    public bool IsSupportedOnThisPlatform => true;

    /// <inheritdoc/>
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    /// <inheritdoc/>
    public async Task<ProbeResult<ProbePayload>> ProbeAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var credentials = new List<CredentialEntry>();
        var warnings = new List<KeyWarning>();
        var deniedVaults = 0;

        foreach (var vault in _vaults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!vault.IsAvailable)
            {
                continue;
            }

            try
            {
                credentials.AddRange(await vault.ListAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (UnauthorizedAccessException)
            {
                // One locked vault must not cost us the others.
                deniedVaults++;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                deniedVaults++;
            }
            catch (InvalidOperationException)
            {
                deniedVaults++;
            }
        }

        foreach (var plaintext in credentials.Where(c => c.IsPlaintextStore).GroupBy(c => c.SourcePath ?? c.Target))
        {
            warnings.Add(new KeyWarning(PlaintextStoreCode, WarningSeverity.High, plaintext.Key));
        }

        var payload = new ProbePayload { Credentials = credentials, Warnings = warnings };

        return deniedVaults > 0 && credentials.Count == 0
            ? ProbeResult<ProbePayload>.Fail(ProbeId, ProbeStatus.AccessDenied, null, stopwatch.Elapsed)
            : ProbeResult<ProbePayload>.Ok(ProbeId, payload, stopwatch.Elapsed);
    }
}
