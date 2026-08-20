using System.Diagnostics;
using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Models;
using GitVault.Core.Settings;
using GitVault.Core.Ssh;

namespace GitVault.Core.Discovery;

/// <summary>Contributes the SSH keys found on disk to a scan.</summary>
public sealed class SshKeyProbe : IProbe
{
    private readonly ISshKeyScanner _scanner;
    private readonly ISettingsService _settings;

    /// <summary>Creates the probe.</summary>
    /// <param name="scanner">Key scanner.</param>
    /// <param name="settings">Settings, for the user's extra key directories.</param>
    public SshKeyProbe(ISshKeyScanner scanner, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(settings);

        _scanner = scanner;
        _settings = settings;
    }

    /// <inheritdoc/>
    public string ProbeId => "ssh.keys";

    /// <inheritdoc/>
    public string DisplayName => "SSH keys";

    /// <inheritdoc/>
    public bool IsSupportedOnThisPlatform => true;

    /// <inheritdoc/>
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public async Task<ProbeResult<ProbePayload>> ProbeAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var keys = await _scanner
                .ScanAsync(_settings.Current.EnabledKeyDirectories, cancellationToken)
                .ConfigureAwait(false);

            var payload = new ProbePayload
            {
                Keys = keys,
                Warnings = [.. keys.SelectMany(k => k.Warnings)],
            };

            return ProbeResult<ProbePayload>.Ok(ProbeId, payload, stopwatch.Elapsed);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ProbeResult<ProbePayload>.Fail(
                ProbeId, ProbeStatus.AccessDenied, ex.Message, stopwatch.Elapsed);
        }
    }
}
