using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using Serilog;

namespace GitVault.App.Services;

/// <summary>
/// Owns the current <see cref="DiscoveryReport"/> and serialises scans. Pages observe it rather
/// than each running their own scan, so one rescan updates the whole window.
/// </summary>
internal sealed partial class ScanCoordinator : ObservableObject, IDisposable
{
    private readonly IDiscoveryOrchestrator _orchestrator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _inFlight;
    private bool _disposed;

    [ObservableProperty]
    private DiscoveryReport _report = DiscoveryReport.Empty;

    [ObservableProperty]
    private bool _isScanning;

    public ScanCoordinator(IDiscoveryOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
    }

    /// <summary>Raised on the scanning thread once a scan has produced a report.</summary>
    internal event EventHandler<DiscoveryReport>? ScanCompleted;

    /// <summary>True once at least one scan has completed.</summary>
    public bool HasScanned => Report.StartedUtc != DateTimeOffset.MinValue;

    /// <summary>Cancels any scan in flight and starts a new one.</summary>
    /// <param name="cancellationToken">Cancels the new scan.</param>
    /// <returns>The resulting report, or the previous one when the scan was cancelled.</returns>
    internal async Task<DiscoveryReport> RescanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        CancellationTokenSource source;
        try
        {
            _inFlight?.Cancel();
            _inFlight?.Dispose();
            _inFlight = source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IsScanning = true;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            var report = await _orchestrator.ScanAsync(source.Token).ConfigureAwait(false);

            Report = report;
            OnPropertyChanged(nameof(HasScanned));
            ScanCompleted?.Invoke(this, report);

            Log.Information(
                "Scan finished in {Duration}: {ProbeCount} probes, {IdentityCount} identities, "
                + "{KeyCount} keys, {AgentCount} agents, {CredentialCount} credentials, "
                + "{ClientCount} clients, {WarningCount} warnings",
                report.Duration,
                report.ProbeStatuses.Count,
                report.Identities.Count,
                report.Keys.Count,
                report.Agents.Count,
                report.Credentials.Count,
                report.Clients.Count,
                report.Warnings.Count);

            return report;
        }
        catch (OperationCanceledException)
        {
            // A superseded scan is normal: the user pressed rescan again, or the window closed.
            return Report;
        }
        finally
        {
            IsScanning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _gate.Dispose();
        _disposed = true;
    }
}
